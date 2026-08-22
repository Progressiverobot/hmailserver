// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // RFC 5464 (METADATA): the annotation store behind GETMETADATA and
   // SETMETADATA, over the hm_imap_metadata table schema 6014 added.
   //
   // Scoping is two ids. A mailbox entry carries the folder's id; a server
   // entry (the "" mailbox) carries folder id 0. A /private entry carries the
   // owning account's id; a /shared entry carries account id 0, which is what
   // makes it one row visible to every session with access to the folder.
   class IMAPMetadataStore
   {
   public:
      struct Entry
      {
         String name;
         String value;
      };

      // The longest value SETMETADATA accepts, matching the column. The refusal
      // carries the RFC's MAXSIZE response code so a client knows the limit.
      static const int MaxValueLength = 2048;

      static bool Get(__int64 accountId, __int64 folderId, const String &entryName, String &value, bool &found);

      // Entries whose name is entryName itself or a descendant of it, to the
      // given depth: 0 = the entry alone, 1 = plus immediate children,
      // -1 = plus all descendants (the RFC's "infinity").
      static std::vector<Entry> List(__int64 accountId, __int64 folderId, const String &entryName, int depth);

      static bool Set(__int64 accountId, __int64 folderId, const String &entryName, const String &value);
      static bool Remove(__int64 accountId, __int64 folderId, const String &entryName);
   };
}
