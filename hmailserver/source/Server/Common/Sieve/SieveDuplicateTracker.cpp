// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "SieveDuplicateTracker.h"
#include "SieveStorage.h"

#include "../Util/FileUtilities.h"
#include "../Util/Hashing/HashCreator.h"
#include "../Util/Charset.h"

#include <time.h>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The header names this store's format, so Load_ can refuse a file some
      // other program wrote. Distinct from the vacation store's header on purpose:
      // pointing one class at the other's file must fail loudly, not merge them.
      const char *StoreHeaderLine = "# hMailServer Sieve duplicate tracking, format 1";

      // Live records per account. Larger than the vacation store's cap because a
      // duplicate window sees every delivered message, not just auto-replied
      // senders - but still bounded, and the bound fails OPEN (see the header):
      // a full store answers "not a duplicate" and records nothing.
      const size_t MaxLiveRecords = 10000;

      // ~80 bytes per record line plus the header; the cap and the byte limit
      // agree with room to spare. A file bigger than this was not written here.
      const long MaxStoreBytes = 1024 * 1024;

      // A century, as in the vacation tracker: defence against arithmetic
      // overflow producing a negative expiry that would read as already-expired.
      const __int64 MaxWindowSeconds = 36500LL * 86400LL;

      __int64 CurrentUnixTime()
      {
         return static_cast<__int64>(::time(nullptr));
      }

      // SHA-256 hex of the UTF-8 form, exactly as the vacation tracker hashes:
      // Message-IDs and handles are attacker-influenced text and must not be able
      // to forge lines in this file or collide across code pages.
      AnsiString HexDigest(const String &input)
      {
         HashCreator hasher(HashCreator::SHA256);
         return hasher.GenerateHashNoSalt(Charset::ToMultiByte(input, "utf-8"), HashCreator::hex);
      }

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

   SieveDuplicateTracker::SieveDuplicateTracker()
   {
   }

   size_t
   SieveDuplicateTracker::MaxTrackedMessages()
   {
      return MaxLiveRecords;
   }

   bool
   SieveDuplicateTracker::IsWellFormedKey_(const AnsiString &key)
   {
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
   SieveDuplicateTracker::KeyFor_(const String &identifier, const String &handle)
   {
      // The identifier arrives already source-tagged by the evaluator, so a
      // ":uniqueid" value and a Message-ID of the same text are different keys.
      // '\n' as the separator is safe for the same reason as in the vacation
      // tracker: the pre-image is hashed, never written.
      return HexDigest(identifier + _T("\n") + handle);
   }

   bool
   SieveDuplicateTracker::Load_(const String &path, __int64 now, std::vector<Record> &records, bool &prunedAny)
   {
      records.clear();
      prunedAny = false;

      if (!FileUtilities::Exists(path))
      {
         // The one absence that is proof: the file is created by the first
         // recorded message, so no file means nothing has been recorded.
         return true;
      }

      if (FileUtilities::FileSize(path) > MaxStoreBytes)
         return false;

      AnsiString content = FileUtilities::ReadCompleteTextFile(path);

      if (content.IsEmpty())
      {
         // Exists but yielded nothing: a failed read or an interrupted write. A
         // store this class wrote always has at least its header line.
         return false;
      }

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
            return false;

         int space = line.Find(' ');
         if (space <= 0)
            return false;

         AnsiString expiryText = line.Mid(0, space);
         AnsiString key = line.Mid(space + 1);

         if (!IsAsciiInt64(expiryText) || !IsWellFormedKey_(key))
            return false;

         Record record;
         record.expires = _atoi64(expiryText.c_str());
         record.key = key;

         if (record.expires <= now)
         {
            prunedAny = true;
            continue;
         }

         records.push_back(record);
      }

      return true;
   }

   bool
   SieveDuplicateTracker::Save_(const String &path, const std::vector<Record> &records)
   {
      if (records.size() > MaxLiveRecords)
         return false;

      AnsiString content = StoreHeaderLine;
      content += "\r\n";

      for (const Record &record : records)
      {
         AnsiString line;
         line.Format("%I64d %hs\r\n", record.expires, record.key.c_str());
         content += line;
      }

      String directory = FileUtilities::GetFilePath(path);
      if (!FileUtilities::Exists(directory) && !FileUtilities::CreateDirectory(directory))
         return false;

      // Sibling-write-then-rename, as in the vacation tracker: the store on disk
      // is always either the complete old file or the complete new one.
      String temporaryPath = path + _T(".tmp");

      if (!FileUtilities::WriteToFile(temporaryPath, content))
      {
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
   SieveDuplicateTracker::CheckAndRecord(const String &accountAddress,
                                         const String &identifier,
                                         const String &handle,
                                         __int64 windowSeconds,
                                         bool refreshOnSeen)
   {
      if (identifier.IsEmpty())
      {
         // RFC 7352 3: no identifier - a message without a Message-ID under the
         // default form - is never a duplicate and is not tracked.
         return false;
      }

      __int64 window = windowSeconds;
      if (window < 1)
         window = 1;
      if (window > MaxWindowSeconds)
         window = MaxWindowSeconds;

      try
      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         String path = SieveStorage::GetDuplicateStorePath(accountAddress);
         __int64 now = CurrentUnixTime();
         AnsiString key = KeyFor_(identifier, handle);

         std::vector<Record> records;
         bool prunedAny = false;

         if (!Load_(path, now, records, prunedAny))
         {
            // FAIL OPEN, the opposite of the vacation tracker, and the whole
            // reason this is a separate class: the common script discards on
            // "duplicate", so a store we cannot read must answer "not a
            // duplicate" - a wrong "new" delivers a copy twice, a wrong
            // "duplicate" destroys a legitimate message. Nothing is recorded
            // either; writing next to a store we do not understand could destroy
            // the record that makes some future answer honest.
            String message;
            message.Format(_T("Sieve duplicate: the seen-store %s could not be read; the test answered ")
                           _T("\"not a duplicate\" without checking. Duplicate detection for this account is ")
                           _T("suspended until the file is repaired or deleted."),
                           path.c_str());
            LOG_APPLICATION(message);
            return false;
         }

         for (Record &record : records)
         {
            if (record.key.Compare(key.c_str()) == 0)
            {
               // A proven duplicate. Under ":last" the window is measured from
               // this occurrence, so the expiry moves out; the write-back also
               // persists any pruning. A failed write cannot change the answer,
               // which is already "duplicate".
               if (refreshOnSeen)
               {
                  record.expires = now + window;
                  Save_(path, records);
               }
               else if (prunedAny)
               {
                  Save_(path, records);
               }

               return true;
            }
         }

         if (records.size() >= MaxLiveRecords)
         {
            // Full of live records. Fail open: answer "not a duplicate" and do
            // NOT evict - a sender who controls their own Message-IDs could
            // otherwise roll everyone else's records out of the window. The cost
            // is missed detection until records expire, never lost mail.
            if (prunedAny)
               Save_(path, records);

            String message;
            message.Format(_T("Sieve duplicate: the seen-store for this account holds %d live records, its ")
                           _T("maximum, so this message was answered \"not a duplicate\" and not recorded."),
                           static_cast<int>(MaxLiveRecords));
            LOG_APPLICATION(message);
            return false;
         }

         Record record;
         record.expires = now + window;
         record.key = key;
         records.push_back(record);

         // A failed write means the message goes unrecorded - its own future
         // duplicate will be missed, which is the open direction's honest price.
         Save_(path, records);

         return false;
      }
      catch (...)
      {
         // Whatever happened, "not a duplicate" is the answer that cannot
         // destroy mail.
         return false;
      }
   }
}
