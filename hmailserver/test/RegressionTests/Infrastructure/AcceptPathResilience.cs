// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    The accept path is shared by every protocol, so anything that goes wrong in
   ///    it goes wrong for SMTP, POP3 and IMAP at the same moment. These tests cover
   ///    the two ways it could be made to misbehave from outside: by being refused,
   ///    and by disappearing.
   /// </summary>
   [TestFixture]
   public class AcceptPathResilience : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      // Long enough that the hold is unmistakably still in force while the probe
      // below runs, short enough that a failing build recovers by itself.
      private const int HoldSeconds = 12;

      // The greeting has to arrive well inside the hold or the test proves nothing.
      private const int ProbeTimeoutMilliseconds = 5000;

      /// <summary>
      ///    Writes [Settings] BlockedIPHoldSeconds and reloads it. The server reads
      ///    hMailServer.ini from its bin directory (Utilities::GetBinDirectory): a
      ///    registered install resolves to {InstallLocation}\Bin, a developer build
      ///    that is not registered reads the ini next to the running executable.
      ///    Write to every existing candidate so the file the service actually reads
      ///    is updated whichever layout this is.
      /// </summary>
      private void SetBlockedIpHoldSeconds(int seconds)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         var wroteAny = false;
         foreach (var iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", "BlockedIPHoldSeconds", seconds.ToString(), iniPath),
               "Failed to write BlockedIPHoldSeconds to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");

         // IniFileSettings caches everything at startup; Reinitialize reloads it.
         _application.Reinitialize();
      }

      /// <summary>
      ///    BlockedIPHoldSeconds is the anti-pounding hold: a connection refused by
      ///    the IP ranges or the per-protocol connection limit is kept open for a
      ///    while before it is dropped, so the peer cannot retry at full speed. It
      ///    was implemented as Sleep() - on the I/O thread that had just accepted the
      ///    connection.
      ///
      ///    There are only TCPIPThreads of those (15 in this suite), and SMTP, POP3
      ///    and IMAP all share them. So the setting handed the peer it was meant to
      ///    punish a way to stop the whole server: open one more refused connection
      ///    than there are threads and every worker is asleep at once, which means no
      ///    accept, no read and no write is dispatched for any protocol until they
      ///    wake. Reconnecting keeps them that way indefinitely. The Control Panel
      ///    offers the setting on its hardening page, so this is a foot-gun the
      ///    product actively recommends.
      ///
      ///    The probe deliberately uses POP3 while the refusals are on SMTP: the
      ///    starvation is not confined to the protocol being attacked, which is the
      ///    part that makes it worth a test.
      /// </summary>
      [Test]
      [Description("A connection refused at accept time is held on a timer, not by sleeping on the I/O thread that accepted it")]
      public void HeldBlockedConnectionsDoNotStarveTheOtherProtocols()
      {
         CustomAsserts.AssertSessionCount(eSessionType.eSTSMTP, 0);

         var originalMaxSmtpConnections = _settings.MaxSMTPConnections;
         var threadCount = _settings.TCPIPThreads;

         var refused = new List<TcpClient>();

         try
         {
            SetBlockedIpHoldSeconds(HoldSeconds);

            // One accepted SMTP session fills the quota, so everything after it is
            // refused - and held.
            _settings.MaxSMTPConnections = 1;

            using (var accepted = new TcpConnection())
            {
               Assert.IsTrue(accepted.Connect(25), "Could not open the one permitted SMTP session.");
               StringAssert.StartsWith("220", accepted.Receive());

               // More refusals than there are I/O threads. Opened without reading,
               // because the point is what the SERVER does with them.
               for (var i = 0; i < threadCount + 5; i++)
               {
                  var blocked = new TcpClient();
                  blocked.Connect("127.0.0.1", 25);
                  refused.Add(blocked);
               }

               using (var probe = new TcpClient())
               {
                  probe.Connect("127.0.0.1", 110);
                  probe.ReceiveTimeout = ProbeTimeoutMilliseconds;

                  var buffer = new byte[256];
                  var read = 0;

                  try
                  {
                     read = probe.GetStream().Read(buffer, 0, buffer.Length);
                  }
                  catch (IOException)
                  {
                     Assert.Fail("No POP3 greeting arrived within " + ProbeTimeoutMilliseconds + " ms while " +
                                 refused.Count + " refused SMTP connections were being held for " + HoldSeconds +
                                 " seconds. Every TCP/IP worker thread is blocked inside the accept path.");
                  }

                  var greeting = Encoding.ASCII.GetString(buffer, 0, read);
                  StringAssert.StartsWith("+OK", greeting);
               }
            }
         }
         finally
         {
            foreach (var blocked in refused)
            {
               try
               {
                  blocked.Close();
               }
               catch (Exception)
               {
                  // Closing a socket the server has already dropped is not a failure.
               }
            }

            _settings.MaxSMTPConnections = originalMaxSmtpConnections;

            // Also restarts the servers, which releases any socket still being held.
            SetBlockedIpHoldSeconds(0);
         }
      }

      /// <summary>
      ///    A peer can be gone before the server has looked at the socket it just
      ///    accepted - a connect-scan that resets immediately is enough, and then
      ///    getpeername answers WSAENOTCONN.
      ///
      ///    TCPServer::HandleAccept read both endpoints through the throwing
      ///    overloads, and it is a boost::asio completion handler: the exception
      ///    unwinds out of io_context::run() onto the IOCP worker, where the barrier
      ///    reports HM4208 at High severity and the crash reporter writes a minidump.
      ///    So an ordinary port scan could fill a stock server's error log and burn
      ///    the ten-dump, 100 MB budget that a genuine fault needs - and the eleventh
      ///    dump is often the one somebody finally looks at.
      ///
      ///    Against the unfixed build this is probabilistic rather than certain: the
      ///    reset has to land before HandleAccept reads the endpoints. Repetition
      ///    across the three listeners is what makes it likely. It never fails
      ///    against the fixed build, where the failure is a TCP/IP log line.
      /// </summary>
      [Test]
      [Description("A client that resets the connection the instant it is accepted is not a server fault")]
      public void ConnectionResetAtAcceptTimeIsNotReportedAsAServerError()
      {
         foreach (var port in new[] {25, 110, 143})
         {
            for (var i = 0; i < 40; i++)
            {
               var socket = new TcpClient();
               socket.Connect("127.0.0.1", port);

               // Linger with a zero timeout makes Close() send RST rather than FIN,
               // which is what leaves the accepted socket with no peer to name.
               socket.LingerState = new LingerOption(true, 0);
               socket.Close();
            }
         }

         CustomAsserts.AssertSessionCount(eSessionType.eSTSMTP, 0);
         CustomAsserts.AssertSessionCount(eSessionType.eSTPOP3, 0);
         CustomAsserts.AssertSessionCount(eSessionType.eSTIMAP, 0);

         // The strongest recovery evidence: an ordinary transaction still works.
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "resetprobe@example.test", "test");
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "After the resets", "Body");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         // Asserted last, because a failure above is more informative than the log.
         CustomAsserts.AssertNoReportedError();
      }
   }
}
