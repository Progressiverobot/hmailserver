// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The one text matcher the palette and the settings index now share.
   ///
   /// It replaces two incomparable algorithms - a character subsequence over page
   /// names and a prefix/substring ladder over setting labels - so every test here
   /// fails against the unfixed code, where "delete log" found the page but not
   /// the setting and "dl" found the setting but not the page, and where ranking
   /// the two kinds of result in one list was not a meaningful operation.
   ///
   /// The rules that need pinning are the ones that are easy to get subtly wrong
   /// and hard to notice: the minimum length before a prefix counts as the same
   /// word, the fact that a query with one extra word must still match, and the
   /// ordering of the score ladder itself, which is what makes results from
   /// different sources comparable at all.
   /// </summary>
   public class SearchTermsTests
   {
      [Theory]
      [InlineData("Anti-spam settings", "anti spam settings")]
      [InlineData("Delete confirmed after (days)", "delete confirmed after days")]
      [InlineData("  TCP/IP   ports  ", "tcp ip ports")]
      [InlineData("Deliver local -> local", "deliver local local")]
      [InlineData("AntiSpam.SpamAssassinHost", "antispam spamassassinhost")]
      [InlineData("Port 465", "port 465")]
      [InlineData("", "")]
      [InlineData("!!!", "")]
      [InlineData(null, "")]
      public void Normalize_StripsPunctuationAndCase(string text, string expected)
      {
         Assert.Equal(expected, SearchTerms.Normalize(text));
      }

      /// <summary>
      /// Stop words are dropped from the word comparison but never from the
      /// normalised text, so "why is mail queued" still matches that phrase
      /// exactly while "mail queued" also reaches it.
      /// </summary>
      [Fact]
      public void Tokenize_DropsStopWordsButNormalizeKeepsThem()
      {
         Assert.Equal("why is mail queued", SearchTerms.Normalize("Why is mail queued?"));
         Assert.Equal(new[] { "why", "mail", "queued" },
            SearchTerms.Tokenize(SearchTerms.Normalize("Why is mail queued?")));
      }

      [Fact]
      public void Tokenize_AllStopWords_YieldsNothing()
      {
         Assert.Empty(SearchTerms.Tokenize(SearchTerms.Normalize("how do I")));
      }

      /// <summary>
      /// Folding happens on both sides of the comparison. One-sided query
      /// expansion would make search work in one direction only - "junk" finding
      /// a page whose text says "spam" but not the reverse - and the asymmetry is
      /// invisible until somebody reports half of it.
      /// </summary>
      [Theory]
      [InlineData("junk", "spam")]
      [InlineData("blacklist", "block")]
      [InlineData("whitelist", "allow")]
      [InlineData("ssl", "tls")]
      [InlineData("certs", "certificate")]
      [InlineData("printer", "device")]
      [InlineData("stuck", "queue")]
      [InlineData("mailbox", "account")]
      [InlineData("email", "mail")]
      [InlineData("prometheus", "monitoring")]
      [InlineData("spam", "spam")]
      [InlineData("nonsenseword", "nonsenseword")]
      public void Canonical_FoldsSynonymsOntoOneWord(string word, string canonical)
      {
         Assert.Equal(canonical, SearchTerms.Canonical(word));
      }

      /// <summary>
      /// Typing the start of a word finds the word. This is the direction that has
      /// to be generous: somebody typing "cert" has not decided yet whether they
      /// mean certificates or a certificate authority.
      /// </summary>
      [Theory]
      [InlineData("log", "logging")]
      [InlineData("cert", "certificate")]
      [InlineData("auth", "authentication")]
      [InlineData("queue", "queued")]
      public void TokenMatches_AcceptsATypedPrefixOfALongerWord(string typed, string word)
      {
         Assert.True(SearchTerms.TokenMatches(typed, word));
      }

      /// <summary>
      /// Ordinary plurals and -ed/-ing forms match in both directions, and are
      /// handled by the prefix rule rather than by a stemmer. A stemmer was
      /// rejected because the obvious rules mangle the words that matter most
      /// here: "queued" stems to "queu", which no longer matches "queue".
      /// </summary>
      [Theory]
      [InlineData("log", "logs")]
      [InlineData("queue", "queued")]
      [InlineData("block", "blocked")]
      [InlineData("port", "ports")]
      [InlineData("guess", "guessing")]
      [InlineData("log", "logging")]
      public void TokenMatches_AcceptsInflectionsInEitherDirection(string word, string inflected)
      {
         Assert.True(SearchTerms.TokenMatches(word, inflected));
         Assert.True(SearchTerms.TokenMatches(inflected, word));
      }

      /// <summary>
      /// A long identifier is not an inflection of a short word, and this is a
      /// mis-ranking that actually happened: with a symmetric prefix rule,
      /// searching for the INI key "LogDeleteDays" matched the setting labelled
      /// "Log destination" - because "logdeletedays" starts with "log" - and that
      /// word match outranked the exact hit on the key itself. Typing a setting's
      /// own identifier took the administrator to a different setting.
      /// </summary>
      [Theory]
      [InlineData("logdeletedays", "log")]
      [InlineData("spamassassinhost", "spam")]
      [InlineData("administrator", "admin")]
      public void TokenMatches_RefusesALongIdentifierMatchingAShortWord(string identifier, string word)
      {
         Assert.False(SearchTerms.TokenMatches(identifier, word));

         // The other direction is still a typed prefix and still matches.
         Assert.True(SearchTerms.TokenMatches(word, identifier));
      }

      /// <summary>
      /// The consequence, end to end: an exact hit on a setting's INI key must be
      /// the best match for that key.
      /// </summary>
      [Fact]
      public void Score_ExactKeyBeatsAWordInsideAnotherSettingsLabel()
      {
         var query = new SearchQuery("LogDeleteDays");

         Assert.Equal(SearchTerms.Exact, query.Score("LogDeleteDays"));
         Assert.Equal(SearchTerms.NoMatch, query.Score("Log destination"));
      }

      /// <summary>
      /// The three-character floor is what stops the prefix rule becoming a
      /// wildcard. Without it, searching for an IP address range would match
      /// "ipranges", "ipv6" and "important", which is most of the application.
      /// </summary>
      [Theory]
      [InlineData("ip", "ipranges")]
      [InlineData("ip", "ipv6")]
      [InlineData("id", "identity")]
      [InlineData("mx", "mxquery")]
      public void TokenMatches_RefusesTwoLetterPrefixes(string shorter, string longer)
      {
         Assert.False(SearchTerms.TokenMatches(shorter, longer));
         Assert.True(SearchTerms.TokenMatches(shorter, shorter));
      }

      [Fact]
      public void TokenMatches_RefusesUnrelatedWords()
      {
         Assert.False(SearchTerms.TokenMatches("spam", "smtp"));
         Assert.False(SearchTerms.TokenMatches("logs", "logging"));   // neither is a prefix of the other
         Assert.False(SearchTerms.TokenMatches(null, "spam"));
         Assert.False(SearchTerms.TokenMatches("spam", null));
      }

      /// <summary>
      /// The ladder has to be ordered, because that ordering is what lets a page
      /// name, an outcome phrase and a setting label be ranked against each other.
      /// </summary>
      [Fact]
      public void ScoreLadder_IsOrderedBestToWorst()
      {
         Assert.True(SearchTerms.Exact < SearchTerms.Prefix);
         Assert.True(SearchTerms.Prefix < SearchTerms.AllTokens);
         Assert.True(SearchTerms.AllTokens + SearchTerms.TokenSpreadPenalty < SearchTerms.Substring);
         Assert.True(SearchTerms.Substring < SearchTerms.MostTokens);
         Assert.True(SearchTerms.MostTokens < SearchTerms.Subsequence);
         Assert.True(SearchTerms.Subsequence < SearchTerms.NoMatch);
      }

      [Fact]
      public void Score_RanksExactThenPrefixThenWords()
      {
         var query = new SearchQuery("delete logs");

         Assert.Equal(SearchTerms.Exact, query.Score("Delete logs"));
         Assert.Equal(SearchTerms.Prefix, query.Score("Delete logs older than N days"));
         Assert.True(query.Score("Logs to delete") >= SearchTerms.AllTokens);
         Assert.True(query.Score("Logs to delete") <= SearchTerms.AllTokens + SearchTerms.TokenSpreadPenalty);
         Assert.Equal(SearchTerms.NoMatch, query.Score("Anti-spam settings"));
      }

      /// <summary>
      /// The same query accounting for more of the candidate ranks better, which is
      /// what puts "Anti-spam settings" above a nine-word setting label that
      /// happens to contain the word "spam".
      /// </summary>
      [Fact]
      public void Score_PrefersTheCandidateTheQueryAccountsForMostOf()
      {
         var query = new SearchQuery("spam");

         Assert.True(query.Score("Anti-spam settings") <
            query.Score("Max message size to spam-scan (KB, 0 = unlimited)"));
      }

      /// <summary>
      /// The behaviour that made the outcome phrases usable. Adding a word to a
      /// query that worked must not make it return nothing - the most confusing
      /// possible outcome for a search box, because it reads as "the extra word is
      /// the one thing we do not support".
      /// </summary>
      [Fact]
      public void Score_ToleratesOneExtraWordInTheQuery()
      {
         Assert.Equal(SearchTerms.Exact, new SearchQuery("stop spam").Score("stop spam"));

         int withExtra = new SearchQuery("stop junk mail").Score("stop spam");
         Assert.NotEqual(SearchTerms.NoMatch, withExtra);
         Assert.True(withExtra >= SearchTerms.MostTokens, "A partial match must rank below every full match.");
      }

      /// <summary>
      /// Guarded, though: a two-word query still has to match completely, and a
      /// long query may not latch onto a single incidental word.
      /// </summary>
      [Fact]
      public void Score_RefusesAMatchOnTooLittleOfTheQuery()
      {
         Assert.Equal(SearchTerms.NoMatch, new SearchQuery("spam ports").Score("TCP/IP ports"));
         Assert.Equal(SearchTerms.NoMatch,
            new SearchQuery("add a distribution list to the finance domain").Score("White list"));
      }

      /// <summary>
      /// Typing initials is a habit worth keeping and is the one thing the previous
      /// matcher was good at - but only where the caller asks for it, because a
      /// subsequence match over 244 setting labels returns nearly all of them.
      /// </summary>
      [Fact]
      public void ScoreLoose_AddsASubsequenceFallbackThatScoreDoesNot()
      {
         var query = new SearchQuery("dq");

         Assert.Equal(SearchTerms.NoMatch, query.Score("Delivery queue"));
         Assert.Equal(SearchTerms.Subsequence, query.ScoreLoose("Delivery queue"));
         Assert.Equal(SearchTerms.NoMatch, query.ScoreLoose("Anti-spam settings"));
      }

      [Fact]
      public void EmptyQuery_MatchesNothing()
      {
         var query = new SearchQuery("   ");

         Assert.True(query.IsEmpty);
         Assert.Empty(query.Tokens);
         Assert.Equal(SearchTerms.NoMatch, query.Score("Anti-spam settings"));
         Assert.Equal(SearchTerms.NoMatch, query.ScoreLoose("Anti-spam settings"));
      }

      [Fact]
      public void BestScore_TakesTheBestOfSeveralCandidates()
      {
         var query = new SearchQuery("auto ban");

         Assert.Equal(SearchTerms.Exact, query.BestScore(new[] { "Cipher suites", "Auto-ban", "TLS 1.3" }));
         Assert.Equal(SearchTerms.NoMatch, query.BestScore(new string[0]));
         Assert.Equal(SearchTerms.NoMatch, query.BestScore(null));
      }

      /// <summary>
      /// A query that is nothing but stop words carries no word information, and
      /// must not be treated as "every word matched".
      /// </summary>
      [Fact]
      public void Score_QueryOfOnlyStopWords_DoesNotMatchEverything()
      {
         var query = new SearchQuery("the");

         Assert.False(query.IsEmpty);
         Assert.Empty(query.Tokens);
         Assert.Equal(SearchTerms.NoMatch, query.Score("Anti-spam settings"));
         Assert.Equal(SearchTerms.Prefix, query.Score("The quick brown fox"));
      }
   }
}
