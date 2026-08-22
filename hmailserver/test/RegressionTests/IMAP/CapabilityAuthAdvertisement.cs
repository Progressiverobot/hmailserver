// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   /// <summary>
   /// CAPABILITY must not offer an authentication mechanism this connection will be
   /// refused for using.
   ///
   /// Authentication is refused on a cleartext connection in two cases, not one: the
   /// port being STARTTLS-required, and the connecting IP range setting
   /// RequireTLSForAuth. Only the first was reflected in the CAPABILITY response, so on
   /// a plain port covered by a range that requires TLS the server advertised
   /// AUTH=PLAIN and then refused it — and a client that takes up that offer sends
   /// base64(authzid NUL authcid NUL password) before it learns anything is wrong. The
   /// password has already crossed the wire in the clear at that point, which is the
   /// whole reason the setting exists.
   ///
   /// The same defect was found and fixed in POP3 CAPA and in the ManageSieve
   /// capability response in the same week; IMAP was never looked at. Its POP3 twin is
   /// POP3/RfcConformance.CapaOmitsAuthenticationCapabilitiesWhenTheIpRangeRequiresTls,
   /// and this fixture is deliberately its mirror image so that the three protocols can
   /// be compared rather than each read on its own.
   ///
   /// LOGINDISABLED is the other half. RFC 3501 section 7.2.1 and RFC 2595 section 3.1
   /// require a server that will not accept LOGIN to say so, and this one never
   /// advertised it at all — so a client had no way to know before sending
   /// "LOGIN &lt;user&gt; &lt;password&gt;" in the clear, which is the same exposure
   /// through the other command.
   /// </summary>
   [TestFixture]
   public class CapabilityAuthAdvertisement : TestFixtureBase
   {
      [Test]
      [Description("RFC 3501/2595: when the connecting IP range requires TLS for authentication, a " +
                   "cleartext CAPABILITY must advertise LOGINDISABLED and no AUTH= mechanism, because " +
                   "the server refuses all of them on that connection.")]
      public void CapabilityOmitsAuthMechanismsAndSaysLoginDisabledWhenTheIpRangeRequiresTls()
      {
         // IMAP SASL is off in the shipped configuration, and therefore in this test
         // environment. That matters more than it looks: with it off no AUTH= is ever
         // advertised, so asserting that AUTH= is absent would pass against the unfixed
         // server exactly as well as the fixed one. Turning it on is what makes the
         // assertion below mean anything - and the negative control at the bottom of
         // this file is what revealed that, on its first run.
         var originalSasl = _settings.IMAPSASLPlainEnabled;
         _settings.IMAPSASLPlainEnabled = true;

         var range = _settings.SecurityRanges.get_ItemByName("My computer");
         range.RequireSSLTLSForAuth = true;
         range.Save();

         try
         {
            // A raw connection rather than ImapClientSimulator, for one reason:
            // ImapClientSimulator.Send takes a single Receive(), and an assertion that
            // something is ABSENT is exactly the assertion a short read satisfies for
            // the wrong reason. Reading to the tagged completion removes that.
            var socket = new TcpConnection();
            ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
            socket.ReadUntil("* OK");

            socket.Send("A01 CAPABILITY\r\n");
            var capabilities = socket.ReadUntil("A01 OK");

            // The defect: AUTH=PLAIN and AUTH=SCRAM-SHA-256 were still listed.
            ClassicAssert.IsFalse(capabilities.Contains("AUTH="),
               "CAPABILITY must not advertise any AUTH= mechanism when the IP range requires TLS for authentication. Got: " + capabilities);

            // RFC 3501 7.2.1: say so, rather than leaving the client to find out by
            // sending the password.
            StringAssert.Contains("LOGINDISABLED", capabilities,
               "CAPABILITY must advertise LOGINDISABLED when LOGIN will be refused. Got: " + capabilities);

            // Negative control. This is a suppression of the authentication capabilities,
            // not an empty or broken capability list - without this the assertions above
            // would pass just as well against a server that answered nothing useful.
            StringAssert.Contains("IMAP4rev1", capabilities,
               "The rest of the capability list must still be present. Got: " + capabilities);

            // And the suppression says the same thing the commands do. This is the pair
            // that matters: an advertisement is only wrong because the command refuses.
            socket.Send("A02 LOGIN someone@example.test password\r\n");
            var login = socket.ReadUntil("A02 ");
            StringAssert.Contains("SSL/TLS-connection is required", login,
               "LOGIN must still be refused on the cleartext connection. Got: " + login);

            socket.Send("A03 AUTHENTICATE PLAIN\r\n");
            var authenticate = socket.ReadUntil("A03 ");
            StringAssert.Contains("SSL/TLS-connection is required", authenticate,
               "AUTHENTICATE must still be refused on the cleartext connection. Got: " + authenticate);

            socket.Disconnect();
         }
         finally
         {
            // PerformBasicSetup calls SecurityRanges.SetDefault() before every test, so
            // this would be undone anyway - but not before the rest of THIS test, and a
            // range that refuses all cleartext authentication breaks every fixture that
            // logs on.
            range.RequireSSLTLSForAuth = false;
            range.Save();

            _settings.IMAPSASLPlainEnabled = originalSasl;
         }
      }

      [Test]
      [Description("RFC 4954 section 4: when the connecting IP range requires TLS for authentication, a " +
                   "cleartext EHLO must not advertise AUTH, because the server answers 530 to it. The " +
                   "same defect as the IMAP one above, in the protocol most exposed to the internet.")]
      public void EhloOmitsAuthWhenTheIpRangeRequiresTls()
      {
         var range = _settings.SecurityRanges.get_ItemByName("My computer");
         range.RequireSSLTLSForAuth = true;
         range.Save();

         try
         {
            var socket = new TcpConnection();
            ClassicAssert.IsTrue(socket.Connect(25), "Could not connect to the SMTP server on port 25.");
            StringAssert.StartsWith("220", socket.Receive());

            socket.Send("EHLO test.example.test\r\n");
            // Read to the end of the multi-line response rather than taking one
            // Receive(). The response always ends "250 HELP", and a short read would
            // make the absence assertion below pass for entirely the wrong reason -
            // the same trap the POP3 fixture documents for CAPA.
            var capabilities = socket.ReadUntil("250 HELP");

            // The defect: "250-AUTH LOGIN PLAIN SCRAM-SHA-256" was still offered, and a
            // client taking it up put the credential on the wire before the 530 arrived.
            ClassicAssert.IsFalse(capabilities.Contains("AUTH"),
               "EHLO must not advertise AUTH when the IP range requires TLS for authentication. Got: " + capabilities);

            // Negative control: the rest of the EHLO response is intact.
            StringAssert.Contains("SIZE", capabilities,
               "The rest of the EHLO response must still be present. Got: " + capabilities);

            // And the suppression agrees with what the command does.
            socket.Send("AUTH LOGIN\r\n");
            var auth = socket.Receive();
            StringAssert.StartsWith("530", auth,
               "AUTH must still be refused on the cleartext connection. Got: " + auth);

            socket.Disconnect();
         }
         finally
         {
            range.RequireSSLTLSForAuth = false;
            range.Save();
         }
      }

      [Test]
      [Description("The negative control for the above: with the default IP range, CAPABILITY advertises " +
                   "the AUTH= mechanisms and does not claim LOGINDISABLED.")]
      public void CapabilityStillAdvertisesAuthMechanismsOnAnOrdinaryConnection()
      {
         var originalSasl = _settings.IMAPSASLPlainEnabled;
         _settings.IMAPSASLPlainEnabled = true;

         try
         {
            var socket = new TcpConnection();
            ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
            socket.ReadUntil("* OK");

            socket.Send("A01 CAPABILITY\r\n");
            var capabilities = socket.ReadUntil("A01 OK");

            // Without this the fixture above could be satisfied by a server that never
            // advertises AUTH= at all, which would be a different defect wearing the same
            // passing test. That is not hypothetical - it is what this control caught on
            // its first run, because IMAP SASL is off in the shipped configuration.
            StringAssert.Contains("AUTH=PLAIN", capabilities,
               "CAPABILITY must advertise AUTH=PLAIN on a connection where authentication is permitted. Got: " + capabilities);

            ClassicAssert.IsFalse(capabilities.Contains("LOGINDISABLED"),
               "CAPABILITY must not claim LOGINDISABLED on a connection where LOGIN is permitted. Got: " + capabilities);

            socket.Disconnect();
         }
         finally
         {
            _settings.IMAPSASLPlainEnabled = originalSasl;
         }
      }

      [Test]
      [Description("The negative control for the SMTP half: with the default IP range, EHLO advertises AUTH.")]
      public void EhloStillAdvertisesAuthOnAnOrdinaryConnection()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(25), "Could not connect to the SMTP server on port 25.");
         StringAssert.StartsWith("220", socket.Receive());

         socket.Send("EHLO test.example.test\r\n");
         var capabilities = socket.ReadUntil("250 HELP");

         StringAssert.Contains("AUTH", capabilities,
            "EHLO must advertise AUTH on a connection where authentication is permitted. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
