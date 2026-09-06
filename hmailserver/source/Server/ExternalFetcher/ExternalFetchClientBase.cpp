// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "ExternalFetchClientBase.h"
#include "FetchAccountUIDList.h"
#include "../Common/Scripting/Result.h"
#include "../Common/Scripting/Events.h"
#include "../Common/BO/FetchAccount.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/MessageData.h"
#include "../Common/BO/Account.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "../Common/Util/ByteBuffer.h"
#include "../Common/Util/OutboundOAuth2TokenClient.h"
#include "../Common/Application/IniFileSettings.h"
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
   ExternalFetchClientBase::ExternalFetchClientBase(std::shared_ptr<FetchAccount> pAccount,
                                                    ConnectionSecurity connectionSecurity,
                                                    boost::asio::io_context& io_context,
                                                    boost::asio::ssl::context& context,
                                                    std::shared_ptr<Event> disconnected,
                                                    AnsiString remote_hostname) :
      TCPConnection(connectionSecurity, io_context, context, disconnected, remote_hostname),
      account_(pAccount),
      download_finalized_(true)
   {
   }

   ExternalFetchClientBase::~ExternalFetchClientBase(void)
   {
   }

   void
   ExternalFetchClientBase::DiscardUnfinishedDownload_()
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

   String
   ExternalFetchClientBase::FoldRemoteUID_(const String &uid)
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

   void 
   ExternalFetchClientBase::PrependHeaders_()
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
   ExternalFetchClientBase::LogFinalizationStage_(const AnsiString &stage, ULONGLONG startTick)
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
   ExternalFetchClientBase::DoSpamProtection_()
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
   ExternalFetchClientBase::ParseMessageHeaders_()
   {
      HM_ASSERT(current_message_);

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
   ExternalFetchClientBase::SaveMessage_()
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
   ExternalFetchClientBase::MarkCurrentMessageAsRead_(const String &uid)
   {
      const String &sUID = uid;

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

   void 
   ExternalFetchClientBase::CreateRecipentList_(std::shared_ptr<MimeHeader> pHeader)
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
   ExternalFetchClientBase::ProcessMIMERecipients_(std::shared_ptr<MimeHeader> pHeader)
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
   ExternalFetchClientBase::RetrieveReceivedDate_(std::shared_ptr<MimeHeader> pHeader)
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
   ExternalFetchClientBase::ProcessReceivedHeaders_(std::shared_ptr<MimeHeader> pHeader)
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
   ExternalFetchClientBase::RemoveInvalidRecipients_()
   {
      if (account_->GetEnableRouteRecipients())
         current_message_->GetRecipients()->RemoveExternal();
      else
         current_message_->GetRecipients()->RemoveNonLocalAccounts();
   }

   int
   ExternalFetchClientBase::GetDaysToKeep_(const String &sUID)
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
   ExternalFetchClientBase::FireOnExternalAccountDownload_(std::shared_ptr<Message> message, const String &uid)
   {
      std::shared_ptr<Result> pResult = Events::FireOnExternalAccountDownload(account_, message, uid);

      if (pResult)
         event_results_[uid] = pResult;
   }

   std::shared_ptr<FetchAccountUIDList>
   ExternalFetchClientBase::GetUIDList_()
   {
      if (!fetch_account_uidlist_)
      {
         fetch_account_uidlist_ = std::shared_ptr<FetchAccountUIDList>(new FetchAccountUIDList());
         fetch_account_uidlist_->Refresh(account_->GetID());
      }

      return fetch_account_uidlist_;
   }
   // This is temp function to log ETRN client commands to SMTP
}
