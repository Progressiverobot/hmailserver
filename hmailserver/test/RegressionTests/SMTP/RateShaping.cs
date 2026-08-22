// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   /// B4 rate shaping. When [Settings] MaxSubmissionsPerIPPerMinute is set, a
   /// single source IP may only start that many MAIL FROM transactions per
   /// minute; further submissions are rejected with a 4xx until the sliding
   /// window drains. The limit is per source IP (not per connection), and 0
   /// (the default) disables the throttle entirely. The per-destination outbound
   /// throttle (MaxOutboundPerDestinationPerMinute) shares the same in-memory
   /// sliding-window limiter, which is additionally covered by the in-server
   /// RateLimiter self-test.
   /// </summary>
   [TestFixture]
   public class RateShaping : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private void WriteSetting(string key, string value)
      {
         // The server reads hMailServer.ini from its bin directory; write to every
         // existing candidate so the file the service actually reads is updated
         // regardless of the install/dev layout.
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

      private static string StartTransaction(TcpConnection socket)
      {
         // Issues a fresh MAIL FROM (one "submission"), returning the reply, then
         // resets so the next MAIL FROM is accepted on the same connection.
         string reply = socket.SendAndReceive("MAIL FROM:<sender@example.com>\r\n");
         if (reply.StartsWith("250"))
         {
            string rsetReply = socket.SendAndReceive("RSET\r\n");
            Assert.IsTrue(rsetReply.StartsWith("250"), "RSET should succeed. Got: " + rsetReply);
         }

         return reply;
      }

      [Test]
      [Description("With MaxSubmissionsPerIPPerMinute=2, the first two MAIL FROM transactions from a " +
                   "source IP succeed and the third is rejected with 421; the throttle is per source IP " +
                   "so a second connection from the same IP is also rejected. Setting the limit back to " +
                   "0 restores unlimited submissions.")]
      public void TestPerIpSubmissionThrottle()
      {
         try
         {
            WriteSetting("MaxSubmissionsPerIPPerMinute", "2");
            _application.Reinitialize();

            var socket = new TcpConnection();
            Assert.IsTrue(socket.Connect(25));
            Assert.IsTrue(socket.Receive().StartsWith("220"));
            socket.Send("HELO example.com\r\n");
            Assert.IsTrue(socket.Receive().StartsWith("250"));

            // First two submissions are within budget.
            Assert.IsTrue(StartTransaction(socket).StartsWith("250"), "First submission should be accepted.");
            Assert.IsTrue(StartTransaction(socket).StartsWith("250"), "Second submission should be accepted.");

            // Third exceeds the per-minute budget for this IP.
            string throttled = StartTransaction(socket);
            Assert.IsTrue(throttled.StartsWith("421"),
               "Third submission should be throttled with 421. Got: " + throttled);

            socket.Send("QUIT\r\n");
            socket.Disconnect();

            // The throttle is keyed on the source IP, so a brand-new connection
            // from the same IP is still over budget.
            var socket2 = new TcpConnection();
            Assert.IsTrue(socket2.Connect(25));
            Assert.IsTrue(socket2.Receive().StartsWith("220"));
            socket2.Send("HELO example.com\r\n");
            Assert.IsTrue(socket2.Receive().StartsWith("250"));

            string throttled2 = socket2.SendAndReceive("MAIL FROM:<sender@example.com>\r\n");
            Assert.IsTrue(throttled2.StartsWith("421"),
               "A new connection from the same IP should still be throttled. Got: " + throttled2);

            socket2.Send("QUIT\r\n");
            socket2.Disconnect();
         }
         finally
         {
            WriteSetting("MaxSubmissionsPerIPPerMinute", "0");
            _application.Reinitialize();
         }
      }

      [Test]
      [Description("With the default MaxSubmissionsPerIPPerMinute=0 the throttle is disabled, so many " +
                   "consecutive MAIL FROM transactions from one IP are all accepted.")]
      public void TestThrottleDisabledByDefault()
      {
         WriteSetting("MaxSubmissionsPerIPPerMinute", "0");
         _application.Reinitialize();

         var socket = new TcpConnection();
         Assert.IsTrue(socket.Connect(25));
         Assert.IsTrue(socket.Receive().StartsWith("220"));
         socket.Send("HELO example.com\r\n");
         Assert.IsTrue(socket.Receive().StartsWith("250"));

         for (int i = 0; i < 10; i++)
         {
            Assert.IsTrue(StartTransaction(socket).StartsWith("250"),
               "Submission " + i + " should be accepted when the throttle is disabled.");
         }

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }
   }
}
