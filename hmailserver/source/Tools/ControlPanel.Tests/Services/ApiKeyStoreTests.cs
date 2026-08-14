using System;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The rules the Control Panel's key store shares with the server's.
   ///
   /// These matter more than most tests in this project. RestApiServer decides what
   /// a valid key looks like, and the Control Panel now writes keys too - so every
   /// rule below exists in two places, and a disagreement between them does not
   /// fail a build or throw an exception. It produces a key that is written
   /// successfully, listed on the page, and refused by the server with no
   /// explanation an administrator could act on.
   ///
   /// The values pinned here come from RestApiServer.cpp: ApiKeyTokenPrefix,
   /// ApiKeySecretBytes, ApiKeyIdBytes, ApiKeyDefaultLifetimeDays, the label rule
   /// in HandleCreateApiKey_, and HashApiKeyToken.
   /// </summary>
   public class ApiKeyStoreTests
   {
      // ---- the token and its digest ----------------------------------------

      /// <summary>
      /// The one value that has to be byte-for-byte right. The server stores
      /// HashApiKeyToken(token) and compares an incoming token's digest against
      /// it, so a digest computed differently here makes every key this page
      /// creates unusable - and the failure looks like "the key does not work",
      /// with nothing in any log pointing at the cause.
      ///
      /// The expected value is an independent SHA-256 of the ASCII bytes, not a
      /// value copied out of this implementation's own output.
      /// </summary>
      [Fact]
      public void HashToken_IsPlainLowerHexSha256OfTheAsciiToken()
      {
         string token = ApiKeyStore.TokenPrefix + new string('a', 0) + string.Concat(Enumerable.Repeat("ab", 32));

         Assert.Equal("hmapi_abababababababababababababababababababababababababababababababab", token);
         Assert.Equal("5d5d0cda64307f0892e1a8f8f2c00d7b60d7f75c0d82c403c204b6f80477431a",
            ApiKeyStore.HashToken(token));
      }

      [Fact]
      public void HashToken_IsSixtyFourLowerCaseHexCharacters()
      {
         string hash = ApiKeyStore.HashToken("hmapi_" + ApiKeyStore.RandomLowerHex(ApiKeyStore.SecretBytes));

         Assert.Equal(64, hash.Length);
         Assert.All(hash, c => Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), "'" + c + "' is not lower-case hex."));
      }

      /// <summary>
      /// The shape RestApiServer::AuthenticateApiKey_ checks before it hashes
      /// anything: the prefix, then exactly ApiKeySecretBytes*2 lower-hex
      /// characters. A token of any other length is rejected without a digest
      /// being computed, so getting the length wrong here would produce keys that
      /// are refused at the first check.
      /// </summary>
      [Fact]
      public void RandomLowerHex_ProducesTwoCharactersPerByte()
      {
         string secret = ApiKeyStore.RandomLowerHex(ApiKeyStore.SecretBytes);
         string id = ApiKeyStore.RandomLowerHex(ApiKeyStore.IdBytes);

         Assert.Equal(64, secret.Length);
         Assert.Equal(16, id.Length);
         Assert.All(secret + id, c => Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
      }

      /// <summary>
      /// Two keys minted in succession must not collide - in the token, which is
      /// the credential, or in the id, which names the ini section a key is
      /// stored in and revoked by. A repeated id would silently overwrite an
      /// existing key's section.
      /// </summary>
      [Fact]
      public void RandomLowerHex_DoesNotRepeat()
      {
         var seen = new System.Collections.Generic.HashSet<string>();

         for (int i = 0; i < 200; i++)
            Assert.True(seen.Add(ApiKeyStore.RandomLowerHex(ApiKeyStore.IdBytes)), "An id repeated within 200 draws.");
      }

      /// <summary>The constants this page shares with the server.</summary>
      [Fact]
      public void Constants_MatchRestApiServer()
      {
         Assert.Equal("hmapi_", ApiKeyStore.TokenPrefix);
         Assert.Equal("Key.", ApiKeyStore.SectionPrefix);
         Assert.Equal(32, ApiKeyStore.SecretBytes);
         Assert.Equal(8, ApiKeyStore.IdBytes);
         Assert.Equal(90, ApiKeyStore.DefaultLifetimeDays);
         Assert.Equal(64, ApiKeyStore.MaxLabelLength);
         Assert.Equal("full", ApiKeyStore.ScopeFull);
         Assert.Equal("readonly", ApiKeyStore.ScopeReadOnly);
      }

      // ---- the label -------------------------------------------------------

      [Fact]
      public void ValidateLabel_AcceptsAnOrdinaryLabel()
      {
         Assert.Null(ApiKeyStore.ValidateLabel("Grafana probe"));
         Assert.Null(ApiKeyStore.ValidateLabel(new string('x', ApiKeyStore.MaxLabelLength)));
      }

      [Theory]
      [InlineData("")]
      [InlineData("   ")]
      [InlineData(null)]
      public void ValidateLabel_RequiresOne(string label)
      {
         Assert.NotNull(ApiKeyStore.ValidateLabel(label));
      }

      [Fact]
      public void ValidateLabel_RefusesOneTooLong()
      {
         Assert.NotNull(ApiKeyStore.ValidateLabel(new string('x', ApiKeyStore.MaxLabelLength + 1)));
      }

      /// <summary>
      /// The label becomes an ini line and a log line. A carriage return inside it
      /// would appear to the server's line-by-line parser as further lines - a
      /// second [Key.*] section, or a Scope=full line under an existing one - so
      /// this is an injection guard, not a tidiness rule.
      /// </summary>
      [Theory]
      [InlineData("CI\r\nScope=full")]
      [InlineData("CI\nfoo")]
      [InlineData("CI\tdeploy")]
      [InlineData("CIdeploy")]
      public void ValidateLabel_RefusesControlCharacters(string label)
      {
         Assert.NotNull(ApiKeyStore.ValidateLabel(label));
      }

      // ---- the domain list -------------------------------------------------

      [Fact]
      public void NormalizeDomains_TrimsLowerCasesAndJoins()
      {
         string result = ApiKeyStore.NormalizeDomains(" Example.COM ,  example.net ", out string error);

         Assert.Null(error);
         Assert.Equal("example.com,example.net", result);
      }

      [Fact]
      public void NormalizeDomains_EmptyMeansEveryDomain()
      {
         Assert.Equal("", ApiKeyStore.NormalizeDomains("", out string error));
         Assert.Null(error);

         Assert.Equal("", ApiKeyStore.NormalizeDomains(null, out error));
         Assert.Null(error);
      }

      /// <summary>
      /// A list the administrator asked for that survives normalisation as nothing
      /// would be stored as "", which the server reads as "every domain" - the
      /// opposite of a restriction. It has to be an error instead.
      /// </summary>
      [Theory]
      [InlineData(" , ")]
      [InlineData(",,,")]
      public void NormalizeDomains_RefusesAListThatNormalisesToNothing(string domains)
      {
         Assert.Null(ApiKeyStore.NormalizeDomains(domains, out string error));
         Assert.NotNull(error);
      }

      [Theory]
      [InlineData("not a domain")]
      [InlineData("example.com, oops!")]
      [InlineData(".example.com")]
      [InlineData("example.com.")]
      [InlineData("example..com")]
      public void NormalizeDomains_RefusesSomethingThatIsNotADomainName(string domains)
      {
         Assert.Null(ApiKeyStore.NormalizeDomains(domains, out string error));
         Assert.NotNull(error);
      }

      /// <summary>
      /// The domain need not exist yet - a key may legitimately be minted before
      /// the domain it will manage - so validation is about the shape of the name
      /// and nothing else.
      /// </summary>
      [Theory]
      [InlineData("example.com")]
      [InlineData("mail.example.co.uk")]
      [InlineData("xn--bcher-kva.example")]
      [InlineData("a-b.example.com")]
      public void IsValidDomainName_AcceptsANameThatNeedNotExistYet(string name)
      {
         Assert.True(ApiKeyStore.IsValidDomainName(name));
      }

      [Theory]
      [InlineData("localhost")]      // no dot: cannot be a mail domain here
      [InlineData("")]
      [InlineData(null)]
      public void IsValidDomainName_RefusesTheRest(string name)
      {
         Assert.False(ApiKeyStore.IsValidDomainName(name));
      }

      // ---- the source restriction ------------------------------------------

      [Theory]
      [InlineData("10.0.0.5")]
      [InlineData("10.0.0.1-10.0.0.99")]
      [InlineData("10.0.0.0/24")]
      [InlineData("::1")]
      [InlineData("2001:db8::/32")]
      public void LooksLikeSourceRestriction_AcceptsTheThreeFormsTheServerParses(string value)
      {
         Assert.True(ApiKeyStore.LooksLikeSourceRestriction(value));
      }

      [Theory]
      [InlineData("")]
      [InlineData("anywhere")]
      [InlineData("10.0.0.0/")]
      [InlineData("10.0.0.0/9999")]
      [InlineData("10.0.0.1-")]
      [InlineData("10.0.0.1-nonsense")]
      public void LooksLikeSourceRestriction_RefusesWhatWouldNeverMatch(string value)
      {
         Assert.False(ApiKeyStore.LooksLikeSourceRestriction(value));
      }

      // ---- expiry ----------------------------------------------------------

      /// <summary>
      /// The server's IsExpired_ treats an expiry it cannot read as expired, which
      /// fails closed. The page has to say the same thing, or a key that the server
      /// refuses every request would be listed here as live.
      /// </summary>
      [Theory]
      [InlineData("")]
      [InlineData("soon")]
      [InlineData("2027/01/01")]
      [InlineData("01-01-2027 00:00:00")]
      public void ApiKeyRecord_AnUnreadableExpiryCountsAsExpired(string expires)
      {
         var record = new ApiKeyRecord { Expires = expires };

         Assert.Null(record.ExpiresAt);
         Assert.True(record.IsExpired);
      }

      [Fact]
      public void ApiKeyRecord_ReadsTheServersTimestampFormat()
      {
         var record = new ApiKeyRecord { Expires = "2099-12-31 23:59:59" };

         Assert.Equal(new DateTime(2099, 12, 31, 23, 59, 59), record.ExpiresAt);
         Assert.False(record.IsExpired);
      }

      [Fact]
      public void ApiKeyRecord_APastExpiryIsExpired()
      {
         var record = new ApiKeyRecord
         {
            Expires = DateTime.Now.AddMinutes(-1).ToString(ApiKeyStore.TimestampFormat)
         };

         Assert.True(record.IsExpired);
      }

      /// <summary>
      /// Scope fails closed in the server: only the literal "full" widens a key, so
      /// a record built from a section with no Scope line must be read-only. The
      /// GUI showing a full-authority key as read-only would be the more dangerous
      /// direction, so the default is pinned here.
      /// </summary>
      [Fact]
      public void ApiKeyRecord_DefaultsToReadOnly()
      {
         Assert.True(new ApiKeyRecord().ReadOnly);
      }
   }
}
