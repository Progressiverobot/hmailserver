// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   /// The update check: the first part of the roadmap's live update. The server reads
   /// the project's release feed - the GitHub Releases API, or whatever UpdateFeedUrl
   /// names, which here is a fake on the loopback - compares the newest release the
   /// channel accepts with its own version, and says what it found through
   /// Status.UpdateState and friends, GET /api/v1/update and one application-log
   /// line. Nothing is downloaded and nothing runs; that is the next part.
   ///
   /// The feed documents are shaped as GitHub shapes them, author object and asset
   /// array included, because those share key names ("name", "html_url", "url",
   /// "size") with the release itself and a reader that searched by key name would
   /// take the wrong one.
   /// </summary>
   [TestFixture]
   public class UpdateCheck : TestFixtureBase
   {
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9104;
      private const string AdminPassword = "testar";
      private const string LatestPath = "/repos/Progressiverobot/hmailserver/releases/latest";
      private const string ListPath = "/repos/Progressiverobot/hmailserver/releases";
      private const string Published = "2026-09-12T10:00:00Z";

      private FakeHttpEndpoint _feed;

      [SetUp]
      public void StartFeed()
      {
         _feed = new FakeHttpEndpoint(200, Release(Newer, Published));

         _settings.SetAdministratorPassword(AdminPassword);
         WriteSetting("UpdateCheckEnabled", "0");
         WriteSetting("UpdateChannel", "stable");
         WriteSetting("UpdateFeedUrl", _feed.UrlFor(LatestPath));
         RestPort = RestListener.Start(RestPort);

         // Reinitialize rather than Stop/Start: the settings are read by InitInstance.
         _application.Reinitialize();
      }

      [TearDown]
      public void StopFeed()
      {
         WriteSetting("UpdateCheckEnabled", "0");
         WriteSetting("UpdateChannel", "stable");
         WriteSetting("UpdateFeedUrl", "");
         WriteSetting("HttpProxy", "");
         RestListener.Stop();
         _application.Reinitialize();
         _feed.Dispose();
      }

      [Test]
      public void TheCheckGoesThroughTheConfiguredProxy()
      {
         using (var proxy = new FakeHttpProxy())
         {
            WriteSetting("HttpProxy", proxy.Address);
            _application.Reinitialize();

            var status = _application.Status;
            Assert.IsTrue(status.CheckForUpdate(), "Through the proxy the feed is read as it is directly. Error: " + status.UpdateLastError);
            Assert.AreEqual(Newer, status.AvailableVersion, "The release the feed served, read through the proxy.");

            var targets = proxy.Targets;
            Assert.AreEqual(1, targets.Count, "The proxy was asked exactly once. Targets: " + string.Join(", ", targets));
            Assert.AreEqual(_feed.UrlFor(LatestPath), targets[0], "A plain-http feed is asked for by its absolute URL, which is how a forward proxy is told where to go.");
            Assert.AreEqual(1, _feed.Requests.Count, "The feed heard from the proxy, once.");
            StringAssert.StartsWith("GET " + LatestPath + " ", _feed.Requests[0], "The proxy forwarded the request in origin form.");
         }
      }

      [Test]
      public void AProxyThatRefusesTheTunnelIsReportedByName()
      {
         using (var proxy = new FakeHttpProxy())
         {
            // An https feed behind the proxy, at a port nothing listens on: the proxy
            // cannot reach it and refuses the CONNECT with 502. The check reports the
            // proxy by name and what it refused, so the administrator knows which of
            // the two hops to look at.
            WriteSetting("HttpProxy", proxy.Address);
            WriteSetting("UpdateFeedUrl", "https://127.0.0.1:1/releases/latest");
            _application.Reinitialize();

            var status = _application.Status;
            Assert.IsFalse(status.CheckForUpdate(), "The tunnel was refused, so the check cannot have succeeded.");
            StringAssert.Contains("The proxy " + proxy.Address + " refused CONNECT to 127.0.0.1:1", status.UpdateLastError, "The proxy and the target are named.");
            StringAssert.Contains("502", status.UpdateLastError, "The proxy's own status line is quoted.");
            Assert.AreEqual(new[] { "127.0.0.1:1" }, proxy.Targets.ToArray(), "The proxy was asked to CONNECT to the feed's host and port, and nothing else.");
         }
      }

      [Test]
      public void ANewerReleaseIsReportedWithItsVersionDateAndPage()
      {
         var status = _application.Status;

         Assert.IsTrue(status.CheckForUpdate(), "The feed was served and must have been read. Error: " + status.UpdateLastError);

         Assert.AreEqual(2, status.UpdateState, "State 2 is 'a newer release is available'.");
         Assert.AreEqual(Newer, status.AvailableVersion);
         Assert.AreEqual(Published, status.AvailableVersionPublished);
         Assert.AreEqual("https://github.com/Progressiverobot/hmailserver/releases/tag/v" + Newer, status.AvailableVersionUrl,
            "The release page, not the author's page or the API URL, both of which the feed also carries under html_url and url.");
         Assert.IsNotEmpty(status.UpdateLastChecked);
         Assert.IsEmpty(status.UpdateLastError);

         Assert.AreEqual(1, _feed.Requests.Count, "One check is one request.");
         StringAssert.StartsWith("GET " + LatestPath + " HTTP/1.0", _feed.Requests[0]);
         StringAssert.Contains("User-Agent: hMailServer", _feed.Requests[0], "GitHub refuses a request without a User-Agent.");

         StringAssert.Contains("Update check: hMailServer " + Newer + " is available (published " + Published + ")",
            LogHandler.ReadCurrentDefaultLog(), "Every check writes one application-log line.");
      }

      [Test]
      public void TheRunningVersionIsTheLatestWhenTheFeedShowsIt()
      {
         _feed.SetResponse(200, Release(Running, "2026-09-01T00:00:00Z"));

         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);

         Assert.AreEqual(1, status.UpdateState, "State 1 is 'this is the latest release'.");
         Assert.IsEmpty(status.AvailableVersion, "Nothing is 'available' when the server already runs it.");
         Assert.IsEmpty(status.UpdateLastError);
         StringAssert.Contains("Update check: this server runs " + Running + ", which is the latest release on the stable channel",
            LogHandler.ReadCurrentDefaultLog());
      }

      [Test]
      public void AnOlderReleaseIsNotAnUpdate()
      {
         _feed.SetResponse(200, Release("1.0.0", "2016-01-01T00:00:00Z"));

         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(1, status.UpdateState);
         Assert.IsEmpty(status.AvailableVersion);
      }

      [Test]
      public void VersionsCompareNumericallyNotAsText()
      {
         // A patch number of three digits sorts before a two-digit one as text
         // ("100" < "27") and after it as a number. The check must use the number.
         int[] running = Parts(Running);
         if (running[2] < 2 || running[2] >= 100)
            Assert.Ignore("This test needs a running patch number between 2 and 99; it is " + running[2] + ".");

         string textuallyOlder = running[0] + "." + running[1] + ".100";
         Assert.IsTrue(string.CompareOrdinal(textuallyOlder, Running) < 0, "The premise of the test: as text this version is older.");

         _feed.SetResponse(200, Release(textuallyOlder, Published));

         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState, textuallyOlder + " is newer than " + Running + " as a version number.");
         Assert.AreEqual(textuallyOlder, status.AvailableVersion);

         // A tag with a leading v and a version with a build suffix compare on their numbers alone.
         _feed.SetResponse(200, Release("v" + Running + "-B1", Published));
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(1, status.UpdateState, "v" + Running + "-B1 is the running version, spelled as a tag.");
      }

      [Test]
      public void AFeedThatCannotBeReadIsAFailureThatKeepsItsReason()
      {
         var status = _application.Status;

         // A verdict first, so the failure can be seen to keep it.
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState);

         _feed.SetResponse(500, "{\"message\":\"boom\"}");
         Assert.IsFalse(status.CheckForUpdate(), "A 500 from the feed is not a check that succeeded.");
         Assert.AreEqual(5, status.UpdateState, "State 5 is 'the last check failed'.");
         StringAssert.Contains("HTTP 500", status.UpdateLastError);
         Assert.AreEqual(Newer, status.AvailableVersion,
            "What the last successful check learned is kept beside the error; the feed being down does not un-release anything.");
         StringAssert.Contains("Update check failed: The update feed answered HTTP 500.", LogHandler.ReadCurrentDefaultLog());

         _feed.SetResponse(200, "this is not json", "text/plain");
         Assert.IsFalse(status.CheckForUpdate());
         Assert.AreEqual(5, status.UpdateState);
         StringAssert.Contains("not JSON", status.UpdateLastError);

         _feed.SetResponse(200, "{\"message\":\"Not Found\"}");
         Assert.IsFalse(status.CheckForUpdate(), "JSON that is not a release is not a verdict.");
         StringAssert.Contains("no release", status.UpdateLastError);

         _feed.SetResponse(200, "\"just a string\"");
         Assert.IsFalse(status.CheckForUpdate());
         StringAssert.Contains("neither a release nor a list", status.UpdateLastError);

         // And the next good answer clears the error.
         _feed.SetResponse(200, Release(Newer, Published));
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState);
         Assert.IsEmpty(status.UpdateLastError);
      }

      [Test]
      public void TheStableChannelIgnoresAPreRelease()
      {
         _feed.SetResponse(200, Release(Newer, Published, prerelease: true));

         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(1, status.UpdateState, "A pre-release is not an update on the stable channel.");
         Assert.IsEmpty(status.AvailableVersion);
         StringAssert.Contains("the feed's only release is a pre-release and the channel is stable", LogHandler.ReadCurrentDefaultLog());
      }

      [Test]
      public void ThePreReleaseChannelTakesTheNewestOfTheList()
      {
         string stable = Newer;
         string preRelease = Bump(Newer);
         string draft = Bump(preRelease);

         string list = "[" +
            Release(draft, Published, draft: true) + "," +
            Release("1.0.0", "2016-01-01T00:00:00Z") + "," +
            Release(preRelease, Published, prerelease: true) + "," +
            Release(stable, Published) +
            "]";
         _feed.SetResponse(200, list);
         WriteSetting("UpdateFeedUrl", _feed.UrlFor(ListPath));

         // The stable channel over the list: the pre-release and the draft are passed over.
         _application.Reinitialize();
         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState);
         Assert.AreEqual(stable, status.AvailableVersion, "The newest release that is neither a draft nor a pre-release.");

         // The pre-release channel: the pre-release counts, the draft still does not.
         WriteSetting("UpdateChannel", "prerelease");
         _application.Reinitialize();
         status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState);
         Assert.AreEqual(preRelease, status.AvailableVersion, "The newest published release, pre-release or not; a draft is not published.");
         StringAssert.Contains("Update check: hMailServer " + preRelease + " is available", LogHandler.ReadCurrentDefaultLog());
      }

      [Test]
      public void TheInstallerAndItsBundleAreIdentifiedAmongTheAssets()
      {
         var status = _application.Status;
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);

         (int code, string body) = Http("GET", "/api/v1/update");
         Assert.AreEqual(200, code, body);

         StringAssert.Contains("\"state\":2", body);
         StringAssert.Contains("\"stateName\":\"available\"", body);
         StringAssert.Contains("\"runningVersion\":\"" + Running + "\"", body);
         StringAssert.Contains("\"channel\":\"stable\"", body);
         StringAssert.Contains("\"checkEnabled\":false", body);
         StringAssert.Contains("\"availableVersion\":\"" + Newer + "\"", body);
         StringAssert.Contains("\"publishedAt\":\"" + Published + "\"", body);
         StringAssert.Contains("\"releaseUrl\":\"https://github.com/Progressiverobot/hmailserver/releases/tag/v" + Newer + "\"", body);
         // The release title carries an escaped quote and a non-ASCII character: the
         // parser must unescape them and the route must re-escape the quote.
         StringAssert.Contains("\"releaseName\":\"hMailServer " + Newer + " \\\"live update\\\" \u00e9dition\"", body);
         StringAssert.Contains("\"installer\":{\"name\":\"hMailServer-" + Newer + "-x64.exe\"", body);
         StringAssert.Contains("\"url\":\"https://github.com/Progressiverobot/hmailserver/releases/download/v" + Newer + "/hMailServer-" + Newer + "-x64.exe\"", body);
         StringAssert.Contains("\"size\":77197816", body);
         StringAssert.Contains("\"digest\":\"sha256:bfb1e5d606d3fb18b2bbbe26a8704a29033c807e293988265f47e75891905f1e\"", body);
         StringAssert.Contains("\"bundleUrl\":\"https://github.com/Progressiverobot/hmailserver/releases/download/v" + Newer + "/hMailServer-" + Newer + "-x64.exe.cosign.bundle\"", body);
         StringAssert.Contains("\"lastError\":\"\"", body);

         // A release without an x64 installer is still an update, with nothing to download.
         _feed.SetResponse(200, Release(Newer, Published, withInstaller: false));
         Assert.IsTrue(status.CheckForUpdate(), status.UpdateLastError);
         Assert.AreEqual(2, status.UpdateState);
         body = Http("GET", "/api/v1/update").body;
         StringAssert.Contains("\"installer\":{\"name\":\"\",\"url\":\"\",\"size\":0,\"digest\":\"\",\"bundleUrl\":\"\"}", body);
         StringAssert.Contains("The release carries no x64 installer", LogHandler.ReadCurrentDefaultLog());
      }

      [Test]
      public void ACheckCanBeRunOverRest()
      {
         int before = _feed.Requests.Count;

         (int code, string body) = Http("POST", "/api/v1/update/check");
         Assert.AreEqual(200, code, body);
         Assert.AreEqual(before + 1, _feed.Requests.Count, "POST /api/v1/update/check reads the feed on the request.");
         StringAssert.Contains("\"state\":2", body);
         StringAssert.Contains("\"availableVersion\":\"" + Newer + "\"", body);

         // GET does not read the feed; it reports.
         Assert.AreEqual(200, Http("GET", "/api/v1/update").code);
         Assert.AreEqual(before + 1, _feed.Requests.Count);

         // The failure of the check is in the verdict, not in the status code:
         // the request - run a check - was carried out.
         _feed.SetResponse(503, "");
         (int failedCode, string failedBody) = Http("POST", "/api/v1/update/check");
         Assert.AreEqual(200, failedCode, failedBody);
         StringAssert.Contains("\"state\":5", failedBody);
         StringAssert.Contains("\"lastError\":\"The update feed answered HTTP 503.\"", failedBody);
      }

      [Test]
      public void TheScheduledCheckRunsAtStartupWhenEnabled()
      {
         Assert.AreEqual(0, _feed.Requests.Count, "The fixture starts with the check off; nothing may have been fetched.");

         WriteSetting("UpdateCheckEnabled", "1");
         _application.Reinitialize();

         // The startup task runs on the scheduler's thread; give it a moment.
         WaitUntil(() => _feed.Requests.Count >= 1, 20, "The scheduled check did not read the feed after a restart with UpdateCheckEnabled=1.");
         var status = _application.Status;
         WaitUntil(() => status.UpdateState == 2, 10, "The scheduled check's verdict did not arrive.");
         Assert.AreEqual(Newer, status.AvailableVersion);

         StringAssert.Contains("\"checkEnabled\":true", Http("GET", "/api/v1/update").body);
      }

      [Test]
      public void NothingIsFetchedWhileTheCheckIsOff()
      {
         // The fixture's SetUp restarted the server with UpdateCheckEnabled=0 and the
         // feed in place. The startup task must have declined to use it.
         Thread.Sleep(2000);
         Assert.AreEqual(0, _feed.Requests.Count, "A server whose administrator has not opted in must not call out.");
      }

      // ---- the feed ---------------------------------------------------------------

      // "6.2.27" from the "6.2.27-B37" the Version property gives.
      private string Running
      {
         get { return ((string) _application.Version).Split('-')[0]; }
      }

      private string Newer
      {
         get { return Bump(Running); }
      }

      private static int[] Parts(string version)
      {
         string[] text = version.Split('.');
         var parts = new int[3];
         for (int i = 0; i < 3 && i < text.Length; i++)
            parts[i] = int.Parse(text[i]);
         return parts;
      }

      private static string Bump(string version)
      {
         int[] parts = Parts(version);
         return parts[0] + "." + parts[1] + "." + (parts[2] + 1);
      }

      // One release object as the GitHub Releases API shapes it, including the
      // fields that share names with the release's own: the author's html_url and
      // name, the assets' name, url and size, and a body with escapes in it.
      private static string Release(string version, string publishedAt, bool prerelease = false, bool draft = false, bool withInstaller = true)
      {
         string tag = version.StartsWith("v") ? version : "v" + version;
         string plain = tag.Substring(1);
         string download = "https://github.com/Progressiverobot/hmailserver/releases/download/" + tag + "/";

         var assets = new StringBuilder();
         if (withInstaller)
         {
            assets.Append(Asset(11, "hMailServer-" + plain + "-x64.exe", 77197816, "sha256:bfb1e5d606d3fb18b2bbbe26a8704a29033c807e293988265f47e75891905f1e", download));
            assets.Append(",");
            assets.Append(Asset(12, "hMailServer-" + plain + "-x64.exe.cosign.bundle", 10807, "sha256:be7085eca2f0042a643cc85330d3d8a406fe6e023a375eaf0c680effa366bb46", download));
            assets.Append(",");
         }
         assets.Append(Asset(13, "hmailserver.spdx.json", 260646, "sha256:8b265676f3e7d6a1b860688d84ffc6470d24574601dcddb880bcf8c8864cec74", download));

         return "{" +
            "\"url\":\"https://api.github.com/repos/Progressiverobot/hmailserver/releases/1001\"," +
            "\"assets_url\":\"https://api.github.com/repos/Progressiverobot/hmailserver/releases/1001/assets\"," +
            "\"html_url\":\"https://github.com/Progressiverobot/hmailserver/releases/tag/" + tag + "\"," +
            "\"id\":1001," +
            "\"author\":{\"login\":\"chrisholloway5\",\"id\":5,\"name\":\"not the release\",\"html_url\":\"https://github.com/chrisholloway5\",\"url\":\"https://api.github.com/users/chrisholloway5\",\"type\":\"User\"}," +
            "\"node_id\":\"RE_x\"," +
            "\"tag_name\":\"" + tag + "\"," +
            "\"target_commitish\":\"master\"," +
            "\"name\":\"hMailServer " + plain + " \\\"live update\\\" \\u00e9dition\"," +
            "\"draft\":" + (draft ? "true" : "false") + "," +
            "\"immutable\":false," +
            "\"prerelease\":" + (prerelease ? "true" : "false") + "," +
            "\"created_at\":\"2026-09-12T09:00:00Z\"," +
            "\"updated_at\":\"2026-09-12T09:30:00Z\"," +
            "\"published_at\":\"" + publishedAt + "\"," +
            "\"assets\":[" + assets + "]," +
            "\"tarball_url\":\"https://api.github.com/repos/Progressiverobot/hmailserver/tarball/" + tag + "\"," +
            "\"body\":\"Notes with a quote \\\" a backslash \\\\ a tab \\t and a surrogate pair \\ud83d\\ude80.\\n\"" +
            "}";
      }

      private static string Asset(int id, string name, long size, string digest, string downloadBase)
      {
         return "{" +
            "\"url\":\"https://api.github.com/repos/Progressiverobot/hmailserver/releases/assets/" + id + "\"," +
            "\"id\":" + id + "," +
            "\"name\":\"" + name + "\"," +
            "\"label\":\"\"," +
            "\"content_type\":\"application/octet-stream\"," +
            "\"state\":\"uploaded\"," +
            "\"size\":" + size + "," +
            "\"digest\":\"" + digest + "\"," +
            "\"download_count\":3," +
            "\"browser_download_url\":\"" + downloadBase + name + "\"" +
            "}";
      }

      // ---- plumbing ------------------------------------------------------------------

      private static void WriteSetting(string key, string value)
      {
         IniFileSetting.Write(key, value);
      }

      private static void WaitUntil(Func<bool> condition, int seconds, string message)
      {
         DateTime deadline = DateTime.Now.AddSeconds(seconds);
         while (!condition())
         {
            if (DateTime.Now > deadline)
               Assert.Fail(message);
            Thread.Sleep(100);
         }
      }

      // One HTTP/1.0 request to the REST listener as the administrator.
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
