// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using System.IO;
using hMailServer.Shared;
using System.Runtime.InteropServices;

namespace DBUpdater
{
   public partial class formMain : Form
   {
      private readonly hMailServer.Application _application;
      private UpgradeScripts _upgradeScripts;
      private UpgradeScripts _upgradePath;
      private string _databaseType;
      private string _scriptPath;

      private const string DatabaseTypeMSSQL = "MSSQL";
      private const string DatabaseTypePGSQL = "PGSQL";

      /// <summary>
      /// True once DoUpgrade has committed successfully. Program.Main turns this
      /// into the process exit code so the installer can detect a failed upgrade.
      /// </summary>
      public bool UpgradeSucceeded { get; private set; }

      public formMain(hMailServer.Application application)
      {
         InitializeComponent();

         _application = application;
         _databaseType = null;
      }

      /// <summary>
      /// Reports an error without blocking a silent run: the installer executes this
      /// tool with /silent, where a modal dialog would hang the installation.
      /// </summary>
      private static void ShowError(string message, string caption)
      {
         Console.Error.WriteLine(caption + ": " + message);

         if (!CommandLineParser.IsSilent())
            MessageBox.Show(message, caption);
      }

      public bool CreateUpgradePath()
      {
         _upgradePath = new UpgradeScripts();

         int from = _application.Database.CurrentVersion;
         int to = _application.Database.RequiredVersion;

         // A database from a newer build reaches the loop below and comes out as
         // "no upgrade script is registered for version 6007", which sends the
         // operator looking for a missing file. There is no missing file: this
         // copy of hMailServer is the old one. Reinstalling an earlier build over
         // a newer database is the normal way to arrive here.
         if (from > to)
         {
            ShowError(string.Format("This database is from a newer version of hMailServer (database version {0}); this build requires version {1}. Downgrading a database is not supported - install the newer hMailServer, or restore a backup taken before the upgrade.", from, to), "hMailServer");
            return false;
         }

         // The walk below only terminates while every step moves strictly
         // forward. A duplicate or self-referencing registration in LoadScripts
         // would spin here, adding the same step forever, so the number of
         // registered steps is the ceiling: no valid path can be longer.
         int stepLimit = _upgradeScripts.GetList().Count;

         // Actually create the path.
         while (from != to)
         {
            if (_upgradePath.GetList().Count >= stepLimit)
            {
               ShowError(string.Format("Upgrade path could not be built: it exceeded the {0} registered upgrade steps without reaching version {1}. The upgrade step table in DBUpdater is inconsistent.", stepLimit, to), "hMailServer");
               return false;
            }

            UpgradeScript script = _upgradeScripts.GetScriptUpgradingFrom(from);

            if (script == null)
            {
               ShowError(string.Format("Upgrade path not found. No upgrade script is registered for database version {0} (target version {1}).", from, to), "hMailServer");
               return false;
            }

            string fileName = GetScriptFileName(script);

            if (!File.Exists(fileName))
            {
               ShowError("Required file for upgrade not found:" + Environment.NewLine + fileName, "hMailServer");
               return false;
            }

            _upgradePath.Add(script);

            from = script.To;
         }

         DisplayUpgradePath();

         return true;
      }

      public bool LoadSettings()
      {
         _upgradeScripts = new UpgradeScripts();

         switch (_application.Database.DatabaseType)
         {
            case hMailServer.eDBtype.hDBTypeMSSQL:
               _databaseType = DatabaseTypeMSSQL;
               break;
            case hMailServer.eDBtype.hDBTypeMSSQLCE:
               _databaseType = "MSSQLCE";
               break;
            case hMailServer.eDBtype.hDBTypeMySQL:
               _databaseType = "MySQL";
               break;
            case hMailServer.eDBtype.hDBTypePostgreSQL:
               _databaseType = DatabaseTypePGSQL;
               break;
            default:
               ShowError("Unknown database type", "hMailServer");
               return false;
         }

         LoadScripts();

         _scriptPath = _application.Settings.Directories.DBScriptDirectory;
         if (_scriptPath == null || _scriptPath.Length == 0)
         {
            ShowError("Database script directory could not be found." + Environment.NewLine + "Please check the hMailServer error log.", "hMailServer");
            return false;
         }

         return true;
      }

