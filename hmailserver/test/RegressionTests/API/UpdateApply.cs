// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Linq;
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
   ///    The apply: the third part of the roadmap's live update. The server hands the
   ///    verified installer to hMailServer.Updater, which runs it, waits for the service
   ///    to come back, rolls back if it does not, and writes an outcome the service
   ///    reports at its next start. The "installers" here are programs the fixture
   ///    compiles on the spot - one that does nothing, one that stops the service, one
   ///    that starts it, one that fails - served from the fake feed and signed by the
   ///    suite's own Sigstore, so the whole path runs: download, verify, token, helper,
   ///    installer, wait, rollback, outcome, report.
   /// </summary>
   [TestFixture]
   public class UpdateApply : TestFixtureBase
   {
      private const int RestPort = 9104;
      private const string AdminPassword = "testar";
      private const string LatestPath = "/repos/Progressiverobot/hmailserver/releases/latest";

      private static string _quiet, _stopper, _starter, _failing, _ager, _marker;

      private FakeHttpEndpoint _feed;
      private FakeSigstore _sigstore;
      private string _updatesDirectory;

      [SetUp]
      public void StartFeed()
      {
         CompileFakeInstallers();
         File.Delete(_marker);

         _sigstore = new FakeSigstore();
         _updatesDirectory = Paths.Combine(_settings.Directories.DataDirectory, "Updates");
         CleanUpdates();
         if (TokenListed())
            RestartServerAndReacquireCom();

         _feed = new FakeHttpEndpoint(200, "{}");
         ServeRelease(_quiet, _starter);

         _settings.SetAdministratorPassword(AdminPassword);
         WriteSetting("UpdateCheckEnabled", "0");
         WriteSetting("UpdateChannel", "stable");
         WriteSetting("UpdateFeedUrl", _feed.UrlFor(LatestPath));
         WriteSetting("UpdateTrustRootsFile", _sigstore.WriteTrustRootsFile());
         WriteSetting("UpdateLogPublicKeyFile", _sigstore.WriteLogKeyFile());
         WriteSetting("UpdateRequireAuthenticode", "0");
         WriteSetting("UpdateServiceWaitSeconds", "8");
         WriteSetting("RestApiBindAddress", "127.0.0.1");
         WriteSetting("RestApiPort", RestPort.ToString());
         _application.Reinitialize();
      }

      [TearDown]
      public void StopFeed()
      {
         // An outcome the test did not ask to have reported must not be reported by
         // the reinitialize below: a non-ok one is an ERROR, and the next test's
         // setup would fail on it.
         if (File.Exists(OutcomePath))
            File.Delete(OutcomePath);
         WriteSetting("UpdateCheckEnabled", "0");
         WriteSetting("UpdateFeedUrl", "");
         WriteSetting("UpdateTrustRootsFile", "");
         WriteSetting("UpdateLogPublicKeyFile", "");
         WriteSetting("UpdateRequireAuthenticode", "0");
         WriteSetting("UpdateServiceWaitSeconds", "180");
         WriteSetting("RestApiPort", "0");
         _application.Reinitialize();
         _feed.Dispose();
         _sigstore.Dispose();
         CleanUpdates();
         if (TokenListed())
            RestartServerAndReacquireCom();
      }

      [Test]
      public void AVerifiedInstallerIsHandedToTheHelperWhichRunsItSilently()
      {
         var status = Downloaded();

         Assert.IsTrue(status.InstallUpdate(), "The helper must start. Error: " + status.UpdateLastError);
         Assert.AreEqual(4, status.UpdateState, "State 4 is 'installing'.");
         StringAssert.Contains("Update: hMailServer " + Newer + " is being applied by " + Paths.Combine(_updatesDirectory, "hMailServer.Updater.exe"),
            LogHandler.ReadCurrentDefaultLog());

         string outcome = WaitForOutcome(OutcomePath, 60);
         StringAssert.StartsWith("ok " + Newer + " the installer succeeded and the service is running", outcome);

         // What the installer was run with: silent, no restart, the token, a log.
         string ran = File.ReadAllText(_marker);
         StringAssert.Contains("quiet /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /upgradetoken=", ran);
         StringAssert.Contains("/LOG=" + Paths.Combine(_updatesDirectory, "install-" + Newer + ".log"), ran);
         StringAssert.DoesNotContain("starter", ran, "The service never stopped, so nothing was rolled back.");

         Assert.IsTrue(File.Exists(Paths.Combine(_updatesDirectory, "hMailServer.Updater.exe")), "The helper runs from a copy outside Bin.");
         Assert.IsTrue(File.Exists(Paths.Combine(_updatesDirectory, "apply-" + Newer + ".log")), "The helper keeps a log.");
         string rollback = Paths.Combine(_updatesDirectory, "rollback", "hMailServer-" + Running + "-x64.exe");
         Assert.IsTrue(File.Exists(rollback), "The running version's installer was fetched as the rollback image: " + rollback);
         Assert.IsTrue(File.Exists(rollback + ".cosign.bundle"));
         StringAssert.Contains("Update: the rollback image for hMailServer " + Running + " is " + rollback + ", verified.", LogHandler.ReadCurrentDefaultLog());

         // The token the installer was given authenticates the administrator once.
         string token = Regex.Match(ran, "/upgradetoken=([0-9a-f]{64})").Groups[1].Value;
         Assert.AreEqual(64, token.Length, "A 32-byte token, hex: " + ran);
         Assert.IsNotNull(new Application().Authenticate("Administrator", "token:" + token), "The token is the administrator, once.");
         Assert.IsNull(new Application().Authenticate("Administrator", "token:" + token), "And only once.");
         Assert.IsFalse(TokenListed(), "A redeemed token is gone.");
         StringAssert.Contains("Update apply token redeemed", LogHandler.ReadCurrentDefaultLog());

         // The outcome is reported when the service next starts; a reinitialize runs
         // the same startup task.
         _application.Reinitialize();
         WaitUntil(() => File.Exists(OutcomePath + ".reported"), 30, "The outcome was not reported at startup.");
         StringAssert.Contains("Update applied: hMailServer " + Newer + " is installed", LogHandler.ReadCurrentDefaultLog());
         status = _application.Status;
         StringAssert.StartsWith("ok " + Newer + ": the installer succeeded", status.UpdateApplyOutcome);
         (int code, string body) = Http("GET", "/api/v1/update");
         Assert.AreEqual(200, code, body);
         StringAssert.Contains("\"apply\":{\"status\":\"ok\",\"version\":\"" + Newer + "\",\"detail\":\"the installer succeeded", body);
      }

      [Test]
      public void AServiceThatDoesNotComeBackIsRolledBack()
      {
         // Captured now: the service is about to stop and start again, and the
         // fixture's COM proxy will not survive it.
         string newer = Newer;
         string running = Running;

         ServeRelease(_stopper, _starter);
         var status = Downloaded();
         Assert.IsTrue(status.InstallUpdate(), status.UpdateLastError);

         // The helper: runs the stopper, the service stops; waits UpdateServiceWaitSeconds;
         // runs the rollback image, which starts the service; the service's startup task
         // reports the outcome and renames the file.
         WaitUntil(() => File.Exists(OutcomePath + ".reported"), 120, "The rollback did not complete and get reported.");
         string outcome = File.ReadAllText(OutcomePath + ".reported");
         StringAssert.StartsWith("rolled-back " + newer + " the installer succeeded but the service was not running after 8 seconds", outcome);
         StringAssert.Contains("hMailServer " + running + " was reinstalled and the service is running", outcome);

         string ran = File.ReadAllText(_marker);
         StringAssert.Contains("stopper /VERYSILENT", ran);
         StringAssert.Contains("starter /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /LOG=", ran);
         StringAssert.DoesNotContain("starter /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /upgradetoken", ran,
            "The rollback runs without the token: the version it restores already matches the schema.");

         // The service was restarted behind the harness's back; put the harness back
         // in step with it before anything else goes through COM.
         RestartServerAndReacquireCom();

         StringAssert.Contains("Update to hMailServer " + newer + " did not succeed (rolled-back)", LogHandler.ReadCurrentDefaultLog());
         StringAssert.Contains("Update to hMailServer " + newer + " did not succeed (rolled-back)", LogHandler.ReadErrorLog(),
            "A rollback is an ERROR the administrator must see.");
         Console.WriteLine("hMailServer error log (expected for this fixture):");
         Console.WriteLine(LogHandler.ReadErrorLog());
         LogHandler.ClearErrorLogUntilSettled();
      }

      [Test]
      public void AnInstallerThatFailsWhileTheServiceStaysUpIsNotRolledBack()
      {
         ServeRelease(_failing, _starter);
         var status = Downloaded();
         Assert.IsTrue(status.InstallUpdate(), status.UpdateLastError);

         string outcome = WaitForOutcome(OutcomePath, 60);
         StringAssert.StartsWith("failed " + Newer + " the installer exited with 1 but the service is running", outcome);
         StringAssert.DoesNotContain("starter", File.ReadAllText(_marker), "The service never stopped: no rollback.");

         // Reported as an ERROR at the next start, which a reinitialize stands in for.
         _application.Reinitialize();
         WaitUntil(() => File.Exists(OutcomePath + ".reported"), 30, "The outcome was not reported at startup.");
         StringAssert.Contains("Update to hMailServer " + Newer + " did not succeed (failed)", LogHandler.ReadErrorLog());
         Console.WriteLine("hMailServer error log (expected for this fixture):");
         Console.WriteLine(LogHandler.ReadErrorLog());
         LogHandler.ClearErrorLogUntilSettled();
      }

      [Test]
      public void NoRollbackImageMeansTheOutcomeSaysSo()
      {
         string newer = Newer;
         string running = Running;

         ServeRelease(_stopper, null);
         var status = Downloaded();
         Assert.IsTrue(status.InstallUpdate(), status.UpdateLastError);
         StringAssert.Contains("Update: no rollback image for hMailServer " + running + " could be had (the release feed answered HTTP 404",
            LogHandler.ReadCurrentDefaultLog());

         // The service stops and nothing brings it back; the helper says so.
         string outcome = WaitForOutcome(OutcomePath, 60);
         StringAssert.StartsWith("no-rollback " + newer + " the installer succeeded but the service was not running after 8 seconds", outcome);
         StringAssert.Contains("no rollback image was available", outcome);

         // Bring it back ourselves; its startup reports the outcome as an error.
         RestartServerAndReacquireCom();
         WaitUntil(() => File.Exists(OutcomePath + ".reported"), 30, "The outcome was not reported at startup.");
         StringAssert.Contains("Update to hMailServer " + newer + " did not succeed (no-rollback)", LogHandler.ReadErrorLog());
         Console.WriteLine("hMailServer error log (expected for this fixture):");
         Console.WriteLine(LogHandler.ReadErrorLog());
         LogHandler.ClearErrorLogUntilSettled();
      }

      [Test]
      public void NothingIsAppliedWithoutAVerifiedDownload()
      {
         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState);

         Assert.IsFalse(status.InstallUpdate(), "Nothing has been downloaded and verified.");
         StringAssert.Contains("No verified installer is waiting", status.UpdateLastError);
         Assert.IsFalse(File.Exists(OutcomePath));
         Assert.IsFalse(TokenListed(), "No token is issued for an apply that did not start.");
      }

      [Test]
      public void AnInstallerChangedSinceItWasVerifiedIsRefusedAndDeleted()
      {
         var status = Downloaded();
         string path = status.UpdateInstallerPath;
         byte[] bytes = File.ReadAllBytes(path);
         bytes[100] ^= 0xFF;
         File.WriteAllBytes(path, bytes);

         Assert.IsFalse(status.InstallUpdate(), "A file that no longer matches its bundle must not run.");
         StringAssert.Contains("The installer no longer verifies against its bundle and has been deleted", status.UpdateLastError);
         Assert.IsFalse(File.Exists(path));
         Assert.IsFalse(File.Exists(OutcomePath));
      }

      [Test]
      public void TheTokenExpiresAfterAnHour()
      {
         // The "installer" here ages the token file by two hours: it runs as the
         // service account, which may touch the file, where the suite may not.
         ServeRelease(_ager, _starter);
         var status = Downloaded();
         Assert.IsTrue(status.InstallUpdate(), status.UpdateLastError);
         WaitForOutcome(OutcomePath, 60);

         Assert.IsTrue(TokenListed(), "The ager never redeemed the token.");
         string token = Regex.Match(File.ReadAllText(_marker), "ager .*?/upgradetoken=([0-9a-f]{64})").Groups[1].Value;
         Assert.AreEqual(64, token.Length, "The helper handed the installer a 32-byte token.");

         Assert.IsNull(new Application().Authenticate("Administrator", "token:" + token), "An hour is the token's life.");
         Assert.IsFalse(TokenListed(), "A stale token is removed when it is presented.");
         StringAssert.Contains("Update apply token refused: it had expired", LogHandler.ReadCurrentDefaultLog());

         // And a wrong token, right shape, is refused without touching a fresh one.
         ServeRelease(_quiet, _starter);
         Assert.IsTrue(status.CheckForUpdate() && status.DownloadUpdate() && status.InstallUpdate(), status.UpdateLastError);
         WaitForOutcome(OutcomePath, 60);
         Assert.IsNull(new Application().Authenticate("Administrator", "token:" + new string('0', 64)));
         Assert.IsTrue(TokenListed(), "A wrong guess does not burn the token.");
      }

      [Test]
      public void AnApplyCanBeRunOverRest()
      {
         Downloaded();
         (int code, string body) = Http("POST", "/api/v1/update/install");
         Assert.AreEqual(200, code, body);
         StringAssert.Contains("\"state\":4", body);
         StringAssert.Contains("\"stateName\":\"installing\"", body);
         string outcome = WaitForOutcome(OutcomePath, 60);
         StringAssert.StartsWith("ok " + Newer, outcome);
      }

      // ---- the fixture's installers ---------------------------------------------------

      private static void CompileFakeInstallers()
      {
         if (_quiet != null)
            return;

         string directory = Paths.Combine(Path.GetTempPath(), "hm-fake-installers");
         Directory.CreateDirectory(directory);
         _marker = Paths.Combine(directory, "ran.log");

         _quiet = Compile(directory, "quiet", "", 0);
         _stopper = Compile(directory, "stopper",
            "using (var s = new System.ServiceProcess.ServiceController(\"hMailServer\")) { s.Stop(); s.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(90)); }", 0);
         _starter = Compile(directory, "starter",
            "using (var s = new System.ServiceProcess.ServiceController(\"hMailServer\")) { s.Start(); s.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(90)); }", 0);
         _failing = Compile(directory, "failing", "", 1);
         // Runs as the service account, so it can do what the suite cannot: age the
         // token file, which only SYSTEM, Administrators and that account may touch.
         _ager = Compile(directory, "ager",
            "File.SetLastWriteTimeUtc(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, \"apply-token\"), DateTime.UtcNow.AddHours(-2));", 0);
      }

      // A console program that records its name and arguments, does what it is told to
      // the service, and exits with the code it was given: an installer as far as the
      // helper can tell.
      private static string Compile(string directory, string name, string action, int exitCode)
      {
         string source =
            "using System;\n" +
            "using System.IO;\n" +
            "class FakeInstaller\n{\n" +
            "   static int Main(string[] args)\n   {\n" +
            "      File.AppendAllText(@\"" + _marker + "\", \"" + name + " \" + string.Join(\" \", args) + Environment.NewLine);\n" +
            "      " + action + "\n" +
            "      return " + exitCode + ";\n" +
            "   }\n}\n";

         // The name is one of the five words CompileFakeInstallers passes - "quiet",
         // "stopper", "starter", "failing", "ager" - so what is appended here is a file
         // name, never a rooted path that would displace the directory.
         string path = Paths.Combine(directory, name + ".exe");
         using (CodeDomProvider provider = CodeDomProvider.CreateProvider("CSharp"))
         {
            var parameters = new CompilerParameters {GenerateExecutable = true, OutputAssembly = path, GenerateInMemory = false};
            parameters.ReferencedAssemblies.Add("System.dll");
            parameters.ReferencedAssemblies.Add("System.ServiceProcess.dll");
            CompilerResults results = provider.CompileAssemblyFromSource(parameters, source);
            if (results.Errors.HasErrors)
            {
               var errors = new StringBuilder();
               foreach (CompilerError error in results.Errors)
                  errors.AppendLine(error.ToString());
               Assert.Fail("The fake installer '" + name + "' did not compile:\n" + errors);
            }
         }
         return path;
      }

      // ---- the release -----------------------------------------------------------------

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

      private Status Downloaded()
      {
         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), "The feed must be readable. Error: " + status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState, "The fixture's release must be newer than the running version.");
         Assert.IsTrue(status.DownloadUpdate(), "The installer must download and verify. Error: " + status.UpdateLastError);
         Assert.AreEqual(3, status.UpdateState);
         return status;
      }

      // The newer release's installer is the program at installerExe; the running
      // version's release - the rollback image - is the program at rollbackExe, or
      // absent from the feed when null.
      private void ServeRelease(string installerExe, string rollbackExe)
      {
         byte[] installer = File.ReadAllBytes(installerExe);
         string name = "hMailServer-" + Newer + "-x64.exe";

         _feed.ClearRoutes();
         _feed.SetResponse("/download/" + name, 200, installer);
         _feed.SetResponse("/download/" + name + ".cosign.bundle", 200, Encoding.UTF8.GetBytes(_sigstore.Bundle(installer)), "application/json");
         _feed.SetResponse(200, Release(Newer, name, installer));

         if (rollbackExe != null)
         {
            byte[] rollback = File.ReadAllBytes(rollbackExe);
            string rollbackName = "hMailServer-" + Running + "-x64.exe";
            _feed.SetResponse("/download/" + rollbackName, 200, rollback);
            _feed.SetResponse("/download/" + rollbackName + ".cosign.bundle", 200, Encoding.UTF8.GetBytes(_sigstore.Bundle(rollback)), "application/json");
            _feed.SetResponse("/repos/Progressiverobot/hmailserver/releases/tags/v" + Running, 200, Encoding.UTF8.GetBytes(Release(Running, rollbackName, rollback)), "application/json");
         }
         else
            _feed.SetResponse("/repos/Progressiverobot/hmailserver/releases/tags/v" + Running, 404, Encoding.UTF8.GetBytes("{\"message\":\"Not Found\"}"), "application/json");
      }

      private string Release(string version, string name, byte[] installer)
      {
         string download = _feed.UrlFor("/download/");
         string digest;
         using (var sha = SHA256.Create())
            digest = "sha256:" + FakeSigstore.Hex(sha.ComputeHash(installer));
         return "{\"html_url\":\"https://github.com/Progressiverobot/hmailserver/releases/tag/v" + version + "\"," +
                "\"tag_name\":\"v" + version + "\",\"name\":\"hMailServer " + version + "\",\"draft\":false,\"prerelease\":false," +
                "\"published_at\":\"2026-09-12T10:00:00Z\",\"assets\":[" +
                "{\"name\":\"" + name + "\",\"size\":" + installer.Length + ",\"digest\":\"" + digest + "\",\"browser_download_url\":\"" + download + name + "\"}," +
                "{\"name\":\"" + name + ".cosign.bundle\",\"size\":1,\"browser_download_url\":\"" + download + name + ".cosign.bundle\"}" +
                "]}";
      }

      private static string WaitForOutcome(string path, int seconds)
      {
         WaitUntil(() => File.Exists(path), seconds, "The helper wrote no outcome to " + path + " within " + seconds + " seconds.");
         // Written in one go, but read it once it is complete.
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

      // apply-token is written with a DACL of SYSTEM, Administrators and the
      // service account, and the suite runs as none of those. File.Exists then
      // answers false whether or not the file is there, because it cannot read the
      // attributes - so presence is read off the directory listing, which needs
      // only the directory. Nothing in the suite can delete it; the server revokes
      // it at start, which is what TearDown relies on.
      private bool TokenListed()
      {
         return Directory.Exists(_updatesDirectory) &&
                Directory.GetFiles(_updatesDirectory).Any(f => Path.GetFileName(f) == "apply-token");
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
               // The helper of the previous test may still hold its log open for a moment.
            }
            catch (UnauthorizedAccessException)
            {
               // apply-token is written by the service with a DACL naming SYSTEM,
               // the Administrators group and the service account only - which is
               // the point of it, since it authenticates as the administrator for
               // an hour. The suite runs as none of those, so it cannot delete the
               // file and does not need to: the server revokes it when the apply
               // finishes, and one never redeemed expires in an hour.
            }
         }
      }

      // ---- plumbing --------------------------------------------------------------------

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
