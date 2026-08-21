// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   /*
      Removes the traces of an email address that ordinary account deletion
      deliberately leaves behind.

      PersistentAccount::DeleteObject is thorough about what the ACCOUNT owns -
      messages, rules, app passwords, the mailbox directory, the Sieve tree -
      and blind, on purpose, to the places where the ADDRESS appears as data in
      somebody else's records: archive copies, quarantine rows, message-trace
      rows, greylisting triplets, distribution-list memberships and aliases.
      Deleting those as a side effect of removing an account would be wrong
      twice over - an archive can be under a legal hold that outranks the
      deletion, and a list membership is the list owner's record as much as the
      member's.

      So erasure is its own operation, invoked deliberately (COM:
      Utilities.EraseAddressTraces), for the case where an operator has been
      asked to remove a person rather than close a mailbox. It works on an
      ADDRESS, not an account, because the request usually arrives after the
      account is already gone.

      What it deliberately does NOT touch, and why:

      * Protocol logs and the AWStats journal. Addresses inside log lines are
        bounded by log retention rather than edited; a log that can be
        selectively rewritten is worthless as a record, which is a worse
        property than briefly retaining an address the retention window will
        remove anyway.
      * The archive's Inbound folder. Messages from non-local senders land
        there under the message's own filename, attributed to nobody; finding
        one address's messages in it would mean parsing every message in the
        store. The per-user archive tree is the half that is keyed by address,
        and it is the half that is erased.
      * Backups. A backup archive is outside the running system's reach by
        design.
   */
   class AddressTraceEraser
   {
   public:

      // Erases every reachable trace of the address. Returns false only when a
      // step FAILED - "nothing found" is success with removedCount 0. The count
      // is rows plus quarantine entries plus one per removed archive tree,
      // because the caller's honest report is "how many things were removed",
      // not which tables they were rows in.
      static bool Erase(const String &address, bool includeArchive, int &removedCount);

   private:

      // Counts, then deletes, the rows matching one or two address columns.
      // Returns -1 on failure. Two statements rather than one because the DAL
      // reports success, not affected rows, and an erasure whose report says
      // "done" without saying how much is indistinguishable from one that did
      // nothing.
      static int DeleteAddressRows_(const AnsiString &table, const AnsiString &firstColumn, const AnsiString &secondColumn, const String &address);

      static int EraseQuarantine_(const String &address, bool &failed);
      static int EraseArchive_(const String &address, bool &failed);
   };
}
