// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

// XNode is a global-scope TYPEDEF of struct _tagXMLNode, not a class - and not in
// namespace HM. Declaring `class XNode;` inside the namespace makes a distinct
// HM::XNode and changes the signature of every XMLStore/XMLLoad that takes the real
// one; declaring it at global scope makes it a class where it is a typedef. So the
// underlying struct is what gets forward-declared, and the typedef comes with the
// real header where it is needed.
struct _tagXMLNode;
typedef _tagXMLNode XNode;

namespace HM
{
   /// <summary>
   /// hm_inisettings: the store the [Settings] section of hMailServer.INI used to
   /// be. From schema 6042 THE DATABASE IS THE TRUTH and the file is its cache.
   ///
   /// WHY THE STORE MOVED. A setting that lives only in hMailServer.INI has four
   /// problems and they are one problem: the file exists only on the server. It
   /// cannot be read or written by a Control Panel or a Control Deck connected to
   /// another host; two nodes cannot share it, so an active-active pair cannot
   /// share a configuration; a change to it is saved now and applied at the next
   /// start, with no way to publish it to a running server; and - the one that
   /// loses data - it is in no backup at all. BackupExecuter archives the database
   /// and the message store and never touches the ini, so an operator who restores
   /// onto replacement hardware gets their domains, accounts and mail back and
   /// none of their server settings. The project's rule since 15 September 2026,
   /// in .github/CONTRIBUTING.md, is that a setting belongs in the database; this
   /// is the class that makes that true of the 238 keys already in the file.
   ///
   /// WHY THE FILE IS STILL WRITTEN. Because some readers cannot go to the
   /// database, and one of them by definition never can: hMailServer.exe /Register
   /// reads [Settings] ServiceAccountName and ServiceAccountPassword with no
   /// database open at all, because registering the service is what happens before
   /// there is one. So every row is written into the file as well, and the file
   /// remains a complete, readable copy of the configuration - a cache, not a
   /// store. An administrator can still read it; what they can no longer do is
   /// change a setting by editing it.
   ///
   /// THE MERGE, which is no longer three-way. Each row still remembers what the
   /// file held when the two last agreed (inisettingfilevalue), but that is now
   /// used to tell an EDIT from a stale copy rather than to decide who wins:
   ///
   ///   no row                 the file has a key the store has never seen: a
   ///                          fresh install, or the first start after the
   ///                          migration. It is ADOPTED into the table and used.
   ///   file == filevalue      the file is the cache this store last wrote. The
   ///                          row is used; if the file has drifted from it the
   ///                          line is rewritten.
   ///   file != filevalue      somebody EDITED the file. The row is still used -
   ///                          the database is the store - the key is named in the
   ///                          error log, and the line is put back to the stored
   ///                          value so the direct readers stay correct. Reported
   ///                          once, because after that the two agree again.
   ///   row with no key        the line was deleted from the file. That is no
   ///                          longer how a setting is returned to its default -
   ///                          DELETE over COM or REST is - so the line is written
   ///                          back rather than the row dropped.
   ///
   /// THE DOOR. [SettingsOverride] in the same file is applied over everything,
   /// stored or not, and is named in the error log at every start. It exists for
   /// the support engineer whose database is unreachable or whose stored value is
   /// wrong, and it is deliberately a section nobody edits by accident: it is not
   /// written by the server, not offered by any editor, and it announces itself
   /// every time the service starts. See IniFileSettings::LoadDatabaseSettings,
   /// which applies it, because it has to apply even when this table cannot be
   /// read at all.
   ///
   /// filevalue is only advanced when the file write SUCCEEDS. If the service
   /// account cannot write the ini, the row stays marked as un-synchronised and
   /// the next start tries again, rather than recording a lie.
   /// </summary>
   class IniSettingStore
   {
   public:

      IniSettingStore();
      ~IniSettingStore();

      /// <summary>
      /// Reads the table, reconciles the [Settings] section of the ini against it,
      /// and returns the values the server should run with. Call once, after the
      /// database is open and its schema version has been accepted.
      ///
      /// False means the table could not be read; the caller should carry on with
      /// the ini alone rather than refuse to start, because a server that will not
      /// boot because its settings store is momentarily unavailable is worse than
      /// one running on the copy sitting in front of it. That fallback is also the
      /// only reason the file is kept complete.
      /// </summary>
      bool Synchronize(std::map<String, String> &resolvedValues);

      /// <summary>
      /// The [SettingsOverride] section of the ini: the values an administrator has
      /// declared must be used whatever the database holds. Read from the FILE and
      /// never stored, because the case it exists for is a database that cannot be
      /// reached or cannot be trusted. Static and free of any database call, so
      /// that it works in exactly that case.
      /// </summary>
      static void ReadOverrides(std::map<String, String> &values);

      // ---- backup -----------------------------------------------------------

      bool XMLStore(XNode *pBackupNode);
      bool XMLLoad(XNode *pBackupNode);

      /// <summary>Every row, as name -> (value, filevalue). Public for the backup path.</summary>
      static bool ReadAllRows(std::map<String, std::pair<String, String> > &rows);

