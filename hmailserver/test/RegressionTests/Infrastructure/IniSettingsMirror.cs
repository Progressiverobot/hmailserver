// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The [Settings] section of hMailServer.INI, mirrored into hm_inisettings.
   ///
   ///    The problem this exists to solve is that a setting living only in the ini is
   ///    in no backup at all: BackupExecuter archives the database and the message
   ///    store and never touches the file, so an operator who restores onto
   ///    replacement hardware gets their domains, accounts and mail back and none of
   ///    their server settings.
   ///
   ///    What makes it more than a copy is that the file has to keep working. A dozen
   ///    Control Panel write sites, hmconfig.ps1, an administrator with a text editor
   ///    and - with no database open at all - hMailServer.exe /Register all read the
   ///    file directly. So the two are kept in step by a three-way merge in which the
   ///    FILE WINS, rather than by the database overriding it, and each row remembers
   ///    what the file said when the two last agreed.
   ///
   ///    The three tests below are the three arms of that merge: the file changed, the
   ///    row changed, and the key was removed from the file.
   ///
   ///    Each asserts on two things - what hMailServer.INI now says, and which arm the
   ///    server logged taking. The file is the observable that matters to everything
   ///    that reads it; the log line is what distinguishes "the right value for the
   ///    right reason" from a value that happens to be right because nothing ran. The
   ///    table itself is not read directly because it has no COM accessor yet, and
   ///    inventing one for a test would be a worse design than reading what the server
   ///    already says it did.
   /// </summary>
   [TestFixture]
   public class IniSettingsMirror : TestFixtureBase
   {
      /// <summary>
      ///    A key no shipped setting uses, so that a failure part-way through cannot
      ///    change how the server behaves. The Zz prefix also keeps it at the end of
      ///    the section when the file is read by eye.
      /// </summary>
      private const string ProbeKey = "ZzIniMirrorProbe";

      /// <summary>
      ///    Not merged into the tests' own cleanup: a test that fails before its last
      ///    line would otherwise leave both a key in the file and a row in the table,
      ///    and the next test would start from a state it did not create. Deleting the
      ///    key and remerging is also exactly the operation that drops the row, so this
      ///    returns both halves to empty.
      ///
      ///    A separate name rather than an override, so that the base fixture's
      ///    TearDown - which is where the crash oracle is checked - still runs. NUnit
      ///    runs the derived one first.
      /// </summary>
      [TearDown]
      public void RemoveTheProbeSetting()
      {
         try
         {
            IniFileSetting.Delete(ProbeKey);
            _application.Reinitialize();
         }
         catch (Exception)
         {
            // A cleanup failure must not replace the real failure being reported.
         }
      }

      /// <summary>
      ///    Reinitialize is the only thing that re-reads the ini and re-runs the merge.
      ///    Stop()/Start() does not, because IniFileSettings is loaded at InitInstance.
      /// </summary>
      private void Remerge()
      {
         _application.Reinitialize();
      }

      private void SetMirroredValue(string value)
      {
         _application.Database.ExecuteSQL(
            "update hm_inisettings set inisettingvalue = '" + value +
            "' where inisettingname = '" + ProbeKey + "'");
      }

      [Test]
      [Description("When a setting has been changed both in hMailServer.INI and in the mirrored row, the file wins and the row is brought into line.")]
      public void TestTheFileWinsWhenBothTheFileAndTheRowHaveChanged()
      {
         IniFileSetting.Write(ProbeKey, "fromfile-first");
         Remerge();

         // Change BOTH, so the merge has a genuine conflict to resolve rather than a
         // one-sided change.
         SetMirroredValue("fromdatabase");
         IniFileSetting.Write(ProbeKey, "fromfile-second");

         Remerge();

         // The file wins. Asserted on the file because the file is what the direct
         // readers use: a merge that resolved this the other way would silently
         // discard every Control Panel ini write on the next restart, which is the
         // defect this design exists to avoid rather than to introduce.
         Assert.AreEqual("fromfile-second", IniFileSetting.Read(ProbeKey),
            "The value in hMailServer.INI was overwritten by the mirrored row. The file must win.");

         // Named in an error rather than resolved in silence: the administrator made
         // two changes and only one of them survived, and they are entitled to know
         // which. This also consumes the error so it does not fail a later fixture.
         CustomAsserts.AssertReportedError("changed both in hMailServer.INI and in the database", ProbeKey);
      }

      [Test]
      [Description("A setting changed in the database while the file was not - a restore, or a remote change - is written back into hMailServer.INI.")]
      public void TestTheRowIsWrittenIntoTheFileWhenOnlyTheRowHasChanged()
      {
         IniFileSetting.Write(ProbeKey, "agreed");
         Remerge();

         // Only the row changes. This is what a restored backup looks like, and what a
         // Control Panel write to the mirror will look like once that exists.
         SetMirroredValue("fromdatabase");

         Remerge();

         // Written back into the file, because a value that reached the database but
         // not the file would be invisible to /Register, to hmconfig.ps1 and to anyone
         // reading the file - and would be reverted the next time anything rewrote the
         // section.
         Assert.AreEqual("fromdatabase", IniFileSetting.Read(ProbeKey),
            "A setting changed in the database was not written back into hMailServer.INI, so nothing that reads the file directly would ever see it.");

         RetryHelper.TryAction(TimeSpan.FromSeconds(10), () =>
            RetryableAssert.StringContains(
               "have been written into hMailServer.INI: " + ProbeKey,
               LogHandler.ReadCurrentDefaultLog()));
      }

      [Test]
      [Description("Removing a key from hMailServer.INI drops the mirrored row, instead of resurrecting the setting from the database on the next start.")]
      public void TestRemovingTheKeyFromTheFileDropsTheMirroredRow()
      {
         IniFileSetting.Write(ProbeKey, "willberemoved");
         Remerge();

         // Removing a key is how a setting is returned to its default - for an
         // administrator with a text editor, and for every test in this suite that
         // cleans up after itself.
         IniFileSetting.Delete(ProbeKey);

         Remerge();

         // The first version of the merge resurrected the row here, on the reasoning
         // that the row was the last copy of the value. The regression suite disproved
         // that in the loudest way available: a test that enabled ACME, restarted, then
         // cleaned up by deleting its keys had them written straight back, so
         // AcmeEnabled=1 survived into every following test and 394 of them failed.
         Assert.AreEqual(string.Empty, IniFileSetting.Read(ProbeKey),
            "A setting deleted from hMailServer.INI was written back from the database. A mirror that will not let go of a value is worse than no mirror.");

         // The file being empty is not on its own proof that the row went with it - a
         // mirror that did nothing at all would also leave the file empty. This line is
         // what says the row was dropped rather than merely not written back.
         RetryHelper.TryAction(TimeSpan.FromSeconds(10), () =>
            RetryableAssert.StringContains(
               "have been dropped from the database to match, so they are back at their defaults: " + ProbeKey,
               LogHandler.ReadCurrentDefaultLog()));
      }
   }
}
