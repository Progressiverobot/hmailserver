// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The [Settings] section of hMailServer.INI, administered over COM.
   ///
   ///    This is the half of the INI migration that makes those settings remotely
   ///    administrable. Storing them in the database closed the backup gap; without
   ///    these four members a Control Panel connected from another machine still had
   ///    to open a file that only exists on the server, which is why its own
   ///    log-folder card had to say "not readable from this machine".
   ///
   ///    What the tests below pin is not "a value round-trips". It is the four things
   ///    that make this safe to hand an administrator:
   ///
   ///      - a write reaches the FILE, not just the mirror, because /Register,
   ///        hmconfig.ps1 and the DAV redirects all read the file directly and a
   ///        value that existed only in the database would be invisible to them;
   ///      - deleting a key is not the same as writing an empty one, because an
   ///        absent key falls back to the caller's default while "Key=" reads as 0
   ///        through GetPrivateProfileInt;
   ///      - a name or value that could not survive a round trip through an ini file
   ///        is REFUSED rather than written and silently mangled into a different
   ///        setting;
   ///      - and the enumeration is the file's own contents, which is what lets
   ///        configuration-as-code see these settings at all.
   /// </summary>
   [TestFixture]
   public class IniSettingsOverCom : TestFixtureBase
   {
      /// <summary>
      ///    A key no shipped setting uses, so a failure part-way through cannot change
      ///    how the server behaves.
      /// </summary>
      private const string ProbeKey = "ZzComSettingsProbe";

      [TearDown]
      public void RemoveTheProbeSetting()
      {
         try
         {
            _application.Settings.DeleteIniSetting(ProbeKey);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // A cleanup failure must not replace the real failure being reported.
         }
      }

      [Test]
      [Description("A value written over COM reaches hMailServer.INI itself, not only the database mirror.")]
      public void AValueWrittenOverComReachesTheFile()
      {
         _application.Settings.SetIniSetting(ProbeKey, "written-over-com");

         Assert.AreEqual("written-over-com", _application.Settings.GetIniSetting(ProbeKey),
            "The value did not read back over COM.");

         // The assertion that matters. Everything that reads the ini directly -
         // hMailServer.exe /Register with no database open at all, hmconfig.ps1, the
         // two DAV redirects in WebServicesServer - would never see a value that had
         // only reached the database.
         Assert.AreEqual("written-over-com", IniFileSetting.Read(ProbeKey),
            "The value reached the database but not hMailServer.INI, so nothing that reads the file directly would see it.");
      }

      [Test]
      [Description("Deleting a setting removes the key from the file rather than leaving it present and empty.")]
      public void DeletingASettingRemovesTheKeyRatherThanEmptyingIt()
      {
         _application.Settings.SetIniSetting(ProbeKey, "willberemoved");
         Assert.AreEqual("willberemoved", IniFileSetting.Read(ProbeKey));

         _application.Settings.DeleteIniSetting(ProbeKey);

         Assert.AreEqual(string.Empty, _application.Settings.GetIniSetting(ProbeKey));

         // "Key=" and no key at all are different things: GetPrivateProfileInt reads
         // an empty value as 0 rather than falling back to the caller's default, so a
         // delete that left the line behind would silently set numeric settings to
         // zero. Checked through the enumeration, which lists the names the file
         // actually holds, because reading a value cannot tell the two apart.
         string[] names = SettingNames();

         Assert.IsFalse(names.Contains(ProbeKey),
            "The key is still listed in the [Settings] section after being deleted, so it was emptied rather than removed.");
      }

      [Test]
      [Description("A name or value that could not survive a round trip through an ini file is refused, not written.")]
      public void UnstorableNamesAndValuesAreRefused()
      {
         // Each of these would come back as a different key, or as two settings, or
         // would not fit the column - so writing it would be a change the
         // administrator did not ask for.
         // COMException specifically, not Exception: a refusal has to reach the caller
         // as a COM error with a message an administrator can act on, and asserting
         // the base type would pass just as happily on a NullReferenceException
         // escaping the server.
         foreach (string badName in new[] { "", "has=equals", "has[bracket", "has]bracket", " leadingspace", new string('n', 101) })
         {
            Assert.Throws<COMException>(() => _application.Settings.SetIniSetting(badName, "x"),
               "The name '" + badName + "' was accepted; it cannot be stored and read back as itself.");
         }

         Assert.Throws<COMException>(() => _application.Settings.SetIniSetting(ProbeKey, new string('v', 4001)),
            "A value longer than the 4000 character column was accepted.");

         // A refused write must change nothing at all.
         Assert.AreEqual(string.Empty, IniFileSetting.Read(ProbeKey));
      }

      [Test]
      [Description("The setting names can be enumerated, which is what lets configuration-as-code see these settings.")]
      public void TheSettingNamesCanBeEnumerated()
      {
         string[] before = SettingNames();

         Assert.Greater(before.Length, 10,
            "The [Settings] section should hold dozens of names; this looks like the enumeration is not reading the file.");

         Assert.IsFalse(before.Contains(ProbeKey));

         _application.Settings.SetIniSetting(ProbeKey, "1");

         string[] after = SettingNames();

         Assert.IsTrue(after.Contains(ProbeKey), "A newly written setting did not appear in the enumeration.");
         Assert.AreEqual(before.Length + 1, after.Length, "Exactly one name should have been added.");
      }

      [Test]
      [Description("Reading a setting that is not present returns an empty string rather than failing.")]
      public void AnAbsentSettingReadsAsEmpty()
      {
         // Absent and empty are indistinguishable to every reader of an ini file, so
         // this is the honest answer rather than an error - and it is what lets a
         // caller ask about a setting without knowing whether it has been set.
         Assert.AreEqual(string.Empty, _application.Settings.GetIniSetting(ProbeKey));
      }

      /// <summary>
      ///    The [Settings] names, as the server reports them: one per line, because a
      ///    name cannot contain a line break - the server refuses one.
      /// </summary>
      private string[] SettingNames()
      {
         string raw = _application.Settings.IniSettingNames ?? "";

         return raw
            .Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .ToArray();
      }
   }
}