      private void DisplayUpgradePath()
      {
         foreach (UpgradeScript script in _upgradePath.GetList())
         {
            ListViewItem item = listRequiredUpgrades.Items.Add(GetDatabaseVersionName(script.From));
            item.SubItems.Add(GetDatabaseVersionName(script.To));
            item.Tag = script;
         }
      }

      private void LoadScripts()
      {
         _upgradeScripts.Add(new UpgradeScript(0, 1100));
         _upgradeScripts.Add(new UpgradeScript(1100, 1200));
         _upgradeScripts.Add(new UpgradeScript(1200, 1400));
         _upgradeScripts.Add(new UpgradeScript(1400, 1410));
         _upgradeScripts.Add(new UpgradeScript(1410, 1500));
         _upgradeScripts.Add(new UpgradeScript(1500, 1600));
         _upgradeScripts.Add(new UpgradeScript(1600, 1700));
         _upgradeScripts.Add(new UpgradeScript(1700, 2000));
         _upgradeScripts.Add(new UpgradeScript(2000, 3000));
         _upgradeScripts.Add(new UpgradeScript(3000, 3001));
         _upgradeScripts.Add(new UpgradeScript(3001, 3100));
         _upgradeScripts.Add(new UpgradeScript(3100, 3200));
         _upgradeScripts.Add(new UpgradeScript(3200, 3300));
         _upgradeScripts.Add(new UpgradeScript(3300, 3301));
         _upgradeScripts.Add(new UpgradeScript(3301, 3400));
         _upgradeScripts.Add(new UpgradeScript(3400, 3401));
         _upgradeScripts.Add(new UpgradeScript(3401, 3402));
         _upgradeScripts.Add(new UpgradeScript(3402, 4000));
         _upgradeScripts.Add(new UpgradeScript(4000, 4100));
         _upgradeScripts.Add(new UpgradeScript(4100, 4200));
         _upgradeScripts.Add(new UpgradeScript(4200, 4300));
         _upgradeScripts.Add(new UpgradeScript(4300, 4301));
         _upgradeScripts.Add(new UpgradeScript(4301, 4400));
         _upgradeScripts.Add(new UpgradeScript(4400, 4401));
         _upgradeScripts.Add(new UpgradeScript(4401, 4402));
         _upgradeScripts.Add(new UpgradeScript(4402, 5000));
         _upgradeScripts.Add(new UpgradeScript(5000, 5001));
         _upgradeScripts.Add(new UpgradeScript(5001, 5002));
         _upgradeScripts.Add(new UpgradeScript(5002, 5003));
         _upgradeScripts.Add(new UpgradeScript(5003, 5004));
         _upgradeScripts.Add(new UpgradeScript(5004, 5005));
         _upgradeScripts.Add(new UpgradeScript(5005, 5006));
         _upgradeScripts.Add(new UpgradeScript(5006, 5100));
         _upgradeScripts.Add(new UpgradeScript(5100, 5110));
         _upgradeScripts.Add(new UpgradeScript(5110, 5200));
         _upgradeScripts.Add(new UpgradeScript(5200, 5201));
         _upgradeScripts.Add(new UpgradeScript(5201, 5300));
         _upgradeScripts.Add(new UpgradeScript(5300, 5310));
         _upgradeScripts.Add(new UpgradeScript(5310, 5320));
         _upgradeScripts.Add(new UpgradeScript(5320, 5400));
         _upgradeScripts.Add(new UpgradeScript(5400, 5500));
         _upgradeScripts.Add(new UpgradeScript(5500, 5501));
         _upgradeScripts.Add(new UpgradeScript(5501, 5502));
         _upgradeScripts.Add(new UpgradeScript(5502, 5600));
         _upgradeScripts.Add(new UpgradeScript(5600, 5601));
         _upgradeScripts.Add(new UpgradeScript(5601, 5700));
         _upgradeScripts.Add(new UpgradeScript(5700, 5702));
         _upgradeScripts.Add(new UpgradeScript(5702, 5703));
         _upgradeScripts.Add(new UpgradeScript(5703, 5704));
         _upgradeScripts.Add(new UpgradeScript(5704, 5705));
         _upgradeScripts.Add(new UpgradeScript(5705, 5708));
         _upgradeScripts.Add(new UpgradeScript(5708, 6001));
         _upgradeScripts.Add(new UpgradeScript(6001, 6002));
         _upgradeScripts.Add(new UpgradeScript(6002, 6003));
         _upgradeScripts.Add(new UpgradeScript(6003, 6004));
         _upgradeScripts.Add(new UpgradeScript(6004, 6005));
         _upgradeScripts.Add(new UpgradeScript(6005, 6006));
         _upgradeScripts.Add(new UpgradeScript(6006, 6007));
         _upgradeScripts.Add(new UpgradeScript(6007, 6008));
         _upgradeScripts.Add(new UpgradeScript(6008, 6009));
         _upgradeScripts.Add(new UpgradeScript(6009, 6010));
         _upgradeScripts.Add(new UpgradeScript(6010, 6011));
         _upgradeScripts.Add(new UpgradeScript(6011, 6012));
         _upgradeScripts.Add(new UpgradeScript(6012, 6013));
         _upgradeScripts.Add(new UpgradeScript(6013, 6014));
      }

