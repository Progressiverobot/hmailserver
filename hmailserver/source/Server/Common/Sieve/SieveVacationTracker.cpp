// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "SieveVacationTracker.h"

#include "SieveStorage.h"

#include "../Util/Charset.h"
#include "../Util/FileUtilities.h"
#include "../Util/Hashing/HashCreator.h"

#include <stdlib.h>
#include <time.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The first line of every store. It is checked on read, not merely written:
      // an absent or different header means this file was not written by this code,
      // and a file we do not understand cannot be read as "no reply has been sent".
      // It is also where a future format change announces itself, rather than being
      // guessed at from the record shape.
      const char *StoreHeaderLine = "# hMailServer Sieve vacation response tracking, format 1";

      // How many live records one account's store may hold.
      //
      // Not configurable, and that is a decision rather than an omission. The only
      // thing an administrator could do with a knob here is lower it, and lowering
      // it does not save anything worth saving - the whole store is at most a couple
      // of hundred kilobytes - while raising it beyond what one holiday plausibly
      // needs only delays the point at which something has gone wrong. What the
      // number bounds is a file that is read in full on every auto-reply, so it has
      // to exist; what it must never do is bound the file by forgetting a record
      // (see the header comment).
      //
      // 2000 distinct correspondents inside one suppression window is far more than
      // a person's mailbox sees in a fortnight, so an account that reaches it is
      // being written to by something automatic - which is exactly when we should
      // stop replying anyway.
      const size_t MaxLiveRecords = 2000;

      // Refuse to read a store larger than this. A well-formed store is
      // MaxLiveRecords lines of about seventy bytes plus the header, so a megabyte
      // is two orders of magnitude of slack. The check exists because
      // ReadCompleteTextFile reads the whole file into memory before anything can
      // inspect it: without a size gate, a corrupt (or maliciously placed) file in
      // the data directory would be a memory-exhaustion bug on the delivery thread.
      const long MaxStoreBytes = 1024 * 1024;

      // The longest suppression window that will be recorded, a century. The
      // responder already clamps ":days", so this is defence in depth against an
      // arithmetic overflow producing a negative expiry - which would read as
      // "expired" on the next load and turn the longest possible window into the
      // shortest possible one.
      const __int64 MaxWindowSeconds = 36500LL * 86400LL;

      __int64 CurrentUnixTime()
      {
         return static_cast<__int64>(::time(nullptr));
      }

      // A SHA-256 hex digest of the UTF-8 form of the input. UTF-8 rather than the
      // platform code page because a ":handle" may legitimately be non-ASCII and two
      // handles that differ only outside the code page must not collide.
      AnsiString HexDigest(const String &input)
      {
         HashCreator hasher(HashCreator::SHA256);
         return hasher.GenerateHashNoSalt(Charset::ToMultiByte(input, "utf-8"), HashCreator::hex);
      }

      // Splits an ASCII blob into lines, tolerating CRLF, LF and a missing final
      // newline. Written out rather than using StringParser::SplitString so that the
      // store's parsing does not depend on that function's handling of empty trailing
      // fields, which this format cares about (an empty line must be skipped, not
      // treated as a record).
      //
      // Returns false when the content has more than maxLines lines. Save_ writes a
      // header, one line per record and a trailing empty line, so anything longer was
      // not written by this class - and truncating instead of refusing would be a way
      // to lose the record that says a sender has already been answered. The limit is
      // also what keeps the line vector bounded: MaxStoreBytes of nothing but
      // newlines would otherwise become half a million strings on a delivery thread.
      bool SplitLines(const AnsiString &content, size_t maxLines, std::vector<AnsiString> &lines)
      {
         int start = 0;
         int length = content.GetLength();

         for (int i = 0; i <= length; i++)
         {
            if (i != length && content[i] != '\n')
               continue;

            if (lines.size() >= maxLines)
               return false;

            AnsiString line = content.Mid(start, i - start);
            line.TrimRight("\r");
            lines.push_back(line);

            start = i + 1;
         }

         return true;
      }

      // A decimal integer that _atoi64 can read without overflowing. The length
      // limit is part of the check rather than a separate one: nineteen digits is
      // the most a signed 64-bit value can have, and a longer run of digits in the
      // store is a line this class did not write, so refusing it belongs with the
      // rest of the "we do not understand this file" handling.
      bool IsAsciiInt64(const AnsiString &value)
      {
         const int maxDigits = 19;

         if (value.IsEmpty() || value.GetLength() > maxDigits)
            return false;

         for (int i = 0; i < value.GetLength(); i++)
         {
            if (value[i] < '0' || value[i] > '9')
               return false;
         }

         return true;
      }
   }

   SieveVacationTracker::SieveVacationTracker()
   {
   }

   size_t
   SieveVacationTracker::MaxTrackedResponses()
   {
      return MaxLiveRecords;
   }

   bool
   SieveVacationTracker::IsWellFormedKey_(const AnsiString &key)
   {
      // A SHA-256 hex digest: 64 lower-case hex characters, which is the only thing
      // HashCreator::GenerateHashNoSalt(..., hex) produces.
      const int digestLength = 64;

      if (key.GetLength() != digestLength)
         return false;

      for (int i = 0; i < digestLength; i++)
      {
         char ch = key[i];
         bool hexDigit = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f');
         if (!hexDigit)
            return false;
      }

      return true;
   }

   AnsiString
   SieveVacationTracker::KeyFor_(const String &sender, const String &handle)
   {
      String lowerSender = sender;
      lowerSender.ToLower();

      // '\n' as the field separator inside the pre-image is safe even though a
      // handle may contain one: the pre-image is hashed, never written, so the only
      // property needed here is that the two fields cannot be re-divided in a way
      // that makes two different (sender, handle) pairs collide. A separator that
      // cannot appear in an address gives that.
      return HexDigest(lowerSender + _T("\n") + handle);
   }

   String
   SieveVacationTracker::DeriveHandle(const String &reason,
                                      const String &subject,
                                      const String &fromAddress,
                                      bool mimeReason)
   {
      // RFC 5230 4.4: with no ":handle" the response is identified by its own
      // content. The arguments are joined with a separator that cannot appear in an
      // address and then hashed, so that (reason "a", subject "bc") and
      // (reason "ab", subject "c") do not share a suppression window.
      String preImage;
      preImage += reason;
      preImage += _T("\n");
      preImage += subject;
      preImage += _T("\n");
      preImage += fromAddress;
      preImage += _T("\n");
      preImage += mimeReason ? _T("mime") : _T("text");

      return String(HexDigest(preImage).c_str());
   }

   bool
   SieveVacationTracker::Load_(const String &path, __int64 now, std::vector<Record> &records, bool &prunedAny)
   {
      records.clear();
      prunedAny = false;

      if (!FileUtilities::Exists(path))
      {
         // No store has ever been written for this account. This is the one
         // "not found" that really is proof: the file is created by the first
         // successful claim, so its absence means no claim has ever succeeded.
         return true;
      }

      if (FileUtilities::FileSize(path) > MaxStoreBytes)
         return false;

      AnsiString content = FileUtilities::ReadCompleteTextFile(path);

      if (content.IsEmpty())
      {
         // The file exists but yielded nothing. ReadCompleteTextFile returns an
         // empty string both for a file it could not open and for a file that is
         // genuinely empty, and neither can be read as "nothing has been answered":
         // a store this class wrote always has at least its header line, so an empty
         // one means a failed read or a write that was interrupted before the rename
         // in Save_ could take effect.
         return false;
      }

      // The header line, the records, and the empty line the final CRLF produces.
      std::vector<AnsiString> lines;
      if (!SplitLines(content, MaxLiveRecords + 2, lines))
         return false;

      if (lines.empty() || lines[0].Compare(StoreHeaderLine) != 0)
         return false;

      size_t recordLines = 0;

      for (size_t i = 1; i < lines.size(); i++)
      {
         const AnsiString &line = lines[i];

         if (line.IsEmpty() || line[0] == '#')
            continue;

         recordLines++;

         if (recordLines > MaxLiveRecords)
         {
            // More record lines than Save_ is capable of writing, so this file was
            // not produced by this class. Reading it selectively would mean deciding
            // which half of a file we do not understand to believe.
            return false;
         }

         int space = line.Find(' ');
         if (space <= 0)
            return false;

         AnsiString expiryText = line.Mid(0, space);
         AnsiString key = line.Mid(space + 1);

         if (!IsAsciiInt64(expiryText) || !IsWellFormedKey_(key))
         {
            // A line we cannot parse may be the record that says this sender has
            // already been answered, so the whole store is unusable. Refusing here
            // rather than skipping the line is the difference between "we do not
            // know" and "we checked".
            return false;
         }

         Record record;
         record.expires = _atoi64(expiryText.c_str());
         record.key = key;

         if (record.expires <= now)
         {
            // This is the pruning. An expired record is simply not carried forward,
            // so the store shrinks on the next write without a sweeper task. It also
            // means a record can never resurrect a window that has already passed:
            // the caller only ever sees records that are still in force.
            prunedAny = true;
            continue;
         }

         records.push_back(record);
      }

      return true;
   }

   bool
   SieveVacationTracker::Save_(const String &path, const std::vector<Record> &records)
   {
      if (records.size() > MaxLiveRecords)
      {
         // Unreachable: ClaimResponse refuses to add a record once the store is
         // full, which is the whole point of that check. Kept as a hard stop rather
         // than an assertion because the tempting "fix" if it ever does trip is to
         // drop the excess, and dropping a live record is the mail loop this class
         // exists to prevent. Failing the write instead costs one auto-reply.
         return false;
      }

      AnsiString content = StoreHeaderLine;
      content += "\r\n";

      for (const Record &record : records)
      {
         AnsiString line;
         // "%hs" rather than "%s" for the key: this is a narrow format string, and
         // spelling the width out matches the other AnsiString::Format call sites in
         // the tree (HashCreator, Arc) so nobody has to work out which width "%s"
         // means in a CStdStr<char>.
         line.Format("%I64d %hs\r\n", record.expires, record.key.c_str());
         content += line;
      }

      String directory = FileUtilities::GetFilePath(path);
      if (!FileUtilities::Exists(directory) && !FileUtilities::CreateDirectory(directory))
         return false;

      // Written to a sibling file and renamed over the store rather than written in
      // place. WriteToFile truncates and then writes, so a crash or a full disk
      // half way through an in-place write leaves a truncated store - which Load_
      // correctly refuses to trust, which means the account then gets no auto-replies
      // at all until an administrator notices and deletes the file. The rename is a
      // single operation (FileUtilities::Move is MoveFileEx with
      // MOVEFILE_REPLACE_EXISTING), so the store on disk is always either the
      // complete old one or the complete new one.
      String temporaryPath = path + _T(".tmp");

      if (!FileUtilities::WriteToFile(temporaryPath, content))
      {
         // Leave nothing behind: a stale .tmp is harmless to correctness but would
         // confuse the next person to look in the directory.
         FileUtilities::DeleteFile(temporaryPath);
         return false;
      }

      if (!FileUtilities::Move(temporaryPath, path))
      {
         FileUtilities::DeleteFile(temporaryPath);
         return false;
      }

      return true;
   }

   bool
   SieveVacationTracker::ClaimResponse(const String &accountAddress,
                                       const String &sender,
                                       const String &handle,
                                       __int64 intervalSeconds)
   {
      if (intervalSeconds <= 0)
      {
         // RFC 6131 allows ":seconds 0", which asks for a response to every
         // qualifying message. There is then no window to record and no window to
         // check, so the store is left alone entirely rather than filled with
         // records that are expired the instant they are written.
         return true;
      }

      __int64 window = intervalSeconds > MaxWindowSeconds ? MaxWindowSeconds : intervalSeconds;

      try
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         String path = SieveStorage::GetVacationStorePath(accountAddress);
         __int64 now = CurrentUnixTime();
         AnsiString key = KeyFor_(sender, handle);

         std::vector<Record> records;
         bool prunedAny = false;

         if (!Load_(path, now, records, prunedAny))
         {
            // Fail closed: the store exists and could not be read in full, so we
            // cannot say whether this sender has already been answered. Logged
            // rather than reported as a defect because the situations that produce
            // it - a file locked by a backup or a scanner, a hand-edited store, a
            // truncated file left by an unclean shutdown - are operational, and an
            // ErrorManager report would break every fixture's clean-error-log
            // assertion for something an administrator can fix by deleting a file.
            String message;
            message.Format(_T("Sieve vacation: no auto-reply to %s, the response-tracking store %s could not be read. ")
                           _T("Replying without being able to check it would repeat the reply for every further message ")
                           _T("from this sender. Delete the file to start over."),
                           sender.c_str(), path.c_str());
            LOG_APPLICATION(message);
            return false;
         }

         for (const Record &record : records)
         {
            if (record.key.Compare(key.c_str()) == 0)
            {
               // Still inside the window - Load_ has already discarded anything that
               // expired - so this sender has already been answered. Take the
               // opportunity to write back the pruned set, since this is the only
               // path on a long-lived store that runs often enough to shrink it, and
               // it strictly reduces the record count so it cannot loop. The result
               // is deliberately ignored: it cannot change the answer, which is
               // already "do not send".
               if (prunedAny)
                  Save_(path, records);

               return false;
            }
         }

         if (records.size() >= MaxLiveRecords)
         {
            // The store is full of live records and this sender is not one of them.
            //
            // This is where the earlier version of this class went wrong: it made
            // room by dropping the record whose window expired soonest and sent the
            // reply anyway. That makes the cap meaningless - the sender whose record
            // was dropped is now a sender we have forgotten, and their next message
            // gets a second reply, and so on - and it is precisely the "reply again
            // because no record was found" behaviour the whole design is built to
            // exclude. Refusing costs one auto-reply and cannot cost a loop, so
            // refusing is the answer.
            if (prunedAny)
               Save_(path, records);

            String message;
            message.Format(_T("Sieve vacation: no auto-reply to %s, the response-tracking store %s already holds its ")
                           _T("maximum of %Iu live records. An account with this many distinct correspondents inside one ")
                           _T("suppression window is being written to by something automatic; replies resume as records ")
                           _T("expire."),
                           sender.c_str(), path.c_str(), MaxLiveRecords);
            LOG_APPLICATION(message);
            return false;
         }

         Record fresh;
         fresh.expires = now + window;
         fresh.key = key;
         records.push_back(fresh);

         if (!Save_(path, records))
         {
            // Fail closed, same reasoning. A reply we cannot record is a reply we
            // will send again for the next message from this sender, and again after
            // that: the exact shape of the loop the store exists to prevent.
            String message;
            message.Format(_T("Sieve vacation: no auto-reply to %s, the response-tracking store %s could not be written. ")
                           _T("A reply that cannot be recorded would be repeated for every further message from this sender."),
                           sender.c_str(), path.c_str());
            LOG_APPLICATION(message);
            return false;
         }

         return true;
      }
      catch (...)
      {
         // Same direction as every other failure in here: an unexpected failure must
         // not become permission to send an unrecorded reply. Unlike the read and
         // write failures above this one should not happen at all - every I/O path
         // in here reports by return value - so it is reported rather than logged.
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5897, "SieveVacationTracker::ClaimResponse",
            "An error occurred while consulting the Sieve vacation response-tracking store. No auto-reply was sent.");
         return false;
      }
   }

   void
   SieveVacationTracker::Forget(const String &accountAddress)
   {
      try
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         String path = SieveStorage::GetVacationStorePath(accountAddress);

         if (FileUtilities::Exists(path))
            FileUtilities::DeleteFile(path);

         // A .tmp left behind by an interrupted Save_ is not part of the store, but
         // leaving it here would survive the account's Sieve data being cleared and
         // puzzle the next person to look.
         String temporaryPath = path + _T(".tmp");
         if (FileUtilities::Exists(temporaryPath))
            FileUtilities::DeleteFile(temporaryPath);
      }
      catch (...)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Low, 5898, "SieveVacationTracker::Forget",
            "An error occurred while clearing the Sieve vacation response-tracking store.");
      }
   }
}
