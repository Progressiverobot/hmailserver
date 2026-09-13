// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The account's address book, hm_contacts (schema 6032): one row per account
// and address, with a name, a source (0 = added by the user, 1 = collected
// from a message the account sent) and a creation time. Two surfaces read and
// write it - the webmail's /api/v1/me/contacts routes (RestApiContacts.cpp)
// and the CardDAV address book (CardDavServer.cpp) - and this is the one place
// the SQL and the rule for what counts as an address live, so that a contact
// either surface writes is one the other accepts.

#pragma once

#include <vector>

namespace HM
{
   struct ContactRecord
   {
      ContactRecord() : id(0), source(0) { }

      __int64 id;
      String name;
      String address;   // lower-cased
      int source;
      String created;   // hMailServer system date, YYYY-MM-DD HH:MM:SS

      // What CardDAV keeps beside the name and address (schema 6040): the
      // resource name a client created the contact under - empty for a row the
      // webmail made, which CardDAV serves as <id>.vcf - the card's UID, and the
      // card as the client sent it, empty for a row that never came from a
      // client, for which CardDAV makes a card from the name and address.
      String uri;
      String uid;
      String vcard;
   };

   class ContactStore
   {
   public:
      static const int SourceManual = 0;
      static const int SourceCollected = 1;

      // Every contact of the account, by name then address. False when the
      // table could not be read.
      static bool List(__int64 accountId, std::vector<ContactRecord> &contacts);

      // One contact, which must be the account's own: another account's id is
      // simply not found, so the ids of one address book say nothing about
      // another.
      static bool Get(__int64 accountId, __int64 id, ContactRecord &contact);

      // Whether the account already has this address, and its row id if so.
      static bool FindByAddress(__int64 accountId, const String &address, __int64 &id);

      static bool Insert(__int64 accountId, const String &name, const String &address, int source, ContactRecord &inserted);

      // A new name and address for a contact. A card stored for it follows:
      // its FN, N and preferred EMAIL become the name and address, and the
      // rest of the card - the properties this store has no columns for - stays
      // as the client sent it.
      static bool Update(__int64 accountId, __int64 id, const String &name, const String &address);

      // The contact a CardDAV client created under this resource name, if any.
      static bool FindByUri(__int64 accountId, const String &uri, ContactRecord &contact);

      // A contact from a CardDAV client: the card is kept as sent, under the
      // client's resource name and UID; and a card sent for an existing contact,
      // whose name and address it also carries.
      static bool InsertCard(__int64 accountId, const String &name, const String &address, const String &uri,
                             const String &uid, const String &vcard, ContactRecord &inserted);
      static bool UpdateCard(__int64 accountId, __int64 id, const String &name, const String &address,
                             const String &uid, const String &vcard);

      // False when there was no such contact of this account.
      static bool Delete(__int64 accountId, __int64 id);

      // One address, with a local part and a domain, no whitespace or line
      // breaks, and short enough for the column.
      static bool IsValidAddress(const String &address);

      // "Name <address>", "<address>" or "address" -> the name (may be empty)
      // and the address, lower-cased, without the brackets.
      static void SplitEntry(const String &entry, String &name, String &address);

      // The rule the two surfaces share for a name: trimmed, at most this long.
      static const int MaximumNameLength = 255;
   };
}
