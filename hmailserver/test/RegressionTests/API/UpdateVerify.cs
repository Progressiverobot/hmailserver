// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
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
   ///    The verify: the second part of the roadmap's live update. The server fetches
   ///    the newer release's installer and its Sigstore bundle, and keeps the installer
   ///    only when the bundle proves it is the one this repository's release workflow
   ///    signed. The authority, the log and the workflow identity are the suite's own
   ///    (FakeSigstore), pointed at through UpdateTrustRootsFile and
   ///    UpdateLogPublicKeyFile; the identity strings are the real ones, so the server's
   ///    defaults for them are what is exercised.
   ///
   ///    Every refusal here is a file the server must not keep: not a file that was
   ///    tampered with, not one signed by another workflow, another authority or an
   ///    expired certificate, not one whose log record is forged. Nothing is run.
   /// </summary>
   [TestFixture]
   public class UpdateVerify : TestFixtureBase
   {
      private const int RestPort = 9104;
      private const string AdminPassword = "testar";
      private const string LatestPath = "/repos/Progressiverobot/hmailserver/releases/latest";

      private FakeHttpEndpoint _feed;
      private FakeSigstore _sigstore;
      private byte[] _installer;
      private string _installerName;
      private string _updatesDirectory;

      [SetUp]
      public void StartFeed()
      {
         _sigstore = new FakeSigstore();
         _installer = new byte[200 * 1024 + 17];
         new Random(4242).NextBytes(_installer);
         _installerName = "hMailServer-" + Newer + "-x64.exe";
         _updatesDirectory = Paths.Combine(_settings.Directories.DataDirectory, "Updates");
         CleanUpdates();

         _feed = new FakeHttpEndpoint(200, "{}");
         ServeRelease(_installer, _sigstore.Bundle(_installer));

         _settings.SetAdministratorPassword(AdminPassword);
         WriteSetting("UpdateCheckEnabled", "0");
         WriteSetting("UpdateChannel", "stable");
         WriteSetting("UpdateFeedUrl", _feed.UrlFor(LatestPath));
         WriteSetting("UpdateTrustRootsFile", _sigstore.WriteTrustRootsFile());
         WriteSetting("UpdateLogPublicKeyFile", _sigstore.WriteLogKeyFile());
         WriteSetting("UpdateRequireAuthenticode", "0");
         WriteSetting("RestApiBindAddress", "127.0.0.1");
         WriteSetting("RestApiPort", RestPort.ToString());
         _application.Reinitialize();
      }

      [TearDown]
      public void StopFeed()
      {
         WriteSetting("UpdateCheckEnabled", "0");
         WriteSetting("UpdateFeedUrl", "");
         WriteSetting("UpdateTrustRootsFile", "");
         WriteSetting("UpdateLogPublicKeyFile", "");
         WriteSetting("UpdateRequireAuthenticode", "0");
         WriteSetting("RestApiPort", "0");
         _application.Reinitialize();
         _feed.Dispose();
         _sigstore.Dispose();
         CleanUpdates();
      }

      [Test]
      public void AVerifiedInstallerIsDownloadedAndKept()
      {
         var status = Checked();

         Assert.IsTrue(status.DownloadUpdate(), "The installer and its bundle were served and the bundle is genuine. Error: " + status.UpdateLastError);

         Assert.AreEqual(3, status.UpdateState, "State 3 is 'downloaded and verified'.");
         string path = status.UpdateInstallerPath;
         Assert.AreEqual(Paths.Combine(_updatesDirectory, _installerName), path);
         Assert.IsTrue(File.Exists(path), "The verified installer is in the data directory's Updates folder.");
         Assert.AreEqual(_installer, File.ReadAllBytes(path), "Byte for byte what was served.");
         Assert.IsTrue(File.Exists(path + ".cosign.bundle"), "The bundle is kept beside it.");
         Assert.IsFalse(File.Exists(path + ".partial"), "Nothing partial is left.");
         Assert.AreEqual(FakeSigstore.ReleaseIdentity, status.UpdateSignerIdentity);
         Assert.IsEmpty(status.UpdateLastError);

         // The installer's URL redirected, as a release asset's does, and the
         // redirect was followed on the loopback.
         StringAssert.Contains("GET /download/" + _installerName + " HTTP/1.0", string.Join("\n", _feed.Requests));
         StringAssert.Contains("GET /objects/" + _installerName + " HTTP/1.0", string.Join("\n", _feed.Requests));

         string log = LogHandler.ReadCurrentDefaultLog();
         StringAssert.Contains("Update: hMailServer " + Newer + " downloaded to " + path + " and verified: signed by " + FakeSigstore.ReleaseIdentity, log);

         (int code, string body) = Http("GET", "/api/v1/update");
         Assert.AreEqual(200, code, body);
         StringAssert.Contains("\"state\":3", body);
         StringAssert.Contains("\"stateName\":\"downloaded\"", body);
         StringAssert.Contains("\"downloaded\":{\"path\":\"" + path.Replace("\\", "\\\\") + "\",\"signer\":\"" + FakeSigstore.ReleaseIdentity + "\",\"logTime\":\"", body);
      }

      [Test]
      public void ARecheckKeepsTheVerifiedDownload()
      {
         var status = Checked();
         Assert.IsTrue(status.DownloadUpdate(), status.UpdateLastError);
         string path = status.UpdateInstallerPath;

         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(3, status.UpdateState, "The same release, still current, still verified on disk: nothing to do again.");
         Assert.AreEqual(path, status.UpdateInstallerPath);

         // A different newer release starts over.
         string another = FakeSigstore_Bump(Newer);
         ServeRelease(_installer, _sigstore.Bundle(_installer), another);
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState);
         Assert.IsEmpty(status.UpdateInstallerPath);
      }

      [Test]
      public void ATamperedInstallerIsDeletedAndRefused()
      {
         // The bundle is genuine for the release's bytes; what is served is not them.
         // The feed's digest is made to agree with what is served, so the bundle is
         // what catches it.
         byte[] served = (byte[]) _installer.Clone();
         served[1000] ^= 0xFF;
         ServeRelease(served, _sigstore.Bundle(_installer));

         var status = Checked();
         Assert.IsFalse(status.DownloadUpdate(), "A file that is not the one signed must be refused.");
         Assert.AreEqual(5, status.UpdateState);
         StringAssert.Contains("this is not the file that was signed", status.UpdateLastError);
         AssertNothingKept();
         StringAssert.Contains("Update download failed for hMailServer " + Newer, LogHandler.ReadCurrentDefaultLog());
      }

      [Test]
      public void TheFeedsDigestMustAgreeWithTheDownload()
      {
         ServeRelease(_installer, _sigstore.Bundle(_installer), digest: "sha256:" + new string('0', 64));

         var status = Checked();
         Assert.IsFalse(status.DownloadUpdate());
         StringAssert.Contains("The feed says the installer's SHA-256 is", status.UpdateLastError);
         AssertNothingKept();
      }

      [Test]
      public void ABundleFromAnotherWorkflowIsRefused()
      {
         ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options
         {
            Identity = "https://github.com/someone-else/hmailserver/.github/workflows/sign-release.yml@refs/heads/master"
         }));

         var status = Checked();
         Assert.IsFalse(status.DownloadUpdate());
         StringAssert.Contains("not this repository's release workflow", status.UpdateLastError);
         AssertNothingKept();
      }

      [Test]
      public void ABundleFromAnotherIssuerOrRepositoryIsRefused()
      {
         ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options {Issuer = "https://accounts.google.com"}));
         var status = Checked();
         Assert.IsFalse(status.DownloadUpdate());
         StringAssert.Contains("issued on a token from https://accounts.google.com", status.UpdateLastError);
         AssertNothingKept();

         ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options {Repository = "https://github.com/someone-else/hmailserver"}));
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.IsFalse(status.DownloadUpdate());
         StringAssert.Contains("is for https://github.com/someone-else/hmailserver, not https://github.com/Progressiverobot/hmailserver", status.UpdateLastError);
         AssertNothingKept();
      }

      [Test]
      public void ABundleFromAnotherAuthorityIsRefused()
      {
         using (var stranger = new FakeSigstore("stranger.test"))
         {
            // The stranger's certificate, recorded in the log this server trusts: the
            // chain is what refuses it.
            ServeRelease(_installer, stranger.Bundle(_installer, new FakeSigstore.Options {Log = _sigstore}));

            var status = Checked();
            Assert.IsFalse(status.DownloadUpdate());
            StringAssert.Contains("does not chain to a trusted authority", status.UpdateLastError);
            AssertNothingKept();
         }
      }

      [Test]
      public void ASignatureRecordedOutsideTheCertificatesLifetimeIsRefused()
      {
         // The log says it recorded the signature an hour after a ten-minute
         // certificate was issued: whoever signed then did not hold that identity.
         ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options
         {
            SignedAt = DateTimeOffset.UtcNow.AddHours(-1),
            IntegratedAt = DateTimeOffset.UtcNow
         }));

         var status = Checked();
         Assert.IsFalse(status.DownloadUpdate());
         StringAssert.Contains("after the certificate had expired", status.UpdateLastError);
         AssertNothingKept();
      }

      [Test]
      public void AForgedLogRecordIsRefused()
      {
         using (var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256))
         {
            // The entry timestamp and the checkpoint signed by a key that is not the log's.
            ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options {OtherLogKey = forger}));
            var status = Checked();
            Assert.IsFalse(status.DownloadUpdate());
            StringAssert.Contains("signed entry timestamp does not verify", status.UpdateLastError);
            AssertNothingKept();

            // No timestamp, and an inclusion proof that does not reach the root.
            ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options {IncludePromise = false, BreakProof = true}));
            Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
            Assert.IsFalse(status.DownloadUpdate());
            StringAssert.Contains("inclusion proof does not lead", status.UpdateLastError);
            AssertNothingKept();

            // Neither: the log has not spoken.
            ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options {IncludePromise = false, IncludeProof = false}));
            Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
            Assert.IsFalse(status.DownloadUpdate());
            StringAssert.Contains("neither a signed entry timestamp nor an inclusion proof", status.UpdateLastError);
            AssertNothingKept();
         }
      }

      [Test]
      public void EitherFormOfTheLogsWordSuffices()
      {
         var status = Checked();

         // The signed entry timestamp alone.
         ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options {IncludeProof = false}));
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.IsTrue(status.DownloadUpdate(), status.UpdateLastError);
         Assert.AreEqual(3, status.UpdateState);
         CleanUpdates();

         // The inclusion proof and checkpoint alone.
         ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options {IncludePromise = false}));
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.IsTrue(status.DownloadUpdate(), status.UpdateLastError);
         Assert.AreEqual(3, status.UpdateState);
         CleanUpdates();

         // And the older bundle form, with a certificate chain.
         ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options {ChainForm = true}));
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.IsTrue(status.DownloadUpdate(), status.UpdateLastError);
         Assert.AreEqual(3, status.UpdateState);
      }

      [Test]
      public void ACertificateThatIsNotForCodeSigningIsRefused()
      {
         ServeRelease(_installer, _sigstore.Bundle(_installer, new FakeSigstore.Options {NoCodeSigning = true}));
         var status = Checked();
         Assert.IsFalse(status.DownloadUpdate());
         StringAssert.Contains("not a code-signing certificate", status.UpdateLastError);
         AssertNothingKept();
      }

      [Test]
      public void NothingIsDownloadedWithoutAKnownUpdate()
      {
         ServeRelease(_installer, _sigstore.Bundle(_installer), Running);
         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(1, status.UpdateState);

         Assert.IsFalse(status.DownloadUpdate(), "Nothing to download when this is the latest release.");
         Assert.AreEqual(0, CountRequests("/download/"), "Nothing was fetched.");
         AssertNothingKept();
      }

      [Test]
      public void ARedirectOffTheLoopbackOverPlainHttpIsRefused()
      {
         ServeRelease(_installer, _sigstore.Bundle(_installer));
         _feed.SetRedirect("/download/" + _installerName, "http://192.0.2.1/hMailServer.exe");

         var status = Checked();
         Assert.IsFalse(status.DownloadUpdate());
         StringAssert.Contains("plain http is only accepted to a loopback address", status.UpdateLastError);
         AssertNothingKept();
      }

      [Test]
      public void AnAuthenticodeSignatureIsRequiredWhenConfigured()
      {
         WriteSetting("UpdateRequireAuthenticode", "1");
         _application.Reinitialize();

         var status = Checked();
         Assert.IsFalse(status.DownloadUpdate(), "Random bytes carry no Authenticode signature.");
         StringAssert.Contains("The installer's Authenticode signature was refused: the file is not a signed Windows binary", status.UpdateLastError);
         AssertNothingKept();
      }

      [Test]
      public void ADownloadCanBeRunOverRest()
      {
         Checked();
         (int code, string body) = Http("POST", "/api/v1/update/download");
         Assert.AreEqual(200, code, body);
         StringAssert.Contains("\"state\":3", body);
         StringAssert.Contains("\"signer\":\"" + FakeSigstore.ReleaseIdentity + "\"", body);
         Assert.IsTrue(File.Exists(Paths.Combine(_updatesDirectory, _installerName)));
      }

      // ---- the release --------------------------------------------------------------

      private string Running
      {
         get { return ((string) _application.Version).Split('-')[0]; }
      }

      private string Newer
      {
         get { return FakeSigstore_Bump(Running); }
      }

      private static string FakeSigstore_Bump(string version)
      {
         string[] parts = version.Split('.');
         return parts[0] + "." + parts[1] + "." + (int.Parse(parts[2]) + 1);
      }

      private Status Checked()
      {
         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), "The feed must be readable. Error: " + status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState, "The fixture's release must be newer than the running version.");
         return status;
      }

      // Serves a release whose installer and bundle are what is given: the feed at
      // the latest path, the installer behind a redirect (as a GitHub asset is), the
      // bundle beside it.
      private void ServeRelease(byte[] installer, string bundle, string version = null, string digest = null)
      {
         version = version ?? Newer;
         string name = "hMailServer-" + version + "-x64.exe";
         byte[] hash;
         using (var sha = SHA256.Create())
            hash = sha.ComputeHash(installer);
         digest = digest ?? "sha256:" + FakeSigstore.Hex(hash);

         _feed.ClearRoutes();
         _feed.SetRedirect("/download/" + name, "/objects/" + name);
         _feed.SetResponse("/objects/" + name, 200, installer);
         _feed.SetResponse("/download/" + name + ".cosign.bundle", 200, Encoding.UTF8.GetBytes(bundle), "application/json");
         _feed.SetResponse(200, Release(version, name, installer.Length, digest));
      }

      private string Release(string version, string name, long size, string digest)
      {
         string download = _feed.UrlFor("/download/");
         return "{\"url\":\"https://api.github.com/repos/Progressiverobot/hmailserver/releases/1001\"," +
                "\"html_url\":\"https://github.com/Progressiverobot/hmailserver/releases/tag/v" + version + "\"," +
                "\"id\":1001,\"author\":{\"login\":\"chrisholloway5\",\"html_url\":\"https://github.com/chrisholloway5\"}," +
                "\"tag_name\":\"v" + version + "\",\"name\":\"hMailServer " + version + "\",\"draft\":false,\"prerelease\":false," +
                "\"published_at\":\"2026-09-12T10:00:00Z\",\"assets\":[" +
                "{\"name\":\"" + name + "\",\"size\":" + size + ",\"digest\":\"" + digest + "\",\"browser_download_url\":\"" + download + name + "\"}," +
                "{\"name\":\"" + name + ".cosign.bundle\",\"size\":1,\"browser_download_url\":\"" + download + name + ".cosign.bundle\"}" +
                "],\"body\":\"notes\"}";
      }

      private int CountRequests(string fragment)
      {
         return _feed.Requests.Count(request => request.Contains(fragment));
      }

      private void AssertNothingKept()
      {
         if (!Directory.Exists(_updatesDirectory))
            return;
         string[] left = Directory.GetFiles(_updatesDirectory);
         Assert.IsEmpty(left, "A file that failed verification must not be left in " + _updatesDirectory + ": " + string.Join(", ", left));
      }

      private void CleanUpdates()
      {
         if (!Directory.Exists(_updatesDirectory))
            return;
         foreach (string file in Directory.GetFiles(_updatesDirectory))
         {
            try
            {
               File.Delete(file);
            }
            catch (UnauthorizedAccessException)
            {
               // apply-token carries a DACL naming SYSTEM, Administrators and the
               // service account only; the suite is none of those. The server
               // revokes it after an apply, and one never redeemed expires.
            }
         }
      }

      // ---- plumbing ----------------------------------------------------------------

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
