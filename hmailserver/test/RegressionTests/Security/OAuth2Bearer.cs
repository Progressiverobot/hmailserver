// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    Exercises OAuth2 bearer-token authentication (SASL XOAUTH2 / OAUTHBEARER,
   ///    RFC 7628) over POP3, IMAP and SMTP. The server validates the JWT locally:
   ///    it splits the token, enforces an algorithm allow-list (rejecting the
   ///    "none" algorithm), verifies the HS256 signature against the configured
   ///    OAuth2HmacSecret, checks the expiry, and maps the configured username
   ///    claim (here "email") to a local account.
   ///
   ///    These tests mint HS256 tokens entirely offline with a shared secret, so no
   ///    live identity provider is required. RS256 (asymmetric, public-key) tokens
   ///    are also supported by the server but are not exercised here.
   /// </summary>
   [TestFixture]
   public partial class OAuth2Bearer : TestFixtureBase
   {
      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private const string HmacSecret = "oauth2-shared-test-secret-0123456789";

      private void WriteSetting(string key, string value)
      {
         // The server reads hMailServer.ini from its bin directory; write to every
         // existing candidate so the file the service actually reads is updated
         // regardless of layout, without creating stray ini files.
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

      private void EnableOAuth2(string allowedAlgorithms)
      {
         WriteSetting("OAuth2Enabled", "1");
         // The regression ports are plain (non-TLS), so allow bearer auth without TLS.
         WriteSetting("OAuth2RequireTLS", "0");
         WriteSetting("OAuth2AllowedAlgorithms", allowedAlgorithms);
         WriteSetting("OAuth2HmacSecret", HmacSecret);
         WriteSetting("OAuth2UsernameClaim", "email");
         WriteSetting("OAuth2Issuer", "");
         WriteSetting("OAuth2Audience", "");
         _application.Reinitialize();
      }

      private void DisableOAuth2()
      {
         WriteSetting("OAuth2Enabled", "0");
         WriteSetting("OAuth2RequireTLS", "1");
         WriteSetting("OAuth2AllowedAlgorithms", "RS256");
         WriteSetting("OAuth2HmacSecret", "");
         WriteSetting("OAuth2PublicKeyFile", "");
         WriteSetting("OAuth2UsernameClaim", "email");
         _application.Reinitialize();
      }

      private static string Base64Url(byte[] data)
      {
         return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
      }

      /// <summary>Mints a signed HS256 JWT for <paramref name="email"/> valid until <paramref name="exp"/>.</summary>
      private static string MintHs256(string email, long exp, string secret = HmacSecret, string algHeader = "HS256")
      {
         string header = "{\"alg\":\"" + algHeader + "\",\"typ\":\"JWT\"}";
         string payload = "{\"email\":\"" + email + "\",\"exp\":" + exp + "}";

         string headerSegment = Base64Url(Encoding.UTF8.GetBytes(header));
         string payloadSegment = Base64Url(Encoding.UTF8.GetBytes(payload));
         string signingInput = headerSegment + "." + payloadSegment;

         using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
         {
            byte[] signature = hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput));
            return signingInput + "." + Base64Url(signature);
         }
      }

      /// <summary>Mints an unsigned "alg":"none" JWT (empty signature segment).</summary>
      private static string MintNone(string email, long exp)
      {
         string header = "{\"alg\":\"none\",\"typ\":\"JWT\"}";
         string payload = "{\"email\":\"" + email + "\",\"exp\":" + exp + "}";
         return Base64Url(Encoding.UTF8.GetBytes(header)) + "." +
                Base64Url(Encoding.UTF8.GetBytes(payload)) + ".";
      }

      private static long UnixNow()
      {
         return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
      }

      /// <summary>Wraps a bearer token in the SASL XOAUTH2 client response and base64-encodes it.</summary>
      private static string XOAuth2Response(string email, string token)
      {
         // Use \u0001 (fixed-length unicode escape) for the SASL ^A separators: C#'s \x
         // escape is greedy and would swallow following hex digits (e.g. "\x01auth").
         string sasl = "user=" + email + "\u0001auth=Bearer " + token + "\u0001\u0001";
         return Convert.ToBase64String(Encoding.ASCII.GetBytes(sasl));
      }

      // -- POP3 -------------------------------------------------------------------

      private static string Pop3AuthXOAuth2(string email, string token)
      {
         using (var con = new TcpConnection())
         {
            Assert.IsTrue(con.Connect(110), "Could not connect to POP3.");
            con.ReadUntil("+OK"); // banner
            con.Send("AUTH XOAUTH2 " + XOAuth2Response(email, token) + "\r\n");
            return con.Receive();
         }
      }

      [Test]
      [Description("RFC 7628 SASL XOAUTH2 over POP3: a locally validated HS256 bearer token whose " +
                   "email claim matches a local account authenticates successfully.")]
      public void TestPop3XOAuth2Succeeds()
      {
         const string address = "oauthpop@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintHs256(address, UnixNow() + 3600);

            string response = Pop3AuthXOAuth2(address, token);
            Assert.IsTrue(response.StartsWith("+OK"),
               "A valid bearer token should authenticate over POP3. Got: " + response);
         }
         finally
         {
            DisableOAuth2();
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("An expired bearer token is rejected over POP3.")]
      public void TestPop3XOAuth2ExpiredTokenFails()
      {
         const string address = "oauthpopexp@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintHs256(address, UnixNow() - 3600); // already expired

            string response = Pop3AuthXOAuth2(address, token);
            Assert.IsTrue(response.StartsWith("-ERR"),
               "An expired bearer token must be rejected over POP3. Got: " + response);
         }
         finally
         {
            DisableOAuth2();
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("A token signed with the wrong secret is rejected over POP3.")]
      public void TestPop3XOAuth2WrongSecretFails()
      {
         const string address = "oauthpopsig@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintHs256(address, UnixNow() + 3600, "the-wrong-secret");

            string response = Pop3AuthXOAuth2(address, token);
            Assert.IsTrue(response.StartsWith("-ERR"),
               "A token signed with the wrong secret must be rejected. Got: " + response);
         }
         finally
         {
            DisableOAuth2();
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("Security: an 'alg':'none' token (no signature) must never be accepted, even when " +
                   "its claims are otherwise valid.")]
      public void TestPop3XOAuth2AlgNoneRejected()
      {
         const string address = "oauthpopnone@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintNone(address, UnixNow() + 3600);

            string response = Pop3AuthXOAuth2(address, token);
            Assert.IsTrue(response.StartsWith("-ERR"),
               "An 'alg':'none' token must be rejected. Got: " + response);
         }
         finally
         {
            DisableOAuth2();
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("When OAuth2 is disabled the XOAUTH2 mechanism is not offered and is rejected over POP3.")]
      public void TestPop3XOAuth2DisabledRejected()
      {
         const string address = "oauthpopoff@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            DisableOAuth2();
            string token = MintHs256(address, UnixNow() + 3600);

            string response = Pop3AuthXOAuth2(address, token);
            Assert.IsTrue(response.StartsWith("-ERR"),
               "XOAUTH2 must be rejected when OAuth2 is disabled. Got: " + response);
         }
         finally
         {
            _settings.ClearLogonFailureList();
         }
      }

      // -- IMAP -------------------------------------------------------------------

      private static string ImapAuthXOAuth2(string email, string token)
      {
         using (var con = new TcpConnection())
         {
            Assert.IsTrue(con.Connect(143), "Could not connect to IMAP.");
            con.Receive(); // banner

            // Use the continuation form (no SASL-IR): the server answers with "+ ".
            con.Send("A01 AUTHENTICATE XOAUTH2\r\n");
            string challenge = con.Receive();
            if (!challenge.TrimStart().StartsWith("+"))
               return challenge; // mechanism rejected outright

            con.Send(XOAuth2Response(email, token) + "\r\n");
            return con.Receive();
         }
      }

      [Test]
      [Description("RFC 7628 SASL XOAUTH2 over IMAP: a valid HS256 bearer token authenticates and the " +
                   "session is usable afterwards.")]
      public void TestImapXOAuth2Succeeds()
      {
         const string address = "oauthimap@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _application.Settings.IMAPSASLPlainEnabled = true;
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintHs256(address, UnixNow() + 3600);

            using (var con = new TcpConnection())
            {
               Assert.IsTrue(con.Connect(143), "Could not connect to IMAP.");
               con.Receive(); // banner

               con.Send("A01 AUTHENTICATE XOAUTH2\r\n");
               string challenge = con.Receive();
               Assert.IsTrue(challenge.TrimStart().StartsWith("+"),
                  "Expected a continuation for AUTHENTICATE XOAUTH2. Got: " + challenge);

               con.Send(XOAuth2Response(address, token) + "\r\n");
               string final = con.Receive();
               Assert.IsTrue(final.Contains("A01 OK"),
                  "A valid bearer token should authenticate over IMAP. Got: " + final);

               string noop = con.SendAndReceive("A02 NOOP\r\n");
               Assert.IsTrue(noop.Contains("A02 OK"),
                  "Connection should be usable after a bearer logon. Got: " + noop);
            }
         }
         finally
         {
            DisableOAuth2();
            _application.Settings.IMAPSASLPlainEnabled = false;
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("An expired bearer token is rejected over IMAP.")]
      public void TestImapXOAuth2ExpiredTokenFails()
      {
         const string address = "oauthimapexp@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _application.Settings.IMAPSASLPlainEnabled = true;
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintHs256(address, UnixNow() - 3600);

            string final = ImapAuthXOAuth2(address, token);
            Assert.IsTrue(final.Contains("A01 NO") || final.Contains("A01 BAD"),
               "An expired bearer token must be rejected over IMAP. Got: " + final);
         }
         finally
         {
            DisableOAuth2();
            _application.Settings.IMAPSASLPlainEnabled = false;
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("Security: an 'alg':'none' token must be rejected over IMAP.")]
      public void TestImapXOAuth2AlgNoneRejected()
      {
         const string address = "oauthimapnone@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _application.Settings.IMAPSASLPlainEnabled = true;
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintNone(address, UnixNow() + 3600);

            string final = ImapAuthXOAuth2(address, token);
            Assert.IsTrue(final.Contains("A01 NO") || final.Contains("A01 BAD"),
               "An 'alg':'none' token must be rejected over IMAP. Got: " + final);
         }
         finally
         {
            DisableOAuth2();
            _application.Settings.IMAPSASLPlainEnabled = false;
            _settings.ClearLogonFailureList();
         }
      }

      // -- SMTP -------------------------------------------------------------------

      private static string SmtpAuthXOAuth2(string email, string token)
      {
         using (var con = new TcpConnection())
         {
            Assert.IsTrue(con.Connect(25), "Could not connect to SMTP.");
            con.ReadUntil("220"); // banner
            con.Send("EHLO example.test\r\n");
            con.ReadUntil("250 "); // end of EHLO keyword block
            con.Send("AUTH XOAUTH2 " + XOAuth2Response(email, token) + "\r\n");
            return con.Receive();
         }
      }

      [Test]
      [Description("RFC 7628 SASL XOAUTH2 over SMTP: a valid HS256 bearer token authenticates (235).")]
      public void TestSmtpXOAuth2Succeeds()
      {
         const string address = "oauthsmtp@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintHs256(address, UnixNow() + 3600);

            string response = SmtpAuthXOAuth2(address, token);
            Assert.IsTrue(response.StartsWith("235"),
               "A valid bearer token should authenticate over SMTP. Got: " + response);
         }
         finally
         {
            DisableOAuth2();
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("An expired bearer token is rejected over SMTP (535).")]
      public void TestSmtpXOAuth2ExpiredTokenFails()
      {
         const string address = "oauthsmtpexp@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintHs256(address, UnixNow() - 3600);

            string response = SmtpAuthXOAuth2(address, token);
            Assert.IsTrue(response.StartsWith("535"),
               "An expired bearer token must be rejected over SMTP. Got: " + response);
         }
         finally
         {
            DisableOAuth2();
            _settings.ClearLogonFailureList();
         }
      }

      [Test]
      [Description("Security: an 'alg':'none' token must be rejected over SMTP.")]
      public void TestSmtpXOAuth2AlgNoneRejected()
      {
         const string address = "oauthsmtpnone@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();
         try
         {
            EnableOAuth2("HS256");
            string token = MintNone(address, UnixNow() + 3600);

            string response = SmtpAuthXOAuth2(address, token);
            Assert.IsTrue(response.StartsWith("535"),
               "An 'alg':'none' token must be rejected over SMTP. Got: " + response);
         }
         finally
         {
            DisableOAuth2();
            _settings.ClearLogonFailureList();
         }
      }

      // -- RS256 (asymmetric / public-key) ---------------------------------------

      // RS256 tokens are signed with an RSA private key minted in-process; the
      // matching SubjectPublicKeyInfo PEM is written into the server's program
      // directory (so the LocalSystem service can read it) and configured via
      // OAuth2PublicKeyFile. This exercises the OpenSSL EVP_DigestVerify path that
      // backs real identity-provider tokens, still entirely offline.

      private string _rs256PublicKeyPath;

      private void EnableOAuth2Rs256(string publicKeyPath)
      {
         WriteSetting("OAuth2Enabled", "1");
         WriteSetting("OAuth2RequireTLS", "0");
         WriteSetting("OAuth2AllowedAlgorithms", "RS256");
         WriteSetting("OAuth2HmacSecret", "");
         WriteSetting("OAuth2PublicKeyFile", publicKeyPath);
         WriteSetting("OAuth2UsernameClaim", "email");
         WriteSetting("OAuth2Issuer", "");
         WriteSetting("OAuth2Audience", "");
         _application.Reinitialize();
      }

      /// <summary>
      ///    Alters a JWT signature so that it provably no longer verifies.
      ///
      ///    Deliberately does not touch the last character. base64url carries 6 bits per
      ///    character, so an encoding of a signature whose bit length is not a multiple of
      ///    6 ends with unused padding bits - 4 of them for a 256-byte RS256 signature.
      ///    A flip confined to those bits decodes to the identical byte sequence, the
      ///    signature still verifies, and a test built on that is flaky rather than
      ///    strict. Any interior character is all significant bits.
      /// </summary>
      private static string TamperSignature(string token)
      {
         int lastDot = token.LastIndexOf('.');
         Assert.Greater(lastDot, 0, "A JWT must have a signature segment to tamper.");

         string signature = token.Substring(lastDot + 1);
         Assert.Greater(signature.Length, 2, "The signature segment is too short to tamper safely.");

         // Middle of the signature: never the final character, never the segment boundary.
         int index = signature.Length / 2;
         char original = signature[index];
         char replacement = original == 'A' ? 'B' : 'A';

         string tamperedSignature = signature.Substring(0, index) + replacement + signature.Substring(index + 1);

         return token.Substring(0, lastDot + 1) + tamperedSignature;
      }

      /// <summary>Mints an RS256 (RSASSA-PKCS1-v1_5 / SHA-256) JWT for <paramref name="email"/>.</summary>
      private static string MintRs256(string email, long exp, RSA rsa)
      {
         string header = "{\"alg\":\"RS256\",\"typ\":\"JWT\"}";
         string payload = "{\"email\":\"" + email + "\",\"exp\":" + exp + "}";

         string headerSegment = Base64Url(Encoding.UTF8.GetBytes(header));
         string payloadSegment = Base64Url(Encoding.UTF8.GetBytes(payload));
         string signingInput = headerSegment + "." + payloadSegment;

         byte[] signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
         return signingInput + "." + Base64Url(signature);
      }

      // .NET Framework 4.8.1 lacks RSA.ExportSubjectPublicKeyInfo, so the SPKI
      // ("-----BEGIN PUBLIC KEY-----") PEM is hand-DER-encoded from the public
      // modulus/exponent. PEM_read_bio_PUBKEY on the server reads exactly this.
      private static string ExportPublicKeyPem(RSA rsa)
      {
         RSAParameters p = rsa.ExportParameters(false);

         byte[] algorithmId = DerSequence(Concat(
            // OID 1.2.840.113549.1.1.1 (rsaEncryption)
            new byte[] { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01 },
            new byte[] { 0x05, 0x00 })); // NULL parameters

         byte[] rsaPublicKey = DerSequence(Concat(
            DerInteger(p.Modulus),
            DerInteger(p.Exponent)));

         // BIT STRING wraps the RSAPublicKey DER with a leading 0x00 "unused bits" octet.
         byte[] subjectPublicKey = DerTlv(0x03, Concat(new byte[] { 0x00 }, rsaPublicKey));

         byte[] spki = DerSequence(Concat(algorithmId, subjectPublicKey));

         var sb = new StringBuilder();
         sb.Append("-----BEGIN PUBLIC KEY-----\n");
         sb.Append(Convert.ToBase64String(spki, Base64FormattingOptions.InsertLineBreaks));
         sb.Append("\n-----END PUBLIC KEY-----\n");
         return sb.ToString();
      }

      private static byte[] Concat(params byte[][] arrays)
      {
         int total = 0;
         foreach (byte[] a in arrays)
            total += a.Length;

         var result = new byte[total];
         int offset = 0;
         foreach (byte[] a in arrays)
         {
            Buffer.BlockCopy(a, 0, result, offset, a.Length);
            offset += a.Length;
         }
         return result;
      }

      private static byte[] DerLength(int length)
      {
         if (length < 0x80)
            return new[] { (byte)length };

         var lengthBytes = new System.Collections.Generic.List<byte>();
         int v = length;
         while (v > 0)
         {
            lengthBytes.Insert(0, (byte)(v & 0xFF));
            v >>= 8;
         }
         var result = new System.Collections.Generic.List<byte> { (byte)(0x80 | lengthBytes.Count) };
         result.AddRange(lengthBytes);
         return result.ToArray();
      }

      private static byte[] DerTlv(byte tag, byte[] content)
      {
         return Concat(new[] { tag }, DerLength(content.Length), content);
      }

      private static byte[] DerSequence(byte[] content)
      {
         return DerTlv(0x30, content);
      }

      private static byte[] DerInteger(byte[] magnitude)
      {
         // DER INTEGER is signed: prepend 0x00 when the high bit is set so the
         // value is not interpreted as negative (RSA modulus always needs this).
         if (magnitude.Length > 0 && (magnitude[0] & 0x80) != 0)
            magnitude = Concat(new byte[] { 0x00 }, magnitude);
         return DerTlv(0x02, magnitude);
      }

      [Test]
      [Description("RFC 7628 SASL XOAUTH2 over POP3 with an RS256 (public-key) token: a JWT signed by " +
                   "the RSA private key whose SubjectPublicKeyInfo is configured in OAuth2PublicKeyFile " +
                   "authenticates successfully; a token with a tampered signature is rejected.")]
      public void TestPop3XOAuth2Rs256Succeeds()
      {
         const string address = "oauthrs256@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         _rs256PublicKeyPath = Path.Combine(
            _application.Settings.Directories.ProgramDirectory, "hm_oauth2_test_rs256.pem");

         using (RSA rsa = RSA.Create())
         {
            rsa.KeySize = 2048;
            File.WriteAllText(_rs256PublicKeyPath, ExportPublicKeyPem(rsa));
            try
            {
               EnableOAuth2Rs256(_rs256PublicKeyPath);

               string token = MintRs256(address, UnixNow() + 3600, rsa);
               string response = Pop3AuthXOAuth2(address, token);
               Assert.IsTrue(response.StartsWith("+OK"),
                  "A valid RS256 bearer token should authenticate over POP3. Got: " + response);

               // Tamper the signature so that it genuinely no longer verifies.
               //
               // This used to flip the *final* character of the token, which is flaky and
               // was failing roughly one run in five. An RS256 signature over a 2048-bit
               // key is 256 bytes = 2048 bits, and its base64url encoding is 342
               // characters carrying 2052 bits - so the last character has four unused
               // padding bits. Flipping A to B there changes only padding for many
               // signatures, the decoded bytes come out byte-identical, and the server
               // then quite correctly accepts a token that was never actually modified.
               // Measured: ineffective for 79 of 400 random signatures.
               //
               // Flipping a character in the middle of the signature only ever touches
               // significant bits, so the decoded value always changes.
               string tampered = TamperSignature(token);
               Assert.AreNotEqual(token, tampered, "The fixture failed to tamper the token.");

               string tamperedResponse = Pop3AuthXOAuth2(address, tampered);
               Assert.IsTrue(tamperedResponse.StartsWith("-ERR"),
                  "An RS256 token with a tampered signature must be rejected. Got: " + tamperedResponse);
            }
            finally
            {
               DisableOAuth2();
               _settings.ClearLogonFailureList();
               if (File.Exists(_rs256PublicKeyPath))
                  File.Delete(_rs256PublicKeyPath);
            }
         }
      }

      [Test]
      [Description("RFC 7628 SASL XOAUTH2 over SMTP with an RS256 (public-key) token authenticates (235).")]
      public void TestSmtpXOAuth2Rs256Succeeds()
      {
         const string address = "oauthrs256smtp@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         _rs256PublicKeyPath = Path.Combine(
            _application.Settings.Directories.ProgramDirectory, "hm_oauth2_test_rs256_smtp.pem");

         using (RSA rsa = RSA.Create())
         {
            rsa.KeySize = 2048;
            File.WriteAllText(_rs256PublicKeyPath, ExportPublicKeyPem(rsa));
            try
            {
               EnableOAuth2Rs256(_rs256PublicKeyPath);

               string token = MintRs256(address, UnixNow() + 3600, rsa);
               string response = SmtpAuthXOAuth2(address, token);
               Assert.IsTrue(response.StartsWith("235"),
                  "A valid RS256 bearer token should authenticate over SMTP. Got: " + response);
            }
            finally
            {
               DisableOAuth2();
               _settings.ClearLogonFailureList();
               if (File.Exists(_rs256PublicKeyPath))
                  File.Delete(_rs256PublicKeyPath);
            }
         }
      }
   }
}