      private void buttonClose_Click(object sender, EventArgs e)
      {
         this.Close();
      }

      private string GetDatabaseVersionName(int version)
      {
         switch (version)
         {
            case 0:
               return "hMailServer 1.0";
            case 1100:
               return "hMailServer 1.1";
            case 1200:
               return "hMailServer 1.2";
            case 1400:
               return "hMailServer 1.4";
            case 1410:
               return "hMailServer 1.4.1";
            case 1500:
               return "hMailServer 1.5";
            case 1600:
               return "hMailServer 1.6";
            case 1700:
               return "hMailServer 1.7";
            case 2000:
               return "hMailServer 2.0";
            case 3000:
               return "hMailServer 3.0 Alpha";
            case 3001:
               return "hMailServer 3.0";
            case 3100:
               return "hMailServer 3.1";
            case 3200:
               return "hMailServer 3.2";
            case 3300:
               return "hMailServer 3.3 Alpha";
            case 3301:
               return "hMailServer 3.3";
            case 3400:
               return "hMailServer 3.3 Alpha";
            case 3401:
               return "hMailServer 3.3 Beta";
            case 3402:
               return "hMailServer 3.3";
            case 4000:
               return "hMailServer 4.0";
            case 4100:
               return "hMailServer 4.1";
            case 4200:
               return "hMailServer 4.2";
            case 4300:
               return "hMailServer 4.3 (Alpha)";
            case 4301:
               return "hMailServer 4.3";
            case 4400:
               return "hMailServer 4.4 (Alpha)";
            case 4401:
               return "hMailServer 4.4";
            case 4402:
               return "hMailServer 4.4.2";
            case 5000:
               return "hMailServer 5 (Alpha 1)";
            case 5001:
               return "hMailServer 5 (Alpha 2)";
            case 5002:
               return "hMailServer 5 (Alpha 3)";
            case 5003:
               return "hMailServer 5 (Alpha 4)";
            case 5004:
               return "hMailServer 5 (Alpha 5)";
            case 5005:
               return "hMailServer 5 (Alpha 6)";
            case 5006:
               return "hMailServer 5";
            case 5100:
               return "hMailServer 5.1";
            case 5110:
               return "hMailServer 5.1.2";
            case 5200:
               return "hMailServer 5.2 (Alpha 1)";
            case 5201:
               return "hMailServer 5.2";
            case 5300:
               return "hMailServer 5.3";
            case 5310:
               return "hMailServer 5.3.1";
            case 5320:
               return "hMailServer 5.3.2";
            case 5400:
               return "hMailServer 5.4";
            case 5500:
               return "hMailServer 5.5 (Alpha 1)";
            case 5501:
               return "hMailServer 5.5 (Alpha 2)";
            case 5502:
               return "hMailServer 5.5";
            case 5600:
               return "hMailServer 5.6 (Alpha 1)";
            case 5601:
               return "hMailServer 5.6";
            case 5700:
               return "hMailServer 5.7 (5700)";
            case 5702:
               return "hMailServer 5.7 (5702)";
            case 5703:
               return "hMailServer 5.7 (5703)";
            case 5704:
               return "hMailServer 5.7 (5704)";
            case 5705:
               return "hMailServer 5.7 (5705)";
            case 5708:
               return "hMailServer 5.7 (5708)";
            case 6001:
               return "hMailServer 6.0";
            case 6002:
               return "hMailServer 6.2 (6002)";
            case 6003:
               return "hMailServer 6.2 (6003)";
            case 6004:
               return "hMailServer 6.2 (6004)";
            case 6005:
               return "hMailServer 6.2 (6005)";
            case 6006:
               return "hMailServer 6.2 (6006)";
            case 6007:
               return "hMailServer 6.2 (6007)";
            case 6008:
               return "hMailServer 6.2 (6008)";
            case 6009:
               return "hMailServer 6.2 (6009)";
            case 6010:
               return "hMailServer 6.2 (6010)";
            case 6011:
               return "hMailServer 6.2 (6011)";
            case 6012:
               return "hMailServer 6.2 (6012)";
            case 6013:
               return "hMailServer 6.2 (6013)";
            case 6014:
               return "hMailServer 6.2 (6014)";
            default:
               return "hMailServer (database version " + version + ")";
         }
      }