      // ---- administrative writes --------------------------------------------
      //
      // The COM surface for these settings, which is what lets a Control Panel on
      // another machine administer them at all. The ordering below is the whole
      // design, and it INVERTED when the store moved: THE ROW IS WRITTEN FIRST and
      // the file is brought into line afterwards.
      //
      // It has to be that way round now. File-then-row, which is what this did
      // while the file was the truth, would on a failed row write leave the file
      // holding a value the store does not have - and the next start, seeing a file
      // edited away from its stored value, would correctly discard it. A save that
      // reported success and was silently reverted at the next restart is the exact
      // defect this project keeps removing, so the authoritative write goes first
      // and is the one that decides the answer.

      /// <summary>
      /// Sets one [Settings] value from an administrator, in row-then-file order.
      /// False means the DATABASE could not be written and nothing has been stored.
      ///
      /// True with the file write having failed is a real outcome and is reported
      /// rather than swallowed: the value IS stored and WILL be used, but until the
      /// file can be written the handful of readers that go to it directly - notably
      /// hMailServer.exe /Register, which has no database - keep seeing the old one.
      ///
      /// Note what this does NOT do: it does not make the value take effect in the
      /// running process. Almost every one of these is latched into a typed member
      /// by LoadSettings() at start-up, and reloading them here would rewrite ~150
      /// members underneath running sessions. So the value is persisted and applies
      /// on the next start. Publishing a change to a running server is a roadmap row
      /// of its own (Roadmap2 section 13), and it needs this store first.
      /// </summary>
      static bool WriteSetting(const String &name, const String &value);

      /// <summary>
      /// Removes a setting's key from the file and drops its row, which is now the
      /// ONLY way to return a setting to its default - deleting the line by hand no
      /// longer does it, because the row would simply be written back. False means
      /// the setting is still set.
      ///
      /// The FILE goes first here, which is the opposite of WriteSetting above and
      /// follows from the same rule: leave behind whichever residue the next start
      /// repairs, never the one it mistakes for an instruction. A leftover row is
      /// repaired - Synchronize writes its line back. A leftover line is a key the
      /// table has never seen and is ADOPTED, so dropping the row first and then
      /// failing to remove the line would put the setting straight back at the next
      /// start, after reporting success.
      /// </summary>
      static bool RemoveSetting(const String &name);

      /// <summary>
      /// Whether a name can be stored at all: non-empty, within the 100 character
      /// column, and free of the characters that would make it a different key -
      /// or a different section - when written to an ini file.
      /// </summary>
      static bool IsStorableName(const String &name);

      /// <summary>Whether a value fits the column. 4000 characters, as ReadIniSection_ enforces.</summary>
      static bool IsStorableValue(const String &value);

      /// <summary>
      /// The [Settings] keys the database must never hold, which stay in the file
      /// and nowhere else: today only PasswordPepper.
      ///
      /// The pepper is the one setting whose whole purpose is to be somewhere the
      /// password hashes are not. Crypt::ApplyPepper_ mixes it into every Argon2id
      /// and scrypt hash so that a copy of the database is not enough to attack them;
      /// stored in hm_inisettings it sits in the same database as hm_accounts, and
      /// XMLStore puts it in the same backup archive, which is exactly the copy it
      /// is meant to be missing from. So a file-only key is never adopted, never
      /// served from the store, written only to the file when an administrator sets
      /// it over COM or REST, and any row an earlier start adopted is taken out -
      /// after making sure the file holds the value the server was running on.
      /// </summary>
      static bool IsFileOnlyName(const String &name);

      /// <summary>
      /// Every setting name the server holds: the union of the table and the
      /// [Settings] section, which after a start are the same list.
      ///
      /// The union rather than either alone, and the reason is the two states where
      /// they differ. A row whose line has not been written yet - the file was not
      /// writable at the time - is a setting that exists and is in force, and
      /// leaving it out would hide it from configuration-as-code and from the Deck.
      /// A key in the file with no row is a setting about to be adopted, and hiding
      /// that would make a value an administrator can see in the file unreachable
      /// from every tool. Falls back to the file alone when the table cannot be
      /// read, which is the same fallback Synchronize takes.
      /// </summary>
      static void ReadSettingNames(std::vector<String> &names);

   private:

      /// <summary>One whole section of the ini, as name -> value.</summary>
      static void ReadIniSection_(const String &section, std::map<String, String> &values);

      /// <summary>
      /// Puts the standing note in the file: what the [Settings] section now is,
      /// where the settings actually live, and what [SettingsOverride] is for.
      /// Written as keys of a [SettingsStore] section rather than as comment lines,
      /// because the only portable way this process has to write the file is the
      /// profile API, and it writes keys - the POSIX build implements exactly that
      /// API and nothing else (Common/Util/IniFile.cpp). Rewritten only when it
      /// differs, so a start does not dirty the file for nothing.
      /// </summary>
      static void WriteFileNotice_();

      static bool InsertRow_(const String &name, const String &value, const String &fileValue);
      static bool UpdateRow_(const String &name, const String &value, const String &fileValue);

      /// <summary>
      /// Drops a setting's row, which is what returns it to its default. Called only
      /// from RemoveSetting now: Synchronize used to drop a row whose key had left
      /// the file, and with the database as the store it writes the line back
      /// instead - see the comment there for why that inverted with the precedence.
      /// </summary>
      static bool DeleteRow_(const String &name);

      /// <summary>
      /// Writes one key into a section of the ini. Returns false when the write did
      /// not take - which is not fatal, but must stop filevalue being advanced.
      /// </summary>
      static bool WriteIniValue_(const String &section, const String &name, const String &value);
   };
}
