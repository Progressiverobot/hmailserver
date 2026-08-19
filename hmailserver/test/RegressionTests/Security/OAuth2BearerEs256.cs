// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    ES256 (ECDSA P-256) bearer tokens.
   ///
   ///    ES256 was recognised and explicitly REFUSED until 19 August 2026, and the
   ///    reason was a format mismatch rather than a missing algorithm: JWS carries an
   ///    ECDSA signature as the raw fixed-width R||S pair (RFC 7515 section 3.4) while
   ///    OpenSSL verifies X9.62 DER. Without a transcode between them the server could
   ///    only ever report "signature verification failed", which describes nothing and
   ///    sends whoever hit it looking at their key, their clock and their provider.
   ///
   ///    The transcode's own correctness is pinned in the server's self-tests against
   ///    OpenSSL's encoder, case by case, because a transcode checked by round-tripping
   ///    through its own decoder proves only that it agrees with itself. What these
   ///    tests add is the other half: a token minted the way a real identity provider
   ///    mints one - .NET's ECDsa.SignData emits exactly the raw R||S form - travelling
   ///    the real SASL path into the real validator.
   ///
   ///    A separate file rather than a separate fixture, because the setup, the SASL
   ///    plumbing and the DER helpers are the ones next door and duplicating them to
   ///    avoid one `partial` would be the worse trade.
   /// </summary>
   public partial class OAuth2Bearer
   {
      private string _es256PublicKeyPath;

      private void EnableOAuth2Es256(string publicKeyPath)
      {
         WriteSetting("OAuth2Enabled", "1");
         WriteSetting("OAuth2RequireTLS", "0");
         WriteSetting("OAuth2AllowedAlgorithms", "ES256");
         WriteSetting("OAuth2HmacSecret", "");
         WriteSetting("OAuth2PublicKeyFile", publicKeyPath);
         WriteSetting("OAuth2UsernameClaim", "email");
         WriteSetting("OAuth2Issuer", "");
         WriteSetting("OAuth2Audience", "");
         _application.Reinitialize();
      }

      private static string MintEs256(string email, long exp, ECDsa ecdsa)
      {
         string header = "{\"alg\":\"ES256\",\"typ\":\"JWT\"}";
         string payload = "{\"email\":\"" + email + "\",\"exp\":" + exp + "}";

         string headerSegment = Base64Url(Encoding.UTF8.GetBytes(header));
         string payloadSegment = Base64Url(Encoding.UTF8.GetBytes(payload));
         string signingInput = headerSegment + "." + payloadSegment;

         // Raw R||S, 64 octets - the JWS form, and NOT the DER form OpenSSL verifies.
         // Bridging those two is the entire feature under test.
         byte[] signature = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);

         Assert.AreEqual(64, signature.Length,
            "An ES256 signature is exactly 64 octets. If .NET has started emitting DER here then this " +
            "fixture is no longer testing the transcode it exists to test.");

         return signingInput + "." + Base64Url(signature);
      }

      /// <summary>
      ///    The SubjectPublicKeyInfo PEM for a P-256 key, hand-DER-encoded because
      ///    .NET Framework 4.8.1 has no ExportSubjectPublicKeyInfo - the same reason
      ///    the RSA one next door is hand-encoded.
      /// </summary>
      private static string ExportEcPublicKeyPem(ECDsa ecdsa)
      {
         ECParameters parameters = ecdsa.ExportParameters(false);

         byte[] algorithmId = DerSequence(Concat(
            // OID 1.2.840.10045.2.1 (id-ecPublicKey)
            new byte[] { 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01 },
            // OID 1.2.840.10045.3.1.7 (prime256v1) - the curve is a PARAMETER of the
            // algorithm here, unlike RSA where it is a NULL.
            new byte[] { 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07 }));

         // Uncompressed point: 0x04 || X || Y, each coordinate 32 octets.
         byte[] point = Concat(new byte[] { 0x04 }, parameters.Q.X, parameters.Q.Y);
         byte[] subjectPublicKey = DerTlv(0x03, Concat(new byte[] { 0x00 }, point));

         byte[] spki = DerSequence(Concat(algorithmId, subjectPublicKey));

         var pem = new StringBuilder();
         pem.Append("-----BEGIN PUBLIC KEY-----\n");
         pem.Append(Convert.ToBase64String(spki, Base64FormattingOptions.InsertLineBreaks));
         pem.Append("\n-----END PUBLIC KEY-----\n");
         return pem.ToString();
      }

      [Test]
      [Description("SASL XOAUTH2 over POP3 with an ES256 token: the raw R||S form a real provider emits " +
                   "authenticates, and a tampered signature is refused")]
      public void TestPop3XOAuth2Es256Succeeds()
      {
         const string address = "oauthes256@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         _es256PublicKeyPath = Path.Combine(
            _application.Settings.Directories.ProgramDirectory, "hm_oauth2_test_es256.pem");

         using (ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
         {
            File.WriteAllText(_es256PublicKeyPath, ExportEcPublicKeyPem(ecdsa));
            try
            {
               EnableOAuth2Es256(_es256PublicKeyPath);

               string token = MintEs256(address, UnixNow() + 3600, ecdsa);
               string response = Pop3AuthXOAuth2(address, token);
               Assert.IsTrue(response.StartsWith("+OK"),
                  "A valid ES256 bearer token should authenticate over POP3. Got: " + response);

               string tampered = TamperSignature(token);
               Assert.AreNotEqual(token, tampered, "The fixture failed to tamper the token.");

               string tamperedResponse = Pop3AuthXOAuth2(address, tampered);
               Assert.IsTrue(tamperedResponse.StartsWith("-ERR"),
                  "An ES256 token with a tampered signature must be rejected. Got: " + tamperedResponse);
            }
            finally
            {
               DisableOAuth2();
               _settings.ClearLogonFailureList();
               if (File.Exists(_es256PublicKeyPath))
                  File.Delete(_es256PublicKeyPath);
            }
         }
      }

      [Test]
      [Description("Implementing an algorithm does not enable it: a valid ES256 token is refused while " +
                   "the configured allow-list omits ES256")]
      public void TestEs256StillObeysTheAllowList()
      {
         const string address = "oauthes256denied@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         _es256PublicKeyPath = Path.Combine(
            _application.Settings.Directories.ProgramDirectory, "hm_oauth2_test_es256_denied.pem");

         using (ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
         {
            File.WriteAllText(_es256PublicKeyPath, ExportEcPublicKeyPem(ecdsa));
            try
            {
               EnableOAuth2Es256(_es256PublicKeyPath);

               // An administrator who allow-listed RS256 has said which signatures they
               // trust. Shipping support for another algorithm must not quietly widen
               // that, which is the whole purpose of the allow-list existing separately
               // from what the code can do.
               WriteSetting("OAuth2AllowedAlgorithms", "RS256");
               _application.Reinitialize();

               string token = MintEs256(address, UnixNow() + 3600, ecdsa);
               string response = Pop3AuthXOAuth2(address, token);

               Assert.IsTrue(response.StartsWith("-ERR"),
                  "A perfectly valid ES256 token must be refused while the allow-list omits it. Got: " + response);
            }
            finally
            {
               DisableOAuth2();
               _settings.ClearLogonFailureList();
               if (File.Exists(_es256PublicKeyPath))
                  File.Delete(_es256PublicKeyPath);
            }
         }
      }

      [Test]
      [Description("An RSA key configured against ES256 is refused on the key type rather than " +
                   "surfacing as a signature failure")]
      public void TestAnRsaKeyUnderEs256IsRefused()
      {
         const string address = "oauthes256wrongkey@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "irrelevant-password");

         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         _es256PublicKeyPath = Path.Combine(
            _application.Settings.Directories.ProgramDirectory, "hm_oauth2_test_es256_rsakey.pem");

         using (ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
         using (RSA rsa = RSA.Create())
         {
            rsa.KeySize = 2048;

            // The key is the wrong KIND, not merely the wrong key - the algorithm
            // confusion case. It must be refused because the type does not match ES256,
            // not because a signature happened not to verify.
            File.WriteAllText(_es256PublicKeyPath, ExportPublicKeyPem(rsa));
            try
            {
               EnableOAuth2Es256(_es256PublicKeyPath);

               string token = MintEs256(address, UnixNow() + 3600, ecdsa);
               string response = Pop3AuthXOAuth2(address, token);

               Assert.IsTrue(response.StartsWith("-ERR"),
                  "An RSA public key configured for ES256 must not authenticate anything. Got: " + response);
            }
            finally
            {
               DisableOAuth2();
               _settings.ClearLogonFailureList();
               if (File.Exists(_es256PublicKeyPath))
                  File.Delete(_es256PublicKeyPath);
            }
         }
      }
   }
}
