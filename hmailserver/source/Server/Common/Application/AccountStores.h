// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <map>
#include <vector>

namespace HM
{
   class Account;

   // THE ACCOUNT'S OTHER STORES, IN THE BACKUP AND IN THE RESTORE
   //
   // An account is not only its row in hm_accounts, its folders and its mail. Since
   // 6.3 it is also an address book, the webmail's remembered choices, the messages
   // it has put off sending, the large files it has sent as links, its S/MIME
   // certificates and keys, its calendar, and the history that stops it reusing a
   // password. Each of those arrived as a table of its own beside the subsystem that
   // wanted it, and none of them arrived in the backup - because the backup writes
   // what the business objects' XMLStore methods know how to write, and these have
   // no business object.
   //
   // That is not a gap, it is data loss. Every one of these tables carries a foreign
   // key to hm_accounts with ON DELETE CASCADE, and a restore calls
   // Domains::DeleteAll() before it puts anything back - so restoring an archive
   // taken before this change DELETED the address book, the settings, the scheduled
   // messages, the files, the keys and the calendar, and the archive it restored
   // from had never held them. Nothing failed loudly when each store was added,
   // because nothing was watching.
   //
   // WHAT THIS IS
   //
   // One table of declarations - stores_ and columns_ in the .cpp - naming every
   // table that holds one account's data, every column of it, and what each column
   // means. The backup reads those tables through that declaration; the restore
   // writes them back through the same one. Adding a store is a handful of lines in
   // one array rather than a reader, a writer and a hope.
   //
   // It is checked rather than trusted: build/check-account-stores.py reads the
   // schema's CREATE TABLE statements and this file's declarations, and fails the
   // build when a table that cascades from hm_accounts - or any table with an
   // account-id column - is not accounted for here, or when a column of one is.
   // That check is the part that stops this defect coming back, since what went
   // wrong the first time was a feature added beside a subsystem that enumerates
   // what it knows.
   //
   // WHAT IT IS NOT
   //
   // It is not a general table copier. Two of the columns it carries are references
   // to rows whose identities a restore reassigns - hm_scheduled names a message and
   // a folder - and writing those numbers into the archive and back out would point
   // a scheduled send at whatever message happened to be given that id afterwards.
   // Those are carried as what survives a restore instead: the folder's path and the
   // message's UID. A reference that cannot be resolved on the way back in is
   // dropped, counted and named in the backup log, which is the only safe answer.
   //
   // THE ARCHIVE'S SHAPE
   //
   //   <Account Name="...">
   //     <AccountStores>
   //       <Store Name="hm_contacts">
   //         <Row contactname="Ada" contactaddress="ada@example.test" .../>
   //       </Store>
   //       <Store Name="hm_calendars">
   //         <Row calendarname="calendar" ...>
   //           <Store Name="hm_calendarobjects">
   //             <Row objecturi="a.ics" objectdata.base64="..."/>
   //           </Store>
   //         </Row>
   //       </Store>
   //       <Store Name="hm_scheduled">
   //         <Row schedaction="0" schedat="2026-09-16 09:00:00" schedcreated="...">
   //           <Ref Column="schedmessageid" UID="7"><Path Name="Drafts"/></Ref>
   //           <Ref Column="schedfolderid"><Path Name="INBOX"/></Ref>
   //         </Row>
   //       </Store>
   //     </AccountStores>
   //   </Account>
   //
   // A value is written as an attribute verbatim when every character of it is safe
   // in one, and as base64 of its UTF-8 under the attribute name with ".base64"
   // appended when it is not. Most rows are therefore legible in the index the way
   // the rest of it is, and a vCard, an iCalendar object or a PEM block - text that
   // carries line breaks, and that a person can put anything at all into - cannot
   // make the archive unreadable. The reader takes whichever of the two forms it
   // finds, so the choice is the writer's alone and needs no flag anywhere.
   //
   // VERSIONS, BOTH WAYS
   //
   // An older archive has no <AccountStores> element. Nothing is restored for these
   // tables, which is exactly what restoring that archive did before this change,
   // and the restore does not complain about it.
   //
   // A newer archive is refused before it reaches here: BackupRestorer::Prepare
   // compares the version the archive records against the one running and stops a
   // restore that would silently discard what this build has no column for. What is
   // left is an archive of this version naming a store this build does not have -
   // reachable only by hand-editing one, or from a build whose version string cannot
   // be parsed - and those rows are counted, named in the backup log and left alone.
   // Refusing the whole restore over a table nobody can put back would cost an
   // administrator their mail to protect something they cannot use.
   //
   // WHILE THE SERVER IS RUNNING
   //
   // A backup runs on a live server. Each store is read in one query whose rows are
   // taken into memory and then written, so nothing is held across the archive, and
   // a row deleted while the backup runs is either in that result or is not. The one
   // per-row lookup - the message a scheduled send names - can find that the message
   // has gone, and that is treated as mail flow rather than as a failure: the row is
   // skipped and counted. A read that did not RUN is a different thing entirely, and
   // is reported as a failure, because a store that silently reads as empty is how
   // the archive would come to say an account has no address book.
   class AccountStores
   {
   public:

