// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   /// <summary>
   ///    Static assertions about the generated Public Suffix List data
   ///    (PublicSuffixListData.h) that DMARC::GetOrganizationalDomain resolves
   ///    organizational domains against.
   ///
   ///    The org-domain computation could not be exercised end-to-end when this
   ///    fixture was written: it sits behind DMARC policy discovery, which needs a
   ///    DNS server that answers TXT queries for domains the test controls, and the
   ///    harness had no such server (CustomDnsServer.cs only proves a lookup is
   ///    dispatched). It has one now - FakeDnsServer - and DmarcTreeWalkResolution.cs
   ///    uses it to drive the RFC 9989 tree walk that has since become the primary
   ///    mechanism. This fixture keeps the OTHER half: the Public Suffix List is
   ///    still what answers when the tree walk is switched off and, more importantly,
   ///    whenever a lookup fails transiently - so its data is still load-bearing on
   ///    every DMARC evaluation made during a resolver outage.
   ///
   ///    What is pinned here is that data - what the C++ binary-searches, and the
   ///    semantics it is meant to produce - and the data is where the historical
   ///    defects lived, because it is regenerated wholesale from publicsuffix.org
   ///    (build\generate-public-suffix-list.ps1) and a bad regeneration would be
   ///    committed once and trusted for years.
   ///
   ///    Two of these tests guard failure modes that would not fail loudly
   ///    anywhere else:
   ///
   ///    * An array that is not strictly sorted in ordinal (wcscmp) order does not
   ///      break the build or crash - std::binary_search just stops finding
   ///      entries at random. Every entry it stops finding is a public suffix the
   ///      server no longer knows, and every one of those puts relaxed alignment
   ///      back where it was before this data existed: anyone holding a name
   ///      under the suffix can forge any other name under it past a p=reject.
   ///
   ///    * A truncated or single-section regeneration looks exactly like a
   ///      successful one - the header compiles, the arrays are sorted, lookups
   ///      succeed - it is just missing thousands of rules, with the same
   ///      consequence per missing rule.
   ///
   ///    The remaining tests mirror the C++ lookup over the parsed data and walk
   ///    the specific domains whose behaviour motivated replacing the old
   ///    heuristic, one per historical error direction, so a regression in either
   ///    direction names the bypass it reopens.
   /// </summary>
   [TestFixture]
   public class DmarcOrganizationalDomain : TestFixtureBase
   {
      // TestDirectory is <repo>\hmailserver\test\RegressionTests\bin\x64\Debug.
      // Six levels up is the repository root - the same relative walk
      // Installation\InstallerPackaging.cs uses.
      private static string RepositoryRoot
      {
         get
         {
            return Path.GetFullPath(Paths.Combine(TestContext.CurrentContext.TestDirectory,
                                                 @"..\..\..\..\..\.."));
         }
      }

      private static string GeneratedHeaderPath
      {
         get
         {
            return Paths.Combine(RepositoryRoot,
               @"hmailserver\source\Server\Common\AntiSpam\DMARC\PublicSuffixListData.h");
         }
      }

      private sealed class PublicSuffixData
      {
         public List<string> NormalRules;
         public List<string> WildcardRules;
         public List<string> ExceptionRules;
         public int MaxRuleLabels;

         public HashSet<string> NormalSet;
         public HashSet<string> WildcardSet;
         public HashSet<string> ExceptionSet;
      }

      private static readonly Lazy<PublicSuffixData> Data =
         new Lazy<PublicSuffixData>(LoadGeneratedHeader);

      /// <summary>
      ///    Parses one generated array: the lines between
      ///    'const wchar_t* const NAME[] =' and '};', each of the form L"...",
      ///    with non-ASCII characters as \uXXXX or \UXXXXXXXX universal-character-
      ///    names - the only escapes the generator emits.
      /// </summary>
      private static List<string> ParseArray(string[] lines, string name)
      {
         var result = new List<string>();
         bool inArray = false;

         foreach (string line in lines.Select(rawLine => rawLine.Trim()))
         {
            if (line == "const wchar_t* const " + name + "[] =")
            {
               inArray = true;
               continue;
            }

            if (!inArray)
               continue;

            if (line == "};")
               return result;

            var match = Regex.Match(line, "^L\"(.*)\",$");
            if (!match.Success)
               continue;

            string value = Regex.Replace(match.Groups[1].Value,
               @"\\u([0-9A-Fa-f]{4})|\\U([0-9A-Fa-f]{8})",
               m => m.Groups[1].Success
                  ? ((char) int.Parse(m.Groups[1].Value, NumberStyles.HexNumber)).ToString()
                  : char.ConvertFromUtf32(int.Parse(m.Groups[2].Value, NumberStyles.HexNumber)));

            result.Add(value);
         }

         Assert.Fail("Array " + name + " not found (or not terminated) in " + GeneratedHeaderPath);
         return null;
      }

      private static PublicSuffixData LoadGeneratedHeader()
      {
         ClassicAssert.IsTrue(File.Exists(GeneratedHeaderPath),
            "Generated header missing: " + GeneratedHeaderPath +
            " - run build\\generate-public-suffix-list.ps1.");

         string[] lines = File.ReadAllLines(GeneratedHeaderPath);

         var data = new PublicSuffixData
         {
            NormalRules = ParseArray(lines, "NormalRules"),
            WildcardRules = ParseArray(lines, "WildcardRules"),
            ExceptionRules = ParseArray(lines, "ExceptionRules")
         };

         var maxLine = lines.Select(l => Regex.Match(l, @"const size_t MaxRuleLabels = (\d+);"))
                            .FirstOrDefault(m => m.Success);
         ClassicAssert.IsNotNull(maxLine, "MaxRuleLabels constant not found in the generated header.");
         data.MaxRuleLabels = int.Parse(maxLine.Groups[1].Value);

         data.NormalSet = new HashSet<string>(data.NormalRules, StringComparer.Ordinal);
         data.WildcardSet = new HashSet<string>(data.WildcardRules, StringComparer.Ordinal);
         data.ExceptionSet = new HashSet<string>(data.ExceptionRules, StringComparer.Ordinal);

         return data;
      }

      [Test]
      [Description("Every generated array is strictly sorted in ordinal (wcscmp) order - the std::binary_search contract. Guards a currently-correct invariant; a violation makes lookups miss entries silently, which reopens forgery bypasses.")]
      public void GeneratedArraysAreStrictlySortedForBinarySearch()
      {
         var data = Data.Value;

         foreach (var pair in new[]
         {
            new KeyValuePair<string, List<string>>("NormalRules", data.NormalRules),
            new KeyValuePair<string, List<string>>("WildcardRules", data.WildcardRules),
            new KeyValuePair<string, List<string>>("ExceptionRules", data.ExceptionRules)
         })
         {
            List<string> rules = pair.Value;

            for (int i = 1; i < rules.Count; i++)
            {
               // Strictly greater: equal neighbours are duplicates, and a
               // duplicate means the generator's dedupe broke, which is worth
               // knowing even though binary_search itself would tolerate it.
               ClassicAssert.Less(string.CompareOrdinal(rules[i - 1], rules[i]), 0,
                  pair.Key + " is not strictly sorted at index " + i + ": \"" + rules[i - 1] +
                  "\" >= \"" + rules[i] + "\". std::binary_search over this array misses entries " +
                  "silently, and every missed public suffix lets its registrants forge each other " +
                  "under DMARC relaxed alignment.");
            }
         }
      }

      [Test]
      [Description("The generated tables have plausible sizes, contain both PSL sections, and declare a MaxRuleLabels that matches the data - guards against a truncated or single-section regeneration being committed.")]
      public void GeneratedTablesAreCompleteAndInternallyConsistent()
      {
         var data = Data.Value;

         // The July 2026 list produced 10,409 / 281 / 8 entries. These floors are
         // far below that on purpose: they exist to catch a half-written file or a
         // dropped section, not to break whenever ccTLD registries reorganize.
         ClassicAssert.Greater(data.NormalRules.Count, 5000,
            "Far fewer normal rules than the real Public Suffix List contains - the header looks truncated.");
         ClassicAssert.Greater(data.WildcardRules.Count, 100,
            "Far fewer wildcard rules than the real Public Suffix List contains - the header looks truncated.");
         ClassicAssert.GreaterOrEqual(data.ExceptionRules.Count, 5,
            "Almost no exception rules - the header looks truncated.");

         // One well-known entry from each PSL section: 'uk' can only come from the
         // ICANN section and 'github.io' only from the PRIVATE section, so both
         // being present proves neither section was dropped.
         ClassicAssert.IsTrue(data.NormalSet.Contains("uk"), "ICANN-section rule 'uk' is missing.");
         ClassicAssert.IsTrue(data.NormalSet.Contains("github.io"),
            "PRIVATE-section rule 'github.io' is missing. Without the PRIVATE section, every tenant " +
            "of a shared-hosting domain DMARC-aligns as every other tenant.");

         // IDN rules must be present in both forms, because mail carries domains
         // in ACE (xn--) form but EAI mail can carry raw UTF-8.
         ClassicAssert.IsTrue(data.NormalSet.Contains("xn--p1ai"),
            "The ACE (punycode) form of an IDN rule is missing - IDN suffixes would not match the " +
            "form DNS actually transports.");
         // Cyrillic r-f (U+0440 U+0444), the Unicode twin of xn--p1ai - built
         // from char codes so this file stays pure ASCII and its meaning cannot
         // depend on how a compiler guesses the source encoding.
         string cyrillicRf = new string(new[] { (char) 0x0440, (char) 0x0444 });
         ClassicAssert.IsTrue(data.NormalSet.Contains(cyrillicRf),
            "The Unicode form of an IDN rule is missing - raw-UTF-8 (EAI) domains would not match.");

         // The C++ lookup stops probing at MaxRuleLabels, so a declared value
         // smaller than the data makes the longest rules unreachable - silently.
         int actualMax = 0;
         foreach (string rule in data.NormalRules) actualMax = Math.Max(actualMax, rule.Split('.').Length);
         foreach (string rule in data.ExceptionRules) actualMax = Math.Max(actualMax, rule.Split('.').Length);
         foreach (string rule in data.WildcardRules) actualMax = Math.Max(actualMax, rule.Split('.').Length + 1);

         ClassicAssert.AreEqual(actualMax, data.MaxRuleLabels,
            "MaxRuleLabels does not match the longest rule actually present. Too small and the longest " +
            "rules can never match; too large is wasted probes per message.");
      }

      [Test]
      [Description("The suffixes whose absence from the old heuristic made their registrants mutually forgeable are present, including wildcard and exception rules. Each entry here is a specific closed bypass.")]
      public void HistoricalUnderMatchBypassesAreClosed()
      {
         var data = Data.Value;

         // Multi-label suffixes whose second label is not a registry word: the old
         // shape rule could never infer these, so org("bank.my.id") == org("attacker.my.id")
         // == "my.id" and relaxed alignment passed between strangers.
         foreach (string suffix in new[]
         {
            "com.pt", "my.id", "gr.jp", "ad.jp", "ed.jp", "lg.jp",
            "sc.ke", "me.ke", "art.br", "eco.br", "co.uk"
         })
         {
            ClassicAssert.IsTrue(data.NormalSet.Contains(suffix),
               "Public suffix '" + suffix + "' is missing from the normal-rule table. Every registrant " +
               "under it can forge every other one past a p=reject under relaxed alignment.");
         }

         // Wildcard rules, stored without their '*.' prefix. *.ck and *.kobe.jp
         // are the canonical PSL wildcard cases; if these are missing, the whole
         // wildcard mechanism has most likely been dropped, not just two entries.
         ClassicAssert.IsTrue(data.WildcardSet.Contains("ck"), "Wildcard rule '*.ck' is missing.");
         ClassicAssert.IsTrue(data.WildcardSet.Contains("kobe.jp"), "Wildcard rule '*.kobe.jp' is missing.");

         // Exception rules, stored without their '!'.
         ClassicAssert.IsTrue(data.ExceptionSet.Contains("www.ck"), "Exception rule '!www.ck' is missing.");
         ClassicAssert.IsTrue(data.ExceptionSet.Contains("city.kobe.jp"), "Exception rule '!city.kobe.jp' is missing.");
      }

      [Test]
      [Description("Names that are registrants rather than registry suffixes are NOT in the tables. The old shape rule invented suffixes like gov.je, which turned relaxed alignment strict and failed correctly-signed mail.")]
      public void RegistrantNamesAreNotTreatedAsSuffixes()
      {
         var data = Data.Value;

         // 'gen.ai' (a registrant under the flat .ai registry) and 'gov.je' (a
         // registry-word label the shape rule wrongly promoted, since .je only
         // delegates co/net/org). If either shows up in any table, someone has
         // hand-edited the generated header - the list itself contains neither.
         foreach (string name in new[] { "gen.ai", "gov.je" })
         {
            ClassicAssert.IsFalse(
               data.NormalSet.Contains(name) || data.WildcardSet.Contains(name) || data.ExceptionSet.Contains(name),
               "'" + name + "' is a registrant's domain, not a public suffix, but it appears in the " +
               "generated tables. Its owner's subdomain mail now fails relaxed alignment - rejecting " +
               "real mail, the worse error direction.");
         }
      }

      /// <summary>
      ///    The same algorithm PublicSuffixList.cpp implements, over the parsed
      ///    data: probe every trailing-label candidate against all three tables,
      ///    keep the longest match of each kind, let exceptions beat everything,
      ///    fall back to the implicit '*' rule. Kept in lockstep deliberately - if
      ///    the C++ changes semantics, this mirror is the place that documents the
      ///    old meaning and fails.
      /// </summary>
      private static int GetPublicSuffixLabelCount(string[] labels, PublicSuffixData data)
      {
         int labelCount = labels.Length;
         int maxCandidateLabels = Math.Min(labelCount, data.MaxRuleLabels);

         int longestExceptionMatch = 0;
         int longestListMatch = 0;

         string candidate = "";
         string previousCandidate;

         for (int count = 1; count <= maxCandidateLabels; count++)
         {
            previousCandidate = candidate;
            candidate = count == 1
               ? labels[labelCount - 1]
               : labels[labelCount - count] + "." + candidate;

            if (data.ExceptionSet.Contains(candidate))
               longestExceptionMatch = count;

            if (data.NormalSet.Contains(candidate))
               longestListMatch = count;

            if (count >= 2 && data.WildcardSet.Contains(previousCandidate))
               longestListMatch = count;
         }

         if (longestExceptionMatch > 1)
            return longestExceptionMatch - 1;

         if (longestListMatch > 0)
            return longestListMatch;

         return 1;
      }

      private static string GetOrganizationalDomain(string domain, PublicSuffixData data)
      {
         string lower = domain.ToLowerInvariant().TrimEnd('.');
         string[] labels = lower.Split('.');

         if (labels.Length <= 1)
            return lower;

         int suffixLabels = GetPublicSuffixLabelCount(labels, data);

         int organizationalLabels = suffixLabels + 1;
         if (labels.Length <= organizationalLabels)
            return lower;

         return string.Join(".", labels.Skip(labels.Length - organizationalLabels));
      }

      [Test]
      [Description("Walks the domains that motivated the PSL work through the lookup semantics over the real generated data - one case per historical error direction, plus the wildcard/exception machinery.")]
      public void OrganizationalDomainsMatchThePublicSuffixList()
      {
         var data = Data.Value;

         var cases = new Dictionary<string, string>
         {
            // Under-match direction, closed: with these suffixes missing, the org
            // domain collapsed to the bare suffix and strangers aligned with each
            // other. Now each registrant is their own organizational domain.
            {"bank.com.pt", "bank.com.pt"},
            {"alice.my.id", "alice.my.id"},
            {"bob.my.id", "bob.my.id"},
            {"news.art.br", "news.art.br"},

            // A domain that IS a public suffix is returned unchanged, so a sender
            // claiming the bare suffix aligns with nothing beneath it.
            {"gr.jp", "gr.jp"},
            {"sub.host.gr.jp", "host.gr.jp"},

            // Over-match direction, closed: gov.je is a registrant (.je delegates
            // only co/net/org), so its subdomains collapse to it and relaxed
            // alignment works. The old shape rule made mail.gov.je its own org
            // domain and correctly-signed mail failed.
            {"mail.gov.je", "gov.je"},
            {"mail.gen.ai", "gen.ai"},

            // The everyday case that must never regress.
            {"mail.example.co.uk", "example.co.uk"},
            {"example.co.uk", "example.co.uk"},

            // A flat ccTLD: only the TLD is a suffix, however deep the name.
            {"a.b.c.de", "c.de"},

            // co.io is in the PSL's ICANN section (the .io registry operates it),
            // so mail.co.io is its own organizational domain - what looks like the
            // shape rule's over-match is, for this name, what the registry says.
            {"mail.co.io", "mail.co.io"},

            // Wildcard rules: *.ck makes y.ck a suffix, so x.y.ck is the
            // registrant and deeper labels collapse to it.
            {"x.y.ck", "x.y.ck"},
            {"foo.x.y.ck", "x.y.ck"},

            // Exception rules: !www.ck carves www.ck back out of *.ck, and
            // !city.kobe.jp out of *.kobe.jp.
            {"www.ck", "www.ck"},
            {"sub.www.ck", "www.ck"},
            {"a.city.kobe.jp", "city.kobe.jp"},
            {"x.b.kobe.jp", "x.b.kobe.jp"},

            // PRIVATE section: each github.io tenant is their own organization.
            {"deep.user.github.io", "user.github.io"},

            // A TLD the list does not know falls back to the implicit '*' rule
            // rather than failing: registrant plus TLD.
            {"a.b.example.unknowntld", "example.unknowntld"},

            // IDN in the ACE form DNS transports.
            {"sub.example.xn--p1ai", "example.xn--p1ai"}
         };

         foreach (var testCase in cases)
         {
            string actual = GetOrganizationalDomain(testCase.Key, data);
            ClassicAssert.AreEqual(testCase.Value, actual,
               "GetOrganizationalDomain(\"" + testCase.Key + "\") should be \"" + testCase.Value +
               "\" per the Public Suffix List, but the generated data yields \"" + actual + "\".");
         }
      }
   }
}
