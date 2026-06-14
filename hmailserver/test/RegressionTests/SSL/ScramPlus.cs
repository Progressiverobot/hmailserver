// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using RegressionTests.IMAP;
using RegressionTests.Shared;

namespace RegressionTests.SSL
{
   /// <summary>
   ///    SCRAM-SHA-256-PLUS (RFC 5802 / RFC 7677) with tls-server-end-point channel
   ///    binding (RFC 5929) over a real TLS IMAP connection. These tests require TLS,
   ///    so they live with the SSL suite and configure the SSL ports in each test.
   /// </summary>
   [TestFixture]
   public class ScramPlus : TestFixtureBase
   {
      // Ports created by SslSetup.SetupSSLPorts.
      private const int ImapPlainPort = 14300; // eCSNone
      private const int ImapTlsPort = 14301;   // eCSTLS (implicit TLS)

      [Test]
      [Description("RFC 5802/5929 (SCRAM-SHA-256-PLUS): AUTH=SCRAM-SHA-256-PLUS is advertised in " +
                   "CAPABILITY over TLS (where channel binding is possible) but never on a plain " +
                   "connection.")]
      public void TestScramPlusAdvertisedOnTlsOnly()
      {
         _settings.IMAPSASLPlainEnabled = true;
         SslSetup.SetupSSLPorts(_application);
         try
         {
            using (var con = new TcpConnection(true))
            {
               Assert.IsTrue(con.Connect(ImapTlsPort), "Could not connect to TLS IMAP.");
               con.Receive(); // banner
               con.Send("A01 CAPABILITY\r\n");
               string caps = con.ReadUntil("A01 OK");
               Assert.IsTrue(caps.Contains("AUTH=SCRAM-SHA-256-PLUS"),
                  "TLS CAPABILITY must advertise AUTH=SCRAM-SHA-256-PLUS. " + caps);
            }

            using (var con = new TcpConnection())
            {
               Assert.IsTrue(con.Connect(ImapPlainPort), "Could not connect to plaintext IMAP.");
               con.Receive(); // banner
               con.Send("A01 CAPABILITY\r\n");
               string caps = con.ReadUntil("A01 OK");
               Assert.IsFalse(caps.Contains("SCRAM-SHA-256-PLUS"),
                  "A plain connection must not advertise SCRAM-SHA-256-PLUS. " + caps);
               Assert.IsTrue(caps.Contains("AUTH=SCRAM-SHA-256"),
                  "A plain connection should still advertise the non-PLUS SCRAM-SHA-256. " + caps);
            }
         }
         finally
         {
            _settings.IMAPSASLPlainEnabled = false;
         }
      }

      [Test]
      [Description("RFC 5802/5929 (SCRAM-SHA-256-PLUS): a full channel-bound exchange authenticates a " +
                   "PBKDF2 account over TLS and the server proves it knows the key (ServerSignature).")]
      public void TestScramPlusAuthenticates()
      {
         _settings.IMAPSASLPlainEnabled = true;
         SslSetup.SetupSSLPorts(_application);
         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "plusok@example.test", "SeC-r3t Pass!");

            using (var con = new TcpConnection(true))
            {
               Assert.IsTrue(con.Connect(ImapTlsPort), "Could not connect to TLS IMAP.");
               con.Receive(); // banner

               byte[] cbind = TlsServerEndPoint(con.RemoteCertificate);
               string final = ScramTestClient.AuthenticatePlus(con, "A01", "plusok@example.test", "SeC-r3t Pass!", cbind);
               Assert.IsTrue(final.Contains("A01 OK"),
                  "SCRAM-SHA-256-PLUS authentication should succeed. Got: " + final);

               string noop = con.SendAndReceive("A02 NOOP\r\n");
               Assert.IsTrue(noop.Contains("A02 OK"),
                  "Connection should be usable after a PLUS logon. Got: " + noop);
            }
         }
         finally
         {
            _settings.IMAPSASLPlainEnabled = false;
         }
      }

      [Test]
      [Description("RFC 5929 (SCRAM-SHA-256-PLUS): a channel binding that does not match the server " +
                   "certificate is rejected even with the correct password, which is exactly the " +
                   "man-in-the-middle case channel binding defends against.")]
      public void TestScramPlusWrongBindingFails()
      {
         _settings.IMAPSASLPlainEnabled = true;
         SslSetup.SetupSSLPorts(_application);
         bool autoBan = _settings.AutoBanOnLogonFailure;
         _settings.AutoBanOnLogonFailure = false;
         try
         {
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "plusmitm@example.test", "correct horse");

            using (var con = new TcpConnection(true))
            {
               Assert.IsTrue(con.Connect(ImapTlsPort), "Could not connect to TLS IMAP.");
               con.Receive(); // banner

               byte[] cbind = TlsServerEndPoint(con.RemoteCertificate);
               // Simulate an attacker on a different TLS channel: the binding no longer
               // matches the certificate the server presents.
               cbind[0] ^= 0xFF;

               string final = ScramTestClient.AuthenticatePlus(con, "A01", "plusmitm@example.test", "correct horse", cbind);
               Assert.IsTrue(final.Contains("A01 NO") || final.Contains("A01 BAD"),
                  "A mismatched channel binding must be rejected even with the right password. Got: " + final);
            }
         }
         finally
         {
            _settings.AutoBanOnLogonFailure = autoBan;
            _settings.IMAPSASLPlainEnabled = false;
         }
      }

      [Test]
      [Description("SCRAM-SHA-256-PLUS requires a TLS channel: the mechanism is refused on a plain " +
                   "connection where no channel binding is available.")]
      public void TestScramPlusRejectedWithoutTls()
      {
         _settings.IMAPSASLPlainEnabled = true;
         SslSetup.SetupSSLPorts(_application);
         bool autoBan = _settings.AutoBanOnLogonFailure;
         _settings.AutoBanOnLogonFailure = false;
         try
         {
            using (var con = new TcpConnection())
            {
               Assert.IsTrue(con.Connect(ImapPlainPort), "Could not connect to plaintext IMAP.");
               con.Receive(); // banner

               string resp = con.SendAndReceive("A01 AUTHENTICATE SCRAM-SHA-256-PLUS\r\n");
               Assert.IsTrue(resp.Contains("A01 BAD") || resp.Contains("A01 NO"),
                  "SCRAM-SHA-256-PLUS must be refused without TLS. Got: " + resp);
            }
         }
         finally
         {
            _settings.AutoBanOnLogonFailure = autoBan;
            _settings.IMAPSASLPlainEnabled = false;
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
