// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The translation lookup and the catalogues it reads. Loc is the one path
   /// every caption in the Control Panel takes, so the rules pinned here are the
   /// rules the whole interface follows: English is never looked up, a text the
   /// catalogue lacks comes back in English, a failing catalogue is survived, and
   /// the Swedish catalogue committed beside the source agrees with the English
   /// one about placeholders and Alt-key mnemonics.
   /// </summary>
   [Collection("Loc")]
   public class LocTests : IDisposable
   {
      private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv");

      public void Dispose() => Loc.Reset();

      [Fact]
      public void EnglishIsNeverLookedUp()
      {
         int calls = 0;
         Loc.Use((text, culture) => { calls++; return "translated"; }, CultureInfo.GetCultureInfo("en-GB"));

         Assert.Equal("_Save changes", Loc.L("_Save changes"));
         Assert.Equal(0, calls);
      }

      [Fact]
      public void ATranslationIsReturnedForTheCultureInForce()
      {
         var catalogue = new Dictionary<string, string> { ["_Save changes"] = "_Spara ändringar" };
         Loc.Use((text, culture) => catalogue.TryGetValue(text, out string value) ? value : null, Swedish);

         Assert.Equal("_Spara ändringar", Loc.L("_Save changes"));
         Assert.Same(Swedish, Loc.Culture);
      }

      [Fact]
      public void ATextTheCatalogueLacksComesBackInEnglish()
      {
         Loc.Use((text, culture) => text == "known" ? "" : null, Swedish);

         Assert.Equal("Not in the catalogue", Loc.L("Not in the catalogue"));
         Assert.Equal("known", Loc.L("known"));   // empty is "no translation", not "blank caption"
         Assert.Null(Loc.L(null));
         Assert.Equal("", Loc.L(""));
      }

      [Fact]
      public void ACatalogueThatThrowsIsSurvivedAndNotAskedAgain()
      {
         int calls = 0;
         Loc.Use((text, culture) => { calls++; throw new InvalidOperationException("no satellite"); }, Swedish);

         Assert.Equal("Domains", Loc.L("Domains"));
         Assert.Equal("Accounts", Loc.L("Accounts"));
         Assert.Equal(1, calls);
      }

      [Fact]
      public void FormatTranslatesThenFormatsWithTheUsersCulture()
      {
         Loc.Use((text, culture) => text == "Deleted {0} messages" ? "Tog bort {0} meddelanden" : null, Swedish);

         Assert.Equal("Tog bort 3 meddelanden", Loc.F("Deleted {0} messages", 3));
         Assert.Equal("Untranslated 3", Loc.F("Untranslated {0}", 3));
      }

      [Fact]
      public void MarkingOnlyLeavesTheTextAlone()
      {
         Loc.Use((text, culture) => "översatt", Swedish);

         Assert.Equal("Welcome", Loc.N("Welcome"));
         Assert.Equal("översatt", Loc.L(Loc.N("Welcome")));
      }

      [Theory]
      [InlineData("", true)]
      [InlineData("en", true)]
      [InlineData("en-US", true)]
      [InlineData("sv", false)]
      [InlineData("sv-SE", false)]
      public void EnglishIsRecognisedByLanguageNotRegion(string tag, bool english)
      {
         CultureInfo culture = tag.Length == 0 ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(tag);
         Assert.Equal(english, Loc.IsEnglish(culture));
      }

      [Fact]
      public void AStoredTagResolvesToItsCultureAndNonsenseToEnglish()
      {
         Assert.Equal("sv", Loc.Resolve("sv").Name);
         Assert.Equal("sv", Loc.Resolve(" sv ").Name);
         Assert.Equal("en", Loc.Resolve("no-such-language-zz").Name);
         Assert.Equal(CultureInfo.InstalledUICulture, Loc.Resolve(""));
         Assert.Equal(CultureInfo.InstalledUICulture, Loc.Resolve(null));
      }

      [Fact]
      public void EveryOfferedLanguageHasACatalogueOrIsEnglishOrWindows()
      {
         string resources = ResourcesDirectory();
         if (resources == null)
            return;   // sources not available (a packaged drop)

         // The empty tag is "follow Windows" and English needs no catalogue of its own,
         // so neither is asked for a .resx. The order of the two tests matters: the tag
         // has to be non-empty before GetCultureInfo is handed it.
         foreach (Loc.Language language in Loc.Languages.Where(l => l.Tag.Length != 0 && !Loc.IsEnglish(CultureInfo.GetCultureInfo(l.Tag))))
         {
            Assert.True(File.Exists(Path.Join(resources, "Strings." + language.Tag + ".resx")),
               "The language picker offers '" + language.NativeName + "' and no Strings." + language.Tag + ".resx exists.");
         }
      }

      /// <summary>
      /// The Swedish catalogue against the English one: every key it holds is a
      /// key the English holds; every value keeps the placeholders of its key;
      /// a key with an Alt-key mnemonic is translated with one. The same rules
      /// build/check-localisation.py applies in CI, kept here as well so that a
      /// plain `dotnet test` catches a broken translation on the machine it was
      /// typed on.
      /// </summary>
      [Fact]
      public void TheSwedishCatalogueAgreesWithTheEnglishOne()
      {
         string resources = ResourcesDirectory();
         if (resources == null)
            return;

         Dictionary<string, string> english = ReadResx(Path.Join(resources, "Strings.resx"));
         Dictionary<string, string> swedish = ReadResx(Path.Join(resources, "Strings.sv.resx"));

         Assert.NotEmpty(english);
         Assert.All(english, pair => Assert.Equal(pair.Key, pair.Value));

         var placeholders = new Regex(@"\{(\d+)(?:[:,][^}]*)?\}");
         var problems = new List<string>();
         foreach ((string key, string value) in swedish)
         {
            if (!english.ContainsKey(key))
               problems.Add($"orphan: {key}");
            if (value.Length == 0)
               continue;
            var wanted = placeholders.Matches(key).Select(m => m.Groups[1].Value).ToHashSet();
            var found = placeholders.Matches(value).Select(m => m.Groups[1].Value).ToHashSet();
            if (!wanted.SetEquals(found))
               problems.Add($"placeholders differ: {key} -> {value}");
            if (MnemonicText.Key(key) != null && MnemonicText.Key(value) == null)
               problems.Add($"mnemonic lost: {key} -> {value}");
         }

         Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
      }

      private static Dictionary<string, string> ReadResx(string path)
      {
         var result = new Dictionary<string, string>(StringComparer.Ordinal);
         foreach (XElement data in XDocument.Load(path).Root.Elements("data"))
            result[(string)data.Attribute("name")] = data.Element("value")?.Value ?? "";
         return result;
      }

      private static string ResourcesDirectory()
      {
         string dir = AppContext.BaseDirectory;
         for (int i = 0; i < 8 && dir != null; i++)
         {
            string candidate = Path.Join(dir, "ControlPanel", "Resources");
            if (File.Exists(Path.Join(candidate, "Strings.resx")))
               return candidate;
            dir = Path.GetDirectoryName(dir);
         }
         return null;
      }
   }
}
