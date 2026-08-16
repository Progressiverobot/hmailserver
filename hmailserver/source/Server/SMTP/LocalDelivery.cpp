// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "./LocalDelivery.h"

#include "../common/Application/ObjectCache.h"

#include "../common/BO/Account.h"
#include "../common/BO/Message.h"
#include "../common/BO/MessageRecipient.h"
#include "../common/BO/MessageRecipients.h"

#include "../common/Cache/CacheContainer.h"
#include "../common/Cache/AccountSizeCache.h"

#include "../common/Persistence/PersistentMessageRecipient.h"
#include "../common/Persistence/PersistentMessage.h"
#include "../common/Persistence/PersistentAccount.h"

#include "../common/Tracking/ChangeNotification.h"
#include "../common/Tracking/NotificationServer.h"

#include "../Common/Util/AWstats.h"
#include "../common/Util/TraceHeaderWriter.h"
#include "../common/Util/MessageUtilities.h"
#include "../common/Util/FileUtilities.h"
#include "../common/Util/Parsing/StringParser.h"
#include "../common/Sieve/SieveStorage.h"
#include "../common/Sieve/SieveScript.h"
#include "../common/Sieve/SieveVacationResponder.h"
#include "../common/Sieve/SieveDuplicateTracker.h"

#include "../IMAP/MessagesContainer.h"

