// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    The REST administration API's delivery-queue endpoints
   ///    (hMailServer.ini [Settings] RestApiPort) used to report success for a
   ///    message id that did not exist. DeliveryQueue::ResetDeliveryTime and
   ///    DeliveryQueue::Remove both return void and do nothing for an unknown
   ///    id - the underlying UPDATE simply matches no rows and still reports
   ///    success - so POST /api/v1/queue/&lt;id&gt;/retry and
   ///    DELETE /api/v1/queue/&lt;id&gt; answered 200 {"retried":true} /
   ///    {"deleted":true} for any well-formed number. An administrator (or a
   ///    script driving the API) was told a message had been requeued or
   ///    removed when nothing whatsoever had happened, which is the worst kind
   ///    of wrong answer for a queue tool: it hides the typo.
   ///
   ///    These tests pin both halves. The unknown id must now be 404, and the
   ///    id of a message that really is queued must still be 200 and must
   ///    still act on the message - a fix that made every call 404 would be
   ///    worse than the bug.
   /// </summary>
   [TestFixture]
   public class RestApiQueue : TestFixtureBase
   {
      // Fixed loopback port for the API under test. The listener refuses to
      // run without TLS unless it is bound to 127.0.0.1, which is why this
      // test can speak plain HTTP to it.
      private const int RestPort = 9098;

      // TestSetup.Authenticate() already expects this to be the administrator
      // password, and the REST API authenticates against the same credential.
      // It is set explicitly because an empty administrator password disables
      // the API outright.
      private const string AdminPassword = "testar";

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

      // Issues a minimal authenticated HTTP/1.0 request against the REST
      // listener and returns the parsed status code and body. The connect is
      // retried briefly to absorb the listener bind race right after a
      // reinitialize.
      private static (int status, string body) Http(string method, string path)
      {
         string credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes("Administrator:" + AdminPassword));

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
               byte[] request = Encoding.ASCII.GetBytes(
                  method + " " + path + " HTTP/1.0\r\n" +
                  "Host: 127.0.0.1\r\n" +
                  "Authorization: Basic " + credentials + "\r\n" +
                  "Connection: close\r\n\r\n");

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

      [SetUp]
      public void StartRestApi()
      {
         _settings.SetAdministratorPassword(AdminPassword);

         WriteSetting("RestApiBindAddress", "127.0.0.1");
         WriteSetting("RestApiPort", RestPort.ToString());

         // Reinitialize (not Stop/Start): RestApiPort is cached in
         // IniFileSettings, which is only re-read by InitInstance().
         _application.Reinitialize();

         // Sanity check that the listener is up and the credential works, so a
         // failure below is about the queue endpoints and not about setup.
         (int status, string body) probe = Http("GET", "/api/v1/status");
         Assert.AreEqual(200, probe.status, "REST API did not answer /api/v1/status. Body: " + probe.body);
      }

      [TearDown]
      public void StopRestApi()
      {
         // Never leave messages behind: other fixtures assert that the
         // delivery queue is empty when they start.
         TestSetup.DeleteMessagesInQueue();

         WriteSetting("RestApiPort", "0");
         _application.Reinitialize();
      }

      // Parks a message in the delivery queue by routing a domain at a
      // closed local port: the connection is refused, which is a temporary
      // failure, so the message is retried rather than bounced and sits in
      // the queue with a next-try time five minutes out.
      private static long QueueOneMessage()
      {
         TestSetup.AddRoutePointingAtLocalhost(5, TestSetup.GetNextFreePort(), false);

         var smtp = new SmtpClientSimulator();
         smtp.Send("test@example.test", "queued@dummy-example.com", "Queued", "Queued message body");

         DateTime timeout = DateTime.UtcNow.AddSeconds(60);
         while (DateTime.UtcNow < timeout)
         {
            (int status, string body) queue = Http("GET", "/api/v1/queue");
            Assert.AreEqual(200, queue.status, "GET /api/v1/queue failed. Body: " + queue.body);

            Match match = Regex.Match(queue.body, "\"id\":(\\d+)");
            if (match.Success)
               return long.Parse(match.Groups[1].Value);

            Thread.Sleep(200);
         }

         Assert.Fail("No message reached the delivery queue, so the success path could not be checked.");
         return 0;
      }

      private static bool QueueContains(long messageId)
      {
         (int status, string body) queue = Http("GET", "/api/v1/queue");
         Assert.AreEqual(200, queue.status, "GET /api/v1/queue failed. Body: " + queue.body);

         return queue.body.Contains("\"id\":" + messageId + ",");
      }

      [Test]
      [Description("Retrying a queue id that does not exist is 404, not a reported success")]
      public void UnknownQueueIdCannotBeRetried()
      {
         // messageid is a database identity column, so a value this large
         // cannot have been handed out on a test bench. Confirmed against the
         // live queue listing before it is used.
         const long unknownId = 999999999;
         Assert.IsFalse(QueueContains(unknownId), "The id chosen as 'nonexistent' is in the queue.");

         (int status, string body) retry = Http("POST", "/api/v1/queue/" + unknownId + "/retry");

         Assert.AreEqual(404, retry.status,
            "Retrying a nonexistent queue id must be 404. Body: " + retry.body);
         Assert.IsFalse(retry.body.Contains("\"retried\":true"),
            "The API must not claim to have requeued a message that does not exist. Body: " + retry.body);
      }

      [Test]
      [Description("Deleting a queue id that does not exist is 404, not a reported success")]
      public void UnknownQueueIdCannotBeDeleted()
      {
         const long unknownId = 999999998;
         Assert.IsFalse(QueueContains(unknownId), "The id chosen as 'nonexistent' is in the queue.");

         (int status, string body) delete = Http("DELETE", "/api/v1/queue/" + unknownId);

         Assert.AreEqual(404, delete.status,
            "Deleting a nonexistent queue id must be 404. Body: " + delete.body);
         Assert.IsFalse(delete.body.Contains("\"deleted\":true"),
            "The API must not claim to have deleted a message that does not exist. Body: " + delete.body);
      }

      [Test]
      [Description("A message that really is queued can still be retried and deleted, and only then becomes 404")]
      public void QueuedMessageIsStillRetriedAndDeleted()
      {
         long messageId = QueueOneMessage();

         // The success path is unchanged: an id that is in the queue answers
         // 200 for both verbs.
         (int status, string body) retry = Http("POST", "/api/v1/queue/" + messageId + "/retry");
         Assert.AreEqual(200, retry.status, "Retrying a queued message must still succeed. Body: " + retry.body);
         Assert.IsTrue(retry.body.Contains("\"retried\":true"), "Body: " + retry.body);

         (int status, string body) delete = Http("DELETE", "/api/v1/queue/" + messageId);
         Assert.AreEqual(200, delete.status, "Deleting a queued message must still succeed. Body: " + delete.body);
         Assert.IsTrue(delete.body.Contains("\"deleted\":true"), "Body: " + delete.body);

         // Removal needs the message row lock, which a delivery attempt
         // triggered by the retry above may briefly hold, so wait for the row
         // to actually go away before the interesting assertion.
         DateTime timeout = DateTime.UtcNow.AddSeconds(60);
         while (QueueContains(messageId) && DateTime.UtcNow < timeout)
         {
            Http("DELETE", "/api/v1/queue/" + messageId);
            Thread.Sleep(250);
         }

         Assert.IsFalse(QueueContains(messageId), "The message was never removed from the queue.");

         // The same id, same request, now that the message is gone. This is
         // the assertion the old code could not pass: it answered 200
         // {"deleted":true} for an id it had already deleted.
         (int status, string body) again = Http("DELETE", "/api/v1/queue/" + messageId);
         Assert.AreEqual(404, again.status,
            "Deleting an already-deleted message must be 404. Body: " + again.body);
      }

      [Test]
      [Description("Deleting an account that does not exist is 404 (the pattern the queue endpoints now follow)")]
      public void UnknownAccountCannotBeDeleted()
      {
         (int status, string body) delete = Http("DELETE", "/api/v1/accounts/no-such-account@example.test");

         Assert.AreEqual(404, delete.status,
            "Deleting a nonexistent account must be 404. Body: " + delete.body);
      }
   }
}