      // What a column is, which is what decides how it crosses the archive.
      enum ColumnRole
      {
         // The table's own primary key. Never written and never restored: the
         // database assigns a new one, and every archive that carried old ones
         // would be a set of numbers that mean nothing on the machine reading it.
         RoleIdentity,

         // The account this row belongs to. Never written either - it is the
         // account the row is nested under - and always set from the account being
         // restored, so an archive cannot name somebody else's account.
         RoleAccount,

         // The owning row of the parent store (hm_calendarobjects.objectcalendarid).
         // Set from the identity the parent insert was given.
         RoleParent,

         // Text. Written verbatim, or base64 when it holds anything that would not
         // survive an XML attribute.
         RoleText,

         // A whole number: a flag, a count, a size, a Unix time.
         RoleNumber,

         // A datetime column, carried as the system date string the rest of the
         // server uses ("YYYY-MM-DD HH:MM:SS") and written back through
         // SQLStatement::AddColumnDate, so no backend is asked to read another's
         // idea of a date.
         RoleTimestamp,

         // A message id. Carried as the message's folder path and UID, because the
         // id itself is reassigned by the restore.
         RoleMessage,

         // A folder id. Carried as the folder's path, for the same reason.
         RoleFolder
      };

      struct Column
      {
         const wchar_t *table;
         const wchar_t *name;
         ColumnRole role;
      };

      struct Store
      {
         const wchar_t *table;

         // The store whose rows own these, or 0 for a store owned by the account
         // itself. A child store is read and written inside its parent's row.
         const wchar_t *parent;
      };

      // A per-account table this file does not carry, and the reason. Nothing reads
      // it at run time; it exists so that the decision is written down where the
      // check can see it, and so that a table in neither list fails the build.
      struct Elsewhere
      {
         const wchar_t *table;
         const wchar_t *reason;
      };

      // Writes every one of the account's stores under accountNode. False when a
      // store could not be READ - which fails the backup, rather than producing an
      // archive that quietly says the account has nothing.
      static bool XMLStore(Account &account, XNode *accountNode);

      // Puts them back. The account must already have its id, and its folders must
      // already have been restored if the archive carries anything that references
      // one. False when a row could not be written.
      static bool XMLLoad(Account &account, XNode *accountNode);

      // Every <Row> the tree holds, per store table. Used by the verified restore to
      // hold the archive it has just read back to what this run wrote into it.
      static void CountRows(XNode *node, std::map<String, __int64> &counts);

      // The declarations, for anything that needs to walk them.
      static const Store *Stores(int &count);
      static const Column *Columns(int &count);
      static const Elsewhere *Handled(int &count);

   private:

      static const Store stores_[];
      static const Column columns_[];
      static const Elsewhere elsewhere_[];

      static const int store_count_;
      static const int column_count_;
      static const int elsewhere_count_;
   };
}
