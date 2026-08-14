// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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
