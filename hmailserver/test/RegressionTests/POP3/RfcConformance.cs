// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using RegressionTests.SSL;

namespace RegressionTests.POP3
{
   /// <summary>
   /// POP3 conformance defects found by reconciling the POP3 section of Roadmap.md against
   /// Server/POP3/POP3Connection.cpp. Each test names the behaviour of the unfixed server
   /// in a comment, because that is the only thing that makes the test worth keeping.
   ///
   /// 1. DELE of a message that is already marked deleted answered "+OK msg deleted" a
   ///    second time. RFC 1939 requires a refusal, and every other command in the file
   ///    already treats a flagged message as absent.
   /// 2. PASS with no preceding USER ran a full logon against an empty user name: it fired
   ///    OnClientLogon with a blank identity, registered a failed login against the
   ///    client's IP (feeding the auto-ban) and spent one of the connection's ten
   ///    permitted attempts - all for a command that carried no credential.
   /// 3. USER with an empty or whitespace-only argument was answered "+OK Send your
   ///    password", so the PASS that followed spent an attempt on a name never sent.
   /// 4. CAPA advertised USER and SASL on a cleartext connection whose IP range sets
   ///    RequireTLSForAuth, where both are refused. A client that took up the SASL offer
   ///    put its password on the wire in the clear before learning that.
   /// 5. TOP with a negative or non-numeric line count was silently treated as zero.
   /// 6. TOP always terminated with "\r\n.", and a stored message already ends with CRLF,
   ///    so TOP delivered one blank line of body that RETR does not.
   /// 7. A maildrop held by another session was refused with no response code, which a
   ///    client can only report to the user as a rejected password.
   ///
   /// One test here fixes nothing and is labelled as such: the legacy-mechanism test pins
   /// a deliberate omission (APOP, CRAM-MD5 and DIGEST-MD5 need a reversible or
   /// plaintext-equivalent stored secret) so that offering one later has to be a conscious
   /// change rather than an accident.
   /// </summary>
   [TestFixture]
   public class RfcConformance : TestFixtureBase
   {
      private static TcpConnection ConnectPop3()
      {
         var connection = new TcpConnection();
         Assert.IsTrue(connection.Connect(110), "Could not connect to the POP3 server on port 110.");
         connection.ReadUntil("+OK"); // banner
         return connection;
      }

      /// <summary>
      /// Reads a complete CAPA response. A single Receive() can stop short of the
      /// dot-terminator, and a truncated read would make an "absence" assertion pass for
      /// the wrong reason.
      /// </summary>
      private static string ReadCapa(TcpConnection connection)
      {
         connection.Send("CAPA\r\n");
         return connection.ReadUntil("\r\n.\r\n");
      }

      private static void LogOn(TcpConnection connection, string address, string password)
      {
         Assert.IsTrue(connection.SendAndReceive("USER " + address + "\r\n").StartsWith("+OK"),
            "USER was not accepted.");
         Assert.IsTrue(connection.SendAndReceive("PASS " + password + "\r\n").StartsWith("+OK"),
            "PASS was not accepted.");
      }

      [Test]
      [Description("RFC 1939 DELE: a message already marked deleted is refused rather than marked a " +
                   "second time, and a message that is not deleted is still accepted.")]
      public void DeleteOfAlreadyDeletedMessageIsRefused()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "deletetwice@example.test", "test");

