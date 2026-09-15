// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The settings store: hm_inisettings decides, and hMailServer.INI is its cache.
   ///
   ///    Until schema 6042 this file held the opposite fixture. The [Settings] section
   ///    was the store, the table was a mirror kept beside it so that settings would
   ///    at least appear in a backup, and a three-way merge in which THE FILE WON kept
   ///    the two in step. That was the right answer while the file was the thing the
   ///    server read; it is the wrong answer for a product where two nodes have to
   ///    share a configuration, where a Control Panel on another machine has no share
   ///    to the file, and where the honest meaning of "restore my configuration" is
   ///    that the settings come back with the domains and the accounts.
   ///
   ///    So the precedence is inverted, and these tests are the five arms of what
   ///    that means: a stored setting surviving a restart, a file edit named and
   ///    discarded, an override in the file winning and saying so at every start, a
   ///    stored value changed behind the server reaching the file, and the migration
   ///    that takes a file-only value into the store. Each asserts on two things - what the server is actually using,
   ///    and what it SAID it did - because "the right value for the right reason" and
   ///    "a value that happens to be right because nothing ran" look identical from
   ///    the outside.
   ///
   ///    The one thing that is deliberately not asserted here is the table itself. It
   ///    has no COM accessor, and inventing one for a test would be a worse design
   ///    than proving the same fact the way an operator would: take the value out of
   ///    the file, restart, and see whether the setting survived.
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
      ///    line would otherwise leave a row, a line, or an override behind, and the
      ///    next test would start from a state it did not create.
      ///
      ///    The order matters. The override goes first, because while it is in the
      ///    file it shadows everything else and the server announces it at every
      ///    start. The store goes second, which drops the row and the line together -
      ///    the only thing that now returns a setting to its default.
      ///
      ///    A separate name rather than an override of the base fixture's TearDown, so
      ///    that the base one - which is where the crash oracle is checked - still
      ///    runs. NUnit runs the derived one first.
      /// </summary>
      [TearDown]
      public void RemoveTheProbeSetting()
      {
         try
         {
            IniFileSetting.DeleteFileOnly("SettingsOverride", ProbeKey);
            IniFileSetting.Delete(ProbeKey);
            _application.Reinitialize();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // A cleanup failure must not replace the real failure being reported.
         }
         finally
         {
            // These tests provoke the store's standing reports - an edited file, an
            // override in force - on purpose, so they are cleared here rather than
            // left to fail whichever fixture runs next.
            LogHandler.DeleteErrorLog();
         }
      }

      /// <summary>
      ///    Reinitialize is the only thing that re-reads the ini and re-runs the
      ///    reconciliation. Stop()/Start() does not, because IniFileSettings is loaded
      ///    at InitInstance.
      /// </summary>
      private void Reload()
      {
         _application.Reinitialize();
      }

      /// <summary>What the server is using for the probe, in its own words.</summary>
      private string EffectiveValue()
      {
         return _application.Settings.GetIniSetting(ProbeKey);
      }

      /// <summary>
      ///    Changes the stored value behind the server's back, which is what a
      ///    restored backup and a second node writing the shared database both look
      ///    like from here.
      /// </summary>
      private void SetStoredValue(string value)
      {
         _application.Database.ExecuteSQL(
            "update hm_inisettings set inisettingvalue = '" + value +
            "' where inisettingname = '" + ProbeKey + "'");
      }

      /// <summary>
      ///    The error log, or an empty string when there is none. Used where the
      ///    assertion is that a PARTICULAR thing was not said, rather than that
      ///    nothing at all was.
      /// </summary>
      private static string ErrorLog()
      {
         return System.IO.File.Exists(LogHandler.GetErrorLogFileName())
            ? LogHandler.ReadErrorLog()
            : string.Empty;
      }

      [Test]
      [Description("A setting written over COM survives a service restart with hMailServer.INI never touched by hand - " +
                   "which is what 'the database is the settings store' has to mean before anything else.")]
      public void AStoredSettingSurvivesARestart()
      {
         _application.Settings.SetIniSetting(ProbeKey, "stored-over-com");

         RestartServerAndReacquireCom();

         Assert.AreEqual("stored-over-com", _application.Settings.GetIniSetting(ProbeKey),
            "A setting written over COM did not survive a restart of the service.");

         // And it is in the file too, because that is what keeps hMailServer.exe
         // /Register - which reads ServiceAccountName with no database open at all -
         // seeing the right value.
         Assert.AreEqual("stored-over-com", IniFileSetting.Read(ProbeKey),
            "The stored value did not reach hMailServer.INI, so nothing that reads the file directly would see it.");
      }

      [Test]
      [Description("When a setting is in both stores and they disagree, the database's value is used, the file's is " +
                   "named in the error log, and the line is put back.")]
      public void TheDatabaseWinsAndTheIgnoredFileValueIsNamed()
      {
         _application.Settings.SetIniSetting(ProbeKey, "stored");
         Reload();

         // The file edited by hand, exactly as an administrator would have done it
         // while this section was the store.
         IniFileSetting.WriteFileOnly(ProbeKey, "edited-by-hand");
         Assert.AreEqual("edited-by-hand", IniFileSetting.Read(ProbeKey),
            "The direct file write did not take, so this test would prove nothing.");

         Reload();

         Assert.AreEqual("stored", EffectiveValue(),
            "The value edited into hMailServer.INI was applied. The database is the settings store: the file is a cache.");

         // The line is put back, because every reader that goes to the file directly
         // has to keep seeing what the server is using.
         Assert.AreEqual("stored", IniFileSetting.Read(ProbeKey),
            "The file was left holding a value the server is not using, which is worse than either value on its own.");

         // Named, not silently discarded: somebody made a change and it did not
         // happen, and they are entitled to know which one. This also consumes the
         // error so it does not fail a later fixture.
         CustomAsserts.AssertReportedError("edited in hMailServer.INI", ProbeKey);

         // Reported ONCE. After the line has been put back the two agree again, so a
         // second start has nothing to say - and a message repeated at every start
         // for a fault that has been repaired is a message nobody reads. Asserted on
         // the probe's name rather than on the log being empty, because a
         // reinitialize on this bench may legitimately report something else.
         LogHandler.DeleteErrorLog();
         Reload();

         Assert.IsFalse(ErrorLog().Contains(ProbeKey),
            "The ignored file value was reported a second time, after the file had already been put back.");
      }

      [Test]
      [Description("A key in [SettingsOverride] is applied over the stored value and announced in the error log at " +
                   "every start - the door for a database that is unreachable or wrong.")]
      public void AnOverrideInTheFileWinsAndSaysSoEveryTime()
      {
         _application.Settings.SetIniSetting(ProbeKey, "stored");
         Reload();

         Assert.AreEqual("stored", EffectiveValue());

         IniFileSetting.WriteFileOnly("SettingsOverride", ProbeKey, "forced-from-the-file");

         Reload();

         Assert.AreEqual("forced-from-the-file", EffectiveValue(),
            "The [SettingsOverride] section was not applied. It is the only way to run a server whose stored " +
            "configuration cannot be reached or will not let it start.");

         CustomAsserts.AssertReportedError("SettingsOverride", ProbeKey);

         // The [Settings] line is NOT rewritten to the override. The override is the
         // file's own statement, made in its own section; smearing it into the cache
         // would make it impossible to see what the store actually holds.
         Assert.AreEqual("stored", IniFileSetting.Read(ProbeKey),
            "The override was written into the [Settings] cache, so the stored value is no longer visible anywhere.");

         // Every start, not just the first. An override is a standing deviation that
         // no page shows and no backup carries, and the way it stops being forgotten
         // is that it says so every time.
         LogHandler.DeleteErrorLog();
         Reload();

         CustomAsserts.AssertReportedError("SettingsOverride", ProbeKey);
      }

      [Test]
      [Description("A value changed in the database while hMailServer.INI was not - a restored backup, or another " +
                   "node - is written into the file, because everything that reads the file directly depends on it.")]
      public void AStoredValueChangedBehindTheServerIsWrittenIntoTheFile()
      {
         _application.Settings.SetIniSetting(ProbeKey, "agreed");
         Reload();

         SetStoredValue("changed-in-the-database");

         Reload();

         Assert.AreEqual("changed-in-the-database", EffectiveValue(),
            "A value changed in the store was not picked up.");

         // The assertion that matters: hMailServer.exe /Register reads the service
         // account from this file with no database open at all, so a stored value
         // that never reached it would be invisible to the one caller that can never
         // ask the database.
         Assert.AreEqual("changed-in-the-database", IniFileSetting.Read(ProbeKey),
            "A setting changed in the database was not written into hMailServer.INI, so nothing that reads the " +
            "file directly would ever see it.");

         RetryHelper.TryAction(TimeSpan.FromSeconds(10), () =>
            RetryableAssert.StringContains(
               "have been written into hMailServer.INI: " + ProbeKey,
               LogHandler.ReadCurrentDefaultLog()));
      }

      [Test]
      [Description("A value that exists only in hMailServer.INI is taken into the store on the next start - the " +
                   "migration, which is how 238 settings moved without being moved one at a time.")]
      public void AFileOnlyValueIsTakenIntoTheStore()
      {
         // A key with no row: what every [Settings] key looked like on the morning of
         // the upgrade to 6042.
         IniFileSetting.WriteFileOnly(ProbeKey, "was-only-in-the-file");

         Reload();

         Assert.AreEqual("was-only-in-the-file", EffectiveValue());

         RetryHelper.TryAction(TimeSpan.FromSeconds(10), () =>
            RetryableAssert.StringContains(
               "are now stored in the database, which is the settings store",
               LogHandler.ReadCurrentDefaultLog()));

         // The proof that it reached the TABLE rather than merely being read from the
         // file again: take the line out and restart. Before 6042 that returned the
         // setting to its default, and the row was dropped to match. Now the row is
         // the setting and the line is written back from it.
         IniFileSetting.DeleteFileOnly("Settings", ProbeKey);
         Assert.AreEqual(string.Empty, IniFileSetting.Read(ProbeKey));

         Reload();

         Assert.AreEqual("was-only-in-the-file", EffectiveValue(),
            "A value that was adopted into the store did not survive its line being deleted from the file, so it " +
            "was never really in the store.");

         Assert.AreEqual("was-only-in-the-file", IniFileSetting.Read(ProbeKey),
            "The stored value was not written back into hMailServer.INI after the line was deleted.");

         RetryHelper.TryAction(TimeSpan.FromSeconds(10), () =>
            RetryableAssert.StringContains(
               "have been written back into it: " + ProbeKey,
               LogHandler.ReadCurrentDefaultLog()));
      }

      /// <summary>
      ///    Whether hm_inisettings has a row for a name. The table has no COM accessor,
      ///    and the fixture's rule is not to invent one; its unique index on the name
      ///    answers the question instead - an insert of the name succeeds only when
      ///    there is no row, and is taken straight back out.
      /// </summary>
      private bool HasStoredRow(string name)
      {
         try
         {
            _application.Database.ExecuteSQL(
               "insert into hm_inisettings (inisettingname, inisettingvalue, inisettingfilevalue) values ('" + name +
               "', 'row-probe', 'row-probe')");
         }
         catch (System.Runtime.InteropServices.COMException)
         {
            return true;
         }

         _application.Database.ExecuteSQL("delete from hm_inisettings where inisettingname = '" + name + "'");
         return false;
      }

      private const string PepperKey = "PasswordPepper";

      [Test]
      [Description("PasswordPepper is kept in hMailServer.INI and never in the database: set over COM it reaches the file " +
                   "and not hm_inisettings, and a row an earlier build adopted is taken out at the next start with the " +
                   "file keeping the value - because a pepper protects the password hashes only while it is somewhere " +
                   "a copy of the database is not.")]
      public void ThePasswordPepperIsKeptInTheFileAndNotInTheDatabase()
      {
         const string pepper = "zz-ini-mirror-pepper";

         try
         {
            // The probe can say yes: a key that IS stored has a row. Without this the
            // assertions below could pass because the probe never finds anything.
            _settings.SetIniSetting(ProbeKey, "stored");
            Assert.IsTrue(HasStoredRow(ProbeKey), "The row probe did not find the row of a setting just stored, so it proves nothing.");

            _settings.SetIniSetting(PepperKey, pepper);

            Assert.AreEqual(pepper, IniFileSetting.Read(PepperKey), "The pepper set over COM did not reach hMailServer.INI.");
            Assert.IsFalse(HasStoredRow(PepperKey), "The pepper set over COM was stored in hm_inisettings, beside the hashes it protects.");

            Reload();

            Assert.AreEqual(pepper, _settings.GetIniSetting(PepperKey), "The server did not read the pepper from the file after a restart.");
            Assert.IsFalse(HasStoredRow(PepperKey), "A restart put the pepper into hm_inisettings.");
            StringAssert.DoesNotContain(PepperKey, ErrorLog(), "A pepper in the file was reported as an edit the store discarded.");

            // What a server that ran an earlier build of 6042 has: the pepper adopted into
            // a row. The next start must take the row out and leave the file holding it.
            _application.Database.ExecuteSQL(
               "insert into hm_inisettings (inisettingname, inisettingvalue, inisettingfilevalue) values ('" + PepperKey +
               "', '" + pepper + "', '" + pepper + "')");
            Assert.IsTrue(HasStoredRow(PepperKey));

            Reload();

            Assert.IsFalse(HasStoredRow(PepperKey), "The row an earlier build adopted was not taken out at the next start.");
            Assert.AreEqual(pepper, IniFileSetting.Read(PepperKey), "Taking the row out lost the pepper from the file.");
            RetryHelper.TryAction(TimeSpan.FromSeconds(10), () =>
               RetryableAssert.StringContains("kept in hMailServer.INI only and have been taken out of the database", LogHandler.ReadCurrentDefaultLog()));
         }
         finally
         {
            // Removes the line (and any row) through the store, which is how a pepper is
            // returned to none; Reinitialize so no later fixture hashes with it.
            try
            {
               _settings.DeleteIniSetting(PepperKey);
               _application.Reinitialize();
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // The real failure, if there is one, is the one to report.
            }
         }
      }

      [Test]
      [Description("A value with spaces at either end is stored as hMailServer.INI will read it back, trimmed, so that it " +
                   "is not reported as an edit to the file at every start; and a row a build before this one stored " +
                   "with the spaces stops being reported and is trimmed once.")]
      public void AValueWithSpacesAtEitherEndIsStoredAsTheFileReadsIt()
      {
         _settings.SetIniSetting(ProbeKey, "  padded value  ");

         Assert.AreEqual("padded value", EffectiveValue());

         Reload();
         Reload();

         Assert.AreEqual("padded value", EffectiveValue());
         StringAssert.DoesNotContain(ProbeKey, ErrorLog(),
            "A value stored with spaces at either end was reported as an edit to hMailServer.INI.");

         // The row as an earlier build left it: the spaces in the stored value.
         SetStoredValue("  padded value  ");

         Reload();
         Reload();

         Assert.AreEqual("padded value", EffectiveValue());
         StringAssert.DoesNotContain(ProbeKey, ErrorLog(),
            "A stored value with spaces at either end is still reported as an edit at every start.");
      }
   }
}
