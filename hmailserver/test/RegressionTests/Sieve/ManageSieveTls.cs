// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.SSL;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    TLS on the ManageSieve (RFC 5804) listener.
   ///
   ///    ManageSieve carries a password in a SASL PLAIN response and then carries
   ///    the mail filters of the account it belongs to, and this listener had no
   ///    TLS at all: STARTTLS was answered with
   ///    NO "STARTTLS is not supported on this listener." and the only advice on
   ///    offer was to bind it to 127.0.0.1. It is also the fourth listener to be
   ///    built outside the shared Boost.Asio stack, and the other three each built
   ///    their own SSL_CTX - which is how the REST API and Web Services listeners
   ///    came to miss the TLS hardening that SMTP, POP3 and IMAP get from
   ///    SslContextInitializer, the post-quantum key-exchange groups in
   ///    [Settings] TlsKeyExchangeGroups most recently.
   ///
   ///    So the point of these tests is not only that STARTTLS works, but that the
   ///    context it uses is the shared one. UsesTheSharedKeyExchangeGroups is the
   ///    test that pins that: a listener with a hand-rolled SSL_CTX would answer
   ///    with a classical curve, because the hybrid groups only ever reach a
   ///    context that SslContextInitializer configured.
   ///
   ///    Every test here fails against the code before the change - the first three
   ///    because STARTTLS is refused outright, the last one because the attempt
   ///    counter cannot survive an upgrade that cannot happen.
   /// </summary>
   [TestFixture]
   public class ManageSieveTls : TestFixtureBase
   {
      // A port of its own, so this fixture cannot collide with the ManageSieve
      // round-trip fixture (24190) or the auto-ban fixture (24191).
      private const int ManageSievePort = 24192;

      // The TLS supported-group codepoints and the raw-ClientHello probe that reads
      // the group a listener prefers both moved to Shared/TlsHandshakeProbe once the
      // REST API and Web Services HTTPS listeners needed the same proof. Kept as a
      // using-static-free delegation so the call site below reads unchanged.

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

      /// <summary>
      /// Configures TLS-capable mailbox ports with a certificate and then starts the
      /// ManageSieve listener. Both halves matter: the listener has no certificate
      /// setting of its own, so it borrows the one configured for IMAP/POP3/SMTP -
      /// which is exactly the point, since that is the certificate the client
      /// already trusts and the one that gets renewed.
      /// </summary>
      private void StartListenerWithTls()
      {
         SslSetup.SetupSSLPorts(_application);

         WriteSetting("ManageSieveServerBindAddress", "127.0.0.1");
         WriteSetting("ManageSieveServerPort", ManageSievePort.ToString());

         // Reinitialize so IniFileSettings re-reads the port, the listener starts,
         // and it builds its SSL context from the ports configured above.
         _application.Reinitialize();

         // The listener binds on a worker thread during startup; Session retries the
         // connect, but give the restart a moment first.
         Thread.Sleep(500);
      }

      private void StopListener()
      {
         WriteSetting("ManageSieveServerPort", "0");
         _application.Reinitialize();
      }

      // A minimal ManageSieve client that can upgrade itself to TLS in place.
      // Deliberately separate from the helpers in the other two ManageSieve
      // fixtures: this one has to keep reading and writing through whichever stream
      // is current, which is the whole behaviour under test.
      private sealed class Session : IDisposable
      {
         private readonly TcpClient _client;
         private readonly NetworkStream _networkStream;
         private SslStream _sslStream;
         private Stream _active;

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
                  Thread.Sleep(200);
               }
            }
            if (last != null)
               throw last;

            _networkStream = _client.GetStream();
            _networkStream.ReadTimeout = 15000;
            _active = _networkStream;
         }

         // The raw socket stream, for the hand-built TLS 1.3 probe below. Only valid
         // before HandshakeAsClient.
         public NetworkStream NetworkStream
         {
            get { return _networkStream; }
         }

         private string ReadLine()
         {
            var bytes = new List<byte>();
            int previous = -1;
            while (true)
            {
               int current = _active.ReadByte();
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

         public string ReadUntilClosed(int timeoutMilliseconds)
         {
            _active.ReadTimeout = timeoutMilliseconds;

            var sb = new StringBuilder();
            try
            {
               while (true)
               {
                  int current = _active.ReadByte();
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

         public void SendRaw(string text)
         {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _active.Write(bytes, 0, bytes.Length);
            _active.Flush();
         }

         public string SendCommand(string command)
         {
            SendRaw(command + "\r\n");
            return ReadResponse();
         }

         /// <summary>
         /// Performs the client side of the TLS handshake and switches every
         /// subsequent read and write onto it. The test certificate is not chained to
         /// a trusted root, so validation is bypassed - the same thing every other
         /// TLS test in this suite does.
         /// </summary>
         public void HandshakeAsClient()
         {
            _sslStream = new SslStream(_networkStream, false, (a, b, c, d) => true, null);
            _sslStream.AuthenticateAsClient("localhost", null, TcpConnection.ModernProtocols, false);
            _sslStream.ReadTimeout = 15000;

            _active = _sslStream;
         }

         public bool IsTls
         {
            get { return _sslStream != null && _sslStream.IsEncrypted; }
         }

         public string Authenticate(string username, string password)
         {
            string sasl = "\0" + username + "\0" + password;
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(sasl));
            return SendCommand("AUTHENTICATE \"PLAIN\" \"" + encoded + "\"");
         }

         public void Dispose()
         {
            try { _sslStream?.Dispose(); } catch { }
            try { _networkStream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
         }
      }

      // ---------------------------------------------------------------------------
      // A hand-built TLS 1.3 ClientHello whose key_share carries an *empty*
      // client_shares vector, which is legal (RFC 8446 4.1.2) and leaves the server
      // with no usable share - so it must answer with a HelloRetryRequest naming the
      // group it prefers. That is the only way to observe a server's group
      // preference from .NET: SslStream does not expose the negotiated group, and
      // Windows SChannel may not implement the hybrid KEMs at all.
      //
      // This is the same technique, and deliberately the same shape, as
      // RegressionTests/SSL/PostQuantumKeyExchange.cs, whose class comment explains
      // it at length. It is duplicated rather than shared because that file belongs
      // to another change; folding both onto one helper is a tidy-up for whoever
      // owns both next.
      // ---------------------------------------------------------------------------


      [Test]
      [Description("The capability list advertises STARTTLS when a TLS certificate is available, and stops advertising it once TLS is active.")]
      public void StartTlsIsAdvertisedOnlyWhileItCanBeUsed()
      {
         StartListenerWithTls();

         try
         {
            using (var session = new Session())
            {
               string greeting = session.ReadResponse();

               // Fails before the change: this listener never advertised STARTTLS,
               // because it had no TLS to offer. A client (Roundcube's managesieve
               // plugin, sieve-connect) therefore had no way to protect the SASL
               // PLAIN response it is about to send, and no way to know that.
               StringAssert.Contains("STARTTLS", greeting,
                  "The greeting should advertise STARTTLS. Got: " + greeting);

               string upgrade = session.SendCommand("STARTTLS");
               StringAssert.StartsWith("OK", upgrade.TrimStart(),
                  "STARTTLS should be accepted. Got: " + upgrade);

               session.HandshakeAsClient();
               Assert.IsTrue(session.IsTls, "The connection should be encrypted after the handshake.");

               // RFC 5804 2.2: a fresh capability response follows the handshake, and
               // STARTTLS must be gone from it - a client that saw it twice could not
               // tell a completed upgrade from a stripped one.
               string afterHandshake = session.ReadResponse();
               StringAssert.Contains("IMPLEMENTATION", afterHandshake,
                  "A capability response should follow the TLS handshake. Got: " + afterHandshake);
               Assert.IsFalse(afterHandshake.Contains("STARTTLS"),
                  "STARTTLS must not be advertised once TLS is active. Got: " + afterHandshake);

               // And it is refused if asked for again.
               string second = session.SendCommand("STARTTLS");
               StringAssert.StartsWith("NO", second.TrimStart(),
                  "A second STARTTLS should be refused. Got: " + second);
            }
         }
         finally
         {
            StopListener();
         }
      }

      [Test]
      [Description("A full authenticated ManageSieve session runs over the upgraded TLS connection.")]
      public void ScriptsCanBeManagedOverTls()
      {
         const string address = "managesieve-tls@example.test";
         const string password = "Sieve-Pass1";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

         StartListenerWithTls();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting

               // Fails before the change: the answer here was
               // NO "STARTTLS is not supported on this listener."
               StringAssert.StartsWith("OK", session.SendCommand("STARTTLS").TrimStart());

               session.HandshakeAsClient();
               session.ReadResponse(); // post-handshake capabilities

               // Everything from here on is inside TLS, which also proves the reads
               // and writes actually moved onto the TLS session rather than
               // continuing to use the bare socket underneath it.
               string auth = session.Authenticate(address, password);
               StringAssert.StartsWith("OK", auth.TrimStart(), "Authentication over TLS should succeed. Got: " + auth);

               session.SendCommand("SETACTIVE \"\"");
               session.SendCommand("DELETESCRIPT \"over-tls\"");

               const string scriptBody = "require \"fileinto\";\r\nkeep;\r\n";
               byte[] payload = Encoding.UTF8.GetBytes(scriptBody);

               session.SendRaw("PUTSCRIPT \"over-tls\" {" + payload.Length + "+}\r\n");
               session.SendRaw(scriptBody + "\r\n");
               string put = session.ReadResponse();
               StringAssert.StartsWith("OK", put.TrimStart(), "PUTSCRIPT over TLS should succeed. Got: " + put);

               string list = session.SendCommand("LISTSCRIPTS");
               StringAssert.Contains("\"over-tls\"", list);

               StringAssert.StartsWith("OK", session.SendCommand("DELETESCRIPT \"over-tls\"").TrimStart());
               StringAssert.StartsWith("OK", session.SendCommand("LOGOUT").TrimStart());
            }
         }
         finally
         {
            StopListener();
         }
      }

      [Test]
      [Description("The TLS context this listener uses is the shared one: it prefers the hybrid post-quantum key exchange group, as the SMTP/POP3/IMAP listeners do.")]
      public void StartTlsUsesTheSharedKeyExchangeGroups()
      {
         StartListenerWithTls();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting

               StringAssert.StartsWith("OK", session.SendCommand("STARTTLS").TrimStart());

               // The client offers one hybrid group and one classical group. A
               // listener that built its own SSL_CTX - which is what every optional
               // listener here has done - would answer X25519, because the hybrid
               // groups only reach a context configured by SslContextInitializer from
               // [Settings] TlsKeyExchangeGroups. Answering X25519MLKEM768 is
               // therefore the observable proof that this listener stopped having a
               // TLS configuration of its own.
               //
               // Note this test needs no ini setting of its own: it asserts the
               // shipped default, which is what a user reading "post-quantum key
               // exchange" in the release notes is entitled to get here too.
               int selected = TlsHandshakeProbe.GetPreferredGroup(session.NetworkStream,
                  new[] {TlsHandshakeProbe.GroupX25519Mlkem768, TlsHandshakeProbe.GroupX25519});

               Assert.AreEqual(TlsHandshakeProbe.GroupX25519Mlkem768, selected,
                  "The ManageSieve listener preferred group 0x" + selected.ToString("X4") +
                  " over the hybrid post-quantum group X25519MLKEM768, so it is not using the shared TLS " +
                  "configuration.");
            }
         }
         finally
         {
            StopListener();
         }
      }

      [Test]
      [Description("The per-connection authentication attempt cap is not reset by a STARTTLS upgrade.")]
      public void StartTlsDoesNotResetTheAuthenticationAttemptCap()
      {
         const string address = "managesieve-tls-cap@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "Sieve-Pass1");

         // Auto-ban stays off: the per-connection cap has to hold on its own, and a
         // ban on 127.0.0.1 would block every protocol for whatever ran next.
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         StartListenerWithTls();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting

               // Two failures in clear text...
               StringAssert.Contains("Authentication failed", session.Authenticate(address, "wrong-1"));
               StringAssert.Contains("Authentication failed", session.Authenticate(address, "wrong-2"));

               // ...then the upgrade. A client that could clear the counter by
               // upgrading would get three fresh guesses per STARTTLS, for free, and
               // the cap - which is what makes the auto-ban accounting worth
               // anything on this listener - would mean nothing.
               StringAssert.StartsWith("OK", session.SendCommand("STARTTLS").TrimStart());

               session.HandshakeAsClient();
               session.ReadResponse(); // post-handshake capabilities

               string third = session.Authenticate(address, "wrong-3");
               StringAssert.Contains("Too many invalid logon attempts", third,
                  "The third failed attempt must hit the cap even though STARTTLS was issued in between. Got: " + third);

               string afterCap = session.ReadUntilClosed(5000);
               Assert.AreEqual(string.Empty, afterCap,
                  "The connection should be closed once the attempt cap is reached. Received: " + afterCap);
            }
         }
         finally
         {
            _settings.ClearLogonFailureList();
            StopListener();
         }
      }

      [Test]
      [Description("A plain-text session still works exactly as before: STARTTLS is optional, never required.")]
      public void PlainTextSessionStillWorks()
      {
         const string address = "managesieve-plain@example.test";
         const string password = "Sieve-Pass1";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

         StartListenerWithTls();

         try
         {
            using (var session = new Session())
            {
               string greeting = session.ReadResponse();
               StringAssert.Contains("IMPLEMENTATION", greeting);

               // The negative control for the whole change. Every existing
               // ManageSieve client connects in clear text and never asks for TLS;
               // making TLS available must not make a single one of them stop
               // working. This test passes before the change as well as after - that
               // is the point of it.
               string auth = session.Authenticate(address, password);
               StringAssert.StartsWith("OK", auth.TrimStart(), "Plain-text authentication should still work. Got: " + auth);

               string list = session.SendCommand("LISTSCRIPTS");
               Assert.IsFalse(list.TrimStart().StartsWith("NO"),
                  "LISTSCRIPTS should be permitted after a plain-text authentication. Got: " + list);

               StringAssert.StartsWith("OK", session.SendCommand("LOGOUT").TrimStart());
            }
         }
         finally
         {
            StopListener();
         }
      }
   }
}
