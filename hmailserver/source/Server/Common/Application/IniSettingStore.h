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
   /// The [Settings] section of hMailServer.INI, mirrored into the database.
   ///
   /// WHY THIS EXISTS. A setting that lives only in hMailServer.INI has three
   /// problems, and they are one problem: the file exists only on the server.
   /// It cannot be read or written by a Control Panel connected to another host,
   /// it does not appear in hmconfig.ps1's configuration-as-code, and - the one
   /// that loses data - it is in no backup at all. BackupExecuter archives the
   /// database and the message store; it never touches the ini. An operator who
   /// restores a backup onto replacement hardware gets their domains, accounts
   /// and mail back, and none of their server settings.
   ///
   /// WHAT THIS IS NOT. It is not a second source of truth that silently
   /// overrides the file. That would be the worst outcome available: every
   /// Control Panel INI write - and there are a dozen of them - would appear to
   /// succeed, read back correctly, and be discarded on the next start. This
   /// project has spent a great deal of effort removing exactly that shape of
   /// defect, and reintroducing it wholesale would be a poor trade for a backup.
   ///
   /// So the file and the table are kept in step by a three-way merge, and each
   /// row remembers what the file said when they were last agreed:
   ///
   ///   file != filevalue   the file was edited since the last start, by hand or
   ///                       by the Control Panel. The FILE WINS; the row is
   ///                       updated to match.
   ///   value != filevalue  the row was changed while the file was not - a remote
   ///                       or restored change. The ROW WINS, and it is written
   ///                       back into the file so the file keeps telling the
   ///                       truth.
   ///   both changed        a conflict. The file wins, because it is the copy the
   ///                       operator can see, and the key is named in the log
   ///                       rather than resolved silently.
   ///
   /// Writing the value back into the file is what makes everything that reads
   /// the ini directly keep working: the two DAV redirect settings in
   /// WebServicesServer, UseLanguage, hmconfig.ps1, an administrator with a text
   /// editor - and, importantly, hMailServer.exe /Register, which reads the
   /// service account with no database open at all.
   ///
   /// filevalue is only advanced when the file write SUCCEEDS. If the service
   /// account cannot write the ini, the row stays marked as un-synchronised and
   /// the next start tries again, rather than recording a lie and reverting the
   /// change.
   /// </summary>
   class IniSettingStore
   {
   public:

      IniSettingStore();
      ~IniSettingStore();

      /// <summary>
      /// Reads the table, reconciles it against the [Settings] section of the ini,
      /// and returns the values the server should run with. Call once, after the
      /// database is open and its schema version has been accepted.
      ///
      /// False means the table could not be read; the caller should carry on with
      /// the ini alone rather than refuse to start, because a server that will not
      /// boot because a settings mirror is unavailable is worse than one running
      /// on the configuration in front of it.
      /// </summary>
      bool Synchronize(std::map<String, String> &resolvedValues);

      /// <summary>
      /// Writes one key to the table and marks it as agreed with the file. Called
      /// after the server itself changes a [Settings] value, so the change is not
      /// lost from the mirror until the next restart.
      /// </summary>
      bool Save(const String &key, const String &value);

      // ---- backup -----------------------------------------------------------

      bool XMLStore(XNode *pBackupNode);
      bool XMLLoad(XNode *pBackupNode);

      /// <summary>Every row, as name -> (value, filevalue). Public for the backup path.</summary>
      static bool ReadAllRows(std::map<String, std::pair<String, String> > &rows);

      // ---- administrative writes --------------------------------------------
      //
      // The COM surface for these settings, which is what lets a Control Panel on
      // another machine administer them at all. The ordering below is the whole
      // design and is the same as Synchronize's: THE FILE IS WRITTEN FIRST, and the
      // row is only recorded as agreed with it if that write succeeded. Writing the
      // row first and the file second would produce, on a service account that
      // cannot write the ini, a stored value that every direct reader of the file -
      // hmconfig.ps1, the DAV redirects, /Register - would disagree with, and no
      // way to tell from either copy which was right.

      /// <summary>
      /// Sets one [Settings] value from an administrator, in file-then-row order.
      /// False means the FILE could not be written, and nothing has been changed.
      ///
      /// Note what this does NOT do: it does not make the value take effect. Almost
      /// every one of these is latched into a typed member by LoadSettings() at
      /// start-up, and reloading them here would rewrite ~150 members underneath
      /// running sessions. So the value is persisted and applies on the next start,
      /// which is exactly the behaviour an administrator editing the file gets.
      /// </summary>
      static bool WriteSetting(const String &name, const String &value);

      /// <summary>
      /// Removes a key from the file and drops its row, which is how a setting is
      /// returned to its default. False means the file could not be written.
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
      /// Every name currently in the [Settings] section, in the order the file has
      /// them. Read from the FILE rather than the table because the file is the copy
      /// that decides behaviour, and a name present in one and not the other is
      /// exactly the state an administrator needs to see rather than have smoothed
      /// over.
      /// </summary>
      static void ReadSettingNames(std::vector<String> &names);

   private:

      /// <summary>The whole [Settings] section of the ini, as name -> value.</summary>
      static void ReadIniSection_(std::map<String, String> &values);

      static bool InsertRow_(const String &name, const String &value, const String &fileValue);
      static bool UpdateRow_(const String &name, const String &value, const String &fileValue);

      /// <summary>
      /// Drops a row whose key has been removed from the file. Removing a key is how
      /// a setting is returned to its default, so the mirror has to be able to let
      /// go of one - see the comment in Synchronize for what happens when it cannot.
      /// </summary>
      static bool DeleteRow_(const String &name);

      /// <summary>
      /// Writes one key into the ini. Returns false when the write did not take -
      /// which is not fatal, but must stop filevalue being advanced.
      /// </summary>
      static bool WriteIniValue_(const String &name, const String &value);
   };
}