      private string GetScriptFileName(UpgradeScript script)
      {
         string fileName = "Upgrade" + script.From + "to" + script.To + _databaseType + ".sql";
         string fullPath = Path.Combine(_scriptPath, fileName);

         return fullPath;
      }


      public void DoUpgrade()
      {
         using (new WaitCursor())
         {
            buttonClose.Enabled = false;
            buttonUpgrade.Enabled = false;

            hMailServer.Database database = _application.Database;

            try
            {
               database.BeginTransaction();
            }
            catch (Exception e)
            {
               HandleUpgradeError(database, e, "Transaction");
               return;
            }

            // Run the prerequisites script.
            string prerequisitesScript = GetPrerequisitesScript();
            if (!string.IsNullOrEmpty(prerequisitesScript))
            {
               string fullScriptPath = Path.Combine(_scriptPath, prerequisitesScript);

               try
               {
                  database.ExecuteSQLScript(fullScriptPath);
               }
               catch (Exception ex)
               {
                  HandleUpgradeError(database, ex, fullScriptPath);
                  return;
               }

            }


            foreach (ListViewItem item in listRequiredUpgrades.Items)
            {
               UpgradeScript script = (UpgradeScript) item.Tag;

               string scriptToExecute = GetScriptFileName(script);

               try
               {
                  // Make sure the 
                  database.EnsurePrerequisites(script.To);

                  database.ExecuteSQLScript(scriptToExecute);

                  item.SubItems.Add("Complete");

                  Application.DoEvents();
               }
               catch (Exception e)
               {
                  item.SubItems.Add("Error");

                  HandleUpgradeError(database, e, scriptToExecute);
                  return;
               }

            }

            // Prove the schema really changed before committing it. A script that
            // did not throw is not evidence that it did anything - see
            // SchemaVerification for why - and this is the last moment at which
            // the answer can still be acted on: the probes run on the
            // transaction's own connection (InterfaceDatabase::ExecuteSQL uses
            // conn_ while a transaction is open), so they see the uncommitted DDL
            // and a missing object can still be rolled back on the two backends
            // that have transactional DDL.
            string verificationError;
            if (!VerifyUpgradedSchema(database, out verificationError))
            {
               HandleUpgradeError(database, verificationError, "Schema verification");
               return;
            }

            try
            {
               database.CommitTransaction();
            }
            catch (Exception e)
            {
               HandleUpgradeError(database, e, "Transaction");
               return;
            }

            // hm_dbversion is written by a statement of its own, in the same
            // script as the schema change but independent of whether it worked.
            // Read it back rather than infer it: Database.CurrentVersion
            // re-queries hm_dbversion on every call
            // (DatabaseConnectionManager::GetCurrentDatabaseVersion caches
            // nothing) and the transaction is closed by now, so this is the
            // committed value the server will see when it next starts.
            //
            // Nothing can be rolled back from here, so a mismatch is reported and
            // the error log is deliberately left in place - RemoveErrorLog below
            // would delete the server's own complaint about the version and leave
            // the operator with no trace of it at all.
            int reachedVersion;
            try
            {
               reachedVersion = database.CurrentVersion;
            }
            catch (Exception e)
            {
               ShowError("The upgrade was committed but the resulting database version could not be read: " + e.Message, "hMailServer");
               buttonClose.Enabled = true;
               return;
            }

            // Read through the reference already held, not _application.Database:
            // that property hands out a fresh COM object on every access, and the
            // one below is released a few lines further down.
            int targetVersion = database.RequiredVersion;

            if (reachedVersion != targetVersion)
            {
               ShowError(string.Format("The upgrade scripts ran without reporting an error, but the database is at version {0} and this build of hMailServer requires version {1}. The upgrade is incomplete: restore the backup taken before the upgrade and check the hMailServer error log. The database has NOT been left in a usable state.", reachedVersion, targetVersion), "hMailServer");
               buttonClose.Enabled = true;
               return;
            }

            // The schema is committed and verified from here on, so record success
            // before the cleanup below: a COM/RPC failure while reinitializing must
            // not report an upgrade that did happen as a failure.
            UpgradeSucceeded = true;

            Marshal.ReleaseComObject(database);

            // Database has been upgraded. Reinitialize the connections.
            _application.Reinitialize();

            RemoveErrorLog();

            buttonClose.Enabled = true;
         }

      }

