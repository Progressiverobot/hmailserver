// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    Brute-force protection on the ManageSieve (RFC 5804) listener.
   ///
   ///    These tests exist because ManageSieve had the accounting but not the
   ///    enforcement. A failed AUTHENTICATE was reported to the same per-IP
   ///    auto-ban bookkeeping that SMTP/POP3/IMAP use, which duly wrote an
   ///    auto-ban security range to the database - but that range is only ever
   ///    consulted when a connection is accepted, and it was the Boost.Asio
   ///    listener (TCPServer) doing the consulting. ManageSieve runs its own
   ///    raw-socket accept loop and never looked, so a client that had just been
   ///    banned only had to reconnect to be handed a fresh greeting and three
   ///    more guesses, for as long as it liked. The per-connection cap of three
   ///    attempts made no difference: reconnecting is free.
   ///
   ///    The second test covers the other half of the same hole. RFC 5804 lets
   ///    the SASL response arrive as a literal rather than a quoted string, and
   ///    that form was not implemented: it was rejected as an invalid SASL
   ///    response, its unread payload was then interpreted as a command, and
   ///    because the password check was never reached the attempt was not
   ///    counted against anything.
   /// </summary>
   [TestFixture]
   public class ManageSieveAutoBan : TestFixtureBase
   {
      // A port of its own, so this fixture cannot interfere with the ManageSieve
      // round-trip fixture if the two are ever run in parallel.
      private const int ManageSievePort = 24191;

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

      private void StartListener()
      {
         WriteSetting("ManageSieveServerBindAddress", "127.0.0.1");
         WriteSetting("ManageSieveServerPort", ManageSievePort.ToString());

         // Reinitialize so IniFileSettings re-reads the port and the listener starts.
         _application.Reinitialize();
      }

      private void StopListener()
      {
         WriteSetting("ManageSieveServerPort", "0");
         _application.Reinitialize();
      }

      private bool HasAutoBanRange()
      {
         SecurityRanges ranges = _settings.SecurityRanges;

         for (int i = 0; i < ranges.Count; i++)
         {
            if (ranges[i].Name.StartsWith("Auto-ban:", StringComparison.OrdinalIgnoreCase))
               return true;
         }

         return false;
      }

      // An auto-ban on 127.0.0.1 would block every other protocol for anything that
      // ran afterwards, so it is removed again even though the fixture setup would
      // eventually restore the default ranges.
      private void DeleteAutoBanRanges()
      {
         SecurityRanges ranges = _settings.SecurityRanges;

         for (int i = ranges.Count - 1; i >= 0; i--)
         {
            if (ranges[i].Name.StartsWith("Auto-ban:", StringComparison.OrdinalIgnoreCase))
               ranges.Delete(i);
         }
      }

      // A minimal ManageSieve client. Deliberately separate from the helper in the
      // round-trip fixture, because this one has to be able to observe a connection
      // that is closed without a single byte being sent back.
      private sealed class Session : IDisposable
      {
         private readonly TcpClient _client;
         private readonly NetworkStream _stream;

         public Session()
         {
            _client = new TcpClient();

            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  _client.Connect("127.0.0.1", ManageSievePort);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  System.Threading.Thread.Sleep(200);
               }
            }
            if (last != null)
               throw last;

            _stream = _client.GetStream();
            _stream.ReadTimeout = 15000;
         }

         // Returns null when the server closed the connection instead of sending a
         // complete line.
         private string ReadLine()
         {
            var bytes = new System.Collections.Generic.List<byte>();
            int previous = -1;
            while (true)
            {
               int current = _stream.ReadByte();
               if (current == -1)
               {
                  if (bytes.Count == 0)
                     return null;
                  break;
               }

               if (previous == '\r' && current == '\n')
               {
                  bytes.RemoveAt(bytes.Count - 1); // drop the trailing CR
                  break;
               }

               bytes.Add((byte) current);
               previous = current;
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
         }

         // Reads response lines up to and including the completion line
         // (OK / NO / BYE), or until the server hangs up.
         public string ReadResponse()
         {
            var sb = new StringBuilder();
            while (true)
            {
               string line = ReadLine();
               if (line == null)
                  break;

               sb.AppendLine(line);

               string trimmed = line.TrimStart();
               if (trimmed.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("NO", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("BYE", StringComparison.OrdinalIgnoreCase))
                  break;
            }

            return sb.ToString();
         }

         // Reads everything the server sends until it closes the connection. A
         // connection that is rejected outright yields the empty string. If the
         // server instead answers and keeps the connection open, whatever it sent is
         // returned once the read times out, so the assertion message can show it.
         public string ReadUntilClosed(int timeoutMilliseconds)
         {
            _stream.ReadTimeout = timeoutMilliseconds;

            var sb = new StringBuilder();
            try
            {
               while (true)
               {
                  int current = _stream.ReadByte();
                  if (current == -1)
                     break;

                  sb.Append((char) current);
               }
            }
            catch (IOException)
            {
               // Timed out waiting for either data or a close.
            }

            return sb.ToString();
         }

         private void SendRaw(string text)
         {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
         }

         public string SendCommand(string command)
         {
            SendRaw(command + "\r\n");
            return ReadResponse();
         }

         private static string EncodeSaslPlain(string username, string password)
         {
            string sasl = "\0" + username + "\0" + password;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(sasl));
         }

         // The SASL response as a quoted string.
         public string Authenticate(string username, string password)
         {
            return SendCommand("AUTHENTICATE \"PLAIN\" \"" + EncodeSaslPlain(username, password) + "\"");
         }

         // The SASL response as a non-synchronizing literal, which is the form the
         // RFC 5804 examples use and which real clients send.
         public string AuthenticateWithLiteral(string username, string password)
         {
            string encoded = EncodeSaslPlain(username, password);
            byte[] payload = Encoding.UTF8.GetBytes(encoded);

            SendRaw("AUTHENTICATE \"PLAIN\" {" + payload.Length + "+}\r\n");
            SendRaw(encoded + "\r\n");

            return ReadResponse();
         }

         public void Dispose()
         {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
         }
      }

      [Test]
      [Description("An address auto-banned by repeated failed ManageSieve logons is dropped when it reconnects, instead of being greeted and given a fresh set of attempts.")]
      public void TestAutoBannedAddressIsRefusedOnReconnect()
      {
         const string address = "managesieve-ban@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "Sieve-Pass1");

         _settings.ClearLogonFailureList();
         _settings.AutoBanOnLogonFailure = true;
         _settings.MaxInvalidLogonAttempts = 2;
         _settings.MaxInvalidLogonAttemptsWithin = 5;
         _settings.AutoBanMinutes = 3;

         StartListener();

         try
         {
            // One failed attempt per connection, hanging up in between. This is
            // exactly how an attacker walks around a per-connection cap, and two
            // attempts is the configured auto-ban threshold.
            for (int attempt = 0; attempt < 2; attempt++)
            {
               using (var session = new Session())
               {
                  string greeting = session.ReadResponse();
                  StringAssert.Contains("IMPLEMENTATION", greeting);

                  string response = session.Authenticate(address, "wrong-password");
                  StringAssert.StartsWith("NO", response.TrimStart());
               }
            }

            // Precondition for the real assertion below: the ban was in fact
            // recorded. If this is what fails, the problem is in the accounting
            // rather than in the enforcement.
            Assert.IsTrue(HasAutoBanRange(),
               "Repeated failed ManageSieve logons should have created an auto-ban IP range.");

            // The behaviour under test. The banned address must get nothing at all:
            // no greeting, no capability list, no chance to send a command. Until the
            // listener consulted the security ranges at accept time this connection
            // was greeted like any other, which is what made the ban - and the
            // per-connection cap it backs up - purely decorative.
            using (var session = new Session())
            {
               string received = session.ReadUntilClosed(5000);
               Assert.AreEqual(string.Empty, received,
                  "A banned address must be disconnected before the greeting. Received: " + received);
            }
         }
         finally
         {
            _settings.AutoBanOnLogonFailure = false;
            _settings.ClearLogonFailureList();
            DeleteAutoBanRanges();

            StopListener();
         }
      }

      [Test]
      [Description("A SASL response sent as an RFC 5804 literal authenticates, and its failures count towards the per-connection attempt cap.")]
      public void TestLiteralSaslResponseAuthenticatesAndCountsFailures()
      {
         const string address = "managesieve-literal@example.test";
         const string password = "Sieve-Pass1";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

         // Auto-ban stays off on purpose: the per-connection cap has to hold on its
         // own, without help from the per-IP accounting.
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         StartListener();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting

               // Correct credentials in the literal form are accepted. Previously the
               // literal was not understood: the server answered
               // NO "Invalid SASL response." and then parsed the base64 payload as
               // though it were the next command.
               string authenticated = session.AuthenticateWithLiteral(address, password);
               StringAssert.StartsWith("OK", authenticated.TrimStart());
            }

            using (var session = new Session())
            {
               session.ReadResponse(); // greeting

               // Three failures is the cap. Because the literal form never reached the
               // password check, none of these attempts used to be counted - against
               // this connection or against the address - so the connection stayed
               // open for as many guesses as the client cared to make.
               string first = session.AuthenticateWithLiteral(address, "wrong-password-1");
               StringAssert.Contains("Authentication failed", first);

               string second = session.AuthenticateWithLiteral(address, "wrong-password-2");
               StringAssert.Contains("Authentication failed", second);

               string third = session.AuthenticateWithLiteral(address, "wrong-password-3");
               StringAssert.Contains("Too many invalid logon attempts", third);

               // ...and the connection is closed rather than left open for a fourth.
               string afterCap = session.ReadUntilClosed(5000);
               Assert.AreEqual(string.Empty, afterCap,
                  "The connection should be closed once the attempt cap is reached. Received: " + afterCap);
            }
         }
         finally
         {
            StopListener();
         }
      }
   }
}
