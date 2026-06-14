// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using RegressionTests.POP3;
using RegressionTests.Shared;

namespace RegressionTests.SSL
{
   /// <summary>
   ///    SCRAM-SHA-256-PLUS (RFC 5802 / RFC 7677) with tls-server-end-point channel
   ///    binding (RFC 5929) over a real TLS POP3 connection (RFC 5034). These tests
   ///    require TLS, so they live with the SSL suite and configure the SSL ports in
   ///    each test.
   /// </summary>
   [TestFixture]
   public class ScramPlusPop3 : TestFixtureBase
   {
      // Ports created by SslSetup.SetupSSLPorts.
      private const int Pop3PlainPort = 11000; // eCSNone
      private const int Pop3TlsPort = 11001;   // eCSTLS (implicit TLS)

      [Test]
      [Description("RFC 5802/5929 (SCRAM-SHA-256-PLUS over POP3): CAPA advertises SASL " +
                   "SCRAM-SHA-256-PLUS over TLS (where channel binding is possible) but never on a " +
                   "plain connection.")]
      public void TestScramPlusAdvertisedOnTlsOnly()
      {
         SslSetup.SetupSSLPorts(_application);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "pluspop3cap@example.test", "test");

         using (var con = new TcpConnection(true))
         {
            Assert.IsTrue(con.Connect(Pop3TlsPort), "Could not connect to TLS POP3.");
            con.ReadUntil("+OK"); // banner
            con.Send("CAPA\r\n");
            string caps = con.Receive();
            Assert.IsTrue(caps.Contains("SCRAM-SHA-256-PLUS"),
               "TLS CAPA must advertise SASL SCRAM-SHA-256-PLUS. " + caps);
         }

         using (var con = new TcpConnection())
         {
            Assert.IsTrue(con.Connect(Pop3PlainPort), "Could not connect to plaintext POP3.");
            con.ReadUntil("+OK"); // banner
            con.Send("CAPA\r\n");
            string caps = con.Receive();
            Assert.IsFalse(caps.Contains("SCRAM-SHA-256-PLUS"),
               "A plain connection must not advertise SCRAM-SHA-256-PLUS. " + caps);
            Assert.IsTrue(caps.Contains("SCRAM-SHA-256"),
               "A plain connection should still advertise the non-PLUS SCRAM-SHA-256. " + caps);
         }
      }

      [Test]
      [Description("RFC 5802/5929 (SCRAM-SHA-256-PLUS over POP3): a full channel-bound exchange " +
                   "authenticates a PBKDF2 account over TLS and the server proves it knows the key.")]
      public void TestScramPlusAuthenticates()
      {
         SslSetup.SetupSSLPorts(_application);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "pluspop3ok@example.test", "SeC-r3t Pass!");

         using (var con = new TcpConnection(true))
         {
            Assert.IsTrue(con.Connect(Pop3TlsPort), "Could not connect to TLS POP3.");
            con.ReadUntil("+OK"); // banner

            byte[] cbind = TlsServerEndPoint(con.RemoteCertificate);
            string final = Pop3SaslTestClient.AuthenticateScramPlus(con, "pluspop3ok@example.test", "SeC-r3t Pass!", cbind);
            Assert.IsTrue(final.StartsWith("+OK"),
               "SCRAM-SHA-256-PLUS authentication should succeed (+OK). Got: " + final);

            // The session must be authenticated and usable afterwards.
            con.Send("STAT\r\n");
            Assert.IsTrue(con.Receive().StartsWith("+OK"),
               "Session should be usable after a PLUS logon.");
            con.Send("QUIT\r\n");
            con.Disconnect();
         }
      }

      [Test]
      [Description("RFC 5929 (SCRAM-SHA-256-PLUS over POP3): a channel binding that does not match the " +
                   "server certificate is rejected even with the correct password, which is exactly the " +
                   "man-in-the-middle case channel binding defends against.")]
      public void TestScramPlusWrongBindingFails()
      {
         SslSetup.SetupSSLPorts(_application);
         bool autoBan = _settings.AutoBanOnLogonFailure;
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "pluspop3mitm@example.test", "correct horse");

            using (var con = new TcpConnection(true))
            {
               Assert.IsTrue(con.Connect(Pop3TlsPort), "Could not connect to TLS POP3.");
               con.ReadUntil("+OK"); // banner

               byte[] cbind = TlsServerEndPoint(con.RemoteCertificate);
               // Simulate an attacker on a different TLS channel: the binding no longer
               // matches the certificate the server presents.
               cbind[0] ^= 0xFF;

               string final = Pop3SaslTestClient.AuthenticateScramPlus(con, "pluspop3mitm@example.test", "correct horse", cbind);
               Assert.IsTrue(final.StartsWith("-ERR"),
                  "A mismatched channel binding must be rejected even with the right password. Got: " + final);
            }
         }
         finally
         {
            _settings.AutoBanOnLogonFailure = autoBan;
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("SCRAM-SHA-256-PLUS over POP3 requires a TLS channel: the mechanism is refused on a " +
                   "plain connection where no channel binding is available.")]
      public void TestScramPlusRejectedWithoutTls()
      {
         SslSetup.SetupSSLPorts(_application);
         bool autoBan = _settings.AutoBanOnLogonFailure;
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            using (var con = new TcpConnection())
            {
               Assert.IsTrue(con.Connect(Pop3PlainPort), "Could not connect to plaintext POP3.");
               con.ReadUntil("+OK"); // banner

               con.Send("AUTH SCRAM-SHA-256-PLUS\r\n");
               string resp = con.Receive();
               Assert.IsTrue(resp.StartsWith("-ERR"),
                  "SCRAM-SHA-256-PLUS must be refused without TLS. Got: " + resp);
            }
         }
         finally
         {
            _settings.AutoBanOnLogonFailure = autoBan;
            _settings.ClearLogonFailureList();
         }
      }

      /// <summary>
      ///    Computes the RFC 5929 'tls-server-end-point' channel binding for a server
      ///    certificate: the hash of the DER certificate using the certificate's
      ///    signature hash, with MD5/SHA-1 substituted by SHA-256.
      /// </summary>
      private static byte[] TlsServerEndPoint(X509Certificate cert)
      {
         Assert.IsNotNull(cert, "No server certificate was negotiated.");

         var cert2 = new X509Certificate2(cert);
         HashAlgorithm hash;
         switch (cert2.SignatureAlgorithm.Value)
         {
            case "1.2.840.113549.1.1.12": // sha384RSA
            case "1.2.840.10045.4.3.3":   // ecdsa-with-SHA384
               hash = SHA384.Create();
               break;
            case "1.2.840.113549.1.1.13": // sha512RSA
            case "1.2.840.10045.4.3.4":   // ecdsa-with-SHA512
               hash = SHA512.Create();
               break;
            default: // sha256RSA / ecdsa-with-SHA256 and the MD5/SHA-1 -> SHA-256 substitution
               hash = SHA256.Create();
               break;
         }

         using (hash)
            return hash.ComputeHash(cert.GetRawCertData());
      }
   }
}
