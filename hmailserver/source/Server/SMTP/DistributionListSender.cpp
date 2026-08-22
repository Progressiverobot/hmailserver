// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"

#include "DistributionListSender.h"

#include "PlusAddressing.h"

#include "../Common/Application/ObjectCache.h"
#include "../Common/Cache/CacheContainer.h"

#include "../Common/BO/Domain.h"
#include "../Common/BO/DomainAliases.h"
#include "../Common/BO/DistributionList.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/MessageData.h"
#include "../Common/BO/MessageRecipient.h"
#include "../Common/BO/MessageRecipients.h"

#include "../Common/Mime/Mime.h"

#include "../Common/Persistence/PersistentMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   void
   DistributionListSender::AddListHeaders(std::shared_ptr<Message> message)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Adds the RFC 2369 / RFC 2919 List-* headers to a message posted to a local
   // distribution list. Called while the message is still being accepted, which is
   // before DKIMSigner runs (SMTPDeliverer::PreprocessMessage_ signs at delivery
   // time, reading the message back off disk), so the headers are inside the
   // signature rather than added after it and breaking it.
   //---------------------------------------------------------------------------()
   {
      if (!message)
         return;

      std::shared_ptr<const DistributionList> list = FindPostedList_(message);

      if (!list)
      {
         // Not a distribution-list posting. This is the overwhelmingly common case
         // and it must stay free: nothing below has run, and no file was opened.
         return;
      }

      // Before anything that can return early: the bounce address is an envelope
      // change, not a header change, so it must apply even when the header work
      // below cannot run - a message that already carries a List-Id, a list
      // address that yields no valid identifier, a message file an antivirus is
      // holding open. Every copy this posting produces must bounce to the list
      // owner regardless of which headers it could be given.
      ApplyBounceAddress_(message, list);

      // A posting RecipientParser redirected to the moderator is a forward for
      // approval, not a distribution: it is stamped with the moderation header
      // further down instead of the List-* set.
      const bool moderationForward = MessageIsModerationForward_(message);

      const String listId = list->GetRfc2919ListId();

      if (listId.IsEmpty() && !moderationForward)
      {
         // A list address with no local part or no domain cannot produce a valid
         // RFC 2919 identifier. Nothing is wrong with the message and nothing is
         // wrong with the server, so this is not an error - the posting simply gets
         // no List-* headers.
         String msg;
         msg.Format(_T("DistributionListSender - No RFC 2919 List-Id can be derived from list address '%s'. The posting is delivered without List-* headers."), list->GetAddress().c_str());
         LOG_APPLICATION(msg);
         return;
      }

      const String fileName = PersistentMessage::GetFileName(message);
      const long originalSize = FileUtilities::FileSize(fileName);

      if (originalSize <= 0)
      {
         // No readable message file to add headers to. Leave it alone; whatever is
         // wrong here is not this code's to fix, and every other stage of delivery
         // will report it.
         String msg;
         msg.Format(_T("DistributionListSender - Message file '%s' is missing or empty. The posting is delivered without List-* headers."), fileName.c_str());
         LOG_APPLICATION(msg);
         return;
      }

      // The message is only ever *inspected* through MessageData - never written
      // through it. MessageData::Write goes to MimeBody::SaveAllToFile, which either
      // re-serialises the whole message from the parse tree or copies the body back
      // out of the file it was loaded from; both of those turn a momentarily
      // unreadable file into a message with no body, and return success while doing
      // it. Prepending raw header lines and streaming the original bytes after them
      // (see PrependHeaderLines_) cannot lose the body, because it never has to
      // reconstruct it.
      std::shared_ptr<MessageData> messageData = std::shared_ptr<MessageData>(new MessageData);

      if (!messageData->LoadFromMessage(fileName, message))
      {
         String msg;
         msg.Format(_T("DistributionListSender - Could not load message '%s' to check its headers. The posting is delivered without List-* headers."), fileName.c_str());
         LOG_APPLICATION(msg);
         return;
      }

      if (!LoadedMessageIsGenuine_(messageData))
      {
         // See LoadedMessageIsGenuine_ for why a true return from LoadFromMessage is
         // not proof that the message was read.
         String msg;
         msg.Format(_T("DistributionListSender - Message '%s' loaded without the Received header this server always writes, so the message file was not really read (an antivirus or backup holding the file open is the usual cause). No List-* headers were added and the message was left untouched."), fileName.c_str());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5760, "DistributionListSender::AddListHeaders", msg);
         return;
      }

      std::shared_ptr<MimeBody> mime = messageData->GetMimeMessage();

      if (!mime)
         return;

      if (moderationForward)
      {
         // The moderator's copy gets exactly one thing: a header naming the list
         // that wants the approval. No List-* headers - this message is not being
         // distributed, and stamping it as list mail would tell the moderator's
         // mail client to file it with the list traffic it is supposed to stand
         // out from. The moderator acts on it by resending the posting to the
         // list from an authenticated session.
         const String moderatedListAddress = list->GetAddress();

         if (!IsUsAscii_(moderatedListAddress))
         {
            // Same constraint as the List-* values below: the value becomes raw
            // bytes in the header block, and this path has no RFC 6532 handling.
            // The forward still reaches the moderator - it is just unlabelled.
            String msg;
            msg.Format(_T("DistributionListSender - List address '%s' is not US-ASCII, so its moderation forward carries no X-hMailServer-Moderation header."), moderatedListAddress.c_str());
            LOG_APPLICATION(msg);
            return;
         }

         String moderationHeaderLines;

         AppendFieldIfAbsent_(moderationHeaderLines, mime, "X-hMailServer-Moderation", moderatedListAddress);

         if (moderationHeaderLines.IsEmpty())
         {
            // The message already carries the header - a posting that has been
            // through moderation before, resent into moderation again. Nothing to
            // write, so the file is not touched.
            return;
         }

         PrependHeaderLines_(fileName, moderationHeaderLines, message, originalSize);
         return;
      }

      if (mime->FieldExists("List-Id"))
      {
         // The message already belongs to a mailing list - it has been posted to one
         // of our lists after travelling through another one, or a member posted a
         // message they received from a list. RFC 2919 allows exactly one List-Id,
         // and the upstream list's headers are the ones that describe where the
         // subscriber actually got the mail, so none of ours are added.
         String msg;
         msg.Format(_T("DistributionListSender - Message posted to list '%s' already carries a List-Id, so it is left as it is."), list->GetAddress().c_str());
         LOG_DEBUG(msg);
         return;
      }

      const String listAddress = list->GetAddress();
      const String ownerAddress = DetermineOwnerAddress_(list);

      // These values end up as raw bytes in the header block. A list or owner address
      // outside US-ASCII cannot be represented there without RFC 6532 handling that
      // the rest of this header path does not have, and a mangled unsubscribe address
      // is worse than an absent one.
      if (!IsUsAscii_(listAddress) || !IsUsAscii_(ownerAddress))
      {
         String msg;
         msg.Format(_T("DistributionListSender - List address '%s' or its owner address is not US-ASCII, so no List-* headers were added."), listAddress.c_str());
         LOG_APPLICATION(msg);
         return;
      }

      String headerLines;

      AppendFieldIfAbsent_(headerLines, mime, "List-Id", listId);

      if (list->IsAnnouncementOnly())
      {
         // RFC 2369: a list which does not accept postings says so explicitly, and
         // "NO" is the one value defined for that. hMailServer's announcement mode
         // rejects anything not from the single authorized address, so pointing
         // subscribers at the list address would be pointing them at a 5xx.
         AppendFieldIfAbsent_(headerLines, mime, "List-Post", "NO");
      }
      else
      {
         String listPost;
         listPost += "<mailto:";
         listPost += listAddress;
         listPost += ">";

         AppendFieldIfAbsent_(headerLines, mime, "List-Post", listPost);
      }

      if (!ownerAddress.IsEmpty())
      {
         String listOwner;
         listOwner += "<mailto:";
         listOwner += ownerAddress;
         listOwner += ">";

         String listHelp;
         listHelp += "<mailto:";
         listHelp += ownerAddress;
         listHelp += "?subject=help>";

         // mailto: only, on purpose. RFC 2369's mailto form reaches a person who can
         // act on the request; RFC 8058 one-click would need an HTTPS endpoint that
         // accepts a POST, and advertising one that does not exist makes receivers
         // treat the unsubscribe as broken. The subject names the list so the
         // recipient of the request knows which list to remove the sender from.
         String listUnsubscribe;
         listUnsubscribe += "<mailto:";
         listUnsubscribe += ownerAddress;
         listUnsubscribe += "?subject=unsubscribe%20";
         listUnsubscribe += listAddress;
         listUnsubscribe += ">";

         AppendFieldIfAbsent_(headerLines, mime, "List-Help", listHelp);
         AppendFieldIfAbsent_(headerLines, mime, "List-Owner", listOwner);
         AppendFieldIfAbsent_(headerLines, mime, "List-Unsubscribe", listUnsubscribe);
      }

      if (headerLines.IsEmpty())
      {
         // Every field we would have added is already present. Nothing to write, so
         // the message file is not touched at all.
         return;
      }

      PrependHeaderLines_(fileName, headerLines, message, originalSize);
   }

   bool
   DistributionListSender::LoadedMessageIsGenuine_(std::shared_ptr<MessageData> message_data)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // MessageData::LoadFromMessage returning true does not mean the message was
   // parsed. MimeBody::LoadFromFile returns false when the file cannot be opened at
   // all - an antivirus or backup sharing violation on a file this connection has
   // just closed is enough - and LoadFromMessage turns that into "this is a new
   // message": it sets a charset and MIME-Version and returns true. What the caller
   // then holds is an empty message with two headers and no From, Subject or body.
   //
   // So we look for evidence the server itself always writes.
   // SMTPConnection::AppendMessageHeaders_ prepends a Received header, via
   // SMTPMessageHeaderCreator, to every message accepted over SMTP before it is
   // written to disk. There is no setting which turns that off
   // (trace_headers_written_ is initialised true and reset to true for each
   // message), so a loaded message with no Received header was not read from disk,
   // whatever LoadFromMessage said.
   //---------------------------------------------------------------------------()
   {
      std::shared_ptr<MimeBody> mime = message_data->GetMimeMessage();

      if (!mime)
         return false;

      return mime->FieldExists("Received");
   }

   std::shared_ptr<const DistributionList>
   DistributionListSender::FindPostedList_(std::shared_ptr<Message> message)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Works out which local distribution list, if any, this message was posted to,
   // by resolving the addresses the sender actually used in RCPT TO.
   //
   // This runs for every message the server accepts, so the expensive part is gated
   // carefully. CacheContainer::GetDistributionList falls back to a database read
   // when the address is not a cached list, and it does not remember misses, so
   // asking it about every recipient would add a database query per message to the
   // SMTP accept path - for the great majority of mail, which is not list mail.
   //---------------------------------------------------------------------------()
   {
      std::shared_ptr<const DistributionList> empty;

      std::shared_ptr<MessageRecipients> recipients = message->GetRecipients();

      if (!recipients)
         return empty;

      // Collect the distinct addresses worth resolving first. A 500-member list
      // produces 500 recipients all carrying the same original address, and it only
      // needs looking up once.
      std::set<String> candidateAddresses;

      const std::vector<std::shared_ptr<MessageRecipient> > &vecRecipients = recipients->GetVector();

      for (const std::shared_ptr<MessageRecipient> &recipient : vecRecipients)
      {
         // GetOriginalAddress is the address used in RCPT TO; GetAddress is what it
         // resolved to. RecipientParser::CreateMessageRecipientList_ passes the
         // original through every level of expansion unchanged, so a list whose
         // members include another list still reports the outer list here - which is
         // the list the message belongs to, and the reason nested lists produce one
         // set of headers rather than one set per level.
         const String originalAddress = recipient->GetOriginalAddress();

         if (originalAddress.IsEmpty())
            continue;

         // The cheap gate, and the reason ordinary mail costs nothing here: a
         // distribution list always expands to addresses other than its own, so a
         // posting to one always leaves at least one recipient whose delivery address
         // differs from the address the sender used. Mail addressed straight to a
         // mailbox resolves to itself and is skipped before any lookup.
         if (originalAddress.CompareNoCase(recipient->GetAddress().c_str()) == 0)
            continue;

         String candidate = originalAddress;
         candidate.ToLower();

         candidateAddresses.insert(candidate);
      }

      if (candidateAddresses.empty())
         return empty;

      std::shared_ptr<DomainAliases> domainAliases = ObjectCache::Instance()->GetDomainAliases();

      std::shared_ptr<const DistributionList> found;

      for (const String &candidateAddress : candidateAddresses)
      {
         const String primaryAddress = domainAliases->ApplyAliasesOnAddress(candidateAddress);
         const String primaryDomain = StringParser::ExtractDomain(primaryAddress);

         std::shared_ptr<const Domain> domain = CacheContainer::Instance()->GetDomain(primaryDomain);

         if (!domain)
            continue;

         const String listAddress = PlusAddressing::ExtractAccountAddress(primaryAddress, domain);

         std::shared_ptr<const DistributionList> list = CacheContainer::Instance()->GetDistributionList(listAddress);

         if (!list || !list->GetActive())
            continue;

         if (found && found->GetID() != list->GetID())
         {
            // The same message was posted to two different lists in one transaction.
            // RFC 2919 permits exactly one List-Id and there is no honest way to
            // choose between them, so the message is labelled with neither rather
            // than told it belongs to a list half its recipients are not on.
            String msg;
            msg.Format(_T("DistributionListSender - Message was posted to more than one distribution list ('%s' and '%s'), so no List-* headers were added."), found->GetAddress().c_str(), list->GetAddress().c_str());
            LOG_DEBUG(msg);
            return empty;
         }

         found = list;
      }

      return found;
   }

   void
   DistributionListSender::ApplyBounceAddress_(std::shared_ptr<Message> message, std::shared_ptr<const DistributionList> list)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Replaces the message's envelope sender with the list's bounce address, when
   // the list has one. From here on every copy this posting produces - the member
   // copies, or the moderation forward - is sent with MAIL FROM of the bounce
   // address (ExternalDelivery) and delivered with Return-Path of the bounce
   // address (LocalDelivery::AddTraceHeaders_), both of which read
   // Message::GetFromAddress. A member whose mailbox has died then bounces to the
   // list owner, who can remove them, instead of to the original author, who can
   // do nothing about it - and an external member's receiving server evaluates
   // SPF against the list's own domain rather than the author's, which this
   // server is actually entitled to pass.
   //
   // An empty bounce address is the off switch and the previous behaviour
   // byte for byte: the envelope sender stays whatever MAIL FROM said.
   //
   // Runs while the message is still being accepted and before it is first saved,
   // so the changed sender is what lands in hm_messages.messagefrom - nothing
   // needs to remember to rewrite it later.
   //---------------------------------------------------------------------------()
   {
      const String bounceAddress = list->GetBounceAddress();

      if (bounceAddress.IsEmpty())
         return;

      if (bounceAddress.CompareNoCase(list->GetAddress().c_str()) == 0)
      {
         // A bounce address pointing at the list itself would hand every delivery
         // failure back to the list for redistribution: a dead member's bounce
         // becomes a posting, which produces more copies, which produce more
         // bounces. Refusing the configuration here, loudly, is cheaper than the
         // mail loop; the envelope sender is left as it arrived.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6362, "DistributionListSender::ApplyBounceAddress_",
            Formatter::Format("Distribution list {0} has its own address configured as its bounce address, which would mail-loop every delivery failure back into the list. The setting was ignored and the posting keeps its original envelope sender.",
               list->GetAddress()));
         return;
      }

      if (message->GetFromAddress().CompareNoCase(bounceAddress.c_str()) == 0)
         return;

      String msg;
      msg.Format(_T("DistributionListSender - Envelope sender of the posting to '%s' changed from '%s' to the list's bounce address '%s'."),
         list->GetAddress().c_str(), message->GetFromAddress().c_str(), bounceAddress.c_str());
      LOG_DEBUG(msg);

      message->SetFromAddress(bounceAddress);
   }

   bool
   DistributionListSender::MessageIsModerationForward_(std::shared_ptr<Message> message)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Whether this message is a posting RecipientParser redirected to a list
   // moderator for approval. The flag rides on the MessageRecipient objects the
   // redirect created, which are the same in-memory objects this accept path is
   // still holding - it never round-trips through the database, and does not need
   // to: the header this answer gates is written before the message is queued.
   //
   // True only when EVERY recipient is a moderation forward. The header this
   // gates is stamped on the one message file all recipients share, so it cannot
   // be right for a mixture - and a mixture exists: a list the sender may post to
   // whose members include a nested list that moderates them sends member copies
   // and a moderation forward from the same file. That message is treated as the
   // distribution it mostly is (List-* headers, no moderation header); the nested
   // moderator's copy is the ordinary member copy of it, just addressed to them.
   // The pure case - the posting went to moderation and nowhere else, which is
   // what a moderated posting normally is - answers true.
   //---------------------------------------------------------------------------()
   {
      std::shared_ptr<MessageRecipients> recipients = message->GetRecipients();

      if (!recipients)
         return false;

      const std::vector<std::shared_ptr<MessageRecipient> > &recipientVector = recipients->GetVector();

      if (recipientVector.empty())
         return false;

      for (const std::shared_ptr<MessageRecipient> &recipient : recipientVector)
      {
         if (!recipient->GetIsModerationForward())
            return false;
      }

      return true;
   }

   String
   DistributionListSender::DetermineOwnerAddress_(std::shared_ptr<const DistributionList> list)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Picks the address List-Owner, List-Help and List-Unsubscribe point at. It has
   // to be an address a human actually receives, because nothing in this server
   // processes list commands automatically - the whole point of the mailto-only
   // unsubscribe is that a person reads it.
   //---------------------------------------------------------------------------()
   {
      // An announcement-only list records the single address which is allowed to
      // post to it. That is the closest thing hMailServer stores to a list owner,
      // and it is known to be a real mailbox because postings are checked against it.
      if (list->IsAnnouncementOnly() && !list->GetRequireAddress().IsEmpty())
         return list->GetRequireAddress();

      // Otherwise the domain's catch-all account, if one is configured, since it
      // receives everything the domain cannot otherwise deliver.
      std::shared_ptr<const Domain> domain = CacheContainer::Instance()->GetDomain(list->GetDomainID());

      if (domain && !domain->GetPostmaster().IsEmpty())
         return domain->GetPostmaster();

      const String listDomain = StringParser::ExtractDomain(list->GetAddress());

      if (listDomain.IsEmpty())
         return "";

      // Last resort: postmaster at the list's own domain. RFC 5321 section 4.5.1
      // requires every domain which receives mail to accept it, so it is the
      // standards-blessed fallback rather than a guess - but see the caller: if it
      // is not actually deliverable here, the operator has a non-conforming domain.
      String postmaster;
      postmaster += "postmaster@";
      postmaster += listDomain;

      return postmaster;
   }

   void
   DistributionListSender::AppendFieldIfAbsent_(String &header_lines, std::shared_ptr<MimeBody> mime, const char *field_name, const String &field_value)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Adds one header line, unless the message already carries that field. Checking
   // each field rather than only List-Id means a message which arrived with, say, a
   // List-Unsubscribe of its own never ends up with two of them.
   //---------------------------------------------------------------------------()
   {
      if (mime->FieldExists(field_name))
         return;

      // Every value this class produces is a short RFC 2369 URL in angle brackets or
      // the literal NO, so none of them come close to the RFC 5322 line limit and
      // none of them need folding.
      header_lines += field_name;
      header_lines += ": ";
      header_lines += field_value;
      header_lines += "\r\n";
   }

   bool
   DistributionListSender::IsUsAscii_(const String &value)
   {
      for (const wchar_t character : value)
      {
         if (character > 127)
            return false;
      }

      return true;
   }

   bool
   DistributionListSender::PrependHeaderLines_(const String &message_file_name, const String &header_lines, std::shared_ptr<Message> message, long original_size)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Writes the new header lines followed by every byte of the accepted message to a
   // temporary file beside it, verifies the result is exactly the right length, and
   // then replaces the accepted message with it in a single atomic rename.
   //
   // The accepted message file is only ever opened read-only and is only ever
   // replaced whole. Every failure returns with that file exactly as it was found and
   // still deliverable, because the sender has already been told 250 by the time this
   // runs: a message that is missing, truncated or sitting under the wrong name is
   // permanently lost mail the sender believes was delivered, which is far worse than
   // a list posting without List-* headers.
   //
   // The three places a failure could previously have left it worse than untouched,
   // and what stops each now:
   //
   // 1. The rewrite is built somewhere else. Nothing is ever written to, appended to
   //    or truncated at message_file_name, so a temporary file which cannot be
   //    created, cannot be written, fills the disk or throws part way through leaves
   //    the accepted message untouched by construction. There is no partial file
   //    where the original was because the original is never the write target.
   //
   // 2. There is no move-aside step any more, so there is no window in which
   //    message_file_name does not exist. The earlier version renamed the accepted
   //    message to a .bak and then renamed the temporary file into its place, which
   //    meant a failure - or a crash, or a power loss - between those two renames
   //    left the only copy of an already-accepted message under a name no delivery
   //    stage looks for, needing an operator to rename it back by hand.
   //
   // 3. The replacement is one rename with replace-existing semantics.
   //    FileUtilities::Move is boost::filesystem::rename, which on Windows is
   //    MoveFileExW(from, to, MOVEFILE_REPLACE_EXISTING|MOVEFILE_COPY_ALLOWED);
   //    both files are in the same directory, hence the same volume, so COPY_ALLOWED
   //    never engages and the file system swaps the directory entry as one operation.
   //    message_file_name therefore always names a complete message: the accepted one
   //    if the rename failed, the rewritten one if it succeeded, and never a mixture.
   //
   //    This paragraph used to warn that Move's overwrite flag had to stay false,
   //    because passing true deleted the destination before renaming and a failure
   //    after that delete - a scanner holding the temporary file open is enough - left
   //    no message on disk at all. That delete has since been removed from
   //    FileUtilities::Move for exactly that reason, and the flag with it: there is now
   //    one behaviour, and it is this one. Nothing here needs to opt into it.
   //---------------------------------------------------------------------------()
   {
      const String tempFileName = message_file_name + ".listheaders.tmp";

      // Every file here is opened binary ("rb"/"wb"), so no CRLF translation happens
      // and the byte arithmetic below is exact.
      const AnsiString headerLinesAnsi = header_lines;
      const long expectedSize = original_size + headerLinesAnsi.GetLength();

      bool written = false;

      try
      {
         File temporaryFile;

         if (!temporaryFile.Open(tempFileName, File::OTCreate))
         {
            // No temporary file exists and the accepted message has not been opened,
            // let alone touched. OTCreate is "wb", so this having failed also means
            // nothing was truncated - and had it succeeded on a stale temporary file
            // left by an earlier failure, "wb" would have truncated that, not this
            // message.
            String msg;
            msg.Format(_T("DistributionListSender - Could not create '%s' to add List-* headers. The message is delivered unchanged."), tempFileName.c_str());
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5761, "DistributionListSender::PrependHeaderLines_", msg);
            return false;
         }

         File messageFile;

         written = temporaryFile.Write(headerLinesAnsi) &&
                   messageFile.Open(message_file_name, File::OTReadOnly) &&
                   temporaryFile.Write(messageFile) &&
                   temporaryFile.FlushToDisk();

         temporaryFile.Close();
         messageFile.Close();
      }
      catch (...)
      {
         // File::Write(File&) copies through File::ReadChunk, which does not return
         // false on a read error - it reports and rethrows, and also throws on a
         // failed allocation for the chunk buffer. Letting that escape would carry a
         // failure out of DoPreAcceptMessageModifications_ for a message that is
         // sitting on disk intact, and would leave the temporary file behind. It is
         // handled here as exactly what it is: the rewrite did not happen.
         //
         // Both File objects are declared inside the try block, so their destructors
         // have closed both handles by the time this runs.
         written = false;
      }

      const long newSize = FileUtilities::FileSize(tempFileName);

      // The result is the header lines followed by every original byte, so its size
      // is known exactly. Checking the exact figure rather than merely that the file
      // grew is what catches a *partial* copy: File::Write(File&) stops and returns
      // true if a chunk comes back short of what was asked for at end of file, and a
      // body truncated by less than the length of the headers just added would still
      // leave a larger file.
      //
      // This is also the header-append-succeeded-then-size-check-failed case: the
      // temporary file is deleted here and the accepted message, which was never
      // written to, is still at its own name and delivers - without List-* headers.
      // If even the delete fails it reports itself and leaves a .listheaders.tmp
      // beside the message; that is inert, because every delivery stage opens the
      // name PersistentMessage::GetFileName derives from the database row and nothing
      // enumerates the directory.
      if (!written || newSize != expectedSize)
      {
         FileUtilities::DeleteFile(tempFileName);

         String msg;
         msg.Format(_T("DistributionListSender - Rewrite of '%s' to add List-* headers produced %d bytes where %d were expected (original %d), so it was discarded and the accepted message left untouched and deliverable."), message_file_name.c_str(), (int) newSize, (int) expectedSize, (int) original_size);
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5762, "DistributionListSender::PrependHeaderLines_", msg);
         return false;
      }

      // The one and only modification to the accepted message: replaced whole, or not
      // at all. See (3) above for why the flag is false.
      if (!FileUtilities::Move(tempFileName, message_file_name))
      {
         FileUtilities::DeleteFile(tempFileName);

         String msg;
         msg.Format(_T("DistributionListSender - Could not put the rewritten '%s' in place, so the accepted message is still there unchanged and is delivered without List-* headers."), message_file_name.c_str());
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5763, "DistributionListSender::PrependHeaderLines_", msg);
         return false;
      }

      // The caller re-reads the size from disk immediately after this, but keep the
      // in-memory size correct in case this is ever called from somewhere that does
      // not.
      message->SetSize(newSize);

      return true;
   }
}
