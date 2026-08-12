// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    /.well-known/security.txt (RFC 9116) served by the web services
   ///    listener (hMailServer.ini [Settings] WebServicesHttpPort), alongside
   ///    the existing MTA-STS, autoconfig and ACME handlers.
   ///
   ///    Two properties matter more than the file existing at all. The
   ///    mandatory Expires field must be in the future: RFC 9116 section 2.5.5
   ///    makes a file with an elapsed Expires invalid, so a hardcoded literal
   ///    would quietly turn the whole thing off on a date nobody remembers -
   ///    it is therefore derived from the current time on every request, which
   ///    is what the future-date assertion here pins. And the Contact must be
   ///    a real address: a published RFC 9116 file carrying a placeholder is
   ///    worse than no file, because it tells a finder they have a reporting
   ///    channel when they do not. The contact is taken from the postmaster
   ///    address of the matching local domain, and nothing is served when that
   ///    is not configured.
   ///
   ///    All of these fail against a build without the handler: the path is
   ///    simply not routed, so every request answers 404.
   /// </summary>
   [TestFixture]
   public class SecurityTxt : TestFixtureBase
   {
      private const int WebServicesPort = 9097;

      private const string PolicyUrl =
         "https://github.com/Progressiverobot/hmailserver/blob/master/.github/SECURITY.md";

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      // Issues a minimal HTTP/1.0 GET against the web services listener with
      // the given Host header, which is what selects the mail domain.
      private static (int status, string body) HttpGet(string path, string host)
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
                  "GET " + path + " HTTP/1.0\r\nHost: " + host + "\r\nConnection: close\r\n\r\n");

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
               string body = separator >= 0 ? raw.Substring(separator + 4) : "";

               return (statusCode, body);
            }
         }
      }

      private static string FieldValue(string body, string field)
      {
         foreach (string raw in body.Split('\n'))
         {
            string line = raw.Trim();
            if (line.StartsWith(field + ":", StringComparison.OrdinalIgnoreCase))
               return line.Substring(field.Length + 1).Trim();
         }

         return null;
      }

      [SetUp]
      public void StartWebServices()
      {
         WriteSetting("WebServicesBindAddress", "127.0.0.1");
         WriteSetting("WebServicesHttpPort", WebServicesPort.ToString());

         // Reinitialize (not Stop/Start): the web services ports are cached in
         // IniFileSettings, which is only re-read by InitInstance().
         _application.Reinitialize();
      }

      [TearDown]
      public void StopWebServices()
      {
         WriteSetting("WebServicesHttpPort", "0");
         _application.Reinitialize();
      }

      [Test]
      [Description("security.txt is served for a domain with a postmaster, with a Contact, a future Expires and a Policy")]
      public void SecurityTxtIsServedForConfiguredDomain()
      {
         _domain.Postmaster = "postmaster@example.test";
         _domain.Save();

         (int status, string body) response = HttpGet("/.well-known/security.txt", "example.test");

         Assert.AreEqual(200, response.status, "security.txt should be served. Body: " + response.body);

         Assert.AreEqual("mailto:postmaster@example.test", FieldValue(response.body, "Contact"),
            "Contact should be the domain postmaster. Body: " + response.body);

         Assert.AreEqual(PolicyUrl, FieldValue(response.body, "Policy"),
            "Policy should point at SECURITY.md. Body: " + response.body);

         string expires = FieldValue(response.body, "Expires");
         Assert.IsNotNull(expires, "Expires is mandatory (RFC 9116 2.5.5). Body: " + response.body);

         DateTime expiresUtc = DateTime.Parse(expires, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

         // Derived, not hardcoded: it has to be ahead of now every time the
         // file is fetched, however long after the release that is.
         Assert.Greater(expiresUtc, DateTime.UtcNow,
            "Expires must be in the future or the whole file is invalid. Value: " + expires);
      }

      [Test]
      [Description("security.txt is also served on the service host names for the domain (mta-sts., autoconfig., ...)")]
      public void SecurityTxtIsServedForServiceHostName()
      {
         _domain.Postmaster = "postmaster@example.test";
         _domain.Save();

         (int status, string body) response = HttpGet("/.well-known/security.txt", "mta-sts.example.test");

         Assert.AreEqual(200, response.status, "security.txt should be served. Body: " + response.body);
         Assert.AreEqual("mailto:postmaster@example.test", FieldValue(response.body, "Contact"),
            "Body: " + response.body);
      }

      [Test]
      [Description("Nothing is served when the domain has no postmaster address - a placeholder contact would be worse than no file")]
      public void SecurityTxtIsNotServedWithoutAContact()
      {
         _domain.Postmaster = "";
         _domain.Save();

         (int status, string body) response = HttpGet("/.well-known/security.txt", "example.test");

         Assert.AreEqual(404, response.status,
            "Without a configured contact there must be no file at all. Body: " + response.body);
      }

      [Test]
      [Description("Nothing is served for a domain that is not hosted here")]
      public void SecurityTxtIsNotServedForUnknownDomain()
      {
         _domain.Postmaster = "postmaster@example.test";
         _domain.Save();

         (int status, string body) response = HttpGet("/.well-known/security.txt", "not-hosted-here.test");

         Assert.AreEqual(404, response.status, "Body: " + response.body);
      }
   }
}
