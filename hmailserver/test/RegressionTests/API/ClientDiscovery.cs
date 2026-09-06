// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    The two client-discovery endpoints added alongside the existing
   ///    Thunderbird autoconfig and Outlook autodiscover handlers, both served
   ///    by the web services listener (hMailServer.ini [Settings]
   ///    WebServicesHttpPort).
   ///
   ///    (a) /email.mobileconfig - an Apple configuration profile. Apple
   ///    devices were the one mainstream client with no discovery path here, so
   ///    every iPhone had to be set up by hand from a wiki page. The property
   ///    worth pinning is not that the file exists but that it agrees with the
   ///    other two formats: the profile is built from the same
   ///    GetClientAccessSettings_ data as the Thunderbird XML, so the two must
   ///    advertise the same ports. A second, independently written source of
   ///    truth for "which port is IMAP on" is exactly how an autoconfig handler
   ///    ends up quietly pointing clients at a port that was renumbered years
   ///    ago. The PayloadUUID must also be stable across requests: iOS and
   ///    macOS key an installed profile by that UUID, so a fresh random one per
   ///    request would leave a user who reinstalls the profile with a stack of
   ///    duplicate mail accounts instead of one replaced account.
   ///
   ///    (b) /.well-known/caldav and /.well-known/carddav (RFC 6764). This
   ///    server does not implement CalDAV or CardDAV and is not going to; the
   ///    redirects exist so an administrator can pair a separate calendar
   ///    server and have clients find it. The important half is the negative
   ///    one: with no target configured these must answer 404 and must not
   ///    redirect anywhere. A client that follows a redirect to something which
   ///    does not speak CalDAV reports a broken calendar account and keeps
   ///    retrying it, and the administrator then debugs the wrong server.
   ///
   ///    Against a build without these handlers every test here fails with 404
   ///    "not found", because neither path is routed - except
   ///    WellKnownDavIsNotFoundWithoutAConfiguredTarget, which passes both
   ///    before and after the change and is labelled as the negative control it
   ///    is: it exists to fail if anyone later "improves" the unconfigured case
   ///    into a redirect to this server.
   /// </summary>
   [TestFixture]
   public class ClientDiscovery : TestFixtureBase
   {
      private const int WebServicesPort = 9103;

      private const string CalDavTarget = "https://calendar.example.test/dav/";
      private const string CardDavTarget = "https://contacts.example.test/dav/";

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      // Issues a minimal HTTP/1.0 GET against the web services listener and
      // returns the status line code, the raw response headers and the body.
      // The headers are needed as well as the body here: the whole point of the
      // RFC 6764 endpoints is the Location header.
      private static (int status, string headers, string body) HttpGet(string path, string host, string extraHeaders = "")
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", WebServicesPort);
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
               byte[] request = Encoding.ASCII.GetBytes(
                  "GET " + path + " HTTP/1.0\r\nHost: " + host + "\r\nConnection: close\r\n" + extraHeaders + "\r\n");

               stream.Write(request, 0, request.Length);

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());

               int statusCode = 0;
               string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out statusCode);
               }

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               string headers = separator >= 0 ? raw.Substring(0, separator) : raw;
               string body = separator >= 0 ? raw.Substring(separator + 4) : "";

               return (statusCode, headers, body);
            }
         }
      }

      private static string HeaderValue(string headers, string name)
      {
         string header = headers.Split(new[] { "\r\n" }, StringSplitOptions.None)
            .FirstOrDefault(raw => raw.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));

         return header?.Substring(name.Length + 1).Trim();
      }

      // Reads the value that follows <key>name</key> in an Apple plist. The
      // profile is small and generated, so locating the next value element is
      // enough - no XML parser is needed to pin these properties.
      private static string PlistValue(string body, string name, string element)
      {
         int keyPosition = body.IndexOf("<key>" + name + "</key>", StringComparison.Ordinal);
         if (keyPosition < 0)
            return null;

         int valueStart = body.IndexOf("<" + element + ">", keyPosition, StringComparison.Ordinal);
         if (valueStart < 0)
            return null;

         valueStart += element.Length + 2;

         int valueEnd = body.IndexOf("</" + element + ">", valueStart, StringComparison.Ordinal);
         if (valueEnd < 0)
            return null;

         return body.Substring(valueStart, valueEnd - valueStart);
      }

      // The <port> of the first server element of the given type in the
      // Thunderbird autoconfig XML.
      private static int AutoconfigPort(string xml, string serverType)
      {
         int typePosition = xml.IndexOf("type=\"" + serverType + "\"", StringComparison.Ordinal);
         Assert.Greater(typePosition, 0, "The autoconfig XML has no " + serverType + " server. XML: " + xml);

         int portStart = xml.IndexOf("<port>", typePosition, StringComparison.Ordinal) + 6;
         int portEnd = xml.IndexOf("</port>", portStart, StringComparison.Ordinal);

         return int.Parse(xml.Substring(portStart, portEnd - portStart), CultureInfo.InvariantCulture);
      }

      [SetUp]
      public void StartWebServices()
      {
         // Start from a known-clean discovery configuration: another test in
         // this fixture may have left a redirect target behind.
         WriteSetting("CalDavRedirectUrl", "");
         WriteSetting("CardDavRedirectUrl", "");

         WriteSetting("WebServicesBindAddress", "127.0.0.1");
         WriteSetting("WebServicesHttpPort", WebServicesPort.ToString());

         // Reinitialize (not Stop/Start): the web services ports are cached in
         // IniFileSettings, which is only re-read by InitInstance().
         _application.Reinitialize();
      }

      [TearDown]
      public void StopWebServices()
      {
         // The redirect targets must not survive this fixture. A non-empty but
         // unusable value makes the server report HM5780, which would fail the
         // clean-error-log assertion of whichever fixture ran next.
         WriteSetting("CalDavRedirectUrl", "");
         WriteSetting("CardDavRedirectUrl", "");
         WriteSetting("WebServicesHttpPort", "0");

         _application.Reinitialize();

         LogHandler.DeleteErrorLog();
      }

      // The configuration profile is served over HTTPS only. This listener is plain
      // HTTP, so the tests say what a TLS-terminating proxy in front of it would say.
      private static (int status, string headers, string body) HttpsGet(string path, string host)
      {
         return HttpGet(path, host, "X-Forwarded-Proto: https\r\n");
      }

      [Test]
      [Description("A .mobileconfig asked for over plain HTTP, with no proxy saying otherwise, is refused with the reason - the profile decides which servers get the user's password")]
      public void MobileConfigIsRefusedOverPlainHttp()
      {
         (int status, string headers, string body) response = HttpGet("/email.mobileconfig", "example.test");
         Assert.AreEqual(403, response.status, "Body: " + response.body);
         StringAssert.Contains("HTTPS", response.body);
         StringAssert.Contains("X-Forwarded-Proto", response.body);
         Assert.IsFalse(response.body.Contains("<plist"), "No profile at all over plain HTTP. Body: " + response.body);
      }

      [Test]
      [Description("An Apple .mobileconfig profile is served with the Apple content type and a complete mail payload")]
      public void MobileConfigProfileIsServed()
      {
         (int status, string headers, string body) response = HttpsGet("/email.mobileconfig", "example.test");

         Assert.AreEqual(200, response.status, "A configuration profile should be served. Body: " + response.body);

         // The content type is what makes iOS hand the file to the profile
         // installer instead of showing it as text.
         Assert.AreEqual("application/x-apple-aspen-config", HeaderValue(response.headers, "Content-Type"),
            "Headers: " + response.headers);

         Assert.AreEqual("com.apple.mail.managed", PlistValue(response.body, "PayloadType", "string"),
            "The first PayloadType is the mail payload's. Body: " + response.body);

         // Both directions are mandatory in a com.apple.mail.managed payload. A
         // profile with only an incoming server installs and then cannot send.
         Assert.IsNotNull(PlistValue(response.body, "IncomingMailServerHostName", "string"),
            "Body: " + response.body);
         Assert.IsNotNull(PlistValue(response.body, "OutgoingMailServerHostName", "string"),
            "Body: " + response.body);
         Assert.IsNotNull(PlistValue(response.body, "IncomingMailServerPortNumber", "integer"),
            "Body: " + response.body);
         Assert.IsNotNull(PlistValue(response.body, "OutgoingMailServerPortNumber", "integer"),
            "Body: " + response.body);
      }

      [Test]
      [Description("The .mobileconfig ports match the Thunderbird autoconfig ports - one source of truth, three formats")]
      public void MobileConfigAgreesWithThunderbirdAutoconfig()
      {
         (int status, string headers, string body) autoconfig =
            HttpGet("/mail/config-v1.1.xml", "example.test");

         Assert.AreEqual(200, autoconfig.status, "Body: " + autoconfig.body);

         (int status, string headers, string body) profile =
            HttpsGet("/email.mobileconfig", "example.test");

         Assert.AreEqual(200, profile.status, "Body: " + profile.body);

         int expectedIncoming = AutoconfigPort(autoconfig.body, "imap");
         int expectedOutgoing = AutoconfigPort(autoconfig.body, "smtp");

         Assert.AreEqual(expectedIncoming.ToString(CultureInfo.InvariantCulture),
            PlistValue(profile.body, "IncomingMailServerPortNumber", "integer"),
            "The profile must advertise the same IMAP port as the autoconfig XML. Profile: " + profile.body);

         Assert.AreEqual(expectedOutgoing.ToString(CultureInfo.InvariantCulture),
            PlistValue(profile.body, "OutgoingMailServerPortNumber", "integer"),
            "The profile must advertise the same SMTP port as the autoconfig XML. Profile: " + profile.body);
      }

      [Test]
      [Description("The PayloadUUID is stable across requests, so reinstalling replaces the profile instead of duplicating the account")]
      public void MobileConfigUuidIsStableAcrossRequests()
      {
         (int status, string headers, string body) first = HttpsGet("/email.mobileconfig", "example.test");
         (int status, string headers, string body) second = HttpsGet("/email.mobileconfig", "example.test");

         Assert.AreEqual(200, first.status, "Body: " + first.body);
         Assert.AreEqual(200, second.status, "Body: " + second.body);

         string firstUuid = PlistValue(first.body, "PayloadUUID", "string");
         Assert.IsNotNull(firstUuid, "Body: " + first.body);

         Assert.AreEqual(firstUuid, PlistValue(second.body, "PayloadUUID", "string"),
            "Two fetches of the same profile must carry the same PayloadUUID. A random UUID per request "
            + "makes every reinstall add another copy of the account on the device.");
      }

      [Test]
      [Description("A supplied address is pre-filled, and omitted entirely when none was supplied so the device prompts")]
      public void MobileConfigPrefillsOnlyASuppliedAddress()
      {
         (int status, string headers, string body) withAddress =
            HttpsGet("/email.mobileconfig?emailaddress=someone%40example.test", "example.test");

         Assert.AreEqual(200, withAddress.status, "Body: " + withAddress.body);
         Assert.AreEqual("someone@example.test", PlistValue(withAddress.body, "EmailAddress", "string"),
            "Body: " + withAddress.body);

         (int status, string headers, string body) withoutAddress =
            HttpsGet("/email.mobileconfig", "example.test");

         Assert.AreEqual(200, withoutAddress.status, "Body: " + withoutAddress.body);

         // Absent rather than empty: an empty EmailAddress installs an account
         // with no address on it, while an absent one makes the device ask.
         Assert.IsNull(PlistValue(withoutAddress.body, "EmailAddress", "string"),
            "With no address supplied the key must be left out so the device prompts. Body: "
            + withoutAddress.body);
      }

      [Test]
      [Description("/.well-known/caldav redirects to the configured calendar server (RFC 6764)")]
      public void CalDavWellKnownRedirectsToConfiguredTarget()
      {
         WriteSetting("CalDavRedirectUrl", CalDavTarget);
         _application.Reinitialize();

         (int status, string headers, string body) response = HttpGet("/.well-known/caldav", "example.test");

         Assert.AreEqual(301, response.status,
            "A configured target should produce a permanent redirect. Headers: " + response.headers);

         Assert.AreEqual(CalDavTarget, HeaderValue(response.headers, "Location"),
            "Headers: " + response.headers);
      }

      [Test]
      [Description("/.well-known/carddav redirects to the configured contacts server, independently of CalDAV")]
      public void CardDavWellKnownRedirectsToConfiguredTarget()
      {
         WriteSetting("CardDavRedirectUrl", CardDavTarget);
         _application.Reinitialize();

         (int status, string headers, string body) carddav = HttpGet("/.well-known/carddav", "example.test");

         Assert.AreEqual(301, carddav.status, "Headers: " + carddav.headers);
         Assert.AreEqual(CardDavTarget, HeaderValue(carddav.headers, "Location"), "Headers: " + carddav.headers);

         // The two settings are separate: configuring contacts must not make
         // the server claim a calendar service it was never pointed at.
         (int status, string headers, string body) caldav = HttpGet("/.well-known/caldav", "example.test");

         Assert.AreEqual(404, caldav.status,
            "CardDavRedirectUrl must not answer for CalDAV. Headers: " + caldav.headers);
      }

      [Test]
      [Description("NEGATIVE CONTROL - passes before and after the change: with no target configured the well-known paths 404 and do not redirect")]
      public void WellKnownDavIsNotFoundWithoutAConfiguredTarget()
      {
         // This one does not fail against the current code, because an unrouted
         // path answers 404 too. It is here as a guard: the tempting "fix" for
         // an unconfigured target is to redirect somewhere plausible, and that
         // is the one outcome that must never ship. A client that follows a
         // redirect to a server which does not speak CalDAV reports a broken
         // account and retries forever, where a 404 makes it conclude there is
         // no calendar service and stop.
         (int status, string headers, string body) caldav = HttpGet("/.well-known/caldav", "example.test");
         (int status, string headers, string body) carddav = HttpGet("/.well-known/carddav", "example.test");

         Assert.AreEqual(404, caldav.status, "Headers: " + caldav.headers);
         Assert.AreEqual(404, carddav.status, "Headers: " + carddav.headers);

         Assert.IsNull(HeaderValue(caldav.headers, "Location"), "Headers: " + caldav.headers);
         Assert.IsNull(HeaderValue(carddav.headers, "Location"), "Headers: " + carddav.headers);
      }

      [Test]
      [Description("A configured target that is not an absolute URL is reported as HM5780 and answers 404 rather than emitting a broken Location")]
      public void UnusableCalDavTargetIsReportedAndDoesNotRedirect()
      {
         try
         {
            // No scheme. Left unchecked this would go straight into a Location
            // header, and anything a client did with it would be undefined.
            WriteSetting("CalDavRedirectUrl", "calendar.example.test/dav/");
            _application.Reinitialize();

            (int status, string headers, string body) response = HttpGet("/.well-known/caldav", "example.test");

            Assert.AreEqual(404, response.status,
               "An unusable target must not produce a redirect. Headers: " + response.headers);

            Assert.IsNull(HeaderValue(response.headers, "Location"), "Headers: " + response.headers);

            // An empty setting is the shipped default and stays silent; a
            // non-empty one that cannot be used is a real misconfiguration and
            // is reported, so it can be found and corrected.
            CustomAsserts.AssertReportedError("HM5780");
         }
         finally
         {
            WriteSetting("CalDavRedirectUrl", "");
            LogHandler.DeleteErrorLog();
         }
      }
   }
}
