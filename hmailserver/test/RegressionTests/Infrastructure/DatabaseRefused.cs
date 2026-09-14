// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    A database the server opened and refused - its version behind this
   ///    build's (HM5011) - stays open for DBUpdater, and DBUpdater's path over COM
   ///    has to stay open with it: the versions, the administrator's
   ///    authentication, the script directory (from hMailServer.ini, not the
   ///    configuration the server never loaded), and Database.ExecuteSQL. Until
   ///    6.3.3 the settings object was refused whole on such a database, so the
   ///    script directory was never answered and every upgrade from an older
   ///    schema stopped before its first script (issue #263, an upgrade from
   ///    5.6.8's 5601 to 6.3.2).
   /// </summary>
   [TestFixture]
   public class DatabaseRefused : TestFixtureBase
   {
      [Test]
      [Description("On a database the server refused, DBUpdater's path over COM stays open: the versions are read, the administrator " +
                   "authenticates, the script directory is answered, every other setting is refused with the reason rather than a crash, " +
                   "and the upgrade's last statement and a reinitialise bring the server back")]
      public void TheUpdatersPathStaysOpenOnARefusedDatabase()
      {
         int required = _application.Database.RequiredVersion;
         Assert.AreEqual(required, _application.Database.CurrentVersion, "The bench must start at the required version.");

         _application.Database.ExecuteSQL("update hm_dbversion set value = " + (required - 1));
         try
         {
            // The server re-reads the version and refuses the database - the
            // reinitialise itself reports that, as an upgrade's first symptom.
            try { _application.Reinitialize(); } catch (COMException) { }

            // A fresh object, as DBUpdater makes one.
            var updater = new Application();
            try
            {
               updater.Connect();
               Assert.Fail("Connect must report the refused database.");
            }
            catch (COMException ex)
            {
               StringAssert.Contains("too old", ex.Message);
            }

            Assert.AreEqual(required - 1, updater.Database.CurrentVersion, "The current version is readable.");
            Assert.AreEqual(required, updater.Database.RequiredVersion, "The required version is readable.");
            Assert.IsNotNull(updater.Authenticate("Administrator", "testar"), "The administrator authenticates from hMailServer.ini.");

            string scripts = updater.Settings.Directories.DBScriptDirectory;
            Assert.IsTrue(!string.IsNullOrEmpty(scripts) && Directory.Exists(scripts),
               "The script directory is answered from hMailServer.ini: " + scripts);

            var refusal = Assert.Throws<COMException>(() => { long unused = updater.Settings.MaxMessageSize; });
            StringAssert.Contains("has not loaded its configuration", refusal.Message,
               "A setting that lives in the configuration is refused with the reason, not a crash.");

            // The last statement of any upgrade, and what DBUpdater does after it.
            updater.Database.ExecuteSQL("update hm_dbversion set value = " + required);
            updater.Reinitialize();
         }
         finally
         {
            // Whatever happened above, the bench is put back and the service restarted.
            try { _application.Database.ExecuteSQL("update hm_dbversion set value = " + required); }
            catch (COMException) { }
            RestartServerAndReacquireCom();
         }

         Assert.AreEqual(required, _application.Database.CurrentVersion);
         Assert.IsNotNull(_application.Domains, "The server is back with its configuration loaded.");
      }
   }
}
