// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandAppend.h"
#include "IMAPConnection.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/Domain.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/IMAPFolders.h"
#include "../Common/BO/Message.h"
#include "../Common/Cache/CacheContainer.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "../Common/Tracking/ChangeNotification.h"
#include "../Common/Tracking/NotificationServer.h"
#include "../Common/Util/ByteBuffer.h"
#include "../Common/Util/DiskSpace.h"
#include "../Common/Util/File.h"
#include "../Common/Util/Time.h"
#include "../SMTP/SMTPConfiguration.h"

#include "IMAPSimpleCommandParser.h"
#include "MessagesContainer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{

   IMAPCommandAppend::IMAPCommandAppend() :
      bytes_left_to_receive_(0)
   {
   }

   IMAPCommandAppend::~IMAPCommandAppend()
   {
      try
      {
         KillCurrentMessage_();
      }
      catch (...)
      {

      }
   }

   void 
   IMAPCommandAppend::KillCurrentMessage_()
   {
      if (!current_message_)
         return;

      if (FileUtilities::Exists(message_file_name_))
         FileUtilities::DeleteFile(message_file_name_);
   }

   IMAPResult
   IMAPCommandAppend::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");
      
      current_tag_ = pArgument->Tag();

      // Reset these two so we don't re-use old values.
      flags_to_set_ = "";
      create_time_to_set_ = "";

      // RFC 8508 (REPLACE): the same machinery as APPEND with a target message
      // parameter in front, and the target's removal on success. The UID form
      // arrives via the UID command handler, which sets replace_uid_mode_ for
      // exactly one command.
      bool replaceUsesUid = replace_uid_mode_;
      replace_uid_mode_ = false;
      replace_mode_ = false;
      replace_target_.reset();

      // RFC 6855 section 4 (UTF8=ACCEPT): a client may send the message as
      //
      //    APPEND mailbox [flags] [date] UTF8 (~{n}
      //
      // with the closing ")" following the octets on the wire. Thunderbird 128+
      // does exactly this for every Sent copy once UTF8=ACCEPT is enabled, and
      // the simple parser refused the line outright - its parenthesis count is
      // odd until the octets have gone by - so the server answered "BAD APPEND
      // Command requires at least 2 parameter" and no Sent copy was ever stored
      // (issue #53). The wrapper is stripped here, leaving the plain literal8
      // form the rest of this handler already understands, and the ")" is taken
      // from the continuation text once the octets have arrived.
      String sCommand = pArgument->Command();
      utf8_wrapped_literal_ = StripUtf8LiteralWrapper_(sCommand);
      if (utf8_wrapped_literal_)
         pArgument->Command(sCommand);

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      pParser->Parse(pArgument);

      if (pParser->WordCount() < 1)
         return IMAPResult(IMAPResult::ResultBad, "Command requires parameters.");

      bool isReplace = pParser->Word(0)->Value().CompareNoCase(_T("REPLACE")) == 0;

      // The folder name's position shifts by one when a target parameter
      // precedes it.
      size_t folderWordIndex = isReplace ? 2 : 1;
      size_t folderParamIndex = isReplace ? 1 : 0;

      if (pParser->WordCount() < folderWordIndex + 2)
         return IMAPResult(IMAPResult::ResultBad, isReplace
            ? "REPLACE Command requires at least 3 parameters."
            : "APPEND Command requires at least 2 parameter.");

      if (isReplace)
      {
         std::shared_ptr<IMAPFolder> pCurrentFolder = pConnection->GetCurrentFolder();
         if (!pCurrentFolder)
            return IMAPResult(IMAPResult::ResultBad, "REPLACE is only valid in the selected state.");

         if (pConnection->GetCurrentFolderReadOnly())
            return IMAPResult(IMAPResult::ResultNo, "REPLACE command on read-only folder.");

         // Removing the replaced message needs the same permission EXPUNGE does.
         if (!pConnection->CheckPermission(pCurrentFolder, ACLPermission::PermissionExpunge))
            return IMAPResult(IMAPResult::ResultBad, "ACL: Expunge permission denied (Required for REPLACE command).");

         String sTarget = pParser->Word(1)->Value();

         for (int i = 0; i < sTarget.GetLength(); i++)
         {
            wchar_t ch = sTarget.GetAt(i);
            if (ch < '0' || ch > '9')
               return IMAPResult(IMAPResult::ResultBad, "REPLACE target must be a message number.");
         }

         __int64 target = _ttoi64(sTarget.c_str());
         if (target <= 0)
            return IMAPResult(IMAPResult::ResultBad, "REPLACE target must be a message number.");

         auto currentMessages = MessagesContainer::Instance()->GetMessages(pCurrentFolder->GetAccountID(), pCurrentFolder->GetID());

         if (replaceUsesUid)
         {
            replace_target_ = currentMessages->GetItemByUID((unsigned int) target);
         }
         else
         {
            std::vector<std::shared_ptr<Message>> messageList = currentMessages->GetCopy();
            if (target <= (__int64) messageList.size())
               replace_target_ = messageList[(size_t) target - 1];
         }

         if (!replace_target_)
            return IMAPResult(IMAPResult::ResultNo, "No such message.");
      }

         // Create a new mailbox
      String sFolderName = pParser->GetParamValue(pArgument, (int) folderParamIndex);
      if (!pParser->Word(folderWordIndex)->Clammerized())
         IMAPFolder::UnescapeFolderString(sFolderName);

      if (pParser->ParantheziedWord())
         flags_to_set_ = pParser->ParantheziedWord()->Value();

      // last word.
      std::shared_ptr<IMAPSimpleWord> pWord = pParser->Word(pParser->WordCount()-1);

      if (!pWord)
         return IMAPResult(IMAPResult::ResultBad, "Missing literal");

      AnsiString literalSize;

      if (pWord->Clammerized())
      {
         literalSize = pWord->Value();
      }
      else
      {
         // RFC 3516 (BINARY): the literal8 form "~{n}" marks content that may
         // contain NUL octets. The simple parser reads it as a plain word, so
         // it is recognised here and treated exactly like a literal marker -
         // the binary receive path stores bytes as bytes either way.
         String sWordValue = pWord->Value();

         if (sWordValue.GetLength() > 3 &&
             sWordValue.GetAt(0) == '~' &&
             sWordValue.GetAt(1) == '{' &&
             sWordValue.Right(1) == _T("}"))
         {
            literalSize = sWordValue.Mid(2, sWordValue.GetLength() - 3);
         }
         else
         {
            return IMAPResult(IMAPResult::ResultBad, "Missing literal");
         }
      }

      // Strip a trailing '+' (non-synchronizing literal, RFC 7888) before
      // validating - and remember it: the client is not waiting for a
      // continuation before it sends the message octets.
      bool nonSynchronizingLiteral = false;
      if (literalSize.GetLength() > 0 && literalSize.GetAt(literalSize.GetLength() - 1) == '+')
      {
         literalSize = literalSize.Mid(0, literalSize.GetLength() - 1);
         nonSynchronizingLiteral = true;
      }

      // The octet count must be a plain non-negative integer; reject anything else
      // so atoi cannot overflow into a bogus (possibly enormous) size_t value.
      if (literalSize.IsEmpty())
         return IMAPResult(IMAPResult::ResultBad, "Invalid literal size.");

      for (int i = 0; i < literalSize.GetLength(); i++)
      {
         char ch = literalSize.GetAt(i);
         if (ch < '0' || ch > '9')
            return IMAPResult(IMAPResult::ResultBad, "Invalid literal size.");
      }

      __int64 declaredSize = _atoi64(literalSize);
      if (declaredSize <= 0)
         return IMAPResult(IMAPResult::ResultBad, "Empty message not permitted.");

      // Locate the parameter containing the date to set.
      // Can't use pParser->QuotedWord() since there may
      // be many quoted words in the command. Start past the folder name, which
      // may itself be quoted.
      for (size_t i = folderWordIndex + 1; i < pParser->WordCount(); i++)
      {
         std::shared_ptr<IMAPSimpleWord> pWord = pParser->Word(i);

         if (pWord->Quoted())
         {
            create_time_to_set_ = pWord->Value();

            // date-day-fixed  = (SP DIGIT) / 2DIGIT
            //   ; Fixed-format version of date-day
            // If the date given starts with <space>number, we need
            // to Trim. Doesn't hurt to always do this.
            create_time_to_set_.TrimLeft();
         }
      }

      destination_folder_ = pConnection->GetFolderByFullPath(sFolderName);
      if (!destination_folder_)
         return IMAPResult(IMAPResult::ResultNo, "[TRYCREATE] Folder could not be found.");

      if (!pConnection->CheckPermission(destination_folder_, ACLPermission::PermissionInsert))
         return IMAPResult(IMAPResult::ResultBad, "ACL: Insert permission denied (Required for APPEND command).");

      // A fresh command: forget everything a previous APPEND on this connection
      // left behind.
      pending_messages_.clear();
      command_failed_ = false;
      failure_response_.Empty();
      continuation_line_.Empty();
      receive_state_ = ReceivingLiteral;
      write_failed_ = false;
      append_buffer_.Empty();
      replace_mode_ = isReplace;

      IMAPResult prepareResult = ValidateAndPrepareMessage_(pConnection, declaredSize);
      if (prepareResult.GetResult() != IMAPResult::ResultOK)
         return prepareResult;

      pConnection->SetReceiveBinary(true);

      // The continuation is the synchronizing half of the protocol; a {n+}
      // literal's octets are already on their way (RFC 7888).
      if (!nonSynchronizingLiteral)
         pConnection->SendAsciiData("+ Ready for literal data\r\n");

      return IMAPResult();
   }

   IMAPResult
   IMAPCommandAppend::ValidateAndPrepareMessage_(std::shared_ptr<IMAPConnection> pConnection, __int64 declaredSize)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Everything one message of an APPEND must pass before its octets are
   // accepted - shared between the first message (parsed by ExecuteCommand) and
   // every later one in a MULTIAPPEND (parsed from a continuation line).
   //---------------------------------------------------------------------------
   {
      // Free-space precondition, before anything about this particular message is
      // considered: the disk is not a property of the mailbox, and a public folder
      // has no quota to fall foul of but lives on the same volume as everything
      // else.
      //
      // NO [UNAVAILABLE] (RFC 5530): "temporary failure because a subsystem is
      // down" is the closest thing IMAP has to the SMTP 452, and the distinction
      // matters just as much here. An APPEND is a client saving a draft or filing
      // a Sent copy it may hold nowhere else, so the refusal has to read as "not
      // now" - a client that treats this as permanent discards the message. The
      // text says so in words as well, because the response code is advisory and
      // most clients only show the text.
      //
      // Placed alongside the ceilings below rather than in ExecuteCommand so that
      // it also covers the second and later messages of a MULTIAPPEND, whose
      // literals are parsed from a continuation line and never pass through
      // ExecuteCommand at all.
      if (!DiskSpace::DataDirectoryHasRoomForMail())
         return IMAPResult(IMAPResult::ResultNo, "[UNAVAILABLE] Insufficient system storage - the message was not stored. Please try again later.");

      // Absolute ceiling independent of the configured maximum, so an "unlimited"
      // (0) max message size cannot translate into an unbounded APPEND. TOOBIG
      // (RFC 4469, required alongside the RFC 7889 APPENDLIMIT advertisement)
      // tells the client the literal itself was the problem, so it does not
      // retry the same message.
      const __int64 absoluteMaxMessageBytes = (__int64) 2 * 1024 * 1024 * 1024; // 2 GB
      if (declaredSize > absoluteMaxMessageBytes)
         return IMAPResult(IMAPResult::ResultNo, "[TOOBIG] Message size exceeds the maximum permitted size.");

      // The literal is exactly the message; the CRLF after it belongs to the
      // command line and is handled as continuation text.
      bytes_left_to_receive_ = (size_t) declaredSize;

      std::shared_ptr<const Domain> domain = CacheContainer::Instance()->GetDomain(pConnection->GetAccount()->GetDomainID());
      size_t maxMessageSizeKB = GetMaxMessageSize_(domain);

      if (maxMessageSizeKB > 0 &&
          bytes_left_to_receive_ / 1024 > maxMessageSizeKB)
      {
         String sMessage;
         sMessage.Format(_T("[TOOBIG] Message size exceeds fixed maximum message size. Size: %d KB, Max size: %d KB"),
            bytes_left_to_receive_ / 1024, maxMessageSizeKB);

         return IMAPResult(IMAPResult::ResultNo, sMessage);
      }

      if (!destination_folder_->IsPublicFolder())
      {
         // Make sure that this message fits in the mailbox - counting the
         // messages of this same command that are received but, because a
         // MULTIAPPEND is atomic, not yet saved and so not yet in the account's
         // size. Without this, N-1 messages of headroom could be overshot.
         size_t pendingBytes = 0;
         for (const PendingMessage &pending : pending_messages_)
            pendingBytes += (size_t) pending.message->GetSize();

         std::shared_ptr<const Account> pAccount = CacheContainer::Instance()->GetAccount(pConnection->GetAccount()->GetID());

         if (!pAccount)
            return IMAPResult(IMAPResult::ResultNo, "Account could not be fetched.");

         if (!pAccount->SpaceAvailable(bytes_left_to_receive_ + pendingBytes))
            return IMAPResult(IMAPResult::ResultNo, "[OVERQUOTA] Your quota has been exceeded.");
      }

      current_message_ = std::shared_ptr<Message>(new Message);
      current_message_->SetAccountID(destination_folder_->GetAccountID());
      current_message_->SetFolderID(destination_folder_->GetID());

      // Construct a file name which we'll write the message to.
      //
      // The owner of the DESTINATION folder, not the caller. The row above is
      // already stamped with destination_folder_->GetAccountID(), so using the
      // caller here wrote the bytes into one account's directory while the row
      // named another's - and every later FETCH, by the owner or by the appending
      // delegate, resolved the owner's directory, found nothing, and served the
      // "file does not exist" placeholder. The APPEND itself answered OK.
      std::shared_ptr<const Account> pMessageOwner;
      if (!destination_folder_->IsPublicFolder())
      {
         pMessageOwner = pConnection->GetAccountOwningFolder(destination_folder_);

         // No owner means no path we are willing to guess at.
         if (!pMessageOwner)
            return IMAPResult(IMAPResult::ResultNo, "The destination folder could not be resolved.");
      }

      message_file_name_ = PersistentMessage::GetFileName(pMessageOwner, current_message_);

      return IMAPResult();
   }

   void
   IMAPCommandAppend::ParseBinary(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<ByteBuffer> pBuf)
   {
      append_buffer_.Add(pBuf);

      // The connection stays in binary mode from the first literal to the final
      // CRLF of the command. Between literals the bytes are the rest of the
      // command line (RFC 3502 MULTIAPPEND: another flags/date/{size} group, or
      // nothing before the terminating CRLF), so one received buffer can span a
      // literal's end, the following line and the next literal's start - hence a
      // loop rather than one step per read.
      while (true)
      {
         if (receive_state_ == ReceivingLiteral)
         {
            if (append_buffer_.GetSize() < bytes_left_to_receive_)
            {
               TruncateBuffer_(pConnection);

               pConnection->EnqueueRead("");
               return;
            }

            // Write only the number of bytes still expected for this literal;
            // never spill trailing bytes - they are the continuation of the
            // command line - into the stored message.
            size_t writeLen = bytes_left_to_receive_;

            // A failed command still consumes its literals to keep the protocol
            // in step, but stores nothing.
            if (!command_failed_)
               WriteData_(pConnection, append_buffer_.GetBuffer(), writeLen);

            size_t remaining = append_buffer_.GetSize() - writeLen;
            append_buffer_.Empty(remaining);
            bytes_left_to_receive_ = 0;

            CompleteCurrentMessage_(pConnection);

            continuation_line_.Empty();
            receive_state_ = ReceivingContinuationLine;
            continue;
         }

         // ReceivingContinuationLine: accumulate up to the next CRLF. Everything
         // is appended to the line buffer first and the CRLF searched for there,
         // so a pair split across two reads is still found.
         continuation_line_.append((const char*) append_buffer_.GetBuffer(), append_buffer_.GetSize());
         append_buffer_.Empty();

         size_t lineEnd = continuation_line_.find("\r\n");

         if (lineEnd == std::string::npos)
         {
            // A between-literals line is a flags list, a date and a size - tiny.
            // Anything growing past this is not an APPEND continuation, and
            // buffering it forever would hand an authenticated client a memory
            // sink.
            const size_t maxContinuationLine = 4096;

            if (continuation_line_.size() > maxContinuationLine)
            {
               CleanupPendingMessages_();
               KillCurrentMessage_();

               pConnection->SetReceiveBinary(false);
               pConnection->SendAsciiData(current_tag_ + " BAD APPEND continuation line is too long.\r\n");
               pConnection->EnqueueRead();
               return;
            }

            pConnection->EnqueueRead("");
            return;
         }

         AnsiString line = continuation_line_.substr(0, lineEnd);
         AnsiString residue = continuation_line_.substr(lineEnd + 2);
         continuation_line_.Empty();

         // Bytes after the CRLF are the next literal's octets (or, after the
         // final CRLF, pipelined data the finalize path discards with the
         // buffer, exactly as the single-message APPEND always has).
         if (!residue.empty())
            append_buffer_.Add((const BYTE*) residue.c_str(), residue.size());

         ParseContinuationLine_(pConnection, line);

         if (receive_state_ == ReceivingLiteral && bytes_left_to_receive_ > 0)
            continue;

         // The command completed (or failed terminally): back to line mode.
         pConnection->EnqueueRead();
         return;
      }
   }
   
   bool
   IMAPCommandAppend::WriteData_(const std::shared_ptr<IMAPConnection>  pConn, const BYTE *pBuf, size_t WriteLen)
   {
      if (!current_message_)
         return false;

      String destinationPath = FileUtilities::GetFilePath(message_file_name_);
      if (!FileUtilities::Exists(destinationPath))
         FileUtilities::CreateDirectory(destinationPath);

      File oFile;

      try
      {
         // Both calls report failure by return value (a full or read-only volume,
         // a file locked by AV/backup); ignoring them meant APPEND answered OK
         // for a message that was never stored.
         if (!oFile.Open(message_file_name_, File::OTAppend))
         {
            write_failed_ = true;
            return false;
         }

         if (!oFile.Write(pBuf, WriteLen))
         {
            write_failed_ = true;
            return false;
         }
      }
      catch (...)
      {
         write_failed_ = true;
         return false;
      }

      return true;
   }

   bool
   IMAPCommandAppend::TruncateBuffer_(const std::shared_ptr<IMAPConnection> pConn)
   {
      if (append_buffer_.GetSize() >= 20000)
      {
         WriteData_(pConn, append_buffer_.GetBuffer(), append_buffer_.GetSize());
         bytes_left_to_receive_ -= append_buffer_.GetSize();
         append_buffer_.Empty();
      }

      return true;

   }

   void
   IMAPCommandAppend::CompleteCurrentMessage_(std::shared_ptr<IMAPConnection> pConnection)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // One message's octets have all arrived. Its flags and date are applied and
   // it joins the received-but-unsaved list; nothing touches the database until
   // the whole command has succeeded, because a multi-message APPEND is atomic
   // (RFC 3502) - all stored or none.
   //---------------------------------------------------------------------------
   {
      if (command_failed_ || !current_message_)
      {
         current_message_.reset();
         return;
      }

      // The stored file ends with CRLF, exactly as it always has: the
      // pre-MULTIAPPEND code consumed the command's terminating CRLF in binary
      // mode and wrote it into the file, and consumers of message files rely on
      // the terminator - header parsing of a headers-only message included,
      // which is how its absence was caught (the SORT-by-Date fixture).
      if (!write_failed_)
      {
         const BYTE crlfTerminator[2] = { '\r', '\n' };
         WriteData_(pConnection, crlfTerminator, 2);
      }

      if (write_failed_)
      {
         // The message data never reached disk. Fail the whole command:
         // reporting OK here silently lost Sent Items copies, drafts and
         // migration uploads while the client believed they were saved.
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5211, "IMAPCommandAppend::CompleteCurrentMessage_",
            "APPEND failed because the message file could not be written: " + message_file_name_);

         KillCurrentMessage_();
         write_failed_ = false;
         current_message_.reset();

         FailCommand_(current_tag_ + " NO [SERVERBUG] APPEND failed - the message could not be stored on the server.\r\n");
         return;
      }

      current_message_->SetSize(FileUtilities::FileSize(message_file_name_));
      current_message_->SetState(Message::Delivered);

      // Set message flags.
      bool bSeen = (flags_to_set_.FindNoCase(_T("\\Seen")) >= 0);
      bool bDeleted = (flags_to_set_.FindNoCase(_T("\\Deleted")) >= 0);
      bool bDraft = (flags_to_set_.FindNoCase(_T("\\Draft")) >= 0);
      bool bAnswered = (flags_to_set_.FindNoCase(_T("\\Answered")) >= 0);
      bool bFlagged = (flags_to_set_.FindNoCase(_T("\\Flagged")) >= 0);

      if (bSeen)
      {
         // ACL: If user tries to set the Seen flag, check that he has permission to do so.
         if (!pConnection->CheckPermission(destination_folder_, ACLPermission::PermissionWriteSeen))
         {
            // User does not have permission to set the Seen flag.
            bSeen = false;
         }
      }

      current_message_->SetFlagDeleted(bDeleted);
      current_message_->SetFlagSeen(bSeen);
      current_message_->SetFlagDraft(bDraft);
      current_message_->SetFlagAnswered(bAnswered);
      current_message_->SetFlagFlagged(bFlagged);

      // Set the create time
      if (!create_time_to_set_.IsEmpty())
      {
         // Convert to internal format
         create_time_to_set_ = Time::GetInternalDateFromIMAPInternalDate(create_time_to_set_);
         current_message_->SetCreateTime(create_time_to_set_);
      }

      PendingMessage pending;
      pending.message = current_message_;
      pending.fileName = message_file_name_;
      pending_messages_.push_back(pending);

      current_message_.reset();
   }

   void
   IMAPCommandAppend::ParseContinuationLine_(std::shared_ptr<IMAPConnection> pConnection, const AnsiString &line)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // The command-line text following a completed literal: empty means the
   // command's terminating CRLF, anything else must be another RFC 3502
   // "[flags] [date] {size}" group.
   //---------------------------------------------------------------------------
   {
      String continuation = line;
      continuation.Trim();

      // The message just received was sent as UTF8 (~{n}): its closing ")" is
      // the first thing on the line after the octets. Anything else means the
      // client and server disagree about where the message ended, and the only
      // safe answer is to stop rather than store bytes under the wrong framing.
      if (utf8_wrapped_literal_)
      {
         utf8_wrapped_literal_ = false;

         if (continuation.IsEmpty() || continuation.GetAt(0) != ')')
         {
            TerminateWithProtocolError_(pConnection, " BAD APPEND UTF8 literal is not closed.\r\n");
            return;
         }

         continuation = continuation.Mid(1);
         continuation.Trim();
      }

      if (continuation.IsEmpty())
      {
         FinalizeCommand_(pConnection);
         return;
      }

      // RFC 8508: REPLACE takes exactly one message - its grammar has no
      // MULTIAPPEND extension.
      if (replace_mode_)
      {
         TerminateWithProtocolError_(pConnection, " BAD REPLACE takes exactly one message.\r\n");
         return;
      }

      // Another message follows. Parse its optional flags list, optional quoted
      // date and mandatory literal size.
      flags_to_set_ = "";
      create_time_to_set_ = "";

      if (continuation.GetLength() > 0 && continuation.GetAt(0) == '(')
      {
         int flagsEnd = continuation.Find(_T(")"));
         if (flagsEnd < 0)
         {
            TerminateWithProtocolError_(pConnection, " BAD APPEND flag list is not closed.\r\n");
            return;
         }

         flags_to_set_ = continuation.Mid(1, flagsEnd - 1);
         continuation = continuation.Mid(flagsEnd + 1);
         continuation.Trim();
      }

      if (continuation.GetLength() > 0 && continuation.GetAt(0) == '"')
      {
         int dateEnd = continuation.Find(_T("\""), 1);
         if (dateEnd < 0)
         {
            TerminateWithProtocolError_(pConnection, " BAD APPEND date is not closed.\r\n");
            return;
         }

         create_time_to_set_ = continuation.Mid(1, dateEnd - 1);
         create_time_to_set_.TrimLeft();
         continuation = continuation.Mid(dateEnd + 1);
         continuation.Trim();
      }

      // RFC 6855: a later message of a MULTIAPPEND may be wrapped the same way
      // as the first - "UTF8 (~{n}" with the ")" after its octets.
      if (continuation.GetLength() >= 6 &&
          continuation.Mid(0, 4).CompareNoCase(_T("UTF8")) == 0 &&
          (continuation.GetAt(4) == ' ' || continuation.GetAt(4) == '('))
      {
         String wrapped = continuation.Mid(4);
         wrapped.TrimLeft();

         if (wrapped.GetLength() < 1 || wrapped.GetAt(0) != '(')
         {
            TerminateWithProtocolError_(pConnection, " BAD APPEND continuation must be a literal.\r\n");
            return;
         }

         continuation = wrapped.Mid(1);
         continuation.TrimLeft();
         utf8_wrapped_literal_ = true;
      }

      // RFC 3516: the literal8 form "~{n}" is accepted wherever a literal is.
      if (continuation.GetLength() > 0 && continuation.GetAt(0) == '~')
         continuation = continuation.Mid(1);

      if (continuation.GetLength() < 3 ||
          continuation.GetAt(0) != '{' ||
          continuation.Right(1) != _T("}"))
      {
         TerminateWithProtocolError_(pConnection, " BAD APPEND continuation must be a literal.\r\n");
         return;
      }

      String literalSize = continuation.Mid(1, continuation.GetLength() - 2);

      bool nonSynchronizingLiteral = false;
      if (literalSize.Right(1) == _T("+"))
      {
         literalSize = literalSize.Mid(0, literalSize.GetLength() - 1);
         nonSynchronizingLiteral = true;
      }

      if (literalSize.IsEmpty())
      {
         TerminateWithProtocolError_(pConnection, " BAD Invalid literal size.\r\n");
         return;
      }

      for (int i = 0; i < literalSize.GetLength(); i++)
      {
         wchar_t ch = literalSize.GetAt(i);
         if (ch < '0' || ch > '9')
         {
            TerminateWithProtocolError_(pConnection, " BAD Invalid literal size.\r\n");
            return;
         }
      }

      __int64 declaredSize = _ttoi64(literalSize.c_str());
      if (declaredSize <= 0)
      {
         TerminateWithProtocolError_(pConnection, " BAD Empty message not permitted.\r\n");
         return;
      }

      IMAPResult prepareResult = command_failed_
         ? IMAPResult()
         : ValidateAndPrepareMessage_(pConnection, declaredSize);

      if (prepareResult.GetResult() != IMAPResult::ResultOK)
      {
         if (!nonSynchronizingLiteral)
         {
            // The client is waiting for a continuation; the tagged refusal can
            // take its place and nothing more will be sent (RFC 3502 section 2:
            // refusing any message means storing none).
            CleanupPendingMessages_();
            KillCurrentMessage_();
            current_message_.reset();

            pConnection->SetReceiveBinary(false);

            String refusal = prepareResult.GetResult() == IMAPResult::ResultBad ? _T(" BAD ") : _T(" NO ");
            pConnection->SendAsciiData(current_tag_ + refusal + prepareResult.GetMessage() + _T("\r\n"));
            return;
         }

         // The octets are already on their way ({n+}); consume them to keep the
         // protocol in step, store nothing, and answer at the command's end.
         String refusal = prepareResult.GetResult() == IMAPResult::ResultBad ? _T(" BAD ") : _T(" NO ");
         FailCommand_(current_tag_ + refusal + prepareResult.GetMessage() + _T("\r\n"));

         bytes_left_to_receive_ = (size_t) declaredSize;
         current_message_.reset();
         receive_state_ = ReceivingLiteral;
         return;
      }

      if (command_failed_)
         bytes_left_to_receive_ = (size_t) declaredSize;

      receive_state_ = ReceivingLiteral;

      if (!nonSynchronizingLiteral)
         pConnection->SendAsciiData("+ Ready for literal data\r\n");
   }

   bool
   IMAPCommandAppend::StripUtf8LiteralWrapper_(String &command)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Recognises a command line ending in the RFC 6855 form
   //
   //    ... UTF8 (~{n}      or      ... UTF8 (~{n+}
   //
   // and rewrites it to end in the bare literal8 (~{n} / ~{n+}) instead, so the
   // simple command parser - which counts parentheses across the whole line -
   // accepts it. Returns true when the wrapper was present. Deliberately narrow:
   // the literal must be the last thing on the line, its size must be digits
   // (with the optional RFC 7888 "+"), and UTF8 must be a word of its own.
   //---------------------------------------------------------------------------
   {
      if (command.Right(1) != _T("}"))
         return false;

      int braceOpen = command.ReverseFind(_T("{"));
      if (braceOpen < 0)
         return false;

      // Only a size goes between the braces.
      String size = command.Mid(braceOpen + 1, command.GetLength() - braceOpen - 2);
      if (size.Right(1) == _T("+"))
         size = size.Mid(0, size.GetLength() - 1);

      if (size.IsEmpty())
         return false;

      for (int i = 0; i < size.GetLength(); i++)
      {
         wchar_t ch = size.GetAt(i);
         if (ch < '0' || ch > '9')
            return false;
      }

      int literalStart = braceOpen;
      if (literalStart > 0 && command.GetAt(literalStart - 1) == '~')
         literalStart--;

      if (literalStart < 1 || command.GetAt(literalStart - 1) != '(')
         return false;

      int keywordEnd = literalStart - 1;
      while (keywordEnd > 0 && command.GetAt(keywordEnd - 1) == ' ')
         keywordEnd--;

      const int keywordLength = 4;
      if (keywordEnd < keywordLength)
         return false;

      int keywordStart = keywordEnd - keywordLength;
      if (command.Mid(keywordStart, keywordLength).CompareNoCase(_T("UTF8")) != 0)
         return false;

      // "UTF8" has to be a separate word, not the tail of a folder name.
      if (keywordStart > 0 && command.GetAt(keywordStart - 1) != ' ')
         return false;

      command = command.Mid(0, keywordStart) + command.Mid(literalStart);
      return true;
   }

   void
   IMAPCommandAppend::TerminateWithProtocolError_(std::shared_ptr<IMAPConnection> pConnection, const String &responseAfterTag)
   {
      CleanupPendingMessages_();
      KillCurrentMessage_();
      current_message_.reset();
      append_buffer_.Empty();

      pConnection->SetReceiveBinary(false);
      pConnection->SendAsciiData(current_tag_ + responseAfterTag);
   }

   void
   IMAPCommandAppend::FailCommand_(const String &response)
   {
      // The first failure is the one reported; later ones are consequences.
      if (!command_failed_)
      {
         command_failed_ = true;
         failure_response_ = response;
      }
   }

   void
   IMAPCommandAppend::CleanupPendingMessages_()
   {
      // None of these have database rows yet, so removing the files is the
      // whole rollback.
      for (const PendingMessage &pending : pending_messages_)
      {
         if (FileUtilities::Exists(pending.fileName))
            FileUtilities::DeleteFile(pending.fileName);
      }

      pending_messages_.clear();
   }

   void
   IMAPCommandAppend::FinalizeCommand_(std::shared_ptr<IMAPConnection> pConnection)
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // The command's terminating CRLF has arrived. Save every received message,
   // or - if anything failed along the way - none of them (RFC 3502).
   //---------------------------------------------------------------------------
   {
      append_buffer_.Empty();
      pConnection->SetReceiveBinary(false);

      if (command_failed_)
      {
         CleanupPendingMessages_();
         pConnection->SendAsciiData(failure_response_);

         command_failed_ = false;
         failure_response_.Empty();
         destination_folder_.reset();
         return;
      }

      // The saves that make an APPEND real. A database failure part-way rolls
      // back the rows already created - the command promised atomicity.
      //
      // NO rather than BAD: the command was well formed, the server could not
      // do it. The files are removed so none is left on disk with no row
      // referring to it.
      std::vector<__int64> savedUids;
      std::vector<std::shared_ptr<Message> > savedMessages;

      for (const PendingMessage &pending : pending_messages_)
      {
         if (!PersistentMessage::SaveObject(pending.message))
         {
            for (std::shared_ptr<Message> savedMessage : savedMessages)
               PersistentMessage::DeleteObject(savedMessage);

            CleanupPendingMessages_();

            ErrorManager::Instance()->ReportError(ErrorManager::High, 6091, "IMAPCommandAppend::FinalizeCommand_",
               "An APPEND could not be saved and has been refused. The message files have been removed rather than left on disk with nothing referring to them.");

            pConnection->SendAsciiData(current_tag_ + " NO APPEND failed: the message could not be saved.\r\n");

            destination_folder_.reset();
            return;
         }

         savedMessages.push_back(pending.message);
         savedUids.push_back((__int64) pending.message->GetUID());
         pConnection->AddRecentMessage(pending.message->GetID());
      }

      MessagesContainer::Instance()->SetFolderNeedsRefresh(destination_folder_->GetID());

      String sResponse;
      if (pConnection->GetCurrentFolder() &&
          pConnection->GetCurrentFolder()->GetID() == destination_folder_->GetID())
      {
         std::shared_ptr<Messages> messages = destination_folder_->GetMessages();
         sResponse += IMAPNotificationClient::GenerateExistsString(messages->GetCount());
         sResponse += IMAPNotificationClient::GenerateRecentString((int) pConnection->GetRecentMessageCount());
      }

      // RFC 8508 (REPLACE): the replacement is safely stored, so the replaced
      // message leaves the selected mailbox now - reported before the tagged OK.
      if (replace_mode_ && replace_target_)
      {
         std::shared_ptr<IMAPFolder> selectedFolder = pConnection->GetCurrentFolder();
         if (selectedFolder)
         {
            __int64 targetId = replace_target_->GetID();
            __int64 targetUid = (__int64) replace_target_->GetUID();

            std::vector<__int64> expungedIndexes;
            std::function<bool(int, std::shared_ptr<Message>)> filter =
               [targetId, &expungedIndexes](int index, std::shared_ptr<Message> message)
            {
               if (message->GetID() == targetId)
               {
                  expungedIndexes.push_back(index);
                  return true;
               }

               return false;
            };

            auto selectedMessages = MessagesContainer::Instance()->GetMessages(selectedFolder->GetAccountID(), selectedFolder->GetID());
            selectedMessages->DeleteMessages(filter);

            if (!expungedIndexes.empty())
            {
               if (pConnection->GetQResyncEnabled())
               {
                  std::vector<__int64> vanished;
                  vanished.push_back(targetUid);

                  String sVanished;
                  sVanished.Format(_T("* VANISHED %s\r\n"), IMAPConnection::CompactUidSet(vanished).c_str());
                  sResponse += sVanished;
               }
               else
               {
                  String sExpunge;
                  sExpunge.Format(_T("* %d EXPUNGE\r\n"), (int) expungedIndexes[0]);
                  sResponse += sExpunge;
               }

               pConnection->RemoveRecentMessage(targetId);

               std::shared_ptr<ChangeNotification> pDeleteNotification =
                  std::shared_ptr<ChangeNotification>(new ChangeNotification(selectedFolder->GetAccountID(), selectedFolder->GetID(), ChangeNotification::NotificationMessageDeleted, expungedIndexes));
               Application::Instance()->GetNotificationServer()->SendNotification(pConnection->GetNotificationClient(), pDeleteNotification);
            }
         }

         replace_target_.reset();
         replace_mode_ = false;
      }

      // RFC 4315 (UIDPLUS): report the destination folder's UIDVALIDITY and the
      // UIDs assigned, so the client can reference the messages without a
      // search. With several messages (RFC 3502) the response carries the
      // uid-set, in the order the messages were appended.
      String sAppendUid;
      sAppendUid.Format(_T("[APPENDUID %d %s] "),
         destination_folder_->GetCreationTime().ToInt(),
         IMAPConnection::CompactUidSet(savedUids).c_str());

      // Send the OK response to the client.
      sResponse += current_tag_ + " OK " + sAppendUid + "APPEND completed\r\n";
      pConnection->SendAsciiData(sResponse);

      // Notify the mailbox notifier that the mailbox contents have changed.
      std::shared_ptr<ChangeNotification> pNotification =
         std::shared_ptr<ChangeNotification>(new ChangeNotification(destination_folder_->GetAccountID(), destination_folder_->GetID(), ChangeNotification::NotificationMessageAdded));
      Application::Instance()->GetNotificationServer()->SendNotification(pConnection->GetNotificationClient(), pNotification);

      pending_messages_.clear();
      destination_folder_.reset();
   }

   int
   IMAPCommandAppend::GetMaxMessageSize_(std::shared_ptr<const Domain> pDomain)
   {
      int iMaxMessageSizeKB = Configuration::Instance()->GetSMTPConfiguration()->GetMaxMessageSize();

      if (pDomain)
      {
         int iDomainMaxSizeKB = pDomain->GetMaxMessageSize();
         if (iDomainMaxSizeKB > 0)
         {
            if (iMaxMessageSizeKB == 0 || iMaxMessageSizeKB > iDomainMaxSizeKB)
               iMaxMessageSizeKB = iDomainMaxSizeKB;
         }
      }

      return iMaxMessageSizeKB;
   }

   // static
   __int64
   IMAPCommandAppend::GetEffectiveAppendLimitBytes(std::shared_ptr<const Account> pAccount)
   {
      // The same merge ExecuteCommand enforces: the global maximum, tightened by
      // the account's domain, capped by the 2 GB absolute ceiling. Advertising a
      // number the enforcement would not honour is worse than advertising none.
      const __int64 absoluteMaxMessageBytes = (__int64) 2 * 1024 * 1024 * 1024; // 2 GB

      int maxMessageSizeKB = Configuration::Instance()->GetSMTPConfiguration()->GetMaxMessageSize();

      if (pAccount)
      {
         std::shared_ptr<const Domain> domain = CacheContainer::Instance()->GetDomain(pAccount->GetDomainID());
         if (domain)
         {
            int domainMaxSizeKB = domain->GetMaxMessageSize();
            if (domainMaxSizeKB > 0 &&
                (maxMessageSizeKB == 0 || maxMessageSizeKB > domainMaxSizeKB))
               maxMessageSizeKB = domainMaxSizeKB;
         }
      }

      __int64 limitBytes = (__int64) maxMessageSizeKB * 1024;

      if (limitBytes <= 0 || limitBytes > absoluteMaxMessageBytes)
         limitBytes = absoluteMaxMessageBytes;

      return limitBytes;
   }

}
