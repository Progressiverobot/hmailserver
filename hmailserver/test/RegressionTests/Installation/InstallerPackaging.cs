// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Installation
{
   /// <summary>
   ///    Static assertions about the Inno Setup scripts in hmailserver\installation.
   ///
   ///    The installer is the one artefact this project cannot exercise from the
   ///    suite: running it hijacks the service path and the COM registration of the
   ///    machine the tests run on, which is why the only end-to-end check lives in
   ///    the installer-smoke GitHub workflow on a throwaway runner. Everything in
   ///    between - "is the script self-consistent", "does it still agree with the
   ///    server it installs" - has had nothing watching it, and the cost of that
   ///    showed up immediately:
   ///
   ///    * The .NET 10 migration replaced the runtime probe's glob
   ///      Microsoft.WindowsDesktop.App\8.* with the same string containing a raw
   ///      0x08 byte where "\10" should have been - almost certainly a "\10" read as
   ///      an octal escape by whatever performed the replacement. The commit message
   ///      and the roadmap both say the probe "now globs 10.*". It never matched
   ///      anything, and no editor, diff or review would show the difference.
   ///    * The schema version moved to 6006 the same week. If the shipped upgrade
   ///      scripts and the version the server demands ever disagree, the installed
   ///      server refuses to start (Application::OnDatabaseConnected rejects a
   ///      database that is either too old or too new) - after the installer has
   ///      already replaced the binaries.
   ///
   ///    Not every test here is tied to a defect being fixed. Those that guard a
   ///    currently-correct invariant say so in their Description, so nobody mistakes
   ///    a green run for evidence that something was repaired.
   /// </summary>
   [TestFixture]
   public class InstallerPackaging : TestFixtureBase
   {
      // TestDirectory is <repo>\hmailserver\test\RegressionTests\bin\x64\Debug.
      // Six levels up is the repository root - the same relative walk
      // MIME\FuzzHarness.cs and SSL\SslSetup.cs use.
      private static string RepositoryRoot
      {
         get
         {
            return Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
                                                 @"..\..\..\..\..\.."));
         }
      }

      private static string InstallationRoot
      {
         get { return Path.Combine(RepositoryRoot, @"hmailserver\installation"); }
      }

      private static string SourceRoot
      {
         get { return Path.Combine(RepositoryRoot, @"hmailserver\source"); }
      }

      private static string ReadRepositoryFile(string relativePath)
      {
         string path = Path.Combine(RepositoryRoot, relativePath);
         Assert.IsTrue(File.Exists(path), "Missing file: " + path);
         return File.ReadAllText(path);
      }

      private static string ReadInstallerScript(string fileName)
      {
         return ReadRepositoryFile(Path.Combine(@"hmailserver\installation", fileName));
      }

      private static IEnumerable<FileInfo> InstallerScripts()
      {
         var directory = new DirectoryInfo(InstallationRoot);
         Assert.IsTrue(directory.Exists, "Missing installer directory: " + InstallationRoot);
         return directory.GetFiles("*.iss", SearchOption.TopDirectoryOnly).OrderBy(file => file.Name);
      }

      /// <summary>
      ///    Entry lines of an Inno Setup section - everything that starts with
      ///    "Filename:", in file order, with comment lines dropped. Order matters for
      ///    [UninstallRun], which Inno executes top to bottom.
      /// </summary>
      private static List<string> EntryLines(string script)
      {
         return script.Split('\n')
                      .Select(line => line.Trim())
                      .Where(line => line.StartsWith("Filename:", StringComparison.OrdinalIgnoreCase))
                      .ToList();
      }

      #region ISPP

      /// <summary>
      ///    Enough of the Inno Setup preprocessor to resolve the {#NAME} references
      ///    the scripts use: #define of a string literal, or of literals and other
      ///    defines joined with '+'. Anything it cannot work out (GetEnv, arithmetic)
      ///    is left unresolved rather than guessed at, because a test that silently
      ///    substitutes the wrong value is worse than one that cannot substitute.
      /// </summary>
      private static Dictionary<string, string> PreprocessorDefines()
      {
         var defines = new Dictionary<string, string>();

         // Every define lives in the top-level script, which is included first.
         string script = ReadInstallerScript("hMailServer64.iss");

         foreach (Match match in Regex.Matches(script, @"^\s*#define\s+(\w+)\s*=?\s*(.+?)\s*$",
                                               RegexOptions.Multiline))
         {
            string value = EvaluateDefine(match.Groups[2].Value, defines);

            if (value != null)
               defines[match.Groups[1].Value] = value;
         }

         return defines;
      }

      private static string EvaluateDefine(string expression, Dictionary<string, string> known)
      {
         var value = string.Empty;

         // No literal in these scripts contains a '+', so splitting on it is safe.
         foreach (string rawTerm in expression.Split('+'))
         {
            string term = rawTerm.Trim();

            if (term.Length >= 2 && term.StartsWith("\"") && term.EndsWith("\""))
               value += term.Substring(1, term.Length - 2);
            else if (known.ContainsKey(term))
               value += known[term];
            else
               return null;
         }

         return value;
      }

      private static string ResolveIspp(string text, Dictionary<string, string> defines)
      {
         return Regex.Replace(text, @"\{#(\w+)\}",
                              match => defines.ContainsKey(match.Groups[1].Value)
                                          ? defines[match.Groups[1].Value]
                                          : match.Value);
      }

      #endregion

      [Test]
      [Description("No installer script contains a control character. This is the negative control for the 0x08 that broke the .NET runtime probe.")]
      public void TestInstallerScriptsContainNoControlCharacters()
      {
         var offenders = new List<string>();

         foreach (FileInfo script in InstallerScripts())
         {
            byte[] bytes = File.ReadAllBytes(script.FullName);

            for (int i = 0; i < bytes.Length; i++)
            {
               byte value = bytes[i];

               // Tab, CR and LF are the only control bytes these files may contain.
               if (value < 0x20 && value != 0x09 && value != 0x0A && value != 0x0D)
               {
                  offenders.Add(script.Name + " byte " + i + " = 0x" + value.ToString("x2"));
                  break;
               }
            }
         }

         Assert.IsEmpty(offenders,
            "Control characters in installer scripts: " + string.Join(", ", offenders) +
            ". A control character inside a path or a glob is invisible in every editor and every diff, " +
            "and it silently stops the surrounding expression from ever matching.");
      }

      [Test]
      [Description("Every reference to the bundled .NET Desktop Runtime installer names the same file.")]
      public void TestBundledDotNetRuntimeIsNamedConsistently()
      {
         Dictionary<string, string> defines = PreprocessorDefines();

         string[] scriptsThatReferenceIt = { "section_files_64.iss", "section_run.iss", "hMailServerInnoExtension.iss" };
         var referenced = new List<string>();

         foreach (string fileName in scriptsThatReferenceIt)
         {
            string script = ResolveIspp(ReadInstallerScript(fileName), defines);

            var names = Regex.Matches(script, @"windowsdesktop-runtime-[0-9.]+-win-x64\.exe")
                             .Cast<Match>()
                             .Select(match => match.Value)
                             .Distinct()
                             .ToList();

            Assert.IsNotEmpty(names,
               fileName + " no longer references the bundled .NET Desktop Runtime installer. " +
               "The server component needs it: DBSetup, DBSetupQuick and DBUpdater run during " +
               "installation and cannot start without the runtime.");

            referenced.AddRange(names);
         }

         Assert.AreEqual(1, referenced.Distinct().Count(),
            "The bundled .NET Desktop Runtime is referenced under more than one name (" +
            string.Join(", ", referenced.Distinct()) + "). One of the references is to a file that " +
            "will not be in {tmp}, so either the runtime is never installed or the installer stops with " +
            "a missing-file error. Define the name once in hMailServer64.iss and reference it.");
      }

      [Test]
      [Description("The installed-runtime probe matches the major version of the runtime the installer actually bundles.")]
      public void TestDotNetRuntimeProbeMatchesTheBundledMajorVersion()
      {
         Dictionary<string, string> defines = PreprocessorDefines();
         string extension = ResolveIspp(ReadInstallerScript("hMailServerInnoExtension.iss"), defines);

         Match bundled = Regex.Match(extension + ResolveIspp(ReadInstallerScript("section_run.iss"), defines),
                                     @"windowsdesktop-runtime-(\d+)\.");
         Assert.IsTrue(bundled.Success, "Could not find the bundled .NET runtime installer's version.");
         string major = bundled.Groups[1].Value;

         // The glob DotNetDesktopMissing() hands to FindFirst.
         Match probe = Regex.Match(extension, @"FindFirst\(ExpandConstant\('([^']*)'\)");
         Assert.IsTrue(probe.Success,
            "DotNetDesktopMissing() no longer probes for the installed runtime with FindFirst. " +
            "Without a probe the bundled runtime is either always installed or never installed.");

         string glob = probe.Groups[1].Value;

         // The shared framework records one directory per installed version under
         // Microsoft.WindowsDesktop.App, so the version has to be matched inside that
         // folder. Matching one level up cannot tell .NET 8 from .NET 10 - and
         // Windows' "name.*" wildcard matches an extension-less name too, so a glob
         // ending at the folder itself is satisfied by any runtime of any version.
         string expectedTail = @"\Microsoft.WindowsDesktop.App\" + major + ".*";

         Assert.IsTrue(glob.EndsWith(expectedTail, StringComparison.OrdinalIgnoreCase),
            "DotNetDesktopMissing() globs \"" + glob + "\" but the installer bundles the .NET " + major +
            " Desktop Runtime, so the glob must end with \"" + expectedTail + "\". As written it cannot " +
            "distinguish the required major version, and a probe that always answers the same way makes " +
            "the bundled runtime either useless or unavoidable. When it wrongly answers \"installed\", " +
            "DBSetupQuick and DBUpdater cannot run, and the upgrade finishes with the new server binary " +
            "against the old database schema.");
      }

      [Test]
      [Description("The upgrade scripts the installer ships can take a database to the schema version the server demands, for every engine.")]
      public void TestShippedUpgradeScriptsReachTheRequiredSchemaVersion()
      {
         Match required = Regex.Match(ReadRepositoryFile(@"hmailserver\source\Server\Common\Application\Constants.h"),
                                      @"#define\s+REQUIRED_DB_VERSION\s+(\d+)");
         Assert.IsTrue(required.Success, "Could not read REQUIRED_DB_VERSION from Constants.h.");
         int requiredVersion = int.Parse(required.Groups[1].Value);

         // DBUpdater's upgrade path. The installer never runs DBUpdater directly: it
         // runs DBSetupQuick, which shells DBUpdater.exe /SilentIfOk and propagates
         // its exit code - so this list is what an in-place upgrade actually executes.
         var steps = Regex.Matches(ReadRepositoryFile(@"hmailserver\source\Tools\DBUpdater\formMain.cs"),
                                   @"new\s+UpgradeScript\(\s*(\d+)\s*,\s*(\d+)\s*\)")
                          .Cast<Match>()
                          .Select(match => new
                          {
                             From = int.Parse(match.Groups[1].Value),
                             To = int.Parse(match.Groups[2].Value)
                          })
                          .ToList();

         Assert.GreaterOrEqual(steps.Count, 40,
            "Found only " + steps.Count + " upgrade steps in DBUpdater\\formMain.cs. The declaration format " +
            "probably changed and this test is no longer reading the upgrade path.");

         Assert.AreEqual(requiredVersion, steps.Last().To,
            "The last upgrade step in DBUpdater ends at schema " + steps.Last().To + " but the server requires " +
            requiredVersion + " (REQUIRED_DB_VERSION). An upgrade would run to completion, report success, and " +
            "leave a database the new server refuses to open.");

         // Contiguous: every step must start where the previous one finished, or a
         // database sitting on the version in the gap can never be upgraded at all.
         for (int i = 1; i < steps.Count; i++)
         {
            Assert.AreEqual(steps[i - 1].To, steps[i].From,
               "Gap in DBUpdater's upgrade path: " + steps[i - 1].From + "->" + steps[i - 1].To +
               " is followed by " + steps[i].From + "->" + steps[i].To + ".");
         }

         // Engine suffixes as the script files spell them. MSSQLCE reuses the MSSQL
         // create script but has its own upgrade scripts, because SQL CE rejects most
         // of the DDL the full product accepts.
         string[] engines = { "MSSQL", "MSSQLCE", "MySQL", "PGSQL" };
         string scriptDirectory = Path.Combine(SourceRoot, "DBScripts");
         var missing = new List<string>();

         foreach (var step in steps)
         {
            // 5002 is where the four-engine era starts; before that only MSSQL and
            // MySQL scripts exist, and no supported installation is that old.
            if (step.To < 5002)
               continue;

            foreach (string engine in engines)
            {
               string name = "Upgrade" + step.From + "to" + step.To + engine + ".sql";

               if (!File.Exists(Path.Combine(scriptDirectory, name)))
                  missing.Add(name);
            }
         }

         Assert.IsEmpty(missing,
            "DBUpdater declares upgrade steps whose scripts are not in source\\DBScripts: " +
            string.Join(", ", missing) + ". The installer ships that directory wholesale, so a missing " +
            "script is a database that cannot be upgraded on the customer's machine.");

         // The final step must actually write the new version, and a fresh database
         // must be created at it. These are the two ways a database ends up on a
         // version the server rejects even though every script ran.
         foreach (string engine in engines)
         {
            string upgrade = File.ReadAllText(Path.Combine(scriptDirectory,
               "Upgrade" + steps.Last().From + "to" + steps.Last().To + engine + ".sql"));

            Assert.IsTrue(Regex.IsMatch(upgrade, @"hm_dbversion\s+set\s+value\s*=\s*" + requiredVersion),
               "The " + engine + " upgrade script for the final step does not set hm_dbversion to " +
               requiredVersion + ".");
         }

         foreach (string createScript in new[] { "CreateTablesMSSQL.sql", "CreateTablesMYSQL.sql", "CreateTablesPGSQL.sql" })
         {
            string create = File.ReadAllText(Path.Combine(scriptDirectory, createScript));

            Assert.IsTrue(Regex.IsMatch(create, @"hm_dbversion\s+values\s*\(\s*" + requiredVersion + @"\s*\)"),
               createScript + " does not create hm_dbversion at " + requiredVersion +
               ", so a brand new installation would come up on a schema version the server rejects.");
         }

         Assert.IsTrue(ReadInstallerScript("section_files_common.iss").Contains(@"..\source\DBScripts\*.sql"),
            "The installer no longer ships source\\DBScripts, so DBUpdater would find no scripts to run.");
      }

      [Test]
      [Description("An installation that collected no administrator password must not write one.")]
      public void TestAdministratorPasswordIsOnlyWrittenWhenOneWasCollected()
      {
         string iniSection = ReadInstallerScript("section_ini.iss");

         string entry = EntryLines(iniSection)
            .FirstOrDefault(line => line.IndexOf("AdministratorPassword", StringComparison.OrdinalIgnoreCase) >= 0);

         Assert.IsNotNull(entry, "The [INI] section no longer writes Security\\AdministratorPassword.");

         Assert.IsTrue(entry.IndexOf("Check:", StringComparison.OrdinalIgnoreCase) >= 0,
            "The Security\\AdministratorPassword entry has no Check: condition, so it is written on every " +
            "installation - including every silent one, where the password page does not exist and the " +
            "value written is therefore the MD5 of the empty string. That is not an unset password: the " +
            "server authenticates an empty password against it (Crypt::GetHashType treats any " +
            "32-character value as MD5), the REST API's \"refuse to start while the administrator " +
            "password is unset\" guard stops applying, and setup never offers the password page again.");

         // The condition has to be about the password, not about anything else that
         // happens to be convenient.
         Assert.IsTrue(entry.IndexOf("HasAdministratorPassword", StringComparison.OrdinalIgnoreCase) >= 0,
            "The Security\\AdministratorPassword entry's Check: condition is not HasAdministratorPassword.");

         string extension = ReadInstallerScript("hMailServerInnoExtension.iss");

         Assert.IsTrue(Regex.IsMatch(extension, @"function\s+HasAdministratorPassword\s*\(\s*\)\s*:\s*Boolean",
                                     RegexOptions.IgnoreCase),
            "HasAdministratorPassword is referenced by section_ini.iss but not defined in [Code].");

         Assert.IsTrue(extension.IndexOf("{param:adminpassword", StringComparison.OrdinalIgnoreCase) >= 0,
            "There is no way to supply the administrator password to an unattended install. Without one, " +
            "every silent installation is left with no administrator password at all.");
      }

      [Test]
      [Description("The uninstaller stops the service before deleting it, and tolerates files an old installation never had.")]
      public void TestUninstallStepsAreOrderedAndOptional()
      {
         List<string> entries = EntryLines(ReadInstallerScript("section_uninstallrun.iss"));

         Assert.IsNotEmpty(entries, "section_uninstallrun.iss has no entries.");

         int stopIndex = entries.FindIndex(line =>
            line.IndexOf("net.exe", StringComparison.OrdinalIgnoreCase) >= 0 &&
            Regex.IsMatch(line, @"STOP\s+hMailServer""", RegexOptions.IgnoreCase));

         int unregisterIndex = entries.FindIndex(line =>
            line.IndexOf("/Unregister", StringComparison.OrdinalIgnoreCase) >= 0);

         Assert.GreaterOrEqual(stopIndex, 0, "The uninstaller no longer stops the hMailServer service.");
         Assert.GreaterOrEqual(unregisterIndex, 0, "The uninstaller no longer unregisters hMailServer.");

         Assert.IsTrue(stopIndex < unregisterIndex,
            "The uninstaller runs hMailServer.exe /Unregister before stopping the service. DeleteService on " +
            "a running service only marks it for deletion and leaves the process running, so the uninstaller " +
            "then starts deleting a directory whose files the server still has open, and COM is unregistered " +
            "from under a live server. net.exe stop is synchronous - stopping first is what makes the rest safe.");

         var unguarded = entries
            .Where(line => line.IndexOf("{app}", StringComparison.OrdinalIgnoreCase) >= 0 &&
                           line.IndexOf("skipifdoesntexist", StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();

         Assert.IsEmpty(unguarded,
            "[UninstallRun] entries run a file under {app} without skipifdoesntexist: " +
            string.Join(" | ", unguarded) + ". Inno raises an error dialog for each one that is not there, " +
            "and the entries kept for hMailServer 3 and 4 installations (mysqld-nt, hSMTPServer, hPOP3Server) " +
            "are never present on a current one.");
      }

      [Test]
      [Description("Guards a currently-correct invariant: uninstall must not remove mail, the database or the configuration.")]
      public void TestUninstallDoesNotRemoveMailData()
      {
         // Inno's uninstaller only deletes what it logged, and it removes the
         // directories it created only when they are empty - so Data, Database and
         // Logs survive by construction. An [UninstallDelete] entry is the one way to
         // break that, and it would destroy every message on the server.
         string[] protectedPaths = { @"{app}\Data", @"{app}\Database", @"{app}\Bin\hMailServer.ini" };

         foreach (FileInfo script in InstallerScripts())
         {
            string text = File.ReadAllText(script.FullName);

            int sectionStart = text.IndexOf("[UninstallDelete]", StringComparison.OrdinalIgnoreCase);
            if (sectionStart < 0)
               continue;

            // To the next section header, or the end of the file.
            Match nextSection = Regex.Match(text.Substring(sectionStart + 1), @"^\[", RegexOptions.Multiline);
            string section = nextSection.Success
               ? text.Substring(sectionStart, nextSection.Index + 1)
               : text.Substring(sectionStart);

            foreach (string entry in section.Split('\n').Select(line => line.Trim()))
            {
               if (!entry.StartsWith("Type:", StringComparison.OrdinalIgnoreCase) &&
                   !entry.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                  continue;

               bool recursive = entry.IndexOf("filesandordirs", StringComparison.OrdinalIgnoreCase) >= 0;

               foreach (string protectedPath in protectedPaths)
               {
                  Assert.IsFalse(entry.IndexOf(protectedPath, StringComparison.OrdinalIgnoreCase) >= 0,
                     script.Name + " has an [UninstallDelete] entry covering " + protectedPath + ": " + entry +
                     ". Uninstalling hMailServer must not delete mail, the database or the configuration.");
               }

               Assert.IsFalse(recursive && Regex.IsMatch(entry, @"Name:\s*""\{app\}\\?""", RegexOptions.IgnoreCase),
                  script.Name + " has an [UninstallDelete] entry that recursively deletes {app}: " + entry +
                  ". That is the whole mail store.");
            }
         }
      }

      [Test]
      [Description("Guards a currently-correct invariant: an upgrade must not rewrite the database connection settings or the paths an administrator has moved.")]
      public void TestUpgradePreservesExistingConfiguration()
      {
         List<string> entries = EntryLines(ReadInstallerScript("section_ini.iss"));

         Assert.IsNotEmpty(entries, "section_ini.iss has no entries.");

         // The Database section holds the engine, host, database name, user and
         // password. DBSetup writes it; the installer must never touch it, or an
         // upgrade points a working server at nothing.
         var databaseEntries = entries
            .Where(line => Regex.IsMatch(line, @"Section:\s*""Database""", RegexOptions.IgnoreCase))
            .ToList();

         Assert.IsEmpty(databaseEntries,
            "The [INI] section writes into the ini's Database section: " + string.Join(" | ", databaseEntries) +
            ". Those keys are the database connection an upgrade has to preserve.");

         // Directory keys and the administrator password are defaults for a fresh
         // installation only. ProgramFolder is the deliberate exception: it has to
         // follow the installation directory, which the user may have changed.
         foreach (string entry in entries)
         {
            if (Regex.IsMatch(entry, @"Key:\s*""(ProgramFolder|ValidLanguages)""", RegexOptions.IgnoreCase))
               continue;

            Assert.IsTrue(entry.IndexOf("createkeyifdoesntexist", StringComparison.OrdinalIgnoreCase) >= 0,
               "[INI] entry overwrites an existing value on upgrade: " + entry +
               ". Add createkeyifdoesntexist, or an administrator's customised setting is replaced by the " +
               "installer's default every time they upgrade.");
         }
      }

      [Test]
      [Description("The service the installer creates is given failure recovery actions.")]
      public void TestServiceIsGivenRecoveryActions()
      {
         string extension = ReadInstallerScript("hMailServerInnoExtension.iss");

         // ServiceManager::RegisterService creates the service with auto-start and a
         // dependency on RPCSS, and never calls ChangeServiceConfig2 - so unless the
         // installer sets them, the recovery configuration is Windows' default of
         // "take no action" and a mail server that dies stays dead.
         Assert.IsTrue(Regex.IsMatch(extension, @"sc\.exe'\)\s*,\s*'failure\s+hMailServer", RegexOptions.IgnoreCase),
            "The installer does not configure the hMailServer service's failure actions. Nothing else does " +
            "either: ServiceManager::RegisterService never calls ChangeServiceConfig2.");

         Assert.IsTrue(Regex.IsMatch(extension, @"failure\s+hMailServer[^']*reset=\s*\d+", RegexOptions.IgnoreCase),
            "sc.exe failure is invoked without reset=, so the failure count never clears and the escalating " +
            "restart delays accumulate over the life of the machine.");

         Assert.IsTrue(Regex.IsMatch(extension, @"failure\s+hMailServer[^']*actions=\s*restart/", RegexOptions.IgnoreCase),
            "sc.exe failure is invoked without a restart action.");
      }

      [Test]
      [Description("The service stop the installer performs before replacing files is bounded, and a failure to stop blocks the install.")]
      public void TestServiceStopIsBoundedAndBlocking()
      {
         string extension = ReadInstallerScript("hMailServerInnoExtension.iss");

         // The original was: while (IsServiceStopped('hMailServer') = false) do
         // Sleep(250); - no limit, no check of StopService's result, and no effect on
         // whether the install proceeded. A service that will not stop then means Inno
         // cannot replace hMailServer.exe; if the user answers Ignore, the old binary
         // stays while DBUpdater takes the schema forward, and the server will not run
         // against either version afterwards.
         Assert.IsFalse(Regex.IsMatch(extension,
               @"while\s*\(\s*IsServiceStopped\s*\(\s*'hMailServer'\s*\)\s*=\s*false\s*\)\s*do\s*begin\s*Sleep\s*\(\s*\d+\s*\)\s*;\s*end",
               RegexOptions.IgnoreCase | RegexOptions.Singleline),
            "The installer waits for the hMailServer service to stop in a loop with no upper bound. A service " +
            "stuck in STOP_PENDING freezes the wizard with no message and no way to cancel.");

         Assert.IsTrue(Regex.IsMatch(extension, @"function\s+StopHMailServerService\s*\(\s*\)\s*:\s*Boolean",
                                     RegexOptions.IgnoreCase),
            "StopHMailServerService is gone. Stopping the service has to be a function that can fail, because " +
            "NextButtonClick must be able to refuse to continue when the service is still running.");

         Assert.IsTrue(Regex.IsMatch(extension, @"StopHMailServerService\s*\(\s*\)\s*=\s*false\s*\)\s*then\s*Result\s*:=\s*false",
                                     RegexOptions.IgnoreCase | RegexOptions.Singleline),
            "NextButtonClick does not refuse to advance when the service could not be stopped. Continuing " +
            "leads to Inno's Retry/Ignore prompt on a locked hMailServer.exe, and Ignore leaves the old " +
            "server binary installed against an upgraded database schema.");
      }

      [Test]
      [Description("Guards a currently-correct invariant: the version stamped into the installer matches the server being installed.")]
      public void TestInstallerVersionMatchesTheServerVersion()
      {
         Match version = Regex.Match(ReadRepositoryFile(@"hmailserver\source\Server\Common\Application\Version.h"),
                                     @"#define\s+HMAILSERVER_VERSION\s+""([0-9.]+)""");
         Assert.IsTrue(version.Success, "Could not read HMAILSERVER_VERSION from Version.h.");
         string serverVersion = version.Groups[1].Value;

         string setup = ReadInstallerScript("section_setup_64.iss");

         // RELEASE.md step 6 stamps Version.h and section_setup_64.iss by hand. When
         // they drift, the installed file's version and the product's version disagree
         // and no support conversation about "which build is this" can be trusted.
         foreach (string key in new[] { "AppVersion", "VersionInfoVersion", "AppVerName", "OutputBaseFilename" })
         {
            Match line = Regex.Match(setup, @"^\s*" + key + @"\s*=\s*(.+?)\s*$", RegexOptions.Multiline);

            Assert.IsTrue(line.Success, key + " is missing from section_setup_64.iss.");
            Assert.IsTrue(line.Groups[1].Value.Contains(serverVersion),
               "section_setup_64.iss " + key + " is \"" + line.Groups[1].Value + "\" but Version.h says " +
               serverVersion + ".");
         }
      }
   }
}
