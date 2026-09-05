// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <vector>

namespace HM
{
   /// <summary>
   /// The archive index: one row in hm_archiveindex (schema 6029) per copy the
   /// archiver writes, so the archive can be searched by who, when and what, a
   /// copy can be put on legal hold, and the retention sweep can ask before it
   /// deletes rather than only walk the directory tree.
   ///
   /// The row is written after the file is; a file with no row is still an
   /// archived message (the tree is the archive, the index is the index), and a
   /// row whose file is gone is removed by the sweep when it meets it. Nothing
   /// here reads message bodies: the columns are the envelope and three
   /// headers, which is what an administrator searches by.
   /// </summary>
   class PersistentArchiveIndex
   {
   public:
      enum Direction
      {
         DirectionInbound = 0,     // a copy in Inbound\, from a sender outside this server
         DirectionSent = 1,        // a local sender's Sent- copy
         DirectionReceived = 2     // a local recipient's copy
      };

      struct Entry
      {
         __int64 id;
         String time;
         String domain;
         String mailbox;
         int direction;
         String sender;
         String recipients;
         String subject;
         String messageId;
         String path;
         __int64 size;
         bool hold;
      };

      struct Criteria
      {
         Criteria() : maxRows(200), holdOnly(false) { }

         String domain;         // exact, case-insensitive; empty = any
         String mailbox;        // exact; empty = any
         String sender;         // contains; empty = any
         String recipient;      // contains; empty = any
         String subject;        // contains; empty = any
         String since;          // "YYYY-MM-DD HH:MM:SS"; empty = no lower bound
         String until;          // same; empty = no upper bound
         bool holdOnly;
         int maxRows;           // 1..1000
      };

      // Records one archived copy. Returns the new row id, or 0 when the row
      // could not be written (the file is still archived).
      static __int64 Record(int direction, const String &domain, const String &mailbox, const String &sender,
                            const String &recipients, const String &subject, const String &messageId,
                            const String &path, __int64 size);

      static bool Search(const Criteria &criteria, std::vector<Entry> &entries);
      static bool Get(__int64 id, Entry &entry);
      static bool SetHold(__int64 id, bool hold);

      // The sweep's questions: is this file held, and forget this file.
      static bool IsHeld(const String &path);
      static bool RemoveByPath(const String &path);

      // The address eraser's: forget every copy that names this address.
      static int RemoveByAddress(const String &address);

      // The JSON the COM and REST callers hand out, one shape for both.
      static AnsiString ToJson(const std::vector<Entry> &entries);
      static AnsiString ToJson(const Entry &entry);

   private:
      static bool Read_(std::shared_ptr<DALRecordset> recordset, Entry &entry);
   };
}