         for (var i = 1; i <= 3; i++)
            SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TestBody" + i);

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 3);

         var sim = new Pop3ClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));

         Assert.IsTrue(sim.DELE(2), "The first DELE must be accepted.");

         // The defect: this answered "+OK msg deleted" again. RFC 1939 requires
         // "-ERR message 2 already deleted", and a client resynchronising after a lost
         // response could not otherwise tell an accepted DELE from a repeated one.
         Assert.IsFalse(sim.DELE(2),
            "DELE of a message already marked deleted must be refused (RFC 1939).");

         // Negative control: the refusal is about the flag, not about DELE. If this had
         // regressed, the "fix" would have broken every client that deletes its mail.
         Assert.IsTrue(sim.DELE(3), "DELE of a message that is not deleted must still be accepted.");

         sim.QUIT();
      }

      [Test]
      [Description("RFC 1939: PASS before a successful USER is refused, and the refusal leaves no state " +
                   "behind - the same connection can still log on afterwards.")]
      public void PassWithoutUserIsRefused()
      {
         // Auto-ban would otherwise decide the outcome of this test rather than the code
         // under test: against the unfixed server the bare PASS below counts as a failed
         // logon for 127.0.0.1, and with the ban armed the logon at the end would be
         // refused at the connection and never reach the server's parser. It is off by
         // default in PerformBasicSetup; made explicit because this test is precisely
         // about a command that must NOT register a failure.
         var originalAutoBan = _settings.AutoBanOnLogonFailure;
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "passfirst@example.test", "test");

            using (var connection = ConnectPop3())
            {
               var response = connection.SendAndReceive("PASS test\r\n");

               // The defect: the server logged on with an empty user name and answered
               // "-ERR Invalid user name or password. Please use full email address as
               // user name." - naming a credential the client never sent - having first
               // fired OnClientLogon with a blank username, registered a failed login for
               // this IP and spent one of this connection's ten attempts.
               StringAssert.Contains("USER", response,
                  "PASS with no preceding USER must be refused with a reply that asks for USER. Got: " + response);

               // Negative control: nothing was poisoned by the refusal.
               LogOn(connection, account.Address, "test");

               connection.Send("QUIT\r\n");
               connection.ReadUntil("+OK");
            }
         }
         finally
         {
            _settings.AutoBanOnLogonFailure = originalAutoBan;
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("RFC 1939: USER with no argument, or only whitespace, is refused instead of being " +
                   "answered +OK, so the PASS that follows cannot spend a logon attempt on a name that " +
                   "was never sent.")]
      public void UserWithNoArgumentIsRefused()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "emptyuser@example.test", "test");

         using (var connection = ConnectPop3())
         {
            // The defect: both of these were answered "+OK Send your password".
            var missing = connection.SendAndReceive("USER\r\n");
            Assert.IsTrue(missing.StartsWith("-ERR"),
               "USER with no argument must be refused. Got: " + missing);

            var whitespace = connection.SendAndReceive("USER    \r\n");
            Assert.IsTrue(whitespace.StartsWith("-ERR"),
               "USER with a whitespace-only argument must be refused. Got: " + whitespace);

            // Negative control: a real name is still accepted, and still logs on.
            LogOn(connection, account.Address, "test");

            connection.Send("QUIT\r\n");
            connection.ReadUntil("+OK");
         }
      }

      [Test]
      [Description("RFC 2449: when the connecting IP range requires TLS for authentication, a cleartext " +
                   "CAPA must not advertise USER or SASL, because the server refuses both on that " +
                   "connection.")]
      public void CapaOmitsAuthenticationCapabilitiesWhenTheIpRangeRequiresTls()
      {
         var range = _settings.SecurityRanges.get_ItemByName("My computer");
         range.RequireSSLTLSForAuth = true;
         range.Save();

         try
         {
            using (var connection = ConnectPop3())
            {
               var capabilities = ReadCapa(connection);

               // The defect: USER and "SASL PLAIN SCRAM-SHA-256" were still listed. The
               // consequence is not cosmetic - a client that takes up the SASL offer sends
               // "AUTH PLAIN <base64 authcid NUL password>" and only then learns the
               // connection is unacceptable, so the password has already crossed the wire
               // in the clear.
               Assert.IsFalse(capabilities.Contains("USER"),
                  "CAPA must not advertise USER when the IP range requires TLS for authentication. Got: " + capabilities);
               Assert.IsFalse(capabilities.Contains("SASL"),
                  "CAPA must not advertise SASL when the IP range requires TLS for authentication. Got: " + capabilities);

               // Negative control: this is a suppression of two capabilities, not an empty
               // or broken capability list.
               StringAssert.Contains("UIDL", capabilities,
                  "The rest of the capability list must still be present. Got: " + capabilities);

               // And the suppression says the same thing the commands do.
               var user = connection.SendAndReceive("USER someone@example.test\r\n");
               StringAssert.Contains("SSL/TLS-connection is required", user,
                  "USER must still be refused on the cleartext connection. Got: " + user);

               var auth = connection.SendAndReceive("AUTH PLAIN\r\n");
               StringAssert.Contains("SSL/TLS-connection is required", auth,
                  "AUTH must still be refused on the cleartext connection. Got: " + auth);
            }
         }
         finally
         {
            // PerformBasicSetup calls SecurityRanges.SetDefault() before every test, so
            // this would be undone anyway - but not before the rest of THIS test, and a
            // range that refuses all cleartext authentication breaks every fixture that
            // logs on.
            range.RequireSSLTLSForAuth = false;
            range.Save();
         }
      }

      [Test]
      [Description("RFC 1939: TOP with a negative or non-numeric line count is refused; a count of zero " +
                   "is valid and returns the headers with no body.")]
      public void TopWithInvalidLineCountIsRefused()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "topcount@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TopBody1\r\nTopBody2");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         using (var connection = ConnectPop3())
         {
            LogOn(connection, account.Address, "test");

            // The defect: both of these fell out of _ttoi as 0 and were answered with the
            // headers, so the server obeyed a command that had not been given. RFC 1939
            // defines the argument as "a non-negative number of lines".
            var negative = connection.SendAndReceive("TOP 1 -1\r\n");
            Assert.IsTrue(negative.StartsWith("-ERR"),
               "TOP with a negative line count must be refused. Got: " + negative);

            var text = connection.SendAndReceive("TOP 1 rubbish\r\n");
            Assert.IsTrue(text.StartsWith("-ERR"),
               "TOP with a non-numeric line count must be refused. Got: " + text);

            // Negative control, and a check of one of the two edge cases the roadmap
            // listed as unverified: zero lines is a valid request for the headers alone.
            connection.Send("TOP 1 0\r\n");
            var headers = connection.ReadUntil("\r\n.\r\n");
            Assert.IsTrue(headers.StartsWith("+OK"),
               "TOP with a line count of zero must be accepted. Got: " + headers);
            StringAssert.Contains("Subject: Test", headers,
               "TOP must send the headers. Got: " + headers);
            Assert.IsFalse(headers.Contains("TopBody1"),
               "TOP with a line count of zero must send no body lines. Got: " + headers);

            connection.Send("QUIT\r\n");
            connection.ReadUntil("+OK");
         }
      }

      [Test]
      [Description("RFC 1939: the TOP response ends CRLF '.' CRLF without an invented blank line, so a " +
                   "message read with TOP over its whole body is byte-for-byte what RETR sends.")]
      public void TopDoesNotSendAnExtraBlankLineBeforeTheTerminator()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "topbytes@example.test", "test");

         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Test", "TopBody1\r\nTopBody2");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var sim = new Pop3ClientSimulator();
         Assert.IsTrue(sim.ConnectAndLogon(account.Address, "test"));

         // A line count past the end of the body: RFC 1939 says the whole message is then
         // sent, which is what makes this directly comparable with RETR. The body has no
         // line beginning with a dot, so the two dot-stuffing implementations (TOP stuffs
         // line by line, RETR streams through TransparentTransmissionBuffer) cannot
         // account for a difference.
         var top = sim.TOP(1, 1000);
         var retr = sim.RETR(1);
         sim.QUIT();

         // Both responses open with the same "+OK <n> octets" status line; everything
         // after it is the message and the terminator.
         var topBody = top.Substring(top.IndexOf("\r\n") + 2);
         var retrBody = retr.Substring(retr.IndexOf("\r\n") + 2);

         // The defect: TOP wrote "\r\n." unconditionally after the last line it sent, and
         // a stored message already ends with CRLF, so TOP's copy of the message carried
         // one blank line that RETR's did not.
         Assert.AreEqual(retrBody, topBody,
            "TOP over the whole message must deliver the same bytes as RETR.");
      }

      [Test]
      [Description("RFC 2449: a second session on a maildrop another session holds is refused with the " +
                   "[IN-USE] response code, and CAPA declares RESP-CODES so a client is entitled to " +
                   "read it.")]
      public void LockedMailboxIsRefusedWithTheInUseResponseCode()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "inuse@example.test", "test");

         var first = new Pop3ClientSimulator();
         Assert.IsTrue(first.ConnectAndLogon(account.Address, "test"),
            "The first session must be given the mailbox lock.");

         try
         {
            using (var second = ConnectPop3())
            {
               StringAssert.Contains("RESP-CODES", ReadCapa(second),
                  "CAPA must declare RESP-CODES, or a client is not entitled to read the bracketed code.");

               Assert.IsTrue(second.SendAndReceive("USER " + account.Address + "\r\n").StartsWith("+OK"));
               var response = second.SendAndReceive("PASS test\r\n");

               // The defect: "-ERR Your mailbox is already locked" with no response code.
               // The credentials were accepted and only the maildrop was unavailable, but
               // a client reading only the leading "-ERR" can report nothing except a bad
               // password - which is what mail clients do report today.
               StringAssert.Contains("[IN-USE]", response,
                  "A locked maildrop must be refused with the [IN-USE] response code. Got: " + response);
            }
         }
         finally
         {
            // The lock has to go back. A clean QUIT releases it at once; the Disconnect is
            // the backstop for the case where QUIT itself throws.
            try
            {
               first.QUIT();
            }
            finally
            {
               first.Disconnect();
            }
         }
      }

      [Test]
      [Description("Pins a deliberate omission rather than fixing a defect: APOP, CRAM-MD5, DIGEST-MD5, " +
                   "EXTERNAL, GSSAPI and NTLM are never offered, and an AUTH naming one is refused " +
                   "without leaving the session a line out of step.")]
      public void LegacyChallengeResponseMechanismsAreNeverOffered()
      {
         // This test passes against the unfixed server too, on purpose. APOP, CRAM-MD5 and
         // DIGEST-MD5 all need a reversible or plaintext-equivalent stored secret, which
         // this server deliberately does not keep, so the value here is that adding one of
         // them later has to be a conscious decision that breaks a named test.
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "nolegacy@example.test", "SeC-r3t Pass!");

         using (var connection = ConnectPop3())
         {
            var capabilities = ReadCapa(connection);

            connection.Send("AUTH\r\n");
            var mechanisms = connection.ReadUntil("\r\n.\r\n");

            foreach (var mechanism in new[] { "APOP", "CRAM-MD5", "DIGEST-MD5", "EXTERNAL", "GSSAPI", "NTLM" })
            {
               Assert.IsFalse(capabilities.Contains(mechanism),
                  "CAPA must not advertise " + mechanism + ". Got: " + capabilities);
               Assert.IsFalse(mechanisms.Contains(mechanism),
                  "The AUTH mechanism listing must not include " + mechanism + ". Got: " + mechanisms);
            }

            var refusal = connection.SendAndReceive("AUTH CRAM-MD5\r\n");
            Assert.IsTrue(refusal.StartsWith("-ERR"),
               "An unsupported mechanism must be refused. Got: " + refusal);

            // Refused cleanly means no continuation was armed: the next line is still read
            // as a command rather than swallowed as SASL data.
            var afterRefusal = ReadCapa(connection);
            Assert.IsTrue(afterRefusal.StartsWith("+OK"),
               "The command after a refused mechanism must be parsed as a command. Got: " + afterRefusal);

            // ...and a mechanism the server does support still works on the same session.
            var final = Pop3SaslTestClient.AuthenticatePlain(connection, "nolegacy@example.test", "SeC-r3t Pass!");
            Assert.IsTrue(final.StartsWith("+OK"),
               "AUTH PLAIN must still authenticate after a refused mechanism. Got: " + final);

            connection.Send("QUIT\r\n");
            connection.ReadUntil("+OK");
         }
      }
   }

   /// <summary>
   /// The two STLS items, split into their own fixture because they need the STARTTLS
   /// ports and therefore an SslSetup call, which restarts the server.
   ///
   /// 1. A user name supplied before the handshake survived it, so a PASS sent after STLS
   ///    could complete a logon for an identity the genuine client never named. This is
   ///    the STARTTLS plaintext-injection class: TCPConnection already discards the
   ///    receive buffer when the handshake completes, so injected lines are not executed
   ///    afterwards, but a USER that had already been parsed left its name behind.
   /// 2. CAPA advertised STLS after TLS was active, where ProtocolSTLS_ can only answer
   ///    "-ERR Command not permitted when TLS active" - and the CAPA a client re-issues
   ///    after the handshake (RFC 2595) is exactly the one that was wrong.
   /// </summary>
   [TestFixture]
   public class StlsConformance : TestFixtureBase
   {
      // The shared "STARTTLS optional" POP3 port created by SslSetup, the same one the
      // existing STARTTLS fixtures use. No port from this developer's range is needed.
      private const int StartTlsOptionalPort = 11002;

      // Set up per test rather than once per fixture, following SSL/ScramPlusPop3.cs:
      // PerformBasicSetup runs before every test and resets TCPIPPorts and SSLCertificates,
      // so a fixture-level setup would be relying on the already-bound listener surviving
      // that. Costs one server restart per test.

      [Test]
      [Description("A user name given before STLS is discarded with the rest of the pre-handshake state, " +
                   "so PASS after the handshake is refused and the client has to identify itself over TLS.")]
      public void UserNameFromBeforeStlsIsDiscarded()
      {
         SslSetup.SetupSSLPorts(_application);

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "stlsdiscard@example.test", "test");

         using (var connection = new TcpConnection())
         {
            Assert.IsTrue(connection.Connect(StartTlsOptionalPort),
               "Could not connect to the STARTTLS POP3 port.");
            connection.ReadUntil("+OK"); // banner

            Assert.IsTrue(connection.SendAndReceive("USER " + account.Address + "\r\n").StartsWith("+OK"),
               "USER must be accepted on the cleartext connection.");

            Assert.IsTrue(connection.SendAndReceive("STLS\r\n").StartsWith("+OK"),
               "STLS must be accepted.");
            connection.HandshakeAsClient();

            var response = connection.SendAndReceive("PASS test\r\n");

            // The defect: this answered "+OK Mailbox locked and ready". The user name was
            // taken from a command parsed before the channel was protected, which is
            // exactly the state a man in the middle can write.
            Assert.IsTrue(response.StartsWith("-ERR"),
               "PASS must not complete a logon for a user name supplied before the handshake. Got: " + response);
            StringAssert.Contains("USER", response,
               "The refusal should tell the client to send USER again. Got: " + response);

            // Negative control: identifying again over TLS works, so this discards state
            // rather than breaking STLS logons.
            Assert.IsTrue(connection.SendAndReceive("USER " + account.Address + "\r\n").StartsWith("+OK"));
            Assert.IsTrue(connection.SendAndReceive("PASS test\r\n").StartsWith("+OK"),
               "A USER/PASS pair sent over TLS must still log on.");

            connection.Send("QUIT\r\n");
            connection.ReadUntil("+OK");
         }
      }

      [Test]
      [Description("RFC 2449/2595: STLS is advertised before the handshake and not after it, because it " +
                   "is refused once TLS is active.")]
      public void StlsIsNotAdvertisedOnceTlsIsActive()
      {
         SslSetup.SetupSSLPorts(_application);

         using (var connection = new TcpConnection())
         {
            Assert.IsTrue(connection.Connect(StartTlsOptionalPort),
               "Could not connect to the STARTTLS POP3 port.");
            connection.ReadUntil("+OK"); // banner

            connection.Send("CAPA\r\n");
            var before = connection.ReadUntil("\r\n.\r\n");
            StringAssert.Contains("STLS", before,
               "STLS must be advertised on the cleartext connection. Got: " + before);

            Assert.IsTrue(connection.SendAndReceive("STLS\r\n").StartsWith("+OK"));
            connection.HandshakeAsClient();

            connection.Send("CAPA\r\n");
            var after = connection.ReadUntil("\r\n.\r\n");

            // The defect: STLS was still listed, so the CAPA a client is told to re-issue
            // after the handshake offered a command that could only fail.
            Assert.IsFalse(after.Contains("STLS"),
               "STLS must not be advertised once TLS is active. Got: " + after);

            // Negative control: the post-handshake CAPA is still a real capability list,
            // and one that now offers the channel-bound mechanism.
            StringAssert.Contains("USER", after, "Got: " + after);
            StringAssert.Contains("SCRAM-SHA-256-PLUS", after, "Got: " + after);

            connection.Send("QUIT\r\n");
            connection.ReadUntil("+OK");
         }
      }
   }
}