#include "SMTPConfiguration.h"
#include "SMTPVacationMessageCreator.h"
#include "SMTPForwarding.h"
#include "RuleApplier.h"
#include "RuleResult.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{ 
   LocalDelivery::LocalDelivery(const String &sSendersIP, std::shared_ptr<Message> message, const RuleResult &globalRuleResult) :
      _sendersIP(sSendersIP),
      original_message_(message),
      _globalRuleResult(globalRuleResult)
   {
   }

   LocalDelivery::~LocalDelivery(void)
   {

   }

   /// Delivers the message to local recipients (recipients that exists in this installation)
   /// Returns true if the message has been re-used by us. If it has, the deliverer outside
   /// should not delete the message from the database when we're done.
   bool
   LocalDelivery::Perform(std::vector<String> &saErrorMessages)
   {
      LOG_DEBUG("Performing local delivery");

      bool messageReused = false;

      // NOTE: Since were manipulating the messages recipient vector below, we want to do a copy.
      //       We should iterate over the copy of recipients here. Not over the original list.
      std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients = original_message_->GetRecipients()->GetVector();
      auto iterRecipient = vecRecipients.begin();

      while (iterRecipient != vecRecipients.end())
      {
         std::shared_ptr<MessageRecipient> pRecipient = (*iterRecipient);

         if (pRecipient->GetLocalAccountID() == 0)
         {
            // this is an external recipient.
            iterRecipient++;
            continue;
         }

         // Read the recipients account from database.
         std::shared_ptr<const Account> pCheckAccount = CacheContainer::Instance()->GetAccount(pRecipient->GetLocalAccountID());

         if (pCheckAccount)
         {
            // DSN (RFC 3461): does this recipient want a failure notification?
            int notify = pRecipient->GetDSNNotify();
            bool suppressFailureDsn = (notify != MessageRecipient::DSNNotifyDefault) &&
                                      ((notify & MessageRecipient::DSNNotifyFailure) == 0);

            // Local recipient has been found. Deliver to it.
            DeliverToLocalAccount_(pCheckAccount, vecRecipients.size(), saErrorMessages, pRecipient->GetOriginalAddress(), messageReused, suppressFailureDsn);
         }
         else
         {
            // Local recipient but we could not find it. It has probably been deleted 
            // after we accepted the message.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5165, "LocalDelivery::DeliverToLocalAccount_s", "The recipient account appears to have been deleted after the message was received. Aborting delivery.");
         }

         // Delete this recipient from the database and memory.
         //
         // Unchecked, while the erase below happened either way - so the row could
         // outlive the collection that knows about it. On its own that is an orphan
         // row; combined with a queue delete that also fails it is the message coming
         // back with this recipient still attached and being delivered to them twice.
         if (!PersistentMessageRecipient::DeleteObject(pRecipient))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6106, "LocalDelivery::Perform",
               Formatter::Format("Message {0} was delivered locally to {1} but that recipient could not be removed from the database.",
                  original_message_->GetID(), pRecipient->GetAddress()));
         }

         iterRecipient = vecRecipients.erase(iterRecipient);
      }

      LOG_DEBUG("Local delivery completed");

      return messageReused;
   }


   /// Delivers a single message to a specific account.
   /// Returns true if the delivery was made, false otherwise.
   void
   LocalDelivery::DeliverToLocalAccount_(std::shared_ptr<const Account> account, size_t iNoOfRecipients, std::vector<String> &saErrorMessages, const String &sOriginalAddress, bool &messageReused, bool suppressFailureDsn)
   {
      // First check that we're actually able to deliver a message to this account. If the account
      // has reached it's quota, we should cancel delivery immediately. If we create the account-level
      // message below, there's no turning back.
      if (!CheckAccountQuotas_(account, saErrorMessages, suppressFailureDsn))
      {
         return;
      }

      // We should reuse the message file if only one recipient
      // exists. Reusing message file is good for performance since
      // we don't have to create a new file on disk.
      messageReused = iNoOfRecipients == 1;

      std::shared_ptr<Message> accountLevelMessage = CreateAccountLevelMessage_(original_message_, account, messageReused, sOriginalAddress);
      if (!accountLevelMessage)
      {
         String errorMessage;
         errorMessage.Format(_T("Unable to create account-level message of %I64d for account %s."), original_message_->GetID(), String(account->GetAddress()).c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5209, "SMTPDeliverer::DeliverToLocalAccount_", errorMessage);
         return;
      }

      if (!LocalDeliveryPreProcess_(account, accountLevelMessage, sOriginalAddress, saErrorMessages))
      {
         FileUtilities::DeleteFile(PersistentMessage::GetFileName(account, accountLevelMessage));

         if (messageReused)
         {
            accountLevelMessage->SetAccountID(0);
            messageReused = false;
         }

         return;
      }

      // We don't want to set the message state to Delivered until just before we save it. If we set
      // it earlier, rules and scripts may assume that the message has been delivered and make
      // changes to it and save those to the database. For instance, if there's an account-level rule executing and this rule
      // executes a script which saves the message, only the file changes should be saved. We should
      // not add any rows to the database in this scenario. To ensure this, InterfaceMessage checks the
      // message state. If it's "Delivering", it won't save the changes to the database.
      accountLevelMessage->SetState(Message::Delivered); 
      // Recalculate filesize after Return-Path and optionally Delivered-To header(s) are added
      accountLevelMessage->SetSize(FileUtilities::FileSize(PersistentMessage::GetFileName(account, accountLevelMessage)));

      // This is the save that makes the delivery real, and its result was discarded.
      //
      // What followed on failure: the folder was marked as refreshed, a
      // NotificationMessageAdded went out for a message that was not added, AWStats
      // recorded a successful delivery, and DeliverMessage counted the message
      // delivered and then deleted the queue row - because saErrorMessages was empty
      // and the message had not been rescheduled. The file stayed on disk with
      // nothing pointing at it. So a database failure at this one line lost the
      // message outright, told the recipient nothing, told the sender nothing, and
      // wrote "delivered" into the statistics.
      //
      // Handled the same way as the pre-process failure above - delete the file we
      // wrote, give the reused row back, and stop before anything claims the
      // delivery happened - with two additions: this one is a server fault rather
      // than a recipient-side one, so it is reported; and the sender is told, subject
      // to the same NOTIFY opt-out the quota path honours.
      //
      // A bounce is not the ideal answer to what may be a transient database outage -
      // holding the message for a later attempt would be - but local delivery has no
      // reschedule path the way ExternalDelivery does, and telling the sender it
      // failed is strictly better than the message evaporating. The reschedule is
      // worth building and is a larger change than this one.
      if (!PersistentMessage::SaveObject(accountLevelMessage))
      {
         FileUtilities::DeleteFile(PersistentMessage::GetFileName(account, accountLevelMessage));

         if (messageReused)
         {
            accountLevelMessage->SetAccountID(0);
            messageReused = false;
         }

         String errorMessage;
         errorMessage.Format(_T("Message %I64d could not be saved during delivery to %s, so it has not been delivered. The message file has been removed rather than left on disk with nothing referring to it."),
            original_message_->GetID(), String(account->GetAddress()).c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 6081, "LocalDelivery::DeliverToLocalAccount_", errorMessage);

         if (!suppressFailureDsn)
         {
            saErrorMessages.push_back(Formatter::Format("{0}\r\n   Error Type: SMTP\r\n   Error Description: Delivery failed\r\n   Additional information: The message could not be saved to the recipient's mailbox. The server administrator should check the hMailServer error log.\r\n\r\n",
               account->GetAddress()));
         }

         return;
      }

      // Tell the folder container that the users inbox is updated this will
      // cause a refresh in the imap server whenever a new imap command is sent.
      MessagesContainer::Instance()->SetFolderNeedsRefresh(accountLevelMessage->GetFolderID());

      // Notify the mailbox notifier that the mailbox contents have changed.
      std::shared_ptr<ChangeNotification> changeNotification = 
         std::shared_ptr<ChangeNotification>(new ChangeNotification(accountLevelMessage->GetAccountID(), accountLevelMessage->GetFolderID(), ChangeNotification::NotificationMessageAdded));
      Application::Instance()->GetNotificationServer()->SendNotification(changeNotification);

      AWStats::LogDeliverySuccess(_sendersIP, "127.0.0.1", accountLevelMessage, account->GetAddress());
   }

   /*
   Performs preprocessing for local delivery. 
   Rules, forwarding and quota is taken care of now.

   Returns true if message should be delivered, false if it should be aborted.
   */
   bool 
   LocalDelivery::LocalDeliveryPreProcess_(std::shared_ptr<const Account> account, std::shared_ptr<Message> accountLevelMessage, const String &sOriginalAddress, std::vector<String> &saErrorMessages)
   {
      SendAutoReplyMessage_(account, original_message_);

      // We must run account level rules after the message file has been
      // moved to the destination folder, so that any changes it has only
      // affects this instance.
      RuleResult accountRuleResult;
      if (!RunAccountRules_(account, accountLevelMessage, accountRuleResult))
         return false;

      SMTPForwarding forwarder;

      if (!forwarder.PerformForwarding(account, accountLevelMessage))
      {
         String sMessage = Formatter::Format("SMTPDeliverer - Message {0}: The message was not delivered to {1} because a forward was set up for the account.",
                                                original_message_->GetID(), account->GetAddress());
         LOG_APPLICATION(sMessage);

         return false;
      }

      // Do the final delivery of the message.
      AddTraceHeaders_(account, accountLevelMessage, sOriginalAddress);

      // Evaluate the recipient account's Sieve script (if any). It may fire
      // redirect actions, choose a fileinto folder, and/or cancel the local copy
      // (a discard, or a redirect with no keep).
      String sieveFolder;
      bool sieveDrop = false;
      bool sieveFlagsGiven = false;
      std::vector<String> sieveFlags;
      EvaluateSieveScript_(account, accountLevelMessage, sOriginalAddress, sieveFolder, sieveDrop,
                           sieveFlagsGiven, sieveFlags);

      // Applied here, before the caller's PersistentMessage::SaveObject, which is the
      // only reason a flag set by a script survives at all.
      //
      // Until 15 August 2026 this did not happen: the evaluator filled SieveResult's
      // flags, ManageSieve accepted `require "imap4flags"`, setflag/addflag/removeflag
      // parsed and ran and reported success in the action summary - and delivery read
      // `redirects`, `vacation`, `fileInto` and `keepLocal` and never looked at
      // `flags`. A script marking its own automated mail \Seen was accepted, ran, and
      // changed nothing, which is the worst shape a feature can have: not missing, so
      // nobody adds it, and not working, so nobody can rely on it.
      //
      // Placed before the sieveDrop return on purpose. A discarded message is not
      // stored, so there is nothing to flag - and doing this first would write flags
      // onto an object about to be thrown away.
      if (sieveDrop)
      {
         String sMessage = Formatter::Format("SMTPDeliverer - Message {0}: not kept locally by the Sieve script for {1}.",
                                                original_message_->GetID(), account->GetAddress());
         LOG_APPLICATION(sMessage);
         return false;
      }

      if (sieveFlagsGiven)
         ApplySieveFlags_(account, accountLevelMessage, sieveFlags);

      //
      // Move to IMAP folder. This must be done after we've executed account level rules
      // since the account level rules may override the folders.
      //
      __int64 iDestinationFolderID = accountLevelMessage->GetFolderID();
      __int64 iDestinationAccountID = account->GetID();

      bool bSetByGlobalRule = true;
      String sIMAPFolder = _globalRuleResult.GetMoveToFolder();
      if (!accountRuleResult.GetMoveToFolder().IsEmpty())
      {
         sIMAPFolder = accountRuleResult.GetMoveToFolder();
         bSetByGlobalRule = false;
      }

      // A Sieve fileinto takes precedence over the rule-selected folder.
      if (!sieveFolder.IsEmpty())
      {
         sIMAPFolder = sieveFolder;
         bSetByGlobalRule = false;
      }

      if (!sIMAPFolder.IsEmpty())
         MessageUtilities::MoveToIMAPFolder(accountLevelMessage, account->GetID(), sIMAPFolder, false, bSetByGlobalRule, iDestinationAccountID, iDestinationFolderID);

      if (iDestinationAccountID == 0)
      {
         // This message should be moved to the #public folder structure.
         const String currentPath = PersistentMessage::GetFileName(account, accountLevelMessage);
         PersistentMessage::MoveFileToPublicFolder(currentPath, accountLevelMessage);

         // Since the owner of the message has changed, we'll have to update
         // the owner on the message. Makes sense, right?
         accountLevelMessage->SetAccountID(0);
      }

      return true;
   }

   void
   LocalDelivery::ApplySieveFlags_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message,
                                   const std::vector<String> &sieveFlags)
   {
      // The five the store can hold. They are exactly the five SELECT advertises in
      // PERMANENTFLAGS, and exactly the five SieveParser::SplitFlagList canonicalises,
      // so an exact comparison is right here and a case-insensitive one would only
      // hide a parser change.
      //
      // \Recent is deliberately absent. It is the server's to set - it means "arrived
      // since this mailbox was last selected", not anything a filter decided - and
      // clearing it here because a script's flag list did not mention it would make
      // every filtered message arrive looking already-seen-by-a-client. The same goes
      // for the internal VirusScan and Spam bits, which share the bitmask and are not
      // IMAP flags at all.
      const bool seen = ListContains_(sieveFlags, _T("\\Seen"));
      const bool answered = ListContains_(sieveFlags, _T("\\Answered"));
      const bool flagged = ListContains_(sieveFlags, _T("\\Flagged"));
      const bool deleted = ListContains_(sieveFlags, _T("\\Deleted"));
      const bool draft = ListContains_(sieveFlags, _T("\\Draft"));

      // Set from the whole list rather than only setting the ones present, because
      // RFC 5232's flag variable holds the FINAL set: "removeflag" leaving a flag out
      // is as much an instruction as "addflag" putting one in. A newly delivered
      // message has none of these set anyway, so in practice only the true cases do
      // anything - but writing it this way is what makes a future :flags on a message
      // that already carries flags behave the way the RFC says.
      message->SetFlagSeen(seen);
      message->SetFlagAnswered(answered);
      message->SetFlagFlagged(flagged);
      message->SetFlagDeleted(deleted);
      message->SetFlagDraft(draft);

      // Anything else is a keyword, and this server cannot store one: messageflags is
      // a fixed 8-bit bitmask and SELECT advertises PERMANENTFLAGS without \*, so
      // there is nowhere to put it and no way for a client to see it.
      //
      // Said out loud rather than dropped. Silently discarding half of what a script
      // asked for is precisely the failure this whole change exists to end, and an
      // administrator whose "filed and tagged" rule only files needs to be told which
      // half worked. One line per delivery that uses keywords, naming them.
      std::vector<String> unsupported;

      for (const String &flag : sieveFlags)
      {
         if (flag.Compare(_T("\\Seen")) == 0 || flag.Compare(_T("\\Answered")) == 0 ||
             flag.Compare(_T("\\Flagged")) == 0 || flag.Compare(_T("\\Deleted")) == 0 ||
             flag.Compare(_T("\\Draft")) == 0)
            continue;

         unsupported.push_back(flag);
      }

      if (!unsupported.empty())
      {
         LOG_APPLICATION(Formatter::Format("SMTPDeliverer - The Sieve script for {0} set the flag(s) {1}, which this "
            "server cannot store: only \\Seen, \\Answered, \\Flagged, \\Deleted and \\Draft are held against a "
            "message. The rest of the script was applied.",
            account->GetAddress(), StringParser::JoinVector(unsupported, _T(" "))));
      }
   }

   bool
   LocalDelivery::ListContains_(const std::vector<String> &values, const String &value)
   {
      for (const String &candidate : values)
      {
         if (candidate.Compare(value) == 0)
            return true;
      }

      return false;
   }

   void
   LocalDelivery::EvaluateSieveScript_(std::shared_ptr<const Account> account, std::shared_ptr<Message> message,
                                       const String &sOriginalAddress, String &sieveFolder, bool &sieveDrop,
                                       bool &sieveFlagsGiven, std::vector<String> &sieveFlags)
   {
      sieveFolder = _T("");
      sieveDrop = false;
      sieveFlagsGiven = false;
      sieveFlags.clear();

      String script = SieveStorage::GetActiveScript(account->GetAddress());
      if (script.IsEmpty())
         return;

      // Load the raw message so the script's header/address/size tests can run.
      String messageFileName = PersistentMessage::GetFileName(account, message);
      String rawMessage = FileUtilities::ReadCompleteTextFile(messageFileName);

      // The SMTP envelope. The evaluator can fall back to the Return-Path and
      // Delivered-To trace headers, but Delivered-To is off in the shipped
      // configuration, and without the envelope recipient the RFC 5230 4.5 check that
      // refuses to auto-reply to mail the user was not addressed in has nothing to
      // check against - it would be silently skipped on a default install, which is
      // the failure mode of a loop guard that exists on paper only.
      SieveEnvelope envelope;
      envelope.available = true;
      envelope.from = message->GetFromAddress();
      envelope.to = sOriginalAddress.IsEmpty() ? account->GetAddress() : sOriginalAddress;

      // Answers the "mailboxexists" test (RFC 5490) against the recipient's real
      // folders. The lookup lives beside MoveToIMAPFolder so the test and the
      // fileinto that follows it cannot disagree about what a name refers to.
      __int64 accountID = account->GetID();
      auto mailboxExists = [accountID](const String &mailboxName) -> bool
      {
         return MessageUtilities::FolderExistsForDelivery(accountID, mailboxName);
      };

      // The "duplicate" test's seen-store, bound to this recipient's account. The
      // check and the recording are one operation inside the tracker.
      String duplicateAccount = account->GetAddress();
      auto duplicateCheck = [duplicateAccount](const String &identifier, const String &handle,
                                               __int64 windowSeconds, bool refreshOnSeen) -> bool
      {
         return SieveDuplicateTracker::Instance()->CheckAndRecord(
            duplicateAccount, identifier, handle, windowSeconds, refreshOnSeen);
      };

      SieveResult sieveResult;
      String actions = SieveScript::Evaluate(script, rawMessage, envelope, sieveResult, mailboxExists, message->GetFlagSpam(), duplicateCheck);

      // A script that fails to parse must never break delivery; fall through to a
      // normal keep. (The structured result is already at its defaults in that case,
      // so the early return only exists to log which script and why.)
      if (actions.StartsWith(_T("error:")))
      {
         String sMessage = Formatter::Format("SMTPDeliverer - Message {0}: Sieve script for {1} could not be evaluated ({2}).",
                                                original_message_->GetID(), account->GetAddress(), actions);
         LOG_APPLICATION(sMessage);
         return;
      }

      // The delivery decision is read from the structured result rather than parsed
      // back out of the ';'-joined summary string. Both were available before and this
      // used the summary, which was wrong twice over: a mailbox name containing a ';'
      // ("fileinto \"Projects;2026\"") split into two tokens and filed the message
      // nowhere, and having the vacation decision come from one representation while
      // the keep/fileinto decision came from the other is precisely how the two drift
      // apart. The summary is still what the COM diagnostic and the tests read, so it
      // is not going away - it just is not what delivery acts on.
      for (const String &target : sieveResult.redirects)
      {
         SMTPForwarding forwarder;
         forwarder.RedirectToAddress(account, message, target);
      }

      // Fire the RFC 5230 vacation auto-reply, if the script asked for one and it
      // survived every loop-prevention check the evaluator could apply. The responder
      // applies the suppression window and the remaining guards, and never touches the
      // delivery of the message itself, so nothing below depends on it.
      SieveVacationResponder::Instance()->Respond(account, sieveResult.vacation);

      if (!sieveResult.fileInto.IsEmpty())
         sieveFolder = sieveResult.fileInto;

      // Carried out whole rather than interpreted here. The evaluator has already
      // resolved setflag/addflag/removeflag and any :flags on the storing action into
      // ONE final set - RFC 5232's flag variable - so there is nothing left to decide;
      // reinterpreting it at delivery is how the two representations would drift.
      sieveFlagsGiven = sieveResult.flagsGiven;
      sieveFlags = sieveResult.flags;

      // When the script kept neither an explicit nor implicit local copy (a discard,
      // or a redirect that cancels the implicit keep), drop it locally.
      if (!sieveResult.keepLocal)
         sieveDrop = true;
   }

   bool 
   LocalDelivery::CheckAccountQuotas_(std::shared_ptr<const Account> pCheckAccount, std::vector<String> &saErrorMessages, bool suppressFailureDsn)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Checks that the recipient account has enough space available. If not, 
   // an error message is generated.
   //---------------------------------------------------------------------------()
   {
      // Check if message is small enough to fit inside receivers mailbox.
      // All values are converted to bytes.
      if (!pCheckAccount->SpaceAvailable(original_message_->GetSize()))
      {
         String sMsg = Formatter::Format("{0}\r\n   Error Type: SMTP\r\n   Error Description: Inbox is full\r\n   Additional information: The recipients inbox is full.\r\n\r\n",
            pCheckAccount->GetAddress());

         // DSN (RFC 3461): only bounce when the sender did not opt out via NOTIFY.
         if (!suppressFailureDsn)
            saErrorMessages.push_back(sMsg);  

         __int64 currentSize = AccountSizeCache::Instance()->GetSize(pCheckAccount->GetID()) + original_message_->GetSize();

         String sMessage;
         sMessage.Format(_T("SMTPDeliverer - Message %I64d: The message was not delivered to %s. ")
            // %I64d for currentSize: it is an __int64, and String::Format is variadic,
            // so a %d here consumed 32 bits of a 64-bit argument.
            _T("Delivery to this account was cancelled since the account inbox is full. Max size: %d MB, Current size (including cancelled message): %I64d MB"),
            original_message_->GetID(), String(pCheckAccount->GetAddress()).c_str(), pCheckAccount->GetAccountMaxSize(), (currentSize / 1024 / 1024));

         LOG_APPLICATION(sMessage);

         return false;
      }

      return true;
   }

   std::shared_ptr<Message> 
   LocalDelivery::CreateAccountLevelMessage_(std::shared_ptr<Message> pOriginalMessage, std::shared_ptr<const Account> pRecipientAccount, bool reuseMessage, const String &sOriginalAddress)
   {
      // Copy the original message to the new message. Also copy the message
      // file unless we should reuse the old one.
      std::shared_ptr<Message> pNewMessage;

      if (reuseMessage)
      {
         const String sourceLocation = PersistentMessage::GetFileName(pOriginalMessage);

         pNewMessage = pOriginalMessage;
         pNewMessage->SetAccountID(pRecipientAccount->GetID());

         // Locate the inbox for this user.
         __int64  inboxID = CacheContainer::Instance()->GetInboxIDCache().GetUserInboxFolder(pRecipientAccount->GetID());
         if (inboxID == 0)
         {
            std::shared_ptr<Message> empty;
            return empty;
         }

         pNewMessage->SetFolderID(inboxID);

         if (!PersistentMessage::MoveFileToUserFolder(sourceLocation, pNewMessage, pRecipientAccount))
         {
            std::shared_ptr<Message> empty;
            return empty;
         }
      }
      else
      {
         pNewMessage = PersistentMessage::CopyFromQueueToInbox(pOriginalMessage, pRecipientAccount);

         // The copy fails when the data volume is full or the file is locked by
         // another process. Returning empty lets the caller's existing handling
         // report the failure; dereferencing it here crashed the delivery task
         // and left the queued message locked forever.
         if (!pNewMessage)
            return pNewMessage;

         for (std::shared_ptr<MessageRecipient> recipient : pOriginalMessage->GetRecipients()->GetVector())
         {
            if (recipient->GetAddress().CompareNoCase(pRecipientAccount->GetAddress()) == 0)
            {
               pNewMessage->GetRecipients()->Add(recipient);
               break;
            }
         }
      }

      pNewMessage->SetNoOfRetries(pOriginalMessage->GetNoOfRetries());

      return pNewMessage;
   }

   bool 
   LocalDelivery::AddTraceHeaders_(std::shared_ptr<const Account> account, std::shared_ptr<Message> pMessage, const String &sOriginalAddress)
   {
      std::vector<std::pair<AnsiString, AnsiString> > fieldsToWrite;

      String sFromAddress = pMessage->GetFromAddress();
      AnsiString sReturnPath = sFromAddress.IsEmpty() ? "<>" : "<" + sFromAddress + ">";
      fieldsToWrite.push_back(std::make_pair("Return-Path", sReturnPath));

      if (Configuration::Instance()->GetSMTPConfiguration()->GetAddDeliveredToHeader())
         fieldsToWrite.push_back(std::make_pair("Delivered-To", sOriginalAddress));

      const String fileName = PersistentMessage::GetFileName(account, pMessage);

      TraceHeaderWriter writer;
      return writer.Write(fileName, pMessage, fieldsToWrite);
   }    

   void 
   LocalDelivery::SendAutoReplyMessage_(std::shared_ptr<const Account> pAccount, std::shared_ptr<Message> pMessage)
   {
      // Do this before we move the message to the users
      // directory. If we do it afterwards, the user may have (at least theoretically)
      // have the time to delete the message before the auto-reply is sent. And we need
      // the message to be able to generate a Re:-subject line.

      // Should we send a vacation message back?
      if (!PersistentAccount::GetIsVacationMessageOn(pAccount))
         return;

      // Don't deliver vacation message to ourselves.
      if (pAccount->GetAddress().CompareNoCase(pMessage->GetFromAddress()) == 0)
         return;

      // Don't deliver vacation message when the message is classified as spam
      if (pAccount->GetVacationAbortSpamFlagged() && pMessage->GetFlagSpam())
      {
         LOG_DEBUG("LocalDelivery::SendAutoReplyMessage_ aborted, message marked as spam");
         return;
      }

      // Save a new message with the vacation message in it.
      SMTPVacationMessageCreator::Instance()->CreateVacationMessage(pAccount, 
         pMessage->GetFromAddress(), 
         pAccount->GetVacationSubject(), 
         pAccount->GetVacationMessage(),
         pMessage
         );
   }      

   bool 
   LocalDelivery::RunAccountRules_(std::shared_ptr<const Account> pAccount, std::shared_ptr<Message> pMessage, RuleResult &accountRuleResult)
   {
      // Apply rules on this message.  
      std::shared_ptr<RuleApplier> pRuleApplier = std::shared_ptr<RuleApplier>(new RuleApplier);

      pRuleApplier->ApplyRules(ObjectCache::Instance()->GetAccountRules(pAccount->GetID()), pAccount, pMessage, accountRuleResult);

      if (accountRuleResult.GetDeleteEmail())
      {
         String sDeleteRuleName = accountRuleResult.GetDeleteRuleName();

         String sMessage = Formatter::Format("SMTPDeliverer - Message {0}: The message was not delivered to {1}. Delivery to this account was canceled by an account rule {2}.",
                                                   pMessage->GetID(), pAccount->GetAddress(), sDeleteRuleName);

         LOG_APPLICATION(sMessage);
         return false;
      }

      return true;
   }




}