      /// <summary>
      /// Runs the registered schema probes for every step in the upgrade path.
      /// Returns false, with a message naming the object that is missing, if any
      /// probe fails.
      /// </summary>
      private bool VerifyUpgradedSchema(hMailServer.Database database, out string error)
      {
         error = null;

         foreach (ListViewItem item in listRequiredUpgrades.Items)
         {
            UpgradeScript script = (UpgradeScript) item.Tag;

            foreach (SchemaProbe probe in SchemaVerification.GetProbesFor(script.To))
            {
               try
               {
                  database.ExecuteSQL(probe.Statement);
               }
               catch (Exception e)
               {
                  // The step's script reported success, so the only way to be
                  // here is an error that [IGNORE-ERRORS] discarded. Say that
                  // outright: the operator's next question is always "but it said
                  // it worked".
                  error = string.Format(
                     "{0} did not create {1}. The script reported success because its ALTER statement is marked [IGNORE-ERRORS], which discards every error, not only \"already exists\" - so the real failure was thrown away. The upgrade has been rolled back where the backend allows it. Probe: {2}{3}Error: {4}",
                     Path.GetFileName(GetScriptFileName(script)),
                     probe.Describes,
                     probe.Statement,
                     Environment.NewLine,
                     e.Message);

                  return false;
               }
            }
         }

         return true;
      }

      private void HandleUpgradeError(hMailServer.Database database, Exception error, string scriptToExecute)
      {
         HandleUpgradeError(database, error.Message, scriptToExecute);
      }

      private void HandleUpgradeError(hMailServer.Database database, string error, string scriptToExecute)
      {
         try
         {
            database.RollbackTransaction();
         }
         catch (Exception)
         {
            // When an error occurs in MSSQL, the rollback will be done 
            // automatically. Hence it's not always an error that we cannot
            // rollback.
            //
            // Maybe we should check the actual cause of the rollback failure...
            //
         }
         finally
         {
            ShowError(error, scriptToExecute);
         }

         buttonClose.Enabled = true;
         return;
      }

      private void RemoveErrorLog()
      {
         try
         {
            // Kill the error file, so user isn't notified about old db version.
            string errorFile = _application.Settings.Logging.CurrentErrorLog;

            if (System.IO.File.Exists(errorFile))
               System.IO.File.Delete(errorFile);
         }
         catch (Exception)
         {

         }

      }

      private void formMain_Shown(object sender, EventArgs e)
      {
         labelCurrentDatabaseVersion.Text = GetDatabaseVersionName(_application.Database.CurrentVersion);
         labelRequiredDatabaseVersion.Text = GetDatabaseVersionName(_application.Database.RequiredVersion);
      }

      private void buttonUpgrade_Click(object sender, EventArgs e)
      {
         DialogResult result = MessageBox.Show("Have you taken a backup of the hMailServer database?", "hMailServer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

         if (result == DialogResult.Yes)
            DoUpgrade();
         else if (result == DialogResult.No)
            MessageBox.Show(labelRunBackup.Text, "hMailServer", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
      }

      private void listRequiredUpgrades_DoubleClick(object sender, EventArgs e)
      {
         if (listRequiredUpgrades.SelectedItems.Count != 1)
            return;

         UpgradeScript script = listRequiredUpgrades.SelectedItems[0].Tag as UpgradeScript;
         string scriptToExecute = GetScriptFileName(script);

         try
         {
            System.Diagnostics.Process.Start("notepad.exe", scriptToExecute);
         }
         catch (Exception )
         {
            MessageBox.Show("Notepad could not be started.");
         }
      }

      private string GetPrerequisitesScript()
      {
         switch (_databaseType)
         {
            case DatabaseTypeMSSQL:
               return "ScriptPrerequisitesMSSQL.sql";
            case DatabaseTypePGSQL:
               return "ScriptPrerequisitesPGSQL.sql";
            default:
               return null;
         }
      }

   }
}