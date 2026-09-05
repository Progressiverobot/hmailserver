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
   ///    OAuth 2.0 Token Introspection (RFC 7662), configured with
   ///    OAuth2IntrospectionUrl and the client credentials the provider issued.
   ///
   ///    A signed token says who issued it and when it expires, nothing about what has
   ///    happened since; a stolen token was good until "exp". With introspection every
   ///    token that verifies locally is put to the provider, whose "active" is the
   ///    verdict, cached for OAuth2IntrospectionCacheSeconds. A provider that cannot
   ///    answer refuses the token, as RFC 7662 says an unanswerable token is to be
   ///    treated, unless OAuth2IntrospectionFailOpen=1 says availability wins.
   ///
   ///    The tokens are HS256, minted with the shared secret, so the only thing under
   ///    test is what happens after local validation.
   /// </summary>
   [TestFixture]
   public class OAuth2Introspection : TestFixtureBase
   {
      private const string HmacSecret = "introspection-shared-test-secret-0123456789";
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "introspect@" + _domain.Name, "test");
      }

      [TearDown]
      public new void TearDown()
      {
         WriteSetting("OAuth2Enabled", "0");
         WriteSetting("OAuth2RequireTLS", "1");
         WriteSetting("OAuth2AllowedAlgorithms", "RS256");
         WriteSetting("OAuth2HmacSecret", "");
         WriteSetting("OAuth2IntrospectionUrl", "");
         WriteSetting("OAuth2IntrospectionClientId", "");
         WriteSetting("OAuth2IntrospectionClientSecret", "");
         WriteSetting("OAuth2IntrospectionCacheSeconds", "300");
         WriteSetting("OAuth2IntrospectionFailOpen", "0");
         _application.Reinitialize();
      }

      [Test]
      public void AnActiveTokenLogsOnAndTheRequestCarriesTheTokenAndTheClientCredentials()
      {
         using (var provider = new FakeHttpEndpoint(200, "{\"active\":true,\"scope\":\"mail\"}"))
         {
            Enable(provider.UrlFor("/introspect"), failOpen: false);

            var token = MintHs256(_account.Address, OAuth2Jwks.UnixNow() + 600);
            Assert.That(OAuth2Jwks.Pop3AuthXOAuth2(_account.Address, token), Does.StartWith("+OK"));

            Assert.That(provider.Requests.Count, Is.EqualTo(1));
            var request = provider.Requests[0];
            Assert.That(request, Does.StartWith("POST /introspect HTTP/1.0"));
            Assert.That(request, Does.Contain("Content-Type: application/x-www-form-urlencoded"));
            Assert.That(request, Does.Contain("Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("mail-server:s3cret"))));
            Assert.That(request, Does.Contain("token=" + Uri.EscapeDataString(token)));
            Assert.That(request, Does.Contain("token_type_hint=access_token"));

            // The verdict is cached: the second logon asks nobody.
            Assert.That(OAuth2Jwks.Pop3AuthXOAuth2(_account.Address, token), Does.StartWith("+OK"));
            Assert.That(provider.Requests.Count, Is.EqualTo(1));
         }
      }

      [Test]
      public void ATokenTheProviderReportsInactiveIsRefusedAlthoughItVerifies()
      {
         using (var provider = new FakeHttpEndpoint(200, "{\"active\":false}"))
         {
            Enable(provider.UrlFor("/introspect"), failOpen: false);

            var token = MintHs256(_account.Address, OAuth2Jwks.UnixNow() + 600);
            Assert.That(OAuth2Jwks.Pop3AuthXOAuth2(_account.Address, token), Does.StartWith("-ERR"));
            Assert.That(provider.Requests.Count, Is.EqualTo(1));
         }
      }

      [Test]
      public void AProviderThatCannotAnswerRefusesTheTokenByDefault()
      {
         using (var provider = new FakeHttpEndpoint(503, "try later", "text/plain"))
         {
            Enable(provider.UrlFor("/introspect"), failOpen: false);

            var token = MintHs256(_account.Address, OAuth2Jwks.UnixNow() + 600);
            Assert.That(OAuth2Jwks.Pop3AuthXOAuth2(_account.Address, token), Does.StartWith("-ERR"));
         }
      }

      [Test]
      public void AProviderThatCannotAnswerAcceptsTheTokenWhenFailOpenIsTheWrittenDownChoice()
      {
         using (var provider = new FakeHttpEndpoint(503, "try later", "text/plain"))
         {
            Enable(provider.UrlFor("/introspect"), failOpen: true);

            var token = MintHs256(_account.Address, OAuth2Jwks.UnixNow() + 600);
            Assert.That(OAuth2Jwks.Pop3AuthXOAuth2(_account.Address, token), Does.StartWith("+OK"));
         }
      }

      [Test]
      public void AResponseWithoutTheActiveMemberIsNotAnAnswer()
      {
         using (var provider = new FakeHttpEndpoint(200, "{\"scope\":\"mail\"}"))
         {
            Enable(provider.UrlFor("/introspect"), failOpen: false);

            var token = MintHs256(_account.Address, OAuth2Jwks.UnixNow() + 600);
            Assert.That(OAuth2Jwks.Pop3AuthXOAuth2(_account.Address, token), Does.StartWith("-ERR"));
         }
      }

      // ----------------------------------------------------------------------

      private void Enable(string url, bool failOpen)
      {
         WriteSetting("OAuth2Enabled", "1");
         WriteSetting("OAuth2RequireTLS", "0");
         WriteSetting("OAuth2AllowedAlgorithms", "HS256");
         WriteSetting("OAuth2HmacSecret", HmacSecret);
         WriteSetting("OAuth2PublicKeyFile", "");
         WriteSetting("OAuth2JwksUrl", "");
         WriteSetting("OAuth2UsernameClaim", "email");
         WriteSetting("OAuth2Issuer", "");
         WriteSetting("OAuth2Audience", "");
         WriteSetting("OAuth2IntrospectionUrl", url);
         WriteSetting("OAuth2IntrospectionClientId", "mail-server");
         WriteSetting("OAuth2IntrospectionClientSecret", "s3cret");
         WriteSetting("OAuth2IntrospectionCacheSeconds", "300");
         WriteSetting("OAuth2IntrospectionFailOpen", failOpen ? "1" : "0");
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

      private static string MintHs256(string email, long exp)
      {
         string header = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}";
         // A nonce keeps every minted token distinct, so a cached verdict from an
         // earlier test can never answer for this one.
         string payload = "{\"email\":\"" + email + "\",\"exp\":" + exp + ",\"jti\":\"" + Guid.NewGuid().ToString("N") + "\"}";
         string signingInput = OAuth2Jwks.Base64Url(Encoding.UTF8.GetBytes(header)) + "." + OAuth2Jwks.Base64Url(Encoding.UTF8.GetBytes(payload));
         using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(HmacSecret)))
         {
            byte[] signature = hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput));
            return signingInput + "." + OAuth2Jwks.Base64Url(signature);
         }
      }
   }
}
