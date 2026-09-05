// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    The identity provider's JWK Set (RFC 7517) as the source of RS256 and ES256
   ///    signing keys, configured with OAuth2JwksUrl.
   ///
   ///    Before this, the validator knew one key, from a PEM file copied by hand, and
   ///    a provider rotating its keys stopped every bearer logon until somebody copied
   ///    the new one. Now the token's "kid" selects the key from the published set; a
   ///    kid the cache does not hold triggers one re-fetch, which is what a rotation
   ///    looks like from here. The provider is a FakeHttpEndpoint serving a JWK Set
   ///    built from keys minted in-process, and the tokens are signed with those keys.
   /// </summary>
   [TestFixture]
   public class OAuth2Jwks : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "jwks@" + _domain.Name, "test");

         // The JWK Set fetch reports what went wrong in the debug log and nowhere else.
         _settings.Logging.Enabled = true;
         _settings.Logging.LogDebug = true;
      }

      [TearDown]
      public new void TearDown()
      {
         WriteSetting("OAuth2Enabled", "0");
         WriteSetting("OAuth2RequireTLS", "1");
         WriteSetting("OAuth2AllowedAlgorithms", "RS256");
         WriteSetting("OAuth2JwksUrl", "");
         WriteSetting("OAuth2JwksCacheSeconds", "3600");
         WriteSetting("OAuth2PublicKeyFile", "");
         _application.Reinitialize();
      }

      [Test]
      public void AnRs256TokenIsVerifiedWithTheKeyTheJwkSetPublishesUnderItsKid()
      {
         using (var rsa = new RSACng(2048))
         using (var provider = new FakeHttpEndpoint(200, JwkSet(RsaJwk(rsa, "key-1"))))
         {
            EnableJwks(provider.UrlFor("/.well-known/jwks.json"), "RS256");

            var token = MintRs256(rsa, "key-1", _account.Address, UnixNow() + 600);
            Assert.That(Pop3AuthXOAuth2(_account.Address, token), Does.StartWith("+OK"));
            Assert.That(provider.Requests.Count, Is.EqualTo(1), "The JWK Set was not fetched exactly once.");
            Assert.That(provider.Requests[0], Does.StartWith("GET /.well-known/jwks.json HTTP/1.0"));

            // The second logon is served from the cache.
            Assert.That(Pop3AuthXOAuth2(_account.Address, token), Does.StartWith("+OK"));
            Assert.That(provider.Requests.Count, Is.EqualTo(1));
         }
      }

      [Test]
      public void AnEs256TokenIsVerifiedWithTheP256KeyTheJwkSetPublishes()
      {
         using (var ecdsa = new ECDsaCng(ECCurve.NamedCurves.nistP256))
         using (var provider = new FakeHttpEndpoint(200, JwkSet(EcJwk(ecdsa, "ec-1"))))
         {
            EnableJwks(provider.UrlFor("/jwks"), "ES256");

            var token = MintEs256(ecdsa, "ec-1", _account.Address, UnixNow() + 600);
            Assert.That(Pop3AuthXOAuth2(_account.Address, token), Does.StartWith("+OK"));
         }
      }

      [Test]
      public void AKeyRotationIsPickedUpWhenATokenNamesAKidTheCacheDoesNotHold()
      {
         using (var oldKey = new RSACng(2048))
         using (var newKey = new RSACng(2048))
         using (var provider = new FakeHttpEndpoint(200, JwkSet(RsaJwk(oldKey, "key-1"))))
         {
            EnableJwks(provider.UrlFor("/jwks"), "RS256");

            Assert.That(Pop3AuthXOAuth2(_account.Address, MintRs256(oldKey, "key-1", _account.Address, UnixNow() + 600)), Does.StartWith("+OK"));

            // The provider rotates: the new key under a new kid, the old one gone.
            provider.SetResponse(200, JwkSet(RsaJwk(newKey, "key-2")));

            Assert.That(Pop3AuthXOAuth2(_account.Address, MintRs256(newKey, "key-2", _account.Address, UnixNow() + 600)), Does.StartWith("+OK"),
               "A token signed with the rotated key was refused: the unknown kid should have triggered a re-fetch.");
            Assert.That(provider.Requests.Count, Is.EqualTo(2));

            // The old key is no longer published, so a token still signed with it is
            // exactly the thing a rotation is meant to stop - and the re-fetch that
            // just happened is inside the cooldown, so it costs no request either.
            Assert.That(Pop3AuthXOAuth2(_account.Address, MintRs256(oldKey, "key-1", _account.Address, UnixNow() + 600)), Does.StartWith("-ERR"));
            Assert.That(provider.Requests.Count, Is.EqualTo(2), "An unknown kid inside the refresh cooldown must not fetch again.");
         }
      }

      [Test]
      public void ATokenSignedWithAKeyOfTheWrongKindIsRefusedWhateverItsKid()
      {
         using (var rsa = new RSACng(2048))
         using (var ecdsa = new ECDsaCng(ECCurve.NamedCurves.nistP256))
         using (var provider = new FakeHttpEndpoint(200, JwkSet(EcJwk(ecdsa, "shared-kid"))))
         {
            EnableJwks(provider.UrlFor("/jwks"), "RS256,ES256");

            // An RS256 token naming the kid of an EC key: the set holds no RSA key of that
            // name, and the EC key must never be tried against an RSA signature.
            Assert.That(Pop3AuthXOAuth2(_account.Address, MintRs256(rsa, "shared-kid", _account.Address, UnixNow() + 600)), Does.StartWith("-ERR"));
         }
      }

      [Test]
      public void AJwkSetThatCannotBeFetchedRefusesTheTokenWhenNoPemFileIsConfigured()
      {
         using (var rsa = new RSACng(2048))
         using (var provider = new FakeHttpEndpoint(500, "provider down"))
         {
            EnableJwks(provider.UrlFor("/jwks"), "RS256");

            Assert.That(Pop3AuthXOAuth2(_account.Address, MintRs256(rsa, "key-1", _account.Address, UnixNow() + 600)), Does.StartWith("-ERR"));
         }
      }

      [Test]
      public void AnRs256TokenLogsOnOverSmtpAlthoughItIsLongerThanACommandLine()
      {
         // An RS256 token is over a kilobyte before XOAUTH2 base64-encodes it again -
         // longer than the 510-octet command line SMTP otherwise allows, and the 500
         // POP3 allows - and both used to refuse it with "Line too long" for exactly that
         // reason. The POP3 tests above cover the other protocol; this is the SMTP half.
         using (var rsa = new RSACng(2048))
         using (var provider = new FakeHttpEndpoint(200, JwkSet(RsaJwk(rsa, "key-1"))))
         {
            EnableJwks(provider.UrlFor("/jwks"), "RS256");

            var token = MintRs256(rsa, "key-1", _account.Address, UnixNow() + 600);
            var line = "AUTH XOAUTH2 " + Convert.ToBase64String(Encoding.ASCII.GetBytes("user=" + _account.Address + "\u0001auth=Bearer " + token + "\u0001\u0001"));
            Assert.That(line.Length, Is.GreaterThan(510), "The AUTH line is not long enough to prove anything.");
            Assert.That(SmtpAuthXOAuth2(_account.Address, token), Does.StartWith("235"));
         }
      }

      [Test]
      public void AJwkSetUrlOffTheLoopbackAddressMustBeHttps()
      {
         using (var rsa = new RSACng(2048))
         {
            // No listener is needed: the URL is refused before a connection is attempted.
            EnableJwks("http://192.0.2.10/jwks", "RS256");

            Assert.That(Pop3AuthXOAuth2(_account.Address, MintRs256(rsa, "key-1", _account.Address, UnixNow() + 600)), Does.StartWith("-ERR"));
         }
      }

      // ----------------------------------------------------------------------

      private void EnableJwks(string url, string algorithms)
      {
         WriteSetting("OAuth2Enabled", "1");
         WriteSetting("OAuth2RequireTLS", "0");
         WriteSetting("OAuth2AllowedAlgorithms", algorithms);
         WriteSetting("OAuth2HmacSecret", "");
         WriteSetting("OAuth2PublicKeyFile", "");
         WriteSetting("OAuth2UsernameClaim", "email");
         WriteSetting("OAuth2Issuer", "");
         WriteSetting("OAuth2Audience", "");
         WriteSetting("OAuth2JwksUrl", url);
         WriteSetting("OAuth2JwksCacheSeconds", "3600");
         _application.Reinitialize();
      }

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      internal static string Base64Url(byte[] data)
      {
         return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
      }

      internal static long UnixNow()
      {
         return (long) (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
      }

      private static string JwkSet(params string[] keys)
      {
         return "{\"keys\":[" + string.Join(",", keys) + "]}";
      }

      private static string RsaJwk(RSA rsa, string kid)
      {
         var parameters = rsa.ExportParameters(false);
         return "{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"" + kid + "\",\"n\":\"" + Base64Url(parameters.Modulus) +
                "\",\"e\":\"" + Base64Url(parameters.Exponent) + "\"}";
      }

      private static string EcJwk(ECDsa ecdsa, string kid)
      {
         var parameters = ecdsa.ExportParameters(false);
         return "{\"kty\":\"EC\",\"use\":\"sig\",\"crv\":\"P-256\",\"kid\":\"" + kid + "\",\"x\":\"" + Base64Url(parameters.Q.X) +
                "\",\"y\":\"" + Base64Url(parameters.Q.Y) + "\"}";
      }

      private static string SigningInput(string alg, string kid, string email, long exp)
      {
         string header = "{\"alg\":\"" + alg + "\",\"typ\":\"JWT\",\"kid\":\"" + kid + "\"}";
         string payload = "{\"email\":\"" + email + "\",\"exp\":" + exp + "}";
         return Base64Url(Encoding.UTF8.GetBytes(header)) + "." + Base64Url(Encoding.UTF8.GetBytes(payload));
      }

      private static string MintRs256(RSA rsa, string kid, string email, long exp)
      {
         var signingInput = SigningInput("RS256", kid, email, exp);
         var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
         return signingInput + "." + Base64Url(signature);
      }

      private static string MintEs256(ECDsa ecdsa, string kid, string email, long exp)
      {
         // ECDsa.SignData returns the IEEE P1363 R||S pair, which is the JWS form.
         var signingInput = SigningInput("ES256", kid, email, exp);
         var signature = ecdsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
         return signingInput + "." + Base64Url(signature);
      }

      internal static string SmtpAuthXOAuth2(string email, string token)
      {
         string sasl = "user=" + email + "\u0001auth=Bearer " + token + "\u0001\u0001";
         using (var con = new TcpConnection())
         {
            Assert.IsTrue(con.Connect(25), "Could not connect to SMTP.");
            con.ReadUntil("220");
            con.Send("EHLO example.test\r\n");
            con.ReadUntil("250 ");
            con.Send("AUTH XOAUTH2 " + Convert.ToBase64String(Encoding.ASCII.GetBytes(sasl)) + "\r\n");
            return con.Receive();
         }
      }

      internal static string Pop3AuthXOAuth2(string email, string token)
      {
         string sasl = "user=" + email + "\u0001auth=Bearer " + token + "\u0001\u0001";
         using (var con = new TcpConnection())
         {
            Assert.IsTrue(con.Connect(110), "Could not connect to POP3.");
            con.ReadUntil("+OK");
            con.Send("AUTH XOAUTH2 " + Convert.ToBase64String(Encoding.ASCII.GetBytes(sasl)) + "\r\n");
            return con.Receive();
         }
      }
   }
}
