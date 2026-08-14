// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include ".\POP3ClientConnection.h"

#include "FetchAccountUIDList.h"

#include "../Common/Scripting/Result.h"
#include "../Common/Scripting/Events.h"
#include "../Common/BO/FetchAccount.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/MessageData.h"
#include "../Common/BO/Account.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "../Common/Util/ByteBuffer.h"
#include "../SMTP/RecipientParser.h"
#include "../Common/Util/Parsing/AddressListParser.h"
#include "../Common/Util/Utilities.h"
#include "../Common/Util/ServerStatus.h"
#include "../Common/Mime/Mime.h"
#include "../Common/BO/FetchAccountUID.h"
#include "../Common/BO/MessageRecipients.h"
#include "../common/util/MessageUtilities.h"
#include "../common/Threading/AsynchronousTask.h"
#include "../common/Threading/WorkQueue.h"

#include "../Common/Util/TransparentTransmissionBuffer.h"

#include "../Common/Application/TimeoutCalculator.h"

#include "../Common/Util/VariantDateTime.h"
#include "../Common/Util/Time.h"
#include "../Common/Cache/CacheContainer.h"
#include "../Common/AntiSpam/AntiSpamConfiguration.h"
#include "../Common/AntiSpam/SpamProtection.h"

#include <boost/algorithm/string.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   POP3ClientConnection::POP3ClientConnection(std::shared_ptr<FetchAccount> pAccount,
                                              ConnectionSecurity connectionSecurity,
                                              boost::asio::io_context& io_context, 
                                              boost::asio::ssl::context& context,
                                              std::shared_ptr<Event> disconnected,
                                              AnsiString remote_hostname) :
      TCPConnection(connectionSecurity, io_context, context, disconnected, remote_hostname),
      account_(pAccount),
      current_state_(StateConnected),
      retr_failed_(false),
      download_finalized_(true),
      finalization_enqueued_tick_(0)
   {

      /*
      RFC 1939, Basic Operation
      A POP3 server MAY have an inactivity autologout timer.  Such a timer
      MUST be of at least 10 minutes' duration.

      But since we're a client, we increase this a bit.
      */

      TimeoutCalculator calculator;
      SetTimeout(calculator.Calculate(IniFileSettings::Instance()->GetPOP3CMinTimeout(), IniFileSettings::Instance()->GetPOP3CMaxTimeout()));
   }

   POP3ClientConnection::~POP3ClientConnection(void)
   {
      try
      {
         DiscardUnfinishedDownload_();
      }
      catch (...)
      {
         // A destructor must not let anything escape, and this one runs while the
         // connection is being torn down - possibly during shutdown.
      }
   }

   void
   POP3ClientConnection::DiscardUnfinishedDownload_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Removes the spool file of a download that never completed.
   //
   // A message being downloaded is written straight into a file in the data directory,
   // and only the finalization that runs once \r\n.\r\n has arrived saves a database row
   // referring to it. If the remote server disconnects part-way through a RETR - or the
   // session times out, or the fetch is abandoned for any other reason - that file used
   // to be simply left behind: no row ever referred to it, nothing ever deleted it, and
   // every interrupted fetch added another one to the data directory. Since a fetch talks
   // to a machine the administrator does not control, an unreliable (or hostile) remote
   // server could grow that pile without limit.
   //
   // The message itself is not lost by discarding the fragment: nothing was recorded in
   // the UID table and no DELE was sent, so the next fetch collects it again.
   //---------------------------------------------------------------------------()
   {
      if (download_finalized_ || !current_message_)
         return;

      // Second, independent guard on the one thing that must never happen here: deleting
      // the file of a message that has been saved. A non-zero id means PersistentMessage
      // has written a row pointing at this file, so even if the flag above were somehow
      // wrong - an exception escaping the finalization between the save and the flag, say
      // - the delivered message is not touched.
      if (current_message_->GetID() > 0)
      {
         download_finalized_ = true;
         return;
      }

      // Released first, because the buffer owns the open file handle.
      transmission_buffer_.reset();

      download_finalized_ = true;

      String fileName = PersistentMessage::GetFileName(current_message_);

      if (!FileUtilities::Exists(fileName))
         return;

      if (!FileUtilities::DeleteFile(fileName))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5985, "POP3ClientConnection::DiscardUnfinishedDownload_",
            "Could not delete the spool file of an interrupted external account download: " + fileName);
         return;
      }

      String logMessage =
         Formatter::Format("POP3 External Account: the download of a message from {0} was interrupted, so the incomplete spool file has been discarded. The message has not been deleted from the remote server and will be downloaded again.", account_->GetName());

      LOG_APPLICATION(logMessage);
   }

   void
   POP3ClientConnection::OnConnected()
   {
      if (GetConnectionSecurity() == CSNone || 
          GetConnectionSecurity() == CSSTARTTLSOptional ||
          GetConnectionSecurity() == CSSTARTTLSRequired)
         EnqueueRead();
   }

   void
   POP3ClientConnection::OnHandshakeCompleted()
   {
      if (GetConnectionSecurity() == CSSSL)
         EnqueueRead();
      else if (GetConnectionSecurity() == CSSTARTTLSOptional ||
               GetConnectionSecurity() == CSSTARTTLSRequired)
      {
         SendUserName_();
         EnqueueRead();
      }
   }

   AnsiString 
   POP3ClientConnection::GetCommandSeparator() const
   {
      return "\r\n";
   }

   void
   POP3ClientConnection::ParseData(const AnsiString &sRequest)
   {
      command_buffer_.append(sRequest);
      command_buffer_.append("\r\n");

      bool is_awaiting_multiline_response  = current_state_ == StateCAPASent;

      if (is_awaiting_multiline_response)
      {
         bool multiline_response_completed = sRequest == "." ||
                                             !CommandIsSuccessfull_(command_buffer_);

         if (!multiline_response_completed)
         {
            EnqueueRead();
            return;
         }
      }

      bool postReceive = InternalParseData(command_buffer_);

      // The ASCII buffer has been parsed, so we
      // may clear it now.
      command_buffer_.Empty();

      if (postReceive)
         EnqueueRead();
   }

   bool
   POP3ClientConnection::InternalParseData(const String &sRequest)
   {
      // This code is temporary home of ETRN client settings in GUI
      // It checks External Account for ETRN domain.com for name
      // and if found uses that info to perform ETRN client connections
      String sAccountName = account_->GetName();
      if (sAccountName.StartsWith(_T("ETRN")))
      {
         HandleEtrn_(sRequest, sAccountName);
         return true;
      }
      else
      {
          // No sense in indenting code below inward as this is temp
          // and it'd just have to be moved back.
          // **** Don't miss } below when removing the above code! ****

         LogPOP3String_(sRequest, false);

         bool bRetVal = true;
         switch (current_state_)
         {
         case StateConnected:
            ParseStateConnected_(sRequest);
            return true;
         case StateCAPASent:
            ParseStateCAPASent_(sRequest);
            return true;
         case StateSTLSSent:
            return ParseStateSTLSSent_(sRequest);
         case StateUsernameSent:
            ParseUsernameSent_(sRequest);
            return true;
         case StatePasswordSent:
            ParsePasswordSent_(sRequest);
            return true;
         case StateUIDLRequestSent:
            ParseUIDLResponse_(sRequest);
            return true;
         case StateQUITSent:
            return ParseQuitResponse_(sRequest);
         case StateDELESent:
            ParseDELEResponse_(sRequest);
            return true;
         }
   
         // This will be removed too when ETRN code is moved
       }

      return true;
   }

   bool
   POP3ClientConnection::HandleEtrn_(const String &sRequest, const String &account_name)
   {
      LogSMTPString_(sRequest, false);

      std::vector<String> vecParams = StringParser::SplitString(account_name, " ");
      if (vecParams.size() == 2)
      {
         bool bRetVal = true;
         switch (current_state_)
         {
            // Re-using POP states names for now
         case StateConnected:
            // Realize we shouldn't blindly send but this works for now
            EnqueueWrite_LogAsSMTP("HELO " + vecParams[1]);
            current_state_ = StateUsernameSent;
            return true;
         case StateUsernameSent:
            EnqueueWrite_LogAsSMTP("ETRN " + vecParams[1]);
            Sleep(20);
            current_state_ = StateUIDLRequestSent;
            return true;
         case StateUIDLRequestSent:
            EnqueueWrite_LogAsSMTP("QUIT");
            current_state_ = StateQUITSent;
            Sleep(20);
            return true;

         default:
            return false;
         }
      }
      else
      {
         //We just log error & QUIT because we have no domain to send..
         EnqueueWrite_("NOOP ETRN-Domain not set");
         Sleep(20);
         EnqueueWrite_("QUIT");
         ParseQuitResponse_(sRequest);
         return false;
      }
   }

   void
   POP3ClientConnection::ParseStateConnected_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         if (GetConnectionSecurity() == CSSTARTTLSOptional ||
             GetConnectionSecurity() == CSSTARTTLSRequired)
         {
            SendCAPA_();
            return;
         }
         else
         {
            SendUserName_();
            return;
         }
      }

      // Disconnect immediately.;
      QuitNow_();
      return;
   }

   void
   POP3ClientConnection::SendUserName_()
   {
      // We have connected successfully.
      // Time to send the username.

      String sResponse;
      sResponse.Format(_T("USER %s"), account_->GetUsername().c_str());

      EnqueueWrite_(sResponse);

      current_state_ = StateUsernameSent;
   }

   void
   POP3ClientConnection::SendCAPA_()
   {
      // We have connected successfully.
      // Time to send the username.
      EnqueueWrite_(_T("CAPA"));

      current_state_ = StateCAPASent;
   }

   void
   POP3ClientConnection::ParseStateCAPASent_(const String &sData)
   {
      if (!CommandIsSuccessfull_(sData) || !sData.Contains(_T("STLS")))
      {
         // STLS is not supported.
         if (GetConnectionSecurity() == CSSTARTTLSRequired)
         {
            String message = 
               Formatter::Format("The download of messages from external account {0} failed. The external aAccount is configured to use STARTTLS connection security, but the POP3 server does not support it.", account_->GetName());
            
            LOG_APPLICATION(message)
            QuitNow_();
            return;
         }
         else
         {
            SendUserName_();
            return;
         }
      }

      EnqueueWrite_("STLS");
      current_state_ = StateSTLSSent;
   }

   bool
   POP3ClientConnection::ParseStateSTLSSent_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         EnqueueHandshake();
         return false;
      }

      // Disconnect immediately.
      QuitNow_();
      return true;
   }

   void 
   POP3ClientConnection::ParseUsernameSent_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         // We have connected successfully.
         // Time to send the username.

         String sResponse;
         sResponse.Format(_T("PASS %s"), account_->GetPassword().c_str());

         current_state_ = StatePasswordSent;

         EnqueueWrite_(sResponse);

         return;
      }

      QuitNow_();
      return;
   }

   void 
   POP3ClientConnection::ParsePasswordSent_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         current_state_ = StateUIDLRequestSent;

         SetReceiveBinary(true);

         // We have connected successfully.
         // Time to send the username.
         String sResponse;
         sResponse.Format(_T("UIDL"));
         
         EnqueueWrite_(sResponse);
         return;
      }

      QuitNow_();
      return;
   }

   void
   POP3ClientConnection::ParseUIDLResponse_(const String &sData)
   {
      if (!CommandIsSuccessfull_(sData))
      {
         QuitNow_();
         return;
      }

      std::vector<String> vecLines = StringParser::SplitString(sData, "\r\n");

      if (vecLines.size() < 3)
      {
         // Nothing but the status line and the terminator: an empty mailbox.
         StartMailboxCleanup_();
         return;
      }

      auto iter = vecLines.begin();

      // Move to first line containing a message ID.
      iter++;

      int rejectedLines = 0;
      int foldedUIDs = 0;

      while (iter != vecLines.end())
      {
         String sLine = (*iter);
         iter++;

         if (sLine == _T("."))
            break;

         if (sLine.IsEmpty())
            continue;

         int iMessageIdx = 0;
         String sMessageUID;

         if (!ParseUIDLLine_(sLine, iMessageIdx, sMessageUID))
         {
            rejectedLines++;
            continue;
         }

         String sStorableUID = FoldRemoteUID_(sMessageUID);

         if (sStorableUID != sMessageUID)
            foldedUIDs++;

         uidlresponse_[iMessageIdx] = sStorableUID;
      }

      // Reported at application level rather than debug: a listing this server cannot
      // make sense of means messages it will not collect, and the administrator cannot
      // see that from the outside.
      if (rejectedLines > 0)
      {
         String message =
            Formatter::Format("The remote POP3 server returned {0} unusable line(s) in the UIDL listing of external account {1}. Those messages have been ignored; the rest of the listing is being collected as normal.",
               rejectedLines, account_->GetName());

         LOG_APPLICATION(message);
      }

      if (foldedUIDs > 0)
      {
         String message =
            Formatter::Format("The remote POP3 server returned {0} unique-id(s) longer than 255 characters for external account {1}. RFC 1939 allows 70. They have been shortened for storage, which is what stops the affected messages from being downloaded again on every check.",
               foldedUIDs, account_->GetName());

         LOG_APPLICATION(message);
      }

      cur_message_ = uidlresponse_.begin();

      RequestNextMessage_();
   }

   bool
   POP3ClientConnection::ParseUIDLLine_(const String &line, int &messageIndex, String &messageUID)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Parses one line of a UIDL listing, "<message-number> <unique-id>" (RFC 1939).
   // Returns false for a line that is not of that shape.
   //
   // Rejecting rather than guessing matters because of what the guess used to be. Find
   // returned -1 for a line with no space, Mid(0, -1) range-checked its way to an empty
   // string, _ttoi turned that into 0, and the entire junk line was stored as the
   // unique-id of message number 0. uidlresponse_ is a std::map, which orders by key, so
   // that bogus entry became the FIRST message requested; "RETR 0" is not a message, the
   // server refused it, and a refused RETR used to abandon the whole session. One
   // unparseable line from the remote server therefore meant no mail was ever collected
   // from that account again - on this check and on every check after it.
   //---------------------------------------------------------------------------()
   {
      int spacePosition = line.Find(_T(" "));

      if (spacePosition <= 0)
         return false;

      String indexPart = line.Mid(0, spacePosition);
      String uidPart = line.Mid(spacePosition + 1);

      uidPart.TrimLeft(_T(" \t"));
      uidPart.TrimRight(_T(" \t"));

      if (uidPart.IsEmpty())
         return false;

      // The message number must be a plain positive integer. _ttoi silently answers 0
      // for anything else, and there is no message 0. More than nine digits is refused
      // rather than allowed to overflow into a valid-looking message number.
      const int indexLength = indexPart.GetLength();

      if (indexLength > 9)
         return false;

      for (int i = 0; i < indexLength; i++)
      {
         wchar_t c = indexPart.GetAt(i);

         if (c < '0' || c > '9')
            return false;
      }

      messageIndex = _ttoi(indexPart);

      if (messageIndex <= 0)
         return false;

      messageUID = uidPart;

      return true;
   }

   String
   POP3ClientConnection::FoldRemoteUID_(const String &uid)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Returns a form of the remote unique-id that fits the column it is stored in.
   //
   // hm_fetchaccounts_uids.uidvalue is 255 characters wide and RFC 1939 caps a
   // unique-id at 70, so this normally returns the id unchanged. A server that hands out
   // something longer could not be tracked at all: the insert is refused (or silently
   // truncated) by the database, the next session's Refresh finds no match for the id it
   // was given, and the message is downloaded and delivered AGAIN - not once, but every
   // single time the account is checked, for ever. That is the worst duplicate-delivery
   // shape in this subsystem and it is driven entirely by the remote server.
   //
   // The folded form keeps the first 200 characters and appends a hash of the whole id
   // plus its length, so two ids that share a long prefix still map to different keys.
   //---------------------------------------------------------------------------()
   {
      const int maxStorableLength = 255;
      const int length = uid.GetLength();

      if (length <= maxStorableLength)
         return uid;

      // FNV-1a, 32 bit. Chosen for being short and dependency-free; this is a
      // collision-avoidance aid for a malformed input, not a security decision.
      unsigned int hash = 2166136261u;

      for (int i = 0; i < length; i++)
      {
         hash ^= (unsigned int) (unsigned short) uid.GetAt(i);
         hash *= 16777619u;
      }

      String folded;
      folded.Format(_T("%s~%08x~%d"), uid.Mid(0, 200).c_str(), hash, length);

      return folded;
   }

   bool
   POP3ClientConnection::RequestNextMessage_()
   {
      while (cur_message_ != uidlresponse_.end())
      {
         String sCurrentUID = (*cur_message_).second;

         // Check if the current message is already in the list
         // of fetch UID's

         bool bMessageDownloaded = GetUIDList_()->IsUIDInList(sCurrentUID);

         // A unique-id is meant to identify exactly one message, but a server that
         // derives it from the message content gives the same id to two identical
         // messages. Now that the local record is written even for messages that are
         // about to be deleted (see MarkCurrentMessageAsRead_), the second copy would
         // look like one collected during an earlier session and would be silently
         // dropped - so an id appearing twice in the SAME listing is treated as what it
         // is: a second message.
         //
         // Deliberately limited to the delete-immediately case. When messages are left
         // on the server the same listing comes back every session, and downloading the
         // repeated entry each time would deliver an extra copy on every check, for
         // ever. There, collapsing the duplicate is the lesser of the two evils.
         const bool duplicateWithinThisListing =
            uids_seen_this_session_.find(sCurrentUID) != uids_seen_this_session_.end();

         if (bMessageDownloaded && duplicateWithinThisListing && GetDaysToKeep_(sCurrentUID) == -1)
            bMessageDownloaded = false;

         uids_seen_this_session_.insert(sCurrentUID);

         if (bMessageDownloaded)
         {
            // Mark this message as downloaded. This is so that we can
            // drop it later on when purging the mailbox. (We only purge
            // items we have downloaded). And since it was downloaded during
            // a previous session, we can safely drop it..
            int iID = (*cur_message_).first;
            downloaded_messages_[iID] = sCurrentUID;

            // The message has already been downloaded. Give scripts a chance
            // to override the default delete behavior.
            std::shared_ptr<Message> messageEmpty;
            FireOnExternalAccountDownload_(messageEmpty, sCurrentUID);
         }
         else
         {
            // Request message download now.

            current_message_ = std::shared_ptr<Message> (new Message);

            int iMessageIdx = (*cur_message_).first;

            String sResponse;
            sResponse.Format(_T("RETR %d"), iMessageIdx);

            EnqueueWrite_(sResponse);

            current_state_ = StateRETRSent;
            retr_failed_ = false;

            // From here until the finalization completes there is a message part-way
            // through arriving. If the session ends before that, the fragment on disk
            // has to be cleaned up - see DiscardUnfinishedDownload_.
            download_finalized_ = false;

            // Reset the transmission buffer. It will be
            // recreated when we receive binary the next time.

            transmission_buffer_.reset();

            SetReceiveBinary(true);
                          
            return true;
         }
      
         cur_message_++;

      }

      // We reached the end of the message list.
      if (cur_message_ == uidlresponse_.end())
      {
         StartMailboxCleanup_();
      }


      return false;
   }

   void
   POP3ClientConnection::StartMailboxCleanup_()
   {
      cur_message_ = downloaded_messages_.begin();
      SetReceiveBinary(false);

      MailboxCleanup_();
   }

   void
   POP3ClientConnection::MailboxCleanup_()
   {
      while (cur_message_ != downloaded_messages_.end())
      {
         bool bRet = MessageCleanup_();

         cur_message_++;

         if (bRet)
         {
            // MessageCleanup_ said something to the
            // remote server. We have to return here
            // to receive the response.

            return;
         }
      }

      DeleteUIDsNoLongerOnServer_();

      // Cleanup is complete. Time to quit.
      LOG_DEBUG("POP3 External Account: Normal QUIT.");
      QuitNow_();
     
   }

   void
   POP3ClientConnection::DeleteUIDsNoLongerOnServer_()
   {
      // Delete UID's from the database of those
      // messages that no longer exists on the
      // remote POP3 server. This happens if hMailServer
      // has downloaded a message and than the user has
      // deleted it from the POP3 server.
      std::shared_ptr<FetchAccountUIDList> uidList = GetUIDList_();

      // Build a vector with the UID's to keep. All UID's
      // not in this list should be deleted from the database.

      std::set<String> setUIDs;

      auto iter = uidlresponse_.begin();
      auto iterEnd = uidlresponse_.end();

      for (; iter != iterEnd; iter++)
         setUIDs.insert((*iter).second);

      uidList->DeleteUIDsNotInSet(setUIDs);
   }

   void
   POP3ClientConnection::QuitNow_()
   {
      String sResponse;
      sResponse.Format(_T("QUIT"));
   
      EnqueueWrite_(sResponse);

      SetReceiveBinary(false);
      current_state_ = StateQUITSent;
   }

   bool 
   POP3ClientConnection::ParseQuitResponse_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         // We have quitted successfully.
         EnqueueDisconnect();
      }

      // Quit anyway
      return false;
   }

   void
   POP3ClientConnection::ParseRETRResponse_(const String &sData)
   {
      if (CommandIsSuccessfull_(sData))
      {
         // Log that this message has been downloaded.
         int iID = (*cur_message_).first;
         String sCurrentUID = (*cur_message_).second;
         downloaded_messages_[iID] = sCurrentUID;

         return;
      }

      // The server listed this message in its UIDL response and has now refused to send
      // it. That happens for real reasons - another client deleted it between the two
      // commands, or the server cannot read it back - so only this message is given up
      // on. ParseData moves past it and the rest of the listing is still collected.
      //
      // This used to abandon the entire session and go straight to the mailbox cleanup.
      // Because the listing is walked in message-number order, one message that fails
      // permanently and happens to sit at the front of the mailbox stopped every message
      // behind it from being collected - not merely on this check, but on every check
      // after it, silently and for ever. Nothing was written to any log.
      retr_failed_ = true;

      // No message data follows a refused RETR, so there is no fragment to clean up.
      download_finalized_ = true;

      String response = sData;
      response.TrimRight(_T("\r\n"));

      String message =
         Formatter::Format("The remote POP3 server refused to send message {0} of external account {1}, so it has been skipped. The remaining messages are still being collected. Server response: {2}",
            (*cur_message_).first, account_->GetName(), response);

      LOG_APPLICATION(message);
   }


   void
   POP3ClientConnection::ParseDELEResponse_(const String &sData)
   {
      // The response used to be ignored entirely, which meant a refused deletion was
      // treated exactly like a successful one.
      if (!pending_delete_uid_.IsEmpty())
      {
         if (CommandIsSuccessfull_(sData))
         {
            // The message is gone from the remote server, so the local record of having
            // downloaded it has done its job and can go.
            GetUIDList_()->DeleteUID(pending_delete_uid_);
         }
         else
         {
            // The server refused to delete it, so the message is still there. The local
            // record therefore stays as well: it is the only thing that stops the next
            // fetch downloading and delivering the same message again.
            String response = sData;
            response.TrimRight(_T("\r\n"));

            String message =
               Formatter::Format("The remote POP3 server refused to delete a message downloaded from external account {0}. It has been left on the server and will not be downloaded again. Server response: {1}",
                  account_->GetName(), response);

            LOG_APPLICATION(message);
         }

         pending_delete_uid_.Empty();
      }

      // Clean up the next message.
      MailboxCleanup_();

      return;
   }


   bool
   POP3ClientConnection::CommandIsSuccessfull_(const String &sData)
   {
      if (sData.Mid(0,3).CompareNoCase(_T("+OK")) == 0)
         return true;
      else
         return false;
   }

   void
   POP3ClientConnection::OnConnectionTimeout()
   {  
      String sMessage = "QUIT\r\n";
      EnqueueWrite_(sMessage);
   }

   
   void
   POP3ClientConnection::OnExcessiveDataReceived()
   {  

   }


   void
   POP3ClientConnection::EnqueueWrite_(const String &sData) 
   {
      LogPOP3String_(sData, true);

      EnqueueWrite(sData + "\r\n");
   }

   bool 
   POP3ClientConnection::ParseFirstBinary_(std::shared_ptr<ByteBuffer> pBuf)
   {
      // Locate the first line
      const char *pText = pBuf->GetCharBuffer();
      const char *pEndOfLine = StringParser::Search(pText, pBuf->GetSize(), "\r\n");

      if (!pEndOfLine)
      {
         // Wait for more data
         return false;
      }

      // Skip passed the end of the line
      pEndOfLine += 2;

      size_t iLineLength = pEndOfLine - pText;

      // Copy the first line from the binary buffer.
      AnsiString sLine;
      sLine.append(pText, iLineLength);
      
      LogPOP3String_(sLine, false);
      
      ParseRETRResponse_(sLine);

      size_t iRemaining = pBuf->GetSize() - iLineLength;
      pBuf->Empty(iRemaining);

      return true;
   }

   void 
   POP3ClientConnection::PrependHeaders_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Adds headers to the beginning of the message.
   //---------------------------------------------------------------------------()
   {
      // Add a header with the name of the external account, so that
      // we can check where we downloaded it from later on.

      String sHeader;
      sHeader.Format(_T("X-hMailServer-ExternalAccount: %s\r\n"), account_->GetName().c_str());

      AnsiString sAnsiHeader = sHeader;

      transmission_buffer_->Append((BYTE*) sAnsiHeader.GetBuffer(), sAnsiHeader.GetLength());
   }

   void
   POP3ClientConnection::ParseData(std::shared_ptr<ByteBuffer> pBuf)
   {
      // 
      if (current_state_ == StateUIDLRequestSent)
      {
         command_buffer_.append(pBuf->GetCharBuffer(), pBuf->GetSize());

         pBuf->Empty();

         // A remote server that never terminates its UIDL listing would otherwise grow
         // this buffer without limit: the read below is re-armed for as long as the
         // terminating ".\r\n" has not arrived, there is no cap on how much may arrive,
         // and the machine on the other end is one the administrator does not control.
         // Eight megabytes is roughly a hundred thousand entries at the RFC 1939 maximum
         // unique-id length and several times that for typical ids, which is far more
         // than this fetcher could work through in a session anyway.
         if (command_buffer_.GetLength() > MaxUIDLResponseBytes)
         {
            String message =
               Formatter::Format("The UIDL listing returned by the remote POP3 server for external account {0} exceeded {1} bytes without ending. The fetch has been abandoned; no messages have been deleted from the server.",
                  account_->GetName(), (int) MaxUIDLResponseBytes);

            LOG_APPLICATION(message);

            command_buffer_.clear();
            QuitNow_();
            return;
         }

         if (command_buffer_.StartsWith("-ERR"))
         {
            // The server does not support UIDL. We can't fetch from this server.
            LogPOP3String_(command_buffer_, false);
            QuitNow_();
            return;
         }

         if (command_buffer_ == ".\r\n" || command_buffer_.EndsWith("\r\n.\r\n"))
         {
            ParseUIDLResponse_(command_buffer_);

            command_buffer_.clear();
         }

         EnqueueRead("");
         return;
      }

      // Append message buffer with the binary data we've received.
      if (!transmission_buffer_)
      {
         if (_firstRetrResponseBuffer == nullptr)
            _firstRetrResponseBuffer = std::make_shared<ByteBuffer>();

         _firstRetrResponseBuffer->Add(pBuf);

         if (!ParseFirstBinary_(_firstRetrResponseBuffer))
         {
            EnqueueRead("");
            return;
         }

         pBuf->Empty();
         pBuf->Add(_firstRetrResponseBuffer);
         _firstRetrResponseBuffer->Empty();

         if (retr_failed_)
         {
            // The remote server rejected the RETR, so no message data follows. Creating
            // a message file here would leave a file behind in the data directory which
            // no database row ever refers to.
            pBuf->Empty();

            // Step over the refused message and carry on with the rest of the listing.
            // RequestNextMessage_ clears retr_failed_ when it issues the next RETR, and
            // starts the mailbox cleanup when there is nothing left to ask for. Done here
            // rather than inside ParseRETRResponse_ because that runs before this check:
            // requesting the next message there would clear retr_failed_ too early and
            // this code would then treat the leftover bytes as the start of a message.
            if (cur_message_ != uidlresponse_.end())
               cur_message_++;

            RequestNextMessage_();

            EnqueueRead("");
            return;
         }

         String fileName = PersistentMessage::GetFileName(current_message_);

         // Create a binary buffer for this message. 
         transmission_buffer_ = std::shared_ptr<TransparentTransmissionBuffer>(new TransparentTransmissionBuffer(false));
         if (!transmission_buffer_->Initialize(fileName))
         {
            // We have probably failed to create the file...
            LOG_DEBUG("POP3 External Account: Error creating binary buffer or file.");
            QuitNow_();
            return;
         }

         PrependHeaders_();
      }

      transmission_buffer_->Append(pBuf->GetBuffer(), pBuf->GetSize());
      transmission_buffer_->Flush();

      // Clear the binary buffer.
      pBuf->Empty();

      if (!transmission_buffer_->GetTransmissionEnded())
      {
         EnqueueRead("");
         return;
      }

      // Since this may be a time-consuming task, do it asynchronously
      finalization_enqueued_tick_ = GetTickCount64();
      std::shared_ptr<AsynchronousTask<TCPConnection> > finalizationTask =
         std::shared_ptr<AsynchronousTask<TCPConnection> >(new AsynchronousTask<TCPConnection>
         (std::bind(&POP3ClientConnection::HandlePOP3FinalizationTaskCompleted_, this), shared_from_this()));

      // Checked rather than assumed: the async queue exists only while the servers
      // are running, and an external fetch that is mid-download when they stop
      // reaches here with nothing to post to. Unchecked, that was a null dereference.
      // Nothing is lost by giving up - the message has not been saved and DELE has
      // not been sent, so it is still on the remote server and the next fetch cycle
      // collects it.
      std::shared_ptr<WorkQueue> asyncQueue = Application::Instance()->GetAsyncWorkQueue();

      if (!asyncQueue)
      {
         LOG_APPLICATION(Formatter::Format("External account {0}: the message being downloaded was abandoned because the server is shutting down. It has been left on the remote server and will be fetched again.",
                                           account_ ? account_->GetName() : String(_T("<unknown>"))));

         EnqueueDisconnect();
         return;
      }

      asyncQueue->AddTask(finalizationTask);
   }

   void
   POP3ClientConnection::HandlePOP3FinalizationTaskCompleted_()
   {
      // The spam battery, the download event script and the save below all run on
      // the async work queue that inbound SMTP also uses to acknowledge received
      // mail, so a single slow stage here holds a thread that a sender is waiting
      // on. Time it in stages: a "start" line with no matching "done" line names
      // the stage that stalled, and the queue-wait line shows when the queue itself
      // rather than any one stage is the problem.
      const ULONGLONG queueWaitMs = finalization_enqueued_tick_ > 0
         ? GetTickCount64() - finalization_enqueued_tick_ : 0;

      if (queueWaitMs >= 5000)
      {
         String queueMessage;
         queueMessage.Format(_T("POP3ClientConnection - fetch: waited %I64u ms in the async queue before starting (session %d, external account %s). The async task queue may be saturated."),
            queueWaitMs, (int) GetSessionID(), account_->GetName().c_str());
         LOG_APPLICATION(queueMessage);
      }

      // The entire message has now been downloaded from the
      // remote POP3 server. Save it in the database and deliver
      // it to the account.
      String fileName = PersistentMessage::GetFileName(current_message_);
      current_message_->SetSize(FileUtilities::FileSize(fileName));

      // Read before the buffer is released below, which is also where the file handle
      // goes. The zero-byte test underneath already covered a download that saved
      // nothing at all; this covers the more dangerous shape, where some bytes did land
      // and then a write failed - a full disk part-way through. Without it the
      // truncated file passes the size test, gets saved, is delivered as though
      // complete, and the DELE that follows removes the only intact copy from the
      // remote server. Abandoning the fetch here sends no DELE, so the message is still
      // there to be fetched again once there is room for it.
      const bool spoolWriteFailed = transmission_buffer_ && transmission_buffer_->GetWriteFailed();

      if (current_message_->GetSize() == 0 || spoolWriteFailed)
      {
         // Error handling.
         if (spoolWriteFailed)
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5864, "POP3ClientConnection::HandlePOP3FinalizationTaskCompleted_",
               "A write to the spool file failed while downloading a message from an external POP3 account, so it has not been delivered and has been left on the remote server to be fetched again. The preceding HM5862 or HM5863 gives the underlying reason.");
         else
            LOG_DEBUG("POP3 External Account: Message is 0 bytes.");

         // The file was created before the transfer was abandoned, and the message
         // is never saved, so remove the file rather than leaving it behind in the
         // data directory with no database row referring to it. The buffer is
         // released first since it may still hold the file open.
         transmission_buffer_.reset();

         if (!FileUtilities::DeleteFile(fileName))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5703, "POP3ClientConnection::HandlePOP3FinalizationTaskCompleted_",
               "Could not delete the incomplete downloaded message file: " + fileName);
         }

         // Dealt with, so the teardown cleanup must not look for it a second time.
         download_finalized_ = true;

         QuitNow_();
         return;
      }

      LOG_DEBUG("POP3ClientConnection - fetch: start header-parsing.");
      ULONGLONG stageTick = GetTickCount64();
      ParseMessageHeaders_();
      LogFinalizationStage_("header-parsing", stageTick);

      LOG_DEBUG("POP3ClientConnection - fetch: start spam-protection.");
      stageTick = GetTickCount64();
      bool messageAccepted = DoSpamProtection_();
      LogFinalizationStage_("spam-protection", stageTick);

      if (messageAccepted)
      {
         // Deliberately no deadline here. A message downloaded over POP3 exists
         // only in this process: the RETR has already completed, so the remote
         // server has committed its download state and many providers (Gmail
         // among them) will not serve it again. Abandoning it to free a worker
         // would destroy the only copy, and by this point the expensive work has
         // already been paid for anyway. Slow fetches are reported by the stage
         // timings above rather than being cut short.
         LOG_DEBUG("POP3ClientConnection - fetch: start script/save.");
         stageTick = GetTickCount64();

         // should we scan this message for virus later on?
         current_message_->SetFlagVirusScan(account_->GetUseAntiVirus());

         FireOnExternalAccountDownload_(current_message_, (*cur_message_).second);

         // the message was not classified as spam which we should delete.
         if (!SaveMessage_())
         {
            // The save failed. SaveMessage_ has removed the file and said why; what
            // matters here is what is *not* done next - MarkCurrentMessageAsRead_ does
            // not run, so no UIDL row is written, and QuitNow_ ends the session without
            // a DELE. The message is still on the remote server and will be collected
            // on the next poll. Same handling as a truncated download above, for the
            // same reason: the remote copy is the only one left.
            download_finalized_ = true;

            QuitNow_();
            return;
         }

         // Notify the SMTP deliverer that there is a new message.
         Application::Instance()->SubmitPendingEmail();

         LogFinalizationStage_("script/save", stageTick);
      }

      MarkCurrentMessageAsRead_();

      // This message is completely dealt with: either delivered, or deliberately
      // discarded. Nothing is left on disk for the teardown cleanup to remove.
      download_finalized_ = true;

      // Switch to ASCII since we're going to request a new message.
      SetReceiveBinary(false);

      // Move on to the next message to download
      cur_message_++;

      RequestNextMessage_();

      EnqueueRead("");
   }

   void
   POP3ClientConnection::LogFinalizationStage_(const AnsiString &stage, ULONGLONG startTick)
   {
      const ULONGLONG elapsed = GetTickCount64() - startTick;

      String msg;
      msg.Format(_T("POP3ClientConnection - fetch: done %s in %I64u ms (session %d)."),
         String(stage).c_str(), elapsed, (int) GetSessionID());

      // A stage that runs long is occupying a thread that inbound SMTP needs, so
      // surface a slow one in the normal log rather than only under debug.
      if (elapsed >= 10000)
      {
         LOG_APPLICATION(msg);
      }
      else
      {
         LOG_DEBUG(msg);
      }
   }


   /*
      Run spam proteciton on this message. If it's classified as spam, we will either
      delete it, or we'll tag it as spam.
   */
   bool
   POP3ClientConnection::DoSpamProtection_()
   {
      if (!account_->GetUseAntiSpam())
      {
         // spam protection isn't enabled.
         return true;
      }

      String fileName = PersistentMessage::GetFileName(current_message_);

      IPAddress ipAddress;
      String hostName;

      String senderAddress = current_message_->GetFromAddress();
      MessageUtilities::RetrieveOriginatingAddress(current_message_, hostName, ipAddress);
      // The received header isn't safely parseable so we will always do anti-spam,


      if (SpamProtection::IsWhiteListed(senderAddress, ipAddress))
         return true;

      std::set<std::shared_ptr<SpamTestResult> > setSpamTestResults;
      
      // Run PreTransmissionTests. These consists of light tests such as DNSBL/SPF checks.
      std::set<std::shared_ptr<SpamTestResult> > setResult = 
           SpamProtection::Instance()->RunPreTransmissionTests(senderAddress, ipAddress, ipAddress, hostName);

      setSpamTestResults.insert(setResult.begin(), setResult.end());

      // Run PostTransmissionTests. These consists of more heavy stuff such as SURBL and SpamAssassin-
      setResult =
         SpamProtection::Instance()->RunPostTransmissionTests(senderAddress, ipAddress, ipAddress, current_message_);

      setSpamTestResults.insert(setResult.begin(), setResult.end());
      
      int iTotalSpamScore = SpamProtection::CalculateTotalSpamScore(setSpamTestResults);
      int iSpamDeleteThreshold = Configuration::Instance()->GetAntiSpamConfiguration().GetSpamDeleteThreshold();
      int iSpamMarkThreshold = Configuration::Instance()->GetAntiSpamConfiguration().GetSpamMarkThreshold();

      if (iSpamDeleteThreshold > 0 && iTotalSpamScore >= iSpamDeleteThreshold)
      {
         // Increase the spam-counter
         ServerStatus::Instance()->OnSpamMessageDetected();

         FileUtilities::DeleteFile(fileName);
         return false;
      }
      
      bool classifiedAsSpam = iSpamMarkThreshold > 0 && iTotalSpamScore >= iSpamMarkThreshold;

      if (classifiedAsSpam)
      {
         // Set message SPAM Flag
         current_message_->SetFlagSpam(classifiedAsSpam);

         std::shared_ptr<MessageData> messageData = SpamProtection::AddSpamScoreHeaders(current_message_, setSpamTestResults, classifiedAsSpam);
         
         // Increase the spam-counter
         ServerStatus::Instance()->OnSpamMessageDetected();

         if (messageData)
            messageData->Write(fileName);
      }

      return true;
   }

   void 
   POP3ClientConnection::ParseMessageHeaders_()
   {
      assert(current_message_);

      String fileName = PersistentMessage::GetFileName(current_message_);

      AnsiString sHeader = PersistentMessage::LoadHeader(fileName);
      std::shared_ptr<MimeHeader> pHeader = std::shared_ptr<MimeHeader>(new MimeHeader);
      pHeader->Load(sHeader, sHeader.GetLength());

      {
         // Parse out the sender of the message. 
         String sFrom = pHeader->GetRawFieldValue("From");

         if (!sFrom.IsEmpty())
         {
            AddresslistParser oAddressParser;

            String sFullName, sUser, sDomain;
            oAddressParser.ExtractParts(sFrom, sFullName, sUser, sDomain);

            if (!sUser.IsEmpty() && !sDomain.IsEmpty())
            {
               sFrom = sUser + "@" + sDomain;
               if (StringParser::IsValidEmailAddress(sFrom))
                  current_message_->SetFromAddress(sFrom);
            }
         }
      }      

      // bool bPreprocessRecipientList = true;
      CreateRecipentList_(pHeader);

      // Remove non-existent accounts.
      RemoveInvalidRecipients_();

      RetrieveReceivedDate_(pHeader);
   }

   bool
   POP3ClientConnection::SaveMessage_()
   {
      if (current_message_->GetRecipients()->GetCount() > 0)
      {
         current_message_->SetState(Message::Delivering);

         // Unchecked, and this is the one place in this class where that could
         // destroy mail rather than merely mislay it. On failure the message was not
         // stored, but MarkCurrentMessageAsRead_ ran anyway and the DELE that follows
         // removed it from the remote server - which for a provider that will not
         // serve the same message twice is the only copy. The file was left behind in
         // the data directory as well, one per occurrence, every time the account was
         // checked.
         //
         // Treated exactly like the truncated-download case above: delete the file,
         // report, and abandon the session without a DELE so the message is still on
         // the remote server to be fetched again on the next poll. That is the whole
         // reason this class already has that shape.
         if (!PersistentMessage::SaveObject(current_message_))
         {
            // Released first because it may still hold the file open.
            transmission_buffer_.reset();

            String fileName = PersistentMessage::GetFileName(current_message_);

            if (!FileUtilities::DeleteFile(fileName))
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5703, "POP3ClientConnection::SaveMessage_",
                  "Could not delete the message file for a download that could not be saved: " + fileName);
            }

            ErrorManager::Instance()->ReportError(ErrorManager::High, 6092, "POP3ClientConnection::SaveMessage_",
               "A message downloaded from an external POP3 account could not be saved, so it has not been delivered. It has been left on the remote server to be fetched again rather than deleted from it.");

            return false;
         }

         return true;
      }

      // Nothing in the message resolved to a local account, so it is never saved.
      // The downloaded file has already been written though, and without this it
      // would stay in the data directory forever with no database row referring to
      // it - one file per such message, every time the account is checked.
      String fileName = PersistentMessage::GetFileName(current_message_);

      if (!FileUtilities::DeleteFile(fileName))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5704, "POP3ClientConnection::SaveMessage_",
            "Could not delete the message file for a downloaded message with no local recipients: " + fileName);

         // True, not false: the message was deliberately discarded rather than lost,
         // and a file that could not be tidied up is not a reason to fetch it again.
         return true;
      }

      String sMessage;
      sMessage.Format(_T("POP3 External Account: A message downloaded from %s was discarded because none of its recipients belong to this server."),
         account_->GetName().c_str());
      LOG_APPLICATION(sMessage);

      return true;
   }

   void
   POP3ClientConnection::MarkCurrentMessageAsRead_()
   {
      if (cur_message_ == uidlresponse_.end())
         return;

      String sUID = (*cur_message_).second;

      // Recorded even when this message is about to be deleted from the remote server.
      //
      // That case used to be skipped as an optimisation - why write a row that is about
      // to be removed? - but the delivery and the DELE are two separate events with a
      // gap between them, and anything that interrupts the session in that gap left the
      // message sitting on the remote server with nothing recorded locally to say it had
      // already been delivered. The remote server dropping the connection during the
      // cleanup, a service restart, a DELE the server refuses: in every one of those the
      // next fetch downloaded and delivered the same message a second time. That is the
      // duplicate mail this subsystem is most likely to produce in the field, and it
      // needs one row per message to close.
      //
      // The row does not accumulate: it is removed as soon as the DELE is acknowledged
      // (ParseDELEResponse_), and any row whose message has since left the server is
      // pruned by DeleteUIDsNoLongerOnServer_ at the end of the session.
      //
      // Make sure that the UID exists in the database.
      // If it already exists, AddUID() will do nothing.
      GetUIDList_()->AddUID(sUID);
   }

   bool
   POP3ClientConnection::MessageCleanup_()
   {
      int iIndex = (*cur_message_).first;
      String sUID = (*cur_message_).second;

      int iDaysToKeep = GetDaysToKeep_(sUID);

      String sResponse;

      if (iDaysToKeep == 0)
      {
         // Never delete messages
         return false;
      }
      else if (iDaysToKeep > 0)
      {
         // Check wether we should delete this UID.
         std::shared_ptr<FetchAccountUID> pUID = GetUIDList_()->GetUID(sUID);

         // GetUID answers an empty pointer for an id it does not hold, and this used to
         // dereference it unconditionally - an access violation, so the whole service
         // dies, triggered by what the remote server put in its UIDL listing. It is
         // reachable: a server that hands the same unique-id to two message numbers gets
         // the row deleted while the first one is being cleaned up, and the second one
         // then finds nothing. With no creation date there is no way to judge the
         // retention window, so the safe answer is to leave the message on the server.
         if (!pUID)
         {
            String message =
               Formatter::Format("No download date is recorded for a message on external account {0}, so it has been left on the remote server rather than deleted. This happens when the server gives the same unique-id to more than one message.", account_->GetName());

            LOG_APPLICATION(message);
            return false;
         }

         // Get the creation date of the UID.
         DateTime dtCreation = pUID->GetCreationDate();
         DateTime dtNow = Time::GetDateFromSystemDate(Time::GetCurrentDateTime());

         DateTimeSpan dtSpan = dtNow - dtCreation;

         if (dtSpan.GetNumberOfDays() <= iDaysToKeep)
         {
            // No, we should not delete this UID.
            return false;
         }
      }

      // Delete the message.
      sResponse.Format(_T("DELE %d"), iIndex);
      EnqueueWrite_(sResponse);

      current_state_ = StateDELESent;

      // The local record is kept until the server confirms the deletion. Deleting it
      // here, as this used to, threw away the only evidence that the message had already
      // been delivered - so a DELE the server refused, or a connection that died before
      // the response arrived, left the message on the server and nothing to stop the next
      // fetch delivering it a second time.
      pending_delete_uid_ = sUID;

      return true;
   }

   void 
   POP3ClientConnection::CreateRecipentList_(std::shared_ptr<MimeHeader> pHeader)
   {
      if (account_->GetProcessMIMERecipients() && !account_->GetMIMERecipientHeaders().IsEmpty())
      {  
         ProcessMIMERecipients_(pHeader);
      }  

      if (current_message_->GetRecipients()->GetCount() > 0)
         return;
      
      // Just fetch the account
      if (receiving_account_address_.IsEmpty())
      {
         std::shared_ptr<const Account> pAccount = CacheContainer::Instance()->GetAccount(account_->GetAccountID());
         if (pAccount)
         {
            receiving_account_address_ = pAccount->GetAddress();
         }
      }

      // Add the recipient to the message
      bool recipientOK = false;
      RecipientParser recipientParser;
      recipientParser.CreateMessageRecipientList(receiving_account_address_, current_message_->GetRecipients(), recipientOK);
   }

   void
   POP3ClientConnection::ProcessMIMERecipients_(std::shared_ptr<MimeHeader> pHeader)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Goes through headers in the email and locates recipient. Adds these recipients
   // to the message
   //---------------------------------------------------------------------------()
   {
      /*
      String sTo = pHeader->GetRawFieldValue("To");
      String sCC = pHeader->GetRawFieldValue("CC");
      String sXRCPTTo = pHeader->GetRawFieldValue("X-RCPT-TO");
      String sXEnvelopeTo = pHeader->GetRawFieldValue("X-Envelope-To");

      String sAllRecipients = sTo + "," + sCC + "," + sXRCPTTo + "," + sXEnvelopeTo;
      */

      AnsiString sMimeRecipientHeaders = account_->GetMIMERecipientHeaders();
      std::vector<std::string> sMimeRecipientHeader;
      std::vector<std::string> sMimeRecipientsList;
      boost::split(sMimeRecipientHeader, sMimeRecipientHeaders, boost::is_any_of(";, "), boost::token_compress_on);
      for (std::vector<std::string>::iterator it = sMimeRecipientHeader.begin(); it != sMimeRecipientHeader.end(); ++it)
      {
         auto value = pHeader->GetRawFieldValue(*it);
         if (value)
         {
            sMimeRecipientsList.push_back(value);
         }
      }
      String sAllRecipients = boost::join(sMimeRecipientsList, ",");

      // Parse this list.
      AddresslistParser oListParser;
      std::vector<std::shared_ptr<Address> > vecAddresses = oListParser.ParseList(sAllRecipients);
      auto iterAddress = vecAddresses.begin();

      RecipientParser recipientParser;
      while (iterAddress != vecAddresses.end())
      {
         std::shared_ptr<Address> pAddress = (*iterAddress);

         if (pAddress->sMailboxName.IsEmpty() || pAddress->sDomainName.IsEmpty())
         {
            iterAddress++;
            continue;
         }

         String sAddress = pAddress->sMailboxName + "@" + pAddress->sDomainName;

         // Add the recipient to the message
         bool recipientOK = false;
         recipientParser.CreateMessageRecipientList(sAddress, current_message_->GetRecipients(), recipientOK);

         iterAddress++;
      }

      // Go through the Received headers
      ProcessReceivedHeaders_(pHeader);

      // Remove non-existent accounts.
      RemoveInvalidRecipients_();
}

   void 
   POP3ClientConnection::RetrieveReceivedDate_(std::shared_ptr<MimeHeader> pHeader)
   {
      if (!account_->GetProcessMIMEDate())
         return;

      String sReceivedHeader = pHeader->GetRawFieldValue("Received");

      DateTime dtTime = Utilities::GetDateTimeFromReceivedHeader(sReceivedHeader);

      if (dtTime.GetYear() < 1980 || dtTime.GetYear() > 2040)
         return;

      long minutes = Time::GetUTCRelationMinutes();
      DateTimeSpan dtSpan;
      dtSpan.SetDateTimeSpan(0, 0, minutes, 0);
      dtTime = dtTime + dtSpan;     

      String sDate = Time::GetTimeStampFromDateTime(dtTime);

      

      current_message_->SetCreateTime(sDate);
   }

   void 
   POP3ClientConnection::ProcessReceivedHeaders_(std::shared_ptr<MimeHeader> pHeader)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Goes throguh all the "Received" headers of the email. Tries to parse the
   // addresses and deliver the message to the recipients specified in them.
   //---------------------------------------------------------------------------()
   {
      AnsiString sHeaderName = "Received";
      std::vector<MimeField> &lstFields = pHeader->Fields();
      auto iter = lstFields.begin();
      auto iterEnd = lstFields.end();

      RecipientParser recipientParser;
      for (; iter != iterEnd; iter++)
      {
         MimeField& fd = *iter;

         if (sHeaderName.CompareNoCase(fd.GetName()) == 0)
         {
            String sValue = fd.GetValue();

            String sRecipient = Utilities::GetRecipientFromReceivedHeader(sValue);

            if (!sRecipient.IsEmpty())
            {
               bool recipientOK = false;
               recipientParser.CreateMessageRecipientList(sRecipient, current_message_->GetRecipients(), recipientOK);
            }

         }
      }
   }

   void
   POP3ClientConnection::LogPOP3String_(const String &sLogString, bool bSent)
   {
      String sTemp;

      if (bSent)
      {
         // Check if we should remove the password.
         if (current_state_ == StatePasswordSent)
         {
            // Remove password.
            sTemp = "SENT: ***";
         }
         else
         {
            sTemp = "SENT: " + sLogString;
         }
      }
      else
         sTemp = "RECEIVED: " + sLogString;

      sTemp.TrimRight(_T("\r\n"));

      LOG_POP3_CLIENT(GetSessionID(), GetIPAddressString(), sTemp);
   }
   
   void 
   POP3ClientConnection::OnCouldNotConnect(const AnsiString &sErrorDescription)
   {
      LOG_DEBUG("Connection to remote POP3-server failed. Error message: " + String(sErrorDescription) );
   }

   void 
   POP3ClientConnection::RemoveInvalidRecipients_()
   {
      if (account_->GetEnableRouteRecipients())
         current_message_->GetRecipients()->RemoveExternal();
      else
         current_message_->GetRecipients()->RemoveNonLocalAccounts();
   }

   int
   POP3ClientConnection::GetDaysToKeep_(const String &sUID)
   {
      int iDaysToKeep = account_->GetDaysToKeep();

      // Has an event overriden when messages should be deleted?
      if (event_results_.find(sUID) != event_results_.end())
      {
         std::shared_ptr<Result> result = event_results_[sUID];

         switch (result->GetValue())
         {
         case 1:
            // Delete messages immediately.
            iDaysToKeep = -1;
            break;
         case 2:
            // Delete messages after a specified number of days.
            iDaysToKeep = result->GetParameter();
            break;
         case 3:
            // Never delete messages.
            iDaysToKeep = 0;
            break;
         }
      }

      return iDaysToKeep;
   }

   /// Fires an event which lets the user override the default delete-behavior on the remote server.
   void
   POP3ClientConnection::FireOnExternalAccountDownload_(std::shared_ptr<Message> message, const String &uid)
   {
      std::shared_ptr<Result> pResult = Events::FireOnExternalAccountDownload(account_, message, uid);

      if (pResult)
         event_results_[uid] = pResult;
   }

   std::shared_ptr<FetchAccountUIDList>
   POP3ClientConnection::GetUIDList_()
   {
      if (!fetch_account_uidlist_)
      {
         fetch_account_uidlist_ = std::shared_ptr<FetchAccountUIDList>(new FetchAccountUIDList());
         fetch_account_uidlist_->Refresh(account_->GetID());
      }

      return fetch_account_uidlist_;
   }
   // This is temp function to log ETRN client commands to SMTP
   void
   POP3ClientConnection::LogSMTPString_(const String &sLogString, bool bSent)
   {
      String sTemp;

      if (bSent)
      {
         // Check if we should remove the password.
         if (current_state_ == StatePasswordSent)
         {
            // Remove password.
            sTemp = "SENT: ***";
         }
         else
         {
            sTemp = "SENT: " + sLogString;
         }
      }
      else
         sTemp = "RECEIVED: " + sLogString;

      sTemp.TrimRight(_T("\r\n"));

      LOG_SMTP_CLIENT(GetSessionID(), GetIPAddressString(), sTemp);
   }

   // This is temp function to log ETRN client commands to SMTP
   void
   POP3ClientConnection::EnqueueWrite_LogAsSMTP(const String &sData) 
   {
      LogSMTPString_(sData, true);

      EnqueueWrite(sData + "\r\n");
   }

}
