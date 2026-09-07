// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.CodeDom.Compiler;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.API
{
   /// <summary>
   ///    The unattended part: the last of the roadmap's live update. With
   ///    UpdateCheckEnabled=1 the scheduled task reads the feed when it is due, fetches
   ///    and verifies the installer when UpdateAutoDownload says so, and applies it -
   ///    after a backup, when UpdateBackupBeforeApply says so - when UpdateWindow is
   ///    open. Every step here is the same code a click runs; what is tested is that
   ///    the task takes them on its own, and only when it may.
   /// </summary>
   [TestFixture]
   public class UpdateSchedule : TestFixtureBase
   {
      private const int RestPort = 9104;
      private const string AdminPassword = "testar";
      private const string LatestPath = "/repos/Progressiverobot/hmailserver/releases/latest";

      private static string _quiet, _marker;

      private FakeHttpEndpoint _feed;
      private FakeSigstore _sigstore;
      private string _updatesDirectory;
      private string _backupDirectory;
      private string _previousBackupDestination;

      [SetUp]
      public void StartFeed()
      {
         CompileQuietInstaller();
         File.Delete(_marker);

         _sigstore = new FakeSigstore();
         _updatesDirectory = Paths.Combine(_settings.Directories.DataDirectory, "Updates");
         CleanUpdates();
         _backupDirectory = Path.Combine(Path.GetTempPath(), "hm-update-backup-" + Guid.NewGuid().ToString("N"));
         _previousBackupDestination = _settings.Backup.Destination;

         _feed = new FakeHttpEndpoint(200, "{}");
         ServeRelease(_quiet);

         _settings.SetAdministratorPassword(AdminPassword);
         WriteSetting("UpdateCheckEnabled", "0");
         WriteSetting("UpdateChannel", "stable");
         WriteSetting("UpdateFeedUrl", _feed.UrlFor(LatestPath));
         WriteSetting("UpdateTrustRootsFile", _sigstore.WriteTrustRootsFile());
         WriteSetting("UpdateLogPublicKeyFile", _sigstore.WriteLogKeyFile());
         WriteSetting("UpdateRequireAuthenticode", "0");
         WriteSetting("UpdateServiceWaitSeconds", "8");
         WriteSetting("UpdateAutoDownload", "0");
         WriteSetting("UpdateWindow", "");
         WriteSetting("UpdateBackupBeforeApply", "1");
         WriteSetting("RestApiBindAddress", "127.0.0.1");
         WriteSetting("RestApiPort", RestPort.ToString());
         _application.Reinitialize();
      }

      [TearDown]
      public void StopFeed()
      {
         // Every installer here is the quiet one, so an outcome is 'ok': let the
         // reinitialize below report it, which also takes the state out of
         // 'installing' for the next test.
         bool outcomePending = File.Exists(OutcomePath);
         WriteSetting("UpdateCheckEnabled", "0");
         WriteSetting("UpdateFeedUrl", "");
         WriteSetting("UpdateTrustRootsFile", "");
         WriteSetting("UpdateLogPublicKeyFile", "");
         WriteSetting("UpdateRequireAuthenticode", "0");
         WriteSetting("UpdateServiceWaitSeconds", "180");
         WriteSetting("UpdateAutoDownload", "0");
         WriteSetting("UpdateWindow", "");
         WriteSetting("UpdateBackupBeforeApply", "1");
         WriteSetting("RestApiPort", "0");
         _settings.Backup.Destination = _previousBackupDestination;
         _application.Reinitialize();
         if (outcomePending)
            WaitUntil(() => File.Exists(OutcomePath + ".reported"), 20, "The outcome was not reported.");
         _feed.Dispose();
         _sigstore.Dispose();
         CleanUpdates();
         if (Directory.Exists(_backupDirectory))
            Directory.Delete(_backupDirectory, true);
      }

      [Test]
      public void TheWindowIsReportedAsWrittenAndAsRead()
      {
         // Every day, an hour from the start.
         WriteSetting("UpdateWindow", "03:00");
         _application.Reinitialize();
         string body = Http("GET", "/api/v1/update").body;
         StringAssert.Contains("\"window\":{\"text\":\"every day 03:00-04:00\",\"open\":" + (IsBetween(3, 0, 4, 0) ? "true" : "false") + ",\"error\":\"\"}", body);
         StringAssert.Contains("\"autoDownload\":false", body);
         StringAssert.Contains("\"backupBeforeApply\":true", body);

         // Days and a range, in any case and order; read back Sunday-first, as the week is counted.
         WriteSetting("UpdateWindow", "sat, Sun 02:00-05:00");
         _application.Reinitialize();
         body = Http("GET", "/api/v1/update").body;
         StringAssert.Contains("\"window\":{\"text\":\"Sun,Sat 02:00-05:00\"", body);

         // Not a day, not a time: said, not guessed.
         WriteSetting("UpdateWindow", "Someday 03:00");
         _application.Reinitialize();
         body = Http("GET", "/api/v1/update").body;
         StringAssert.Contains("\"window\":{\"text\":\"Someday 03:00\",\"open\":false,\"error\":\"UpdateWindow: 'someday' is not a day", body);

         WriteSetting("UpdateWindow", "25:00");
         _application.Reinitialize();
         body = Http("GET", "/api/v1/update").body;
         StringAssert.Contains("\"error\":\"UpdateWindow: '25:00' is not a time; use HH:MM.\"", body);

         // Empty: never.
         WriteSetting("UpdateWindow", "");
         _application.Reinitialize();
         body = Http("GET", "/api/v1/update").body;
         StringAssert.Contains("\"window\":{\"text\":\"\",\"open\":false,\"error\":\"\"}", body);
      }

      [Test]
      public void AnUpdateIsAppliedUnattendedInsideTheWindow()
      {
         WriteSetting("UpdateCheckEnabled", "1");
         WriteSetting("UpdateAutoDownload", "1");
         WriteSetting("UpdateBackupBeforeApply", "0");
         WriteSetting("UpdateWindow", WindowAroundNow());
         _application.Reinitialize();

         // The startup task: the check finds the release, the download verifies it,
         // the window is open, the helper is started and the quiet installer runs.
         string outcome = WaitForOutcome(OutcomePath, 90);
         StringAssert.StartsWith("ok " + Newer, outcome);

         string log = LogHandler.ReadCurrentDefaultLog();
         StringAssert.Contains("Update check: hMailServer " + Newer + " is available", log);
         StringAssert.Contains("Update: hMailServer " + Newer + " downloaded to", log);
         StringAssert.Contains("is open and hMailServer " + Newer + " is verified; applying it unattended", log);
         StringAssert.Contains("quiet /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /upgradetoken=", File.ReadAllText(_marker));
         Assert.AreEqual(1, CountRequests("GET " + LatestPath), "The feed was read once by the startup task.");
      }

      [Test]
      public void NothingIsAppliedOutsideTheWindow()
      {
         WriteSetting("UpdateCheckEnabled", "1");
         WriteSetting("UpdateAutoDownload", "1");
         WriteSetting("UpdateBackupBeforeApply", "0");
         WriteSetting("UpdateWindow", WindowAwayFromNow());
         _application.Reinitialize();

         // The task checks and downloads, and stops there.
         var status = _application.Status;
         WaitUntil(() => status.UpdateState == 3, 60, "The task did not download and verify the installer. State: " + status.UpdateState + " " + status.UpdateLastError);
         Thread.Sleep(3000);
         Assert.IsFalse(File.Exists(OutcomePath), "Nothing may be applied outside the window.");
         Assert.AreEqual(3, status.UpdateState);
         StringAssert.DoesNotContain("applying it unattended", LogHandler.ReadCurrentDefaultLog());
      }

      [Test]
      public void TheDownloadIsNotAutomaticUnlessAsked()
      {
         WriteSetting("UpdateCheckEnabled", "1");
         WriteSetting("UpdateAutoDownload", "0");
         WriteSetting("UpdateBackupBeforeApply", "0");
         WriteSetting("UpdateWindow", WindowAroundNow());
         _application.Reinitialize();

         var status = _application.Status;
         WaitUntil(() => status.UpdateState == 2, 30, "The task did not check.");
         Thread.Sleep(3000);
         Assert.AreEqual(2, status.UpdateState, "Known, not fetched: UpdateAutoDownload is off.");
         Assert.AreEqual(0, CountRequests("GET /download/"), "Nothing was fetched.");
         Assert.IsFalse(File.Exists(OutcomePath));
      }

      [Test]
      public void TheBackupComesFirstAndItsAbsenceStopsTheApply()
      {
         WriteSetting("UpdateCheckEnabled", "1");
         WriteSetting("UpdateAutoDownload", "1");
         WriteSetting("UpdateBackupBeforeApply", "1");
         WriteSetting("UpdateWindow", WindowAroundNow());
         _settings.Backup.Destination = "";
         _application.Reinitialize();

         // Verified and waiting, but no destination to back up to: not applied, and
         // the log says what to do about it.
         var status = _application.Status;
         WaitUntil(() => status.UpdateState == 3, 60, "The task did not download and verify the installer.");
         WaitUntil(() => LogHandler.ReadCurrentDefaultLog().Contains("UpdateBackupBeforeApply is on and no backup destination is configured"), 20,
            "The task did not say why it stopped.");
         Thread.Sleep(2000);
         Assert.IsFalse(File.Exists(OutcomePath), "No backup, no apply.");

         // With a destination the backup runs, completes, and the apply follows.
         Directory.CreateDirectory(_backupDirectory);
         _settings.Backup.Destination = _backupDirectory;
         _settings.Backup.BackupSettings = true;
         _settings.Backup.BackupDomains = false;
         _settings.Backup.BackupMessages = false;
         _application.Reinitialize();

         string outcome = WaitForOutcome(OutcomePath, 120);
         StringAssert.StartsWith("ok " + Newer, outcome);
         string log = LogHandler.ReadCurrentDefaultLog();
         StringAssert.Contains("Update: backing up before applying hMailServer " + Newer, log);
         StringAssert.Contains("Update: the backup completed; applying hMailServer " + Newer, log);
         Assert.IsNotEmpty(Directory.GetFileSystemEntries(_backupDirectory), "The backup was written to the destination.");
      }

      // ---- the fixture ---------------------------------------------------------------

      private static void CompileQuietInstaller()
      {
         if (_quiet != null)
            return;
         string directory = Path.Combine(Path.GetTempPath(), "hm-fake-installers");
         Directory.CreateDirectory(directory);
         _marker = Path.Combine(directory, "ran-schedule.log");
         string source =
            "using System;\nusing System.IO;\nclass FakeInstaller\n{\n   static int Main(string[] args)\n   {\n" +
            "      File.AppendAllText(@\"" + _marker + "\", \"quiet \" + string.Join(\" \", args) + Environment.NewLine);\n" +
            "      return 0;\n   }\n}\n";
         string path = Path.Combine(directory, "quiet-schedule.exe");
         using (CodeDomProvider provider = CodeDomProvider.CreateProvider("CSharp"))
         {
            var parameters = new CompilerParameters {GenerateExecutable = true, OutputAssembly = path, GenerateInMemory = false};
            parameters.ReferencedAssemblies.Add("System.dll");
            CompilerResults results = provider.CompileAssemblyFromSource(parameters, source);
            if (results.Errors.HasErrors)
               Assert.Fail("The fake installer did not compile: " + results.Errors[0]);
         }
         _quiet = path;
      }

      private string Running
      {
         get { return ((string) _application.Version).Split('-')[0]; }
      }

      private string Newer
      {
         get
         {
            string[] parts = Running.Split('.');
            return parts[0] + "." + parts[1] + "." + (int.Parse(parts[2]) + 1);
         }
      }

      private string OutcomePath
      {
         get { return Paths.Combine(_updatesDirectory, "last-apply.txt"); }
      }

      // "HH:MM-HH:MM" around now, every day: an hour either side, which never
      // crosses a day boundary in a way the window cannot express.
      private static string WindowAroundNow()
      {
         DateTime now = DateTime.Now;
         return now.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture) + "-" + now.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture);
      }

      private static string WindowAwayFromNow()
      {
         DateTime now = DateTime.Now;
         return now.AddHours(3).ToString("HH:mm", CultureInfo.InvariantCulture) + "-" + now.AddHours(4).ToString("HH:mm", CultureInfo.InvariantCulture);
      }

      private static bool IsBetween(int fromHour, int fromMinute, int toHour, int toMinute)
      {
         int minutes = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
         return minutes >= fromHour * 60 + fromMinute && minutes < toHour * 60 + toMinute;
      }

      private void ServeRelease(string installerExe)
      {
         byte[] installer = File.ReadAllBytes(installerExe);
         string name = "hMailServer-" + Newer + "-x64.exe";
         string rollbackName = "hMailServer-" + Running + "-x64.exe";

         _feed.ClearRoutes();
         _feed.SetResponse("/download/" + name, 200, installer);
         _feed.SetResponse("/download/" + name + ".cosign.bundle", 200, Encoding.UTF8.GetBytes(_sigstore.Bundle(installer)), "application/json");
         _feed.SetResponse(200, Release(Newer, name, installer));
         // The same quiet program as the rollback image, so the apply has one.
         _feed.SetResponse("/download/" + rollbackName, 200, installer);
         _feed.SetResponse("/download/" + rollbackName + ".cosign.bundle", 200, Encoding.UTF8.GetBytes(_sigstore.Bundle(installer)), "application/json");
         _feed.SetResponse("/repos/Progressiverobot/hmailserver/releases/tags/v" + Running, 200, Encoding.UTF8.GetBytes(Release(Running, rollbackName, installer)), "application/json");
      }

      private string Release(string version, string name, byte[] installer)
      {
         string download = _feed.UrlFor("/download/");
         string digest = "sha256:" + FakeSigstore.Hex(SHA256.Create().ComputeHash(installer));
         return "{\"html_url\":\"https://github.com/Progressiverobot/hmailserver/releases/tag/v" + version + "\"," +
                "\"tag_name\":\"v" + version + "\",\"name\":\"hMailServer " + version + "\",\"draft\":false,\"prerelease\":false," +
                "\"published_at\":\"2026-09-12T10:00:00Z\",\"assets\":[" +
                "{\"name\":\"" + name + "\",\"size\":" + installer.Length + ",\"digest\":\"" + digest + "\",\"browser_download_url\":\"" + download + name + "\"}," +
                "{\"name\":\"" + name + ".cosign.bundle\",\"size\":1,\"browser_download_url\":\"" + download + name + ".cosign.bundle\"}" +
                "]}";
      }

      private int CountRequests(string fragment)
      {
         int count = 0;
         foreach (string request in _feed.Requests)
            if (request.Contains(fragment))
               count++;
         return count;
      }

      private static string WaitForOutcome(string path, int seconds)
      {
         WaitUntil(() => File.Exists(path), seconds, "The helper wrote no outcome to " + path + " within " + seconds + " seconds.");
         Thread.Sleep(200);
         return File.ReadAllText(path);
      }

      private static void WaitUntil(Func<bool> condition, int seconds, string message)
      {
         DateTime deadline = DateTime.Now.AddSeconds(seconds);
         while (!condition())
         {
            if (DateTime.Now > deadline)
               Assert.Fail(message);
            Thread.Sleep(250);
         }
      }

      private void CleanUpdates()
      {
         if (!Directory.Exists(_updatesDirectory))
            return;
         foreach (string file in Directory.GetFiles(_updatesDirectory, "*", SearchOption.AllDirectories))
         {
            try
            {
               File.Delete(file);
            }
            catch (IOException)
            {
            }
         }
      }

      private static void WriteSetting(string key, string value)
      {
         IniFileSetting.Write(key, value);
      }

      private static (int code, string body) Http(string method, string path)
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", RestPort);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  Thread.Sleep(200);
               }
            }
            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               string request =
                  method + " " + path + " HTTP/1.0\r\n" +
                  "Host: 127.0.0.1\r\n" +
                  "Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword)) + "\r\n" +
                  "Connection: close\r\n\r\n";
               byte[] bytes = Encoding.ASCII.GetBytes(request);
               stream.Write(bytes, 0, bytes.Length);

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               int code = 0;
               Match status = Regex.Match(raw, "^HTTP/1\\.[01] (\\d{3})");
               if (status.Success)
                  code = int.Parse(status.Groups[1].Value);
               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               return (code, separator >= 0 ? raw.Substring(separator + 4) : "");
            }
         }
      }
   }
}
