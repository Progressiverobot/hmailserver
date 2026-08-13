using System;
using System.Collections.Generic;
using System.Text;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The one text matcher the Control Panel's search uses, shared by the
   /// command palette and by <see cref="SettingsSearchIndex"/>.
   ///
   /// It exists because the palette previously used two unrelated algorithms -
   /// a character subsequence over page names and a prefix/substring rank over
   /// setting labels - and the two disagreed about what "matches" means. Typing
   /// "delete log" found the page but not the setting; typing "dl" found the
   /// setting but not the page. Ranking pages, tasks and settings against each
   /// other in one list is only meaningful if all three are scored the same way,
   /// so the scoring lives here rather than in either caller.
   ///
   /// Scores are "golf" scores: lower is better, and <see cref="NoMatch"/> is
   /// int.MaxValue so that a caller can take the best of several fields with a
   /// plain Math.Min instead of carrying a sentinel around.
   /// </summary>
   public static class SearchTerms
   {
      /// <summary>Not a match at all. int.MaxValue so Math.Min just works.</summary>
      public const int NoMatch = int.MaxValue;

      /// <summary>The whole candidate is the query.</summary>
      public const int Exact = 0;

      /// <summary>The candidate begins with the query.</summary>
      public const int Prefix = 5;

      /// <summary>
      /// Every word of the query appears in the candidate, in any order. The
      /// caller adds up to <see cref="TokenSpreadPenalty"/> on top, so that a
      /// query which accounts for most of the candidate's words beats one that
      /// picks two words out of nine.
      /// </summary>
      public const int AllTokens = 10;

      /// <summary>Largest penalty added to <see cref="AllTokens"/> for unmatched candidate words.</summary>
      public const int TokenSpreadPenalty = 4;

      /// <summary>The query appears inside the candidate but not at the start.</summary>
      public const int Substring = 15;

      /// <summary>
      /// Most of the query's words appear in the candidate, but not all of them.
      /// The caller adds the number of words that did not match.
      ///
      /// This exists because people type more than the minimum. "stop junk mail"
      /// is the same request as "stop spam" with one extra word, and requiring
      /// every word to match meant that adding a word to a query that worked
      /// could make it return nothing at all - the most confusing possible
      /// behaviour for a search box, because it looks like the extra word was the
      /// specific thing we do not support.
      ///
      /// Guarded rather than open-ended: at least two words must match and at
      /// least two thirds of them, so a two-word query still has to match
      /// completely and a long query cannot latch onto one incidental word.
      /// </summary>
      public const int MostTokens = 25;

      /// <summary>Fewest query words that may match for <see cref="MostTokens"/> to apply.</summary>
      public const int MinPartialTokens = 2;

      /// <summary>
      /// The query's characters appear in the candidate in order but not
      /// contiguously ("dq" -> "Delivery queue"). Kept because the previous
      /// palette matched page names this way and people type initials, but
      /// ranked far below any real match and offered only where the caller asks
      /// for it - a subsequence match over 244 setting labels is noise.
      /// </summary>
      public const int Subsequence = 40;

      /// <summary>
      /// Words that carry no meaning in a query typed at a mail server. Removed
      /// from the token comparison only - never from the normalised text - so
      /// that "why is mail queued" still matches the phrase "why is mail queued"
      /// exactly while also matching "mail queued".
      /// </summary>
      private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
      {
         "a", "an", "the", "is", "are", "am", "was", "be", "been", "being",
         "do", "does", "did", "how", "of", "for", "in", "on", "at", "to", "into",
         "it", "its", "this", "that", "these", "those", "with", "and", "or",
         "my", "me", "i", "we", "our", "you", "your", "can", "could", "should",
         "would", "will", "please", "get", "got", "there", "here"
      };

      /// <summary>
      /// Verbs that say what the user wants DONE rather than what it is done to.
      ///
      /// These exist for one narrow rule: a two-word query where only one word matched
      /// is accepted when the word that did not match is one of these. That is the
      /// difference between "reduce spam", where "spam" is the whole topic and "reduce"
      /// is packaging, and "spam ports", where dropping either word changes what was
      /// asked for - so the first must find the spam pages and the second must still
      /// find nothing.
      ///
      /// Deliberately not merged into <see cref="StopWords"/>. A stop word is dropped
      /// from the comparison entirely, which would make "reduce" and "delete"
      /// invisible - and "delete a user" needs "delete" to reach the intent phrases
      /// that were added for exactly that query. These words are matched normally and
      /// only forgiven when they are the sole thing missing.
      /// </summary>
      private static readonly HashSet<string> ActionVerbs = new HashSet<string>(StringComparer.Ordinal)
      {
         "add", "new", "create", "make", "set", "setup", "configure", "change", "edit",
         "update", "enable", "disable", "turn", "allow", "block", "stop", "start",
         "reduce", "increase", "improve", "clear", "flush", "purge", "clean",
         "delete", "remove", "revoke", "reset", "restore", "import", "export",
         "upload", "download", "install", "schedule", "show", "find", "open", "view",
         "check", "test", "fix", "move", "rename", "send", "receive", "limit"
      };

      /// <summary>
      /// True if the word says what the user wants done rather than what it is done to.
      /// Exposed as a predicate rather than the set so the table stays private and
      /// SearchQuery cannot accumulate its own opinions about it.
      /// </summary>
      internal static bool IsActionVerb(string word)
      {
         return word != null && ActionVerbs.Contains(word);
      }

      /// <summary>
      /// Word-level synonyms, folded onto a single canonical word on both sides
      /// of the comparison. Symmetric folding rather than one-sided query
      /// expansion: expanding only the query means "junk" finds the page whose
      /// text says "spam" but the reverse fails, and the asymmetry is invisible
      /// until someone reports that search works one way round.
      ///
      /// This is deliberately a small, hand-picked table. A stemmer was
      /// considered and rejected: the obvious rules mangle exactly the words
      /// that matter here ("queued" stems to "queu", which no longer matches
      /// "queue"), and the prefix rule in <see cref="TokenMatches"/> already
      /// covers ordinary plurals and -ed/-ing forms without guessing.
      /// </summary>
      private static readonly Dictionary<string, string> Synonyms = new Dictionary<string, string>(StringComparer.Ordinal)
      {
         // "email" and "mail" are the same word to everyone except a parser.
         ["email"] = "mail",
         ["emails"] = "mail",

         // Unwanted mail.
         ["junk"] = "spam",
         ["unsolicited"] = "spam",
         ["ugc"] = "spam",
         ["phishing"] = "spam",

         // Refusing something.
         ["ban"] = "block",
         ["banned"] = "block",
         ["banning"] = "block",
         ["deny"] = "block",
         ["denied"] = "block",
         ["reject"] = "block",
         ["rejected"] = "block",
         ["blacklist"] = "block",
         ["blacklisted"] = "block",
         ["blocklist"] = "block",
         ["refuse"] = "block",
         ["forbid"] = "block",

         // Permitting something.
         ["permit"] = "allow",
         ["whitelist"] = "allow",
         ["whitelisted"] = "allow",
         ["allowlist"] = "allow",
         ["exempt"] = "allow",
         ["bypass"] = "allow",

         // Mail that has not moved.
         ["stuck"] = "queue",
         ["stalled"] = "queue",
         ["stall"] = "queue",
         ["backlog"] = "queue",
         ["spool"] = "queue",
         ["spooled"] = "queue",
         ["pending"] = "queue",
         ["waiting"] = "queue",

         // People.
         ["user"] = "account",
         ["users"] = "account",
         ["mailbox"] = "account",
         ["mailboxes"] = "account",
         ["accounts"] = "account",
         ["person"] = "account",
         ["staff"] = "account",
         ["employee"] = "account",

         // Transport security. "ssl" and "tls" are the same subject to an
         // administrator even though they are different protocols to us.
         ["ssl"] = "tls",
         ["https"] = "tls",
         ["starttls"] = "tls",
         ["encryption"] = "tls",
         ["encrypted"] = "tls",
         ["cert"] = "certificate",
         ["certs"] = "certificate",
         ["certificates"] = "certificate",
         ["pfx"] = "certificate",
         ["pem"] = "certificate",
         ["letsencrypt"] = "acme",

         // Relaying.
         ["smarthost"] = "relay",
         ["relaying"] = "relay",
         ["relays"] = "relay",
         ["forwarder"] = "relay",

         // Things that are not people but still send mail.
         ["printer"] = "device",
         ["scanner"] = "device",
         ["copier"] = "device",
         ["appliance"] = "device",
         ["iot"] = "device",
         ["camera"] = "device",

         // Malware, all folded onto "virus".
         ["viruses"] = "virus",
         ["malware"] = "virus",
         ["clamav"] = "virus",
         ["antivirus"] = "virus",

         // Authentication.
         ["oauth"] = "oauth2",
         ["xoauth2"] = "oauth2",
         ["modern"] = "oauth2",
         ["passwords"] = "password",
         ["passwd"] = "password",
         ["credentials"] = "password",
         ["totp"] = "twofactor",
         ["mfa"] = "twofactor",
         ["2fa"] = "twofactor",

         // Outside monitoring.
         ["prometheus"] = "monitoring",
         ["grafana"] = "monitoring",
         ["metrics"] = "monitoring",
         ["monitor"] = "monitoring",
         ["nagios"] = "monitoring",
         ["zabbix"] = "monitoring",

         // Client setup.
         ["autodiscover"] = "autoconfiguration",
         ["autoconfig"] = "autoconfiguration",
         ["outlook"] = "autoconfiguration",
         ["thunderbird"] = "autoconfiguration",

         // Repeated logon failures, all folded onto "bruteforce".
         ["brute"] = "bruteforce",
         ["hammering"] = "bruteforce",
         ["lockout"] = "bruteforce",
         ["autoban"] = "bruteforce",

         // Speed.
         ["slow"] = "performance",
         ["sluggish"] = "performance",
         ["latency"] = "performance",
         ["throughput"] = "performance"
      };

      /// <summary>
      /// Lower-cases, replaces every run of non-alphanumeric characters with a
      /// single space and trims. Punctuation has to go: setting labels are full
      /// of it ("Delete confirmed after (days)", "Deliver local -> local",
      /// "Anti-spam"), and nobody types it. Digits are kept because ports and
      /// protocol versions are how people search for those pages.
      /// </summary>
      public static string Normalize(string text)
      {
         if (string.IsNullOrEmpty(text))
            return "";

         var builder = new StringBuilder(text.Length);
         bool pendingSpace = false;

         foreach (char c in text)
         {
            if (char.IsLetterOrDigit(c))
            {
               if (pendingSpace && builder.Length > 0)
                  builder.Append(' ');
               pendingSpace = false;
               builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
               pendingSpace = true;
            }
         }

         return builder.ToString();
      }

      /// <summary>
      /// Splits already-normalised text into comparable words: stop words
      /// dropped, synonyms folded. Returns an empty list when the text is
      /// nothing but stop words, which callers must treat as "no token
      /// information" rather than as "matches everything".
      /// </summary>
      public static List<string> Tokenize(string normalized)
      {
         var tokens = new List<string>();
         if (string.IsNullOrEmpty(normalized))
            return tokens;

         foreach (string word in normalized.Split(' '))
         {
            if (word.Length == 0 || StopWords.Contains(word))
               continue;

            tokens.Add(Canonical(word));
         }

         return tokens;
      }

      /// <summary>The word every synonym of <paramref name="word"/> folds onto.</summary>
      public static string Canonical(string word)
         => word != null && Synonyms.TryGetValue(word, out string canonical) ? canonical : word;

      /// <summary>Shortest word that may match as a prefix of a longer one.</summary>
      public const int MinPrefixLength = 3;

      /// <summary>
      /// How much longer than the candidate a query word may be and still count
      /// as the same word. Four characters covers every English inflection that
      /// matters here - s, es, ed, ing - and nothing longer.
      /// </summary>
      public const int MaxInflectionLength = 4;

      /// <summary>
      /// Whether two canonical words should be considered the same word: equal,
      /// the query is a prefix of the candidate ("log" -> "logging", "cert" ->
      /// "certificate"), or the query is the candidate with an inflection on it
      /// ("queued" -> "queue", "ports" -> "port").
      ///
      /// Both bounds were put here after a mis-ranking, and removing either one
      /// brings it back.
      ///
      /// The <see cref="MinPrefixLength"/> floor stops the prefix rule becoming a
      /// wildcard: without it "ip" matches "ipranges", "ipv6" and "important", so
      /// a search for an IP address range returns most of the application.
      ///
      /// The <see cref="MaxInflectionLength"/> bound applies only when the query
      /// word is the longer one, and exists because the rule was symmetric to
      /// begin with. That made searching for the INI key "LogDeleteDays" match the
      /// setting labelled "Log destination" - "logdeletedays" starts with "log" -
      /// and that eleven-word-token match outranked the exact hit on the key
      /// itself, so typing a setting's own identifier took you to a different
      /// setting. A query word that is ten characters longer than the candidate is
      /// not an inflection of it.
      /// </summary>
      public static bool TokenMatches(string queryToken, string candidateToken)
      {
         if (queryToken == null || candidateToken == null)
            return false;

         if (string.Equals(queryToken, candidateToken, StringComparison.Ordinal))
            return true;

         int shorter = Math.Min(queryToken.Length, candidateToken.Length);
         if (shorter < MinPrefixLength)
            return false;

         if (queryToken.Length < candidateToken.Length)
            return candidateToken.StartsWith(queryToken, StringComparison.Ordinal);

         return queryToken.Length - candidateToken.Length <= MaxInflectionLength
            && queryToken.StartsWith(candidateToken, StringComparison.Ordinal);
      }

      /// <summary>
      /// Whether every character of <paramref name="query"/> occurs in
      /// <paramref name="candidate"/> in order. Both arguments must already be
      /// normalised.
      /// </summary>
      public static bool IsSubsequence(string query, string candidate)
      {
         if (string.IsNullOrEmpty(query))
            return true;

         int index = 0;
         foreach (char c in query)
         {
            index = candidate.IndexOf(c, index);
            if (index < 0)
               return false;
            index++;
         }

         return true;
      }
   }

   /// <summary>
   /// A query normalised once and then scored against many candidates. The
   /// palette re-runs the whole search on every keystroke against roughly 300
   /// candidates with two or three searchable fields each, so the query side of
   /// the work is done once here rather than once per candidate.
   /// </summary>
   public sealed class SearchQuery
   {
      private readonly List<string> tokens_;

      public SearchQuery(string text)
      {
         Text = SearchTerms.Normalize(text);
         tokens_ = SearchTerms.Tokenize(Text);
      }

      /// <summary>The normalised query: lower case, single spaced, no punctuation.</summary>
      public string Text { get; }

      /// <summary>The comparable words of the query; empty when it is all stop words.</summary>
      public IReadOnlyList<string> Tokens => tokens_;

      /// <summary>True when there is nothing to search for.</summary>
      public bool IsEmpty => Text.Length == 0;

      /// <summary>
      /// Scores <paramref name="candidate"/>, or <see cref="SearchTerms.NoMatch"/>.
      /// </summary>
      public int Score(string candidate)
      {
         if (IsEmpty)
            return SearchTerms.NoMatch;

         string normalized = SearchTerms.Normalize(candidate);
         if (normalized.Length == 0)
            return SearchTerms.NoMatch;

         if (string.Equals(normalized, Text, StringComparison.Ordinal))
            return SearchTerms.Exact;

         if (normalized.StartsWith(Text, StringComparison.Ordinal))
            return SearchTerms.Prefix;

         int matched = 0;

         // Which single query word failed to match, when exactly one did. The partial
         // rule below forgives a missing ACTION verb ("reduce spam") but not a missing
         // topic ("spam ports"), and it needs to know which word was missing to tell
         // those apart.
         string soleUnmatchedToken = null;

         if (tokens_.Count > 0)
         {
            List<string> candidateTokens = SearchTerms.Tokenize(normalized);

            // Every query word is counted, not just the ones before the first
            // miss, because the partial rank below needs to know how many matched
            // rather than merely whether all of them did.
            foreach (string queryToken in tokens_)
            {
               bool hit = false;

               foreach (string candidateToken in candidateTokens)
               {
                  if (SearchTerms.TokenMatches(queryToken, candidateToken))
                  {
                     matched++;
                     hit = true;
                     break;
                  }
               }

               if (!hit)
                  soleUnmatchedToken = soleUnmatchedToken == null ? queryToken : null;
            }

            if (matched == tokens_.Count)
            {
               // Prefer the candidate the query accounts for most of, so that
               // "spam" ranks "Anti-spam settings" above "Bypass on SPF
               // success" purely on how much else the candidate is about.
               int spread = Math.Max(0, candidateTokens.Count - tokens_.Count);
               return SearchTerms.AllTokens + Math.Min(SearchTerms.TokenSpreadPenalty, spread);
            }
         }

         if (normalized.IndexOf(Text, StringComparison.Ordinal) >= 0)
            return SearchTerms.Substring;

         if (matched >= SearchTerms.MinPartialTokens && matched * 3 >= tokens_.Count * 2)
            return SearchTerms.MostTokens + (tokens_.Count - matched);

         // Two-word queries used to be unreachable here, and two words is the commonest
         // length a person types. For a two-token query the branch above needs matched
         // >= 2, and matched == 2 is matched == tokens_.Count, which the all-tokens
         // branch has already returned - so the partial rule could never fire, and
         // adding one ordinary verb to a working one-word query returned nothing at all.
         // "spam" worked and "reduce spam" did not; so did "enable imap", "clear queue",
         // "new domain", "add route", "schedule backup" and nine others measured.
         //
         // A single match out of two is accepted only when the word that did NOT match
         // is an action verb - what the user wants done, rather than what it is done to.
         // "reduce spam" therefore finds the spam pages, while "spam ports" still finds
         // nothing, because dropping either half of that one changes the question. A
         // first attempt keyed this on the matched word being four characters or longer
         // and was too loose: it accepted "spam ports" against "TCP/IP ports", which an
         // existing test correctly refuses.
         //
         // Scored below MostTokens so that anything matching more of the query outranks
         // it, and the query is guaranteed to have exactly one unmatched word here, so
         // soleUnmatchedToken is the word in question.
         if (tokens_.Count == 2 && matched == 1 && SearchTerms.IsActionVerb(soleUnmatchedToken))
            return SearchTerms.MostTokens + tokens_.Count;

         return SearchTerms.NoMatch;
      }

      /// <summary>
      /// <see cref="Score"/> plus a character-subsequence fallback, for
      /// candidates where typing initials is a reasonable thing to do (page and
      /// group names). Not used for setting labels: a subsequence match across
      /// 244 of them returns almost all of them.
      /// </summary>
      public int ScoreLoose(string candidate)
      {
         // Guarded before the subsequence test rather than relying on Score's
         // guard: the empty string is a subsequence of everything, so without this
         // an empty query would match every candidate at the subsequence rank.
         if (IsEmpty)
            return SearchTerms.NoMatch;

         int score = Score(candidate);
         if (score != SearchTerms.NoMatch)
            return score;

         string normalized = SearchTerms.Normalize(candidate);
         return normalized.Length > 0 && SearchTerms.IsSubsequence(Text, normalized)
            ? SearchTerms.Subsequence
            : SearchTerms.NoMatch;
      }

      /// <summary>Best (lowest) score over several candidate strings.</summary>
      public int BestScore(IEnumerable<string> candidates)
      {
         int best = SearchTerms.NoMatch;
         if (candidates == null)
            return best;

         foreach (string candidate in candidates)
         {
            int score = Score(candidate);
            if (score < best)
               best = score;
         }

         return best;
      }
   }
}
