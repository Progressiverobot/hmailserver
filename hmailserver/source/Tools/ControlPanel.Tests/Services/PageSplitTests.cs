using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// Splitting "Auto-ban &amp; SSL/TLS" into two pages.
   ///
   /// The roadmap's words for that page were "two unrelated concerns in one page …
   /// the title is an admission that the page is a bucket". Brute-force lockout and
   /// transport encryption share nothing: you arrive at one of them because somebody
   /// is guessing passwords and at the other because a client cannot negotiate a
   /// cipher, and whichever you came for, half the page was noise.
   ///
   /// A split is the one change to this navigation that cannot keep a page title, so
   /// it is also the change most likely to quietly break something. These tests pin
   /// the four things that keep it honest: both halves exist and live in the group
   /// their subject belongs to; the settings went with the half that owns them; the
   /// old title and the documented path still lead somewhere useful; and the view
   /// really was split rather than the navigation being relabelled over an unchanged
   /// page.
   ///
   /// Every test here fails against the tree before the split, and not merely for
   /// want of a type: "autoban" is not a page, the four auto-ban settings are indexed
   /// against "tls", BuildTls still builds the Auto-ban card, and the page title
   /// names both subjects at once.
   /// </summary>
   public class PageSplitTests
   {
      [Fact]
      public void BothHalves_ExistAndSitInTheGroupTheirSubjectBelongsTo()
      {
         NavNode autoban = NavigationMap.Find("autoban");
         NavNode tls = NavigationMap.Find("tls");

         Assert.True(autoban != null, "There is no 'autoban' page, so the brute-force half of the split is unreachable.");
         Assert.True(tls != null, "There is no 'tls' page.");

         Assert.Equal("Access & abuse protection", NavigationMap.GroupOf("autoban"));
         Assert.Equal("TLS & certificates", NavigationMap.GroupOf("tls"));
      }

      /// <summary>
      /// The defect, stated as an assertion. A page title naming two subjects is the
      /// symptom the roadmap named, and it is easy to reintroduce by merging a page
      /// back in and giving it an "X &amp; Y" title.
      /// </summary>
      [Fact]
      public void NeitherHalf_IsTitledAfterTwoSubjects()
      {
         string autoban = NavigationMap.TitleOf("autoban");
         string tls = NavigationMap.TitleOf("tls");

         Assert.DoesNotContain("TLS", autoban, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("SSL", autoban, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("ban", tls, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("logon", tls, StringComparison.OrdinalIgnoreCase);
      }

      /// <summary>
      /// The price of the one title change. Both halves answer to the old name, so a
      /// forum answer or a runbook naming it still finds the page its reader wanted -
      /// and both are offered, because the text cannot tell which half they meant.
      /// </summary>
      [Fact]
      public void TheOldCombinedTitle_OffersBothHalves()
      {
         List<string> pages = PaletteSearch.Query("Auto-ban & SSL/TLS", null, null)
            .Where(r => r.IsSelectable)
            .Take(4)
            .Select(r => r.Page)
            .ToList();

         Assert.Contains("autoban", pages);
         Assert.Contains("tls", pages);
      }

      [Fact]
      public void TheDocumentedPath_StillResolves_AndBothHalvesHaveOneOfTheirOwn()
      {
         Assert.Equal("autoban", NavigationMap.LegacyPaths["Settings > Security > Auto-ban & SSL/TLS"]);
         Assert.Equal("autoban", NavigationMap.LegacyPaths["Settings > Security > Auto-ban"]);
         Assert.Equal("tls", NavigationMap.LegacyPaths["Settings > Security > SSL/TLS"]);
      }

      /// <summary>
      /// A subject that spans two pages stays navigable through signposts rather than
      /// by putting a page in two branches of the tree - the rule the TLS group
      /// already follows. Here it also serves the reader who followed the old title
      /// and landed on the wrong half of it.
      /// </summary>
      [Fact]
      public void EachHalf_SignpostsTheOther()
      {
         Assert.Contains("tls", NavigationMap.Find("autoban").SeeAlso);
         Assert.Contains("autoban", NavigationMap.Find("tls").SeeAlso);
      }

      /// <summary>
      /// The settings moved with the page. The palette's whole value is that picking
      /// a setting opens the page hosting it; an index entry left pointing at the
      /// page a setting used to be on sends the reader somewhere the control is not,
      /// which is worse than not finding it.
      /// </summary>
      [Theory]
      [InlineData("AutoBanOnLogonFailure", "autoban")]
      [InlineData("MaxInvalidLogonAttempts", "autoban")]
      [InlineData("MaxInvalidLogonAttemptsWithin", "autoban")]
      [InlineData("AutoBanMinutes", "autoban")]
      [InlineData("TlsVersion12Enabled", "tls")]
      [InlineData("TlsVersion13Enabled", "tls")]
      [InlineData("SslCipherList", "tls")]
      [InlineData("TlsCipherSuites13", "tls")]
      [InlineData("TlsOptionPreferServerCiphersEnabled", "tls")]
      [InlineData("VerifyRemoteSslCertificate", "tls")]
      public void EachSetting_IsIndexedAgainstTheHalfThatShowsIt(string key, string page)
      {
         SettingEntry entry = SettingsSearchIndex.Entries
            .FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

         Assert.True(entry != null, "'" + key + "' fell out of the settings index. Re-run build/generate-settings-index.ps1.");
         Assert.Equal(page, entry.Page);
      }

      /// <summary>
      /// The view was split too, not just the navigation over it.
      ///
      /// Source scanning, for the reason SettingClaimsTests gives: these are string
      /// literals inside a WPF view that the test project cannot reference, and a
      /// navigation entry pointing at a page that still shows both subjects would
      /// pass every other test here.
      /// </summary>
      [Fact]
      public void TheTwoPageBuilders_EachOwnOneSubject()
      {
         string source = ReadServerSettingsView();
         if (source == null)
            return;   // sources not available (e.g. running from a packaged drop)

         string tls = BuilderBody(source, "BuildTls");
         string autoban = BuilderBody(source, "BuildAutoBan");

         Assert.True(tls != null, "BuildTls is gone from ServerSettingsView.");
         Assert.True(autoban != null,
            "ServerSettingsView has no BuildAutoBan, so the 'autoban' page cannot be the auto-ban settings.");

         foreach (string key in new[] { "AutoBanOnLogonFailure", "MaxInvalidLogonAttempts", "AutoBanMinutes" })
         {
            Assert.Contains(key, autoban, StringComparison.Ordinal);
            Assert.DoesNotContain(key, tls, StringComparison.Ordinal);
         }

         foreach (string key in new[] { "TlsVersion12Enabled", "SslCipherList", "VerifyRemoteSslCertificate" })
         {
            Assert.Contains(key, tls, StringComparison.Ordinal);
            Assert.DoesNotContain(key, autoban, StringComparison.Ordinal);
         }
      }

      /// <summary>
      /// The words an administrator types when somebody is attacking them. Every one
      /// of these used to land on a page that was half cipher configuration; each has
      /// to open the auto-ban page and open it first, because a first result that is
      /// nearly right still costs a decision.
      /// </summary>
      [Theory]
      [InlineData("brute force")]
      [InlineData("locked out")]
      [InlineData("fail2ban")]
      [InlineData("hammering")]
      [InlineData("password guessing")]
      public void BruteForceWords_OpenTheAutoBanPageFirst(string query)
      {
         IReadOnlyList<PaletteRow> rows = PaletteSearch.Query(query, null, null);
         int first = PaletteSearch.FirstSelectable(rows);

         Assert.True(first >= 0, "'" + query + "' matched nothing at all.");
         Assert.Equal("autoban", rows[first].Page);
      }

      /// <summary>
      /// ...and the TLS words still reach the TLS half. "ssl" deliberately only has
      /// to be in the first three: it ties with "SSL certificates" on match quality,
      /// and which of the two a given administrator meant is not something the text
      /// can decide.
      /// </summary>
      [Theory]
      [InlineData("cipher list")]
      [InlineData("tls 1.3")]
      [InlineData("weak ciphers")]
      [InlineData("ssl")]
      public void TlsWords_StillReachTheTlsPage(string query)
      {
         List<string> pages = PaletteSearch.Query(query, null, null)
            .Where(r => r.IsSelectable)
            .Take(3)
            .Select(r => r.Page)
            .ToList();

         Assert.Contains("tls", pages);
      }

      /// <summary>
      /// A COM-backed checkbox silently dropped its note: every other editor on these
      /// pages calls Annotate and this one returned the bare checkbox, so a caption
      /// written beside one appeared nowhere at all. The auto-ban page is the first to
      /// need it - "0 disables auto-ban entirely" belongs on the control, not in a
      /// paragraph above it - which is how the omission was found.
      ///
      /// Scanned rather than rendered, because building the control needs a
      /// dispatcher and a live COM session; what matters is that the row type reaches
      /// Annotate at all.
      /// </summary>
      [Fact]
      public void AComCheckboxWithANote_ShowsIt()
      {
         string source = ReadServerSettingsView();
         if (source == null)
            return;

         string comBool = ClassBody(source, "class ComBool");
         Assert.True(comBool != null, "ComBool is gone from ServerSettingsView.");
         Assert.Contains("Annotate(box_", comBool, StringComparison.Ordinal);

         // And the auto-ban page actually uses one, so the path is exercised by the
         // application rather than merely available.
         string autoban = BuilderBody(source, "BuildAutoBan");
         Assert.True(autoban != null);
         Assert.Matches(new Regex("new ComBool[\\s\\S]{0,400}?Blurb ="), autoban);
      }

      /// <summary>The body of one Build&lt;Section&gt; method, or null.</summary>
      private static string BuilderBody(string source, string method)
      {
         Match match = Regex.Match(source, "private void " + method + @"\(\)\s*\{(.*?)\n      \}", RegexOptions.Singleline);
         return match.Success ? match.Groups[1].Value : null;
      }

      /// <summary>The body of one nested class, or null. Same shape as BuilderBody, one level in.</summary>
      private static string ClassBody(string source, string declaration)
      {
         Match match = Regex.Match(source, Regex.Escape(declaration) + @"[^\{]*\{(.*?)\n      \}", RegexOptions.Singleline);
         return match.Success ? match.Groups[1].Value : null;
      }

      private static string ReadServerSettingsView()
      {
         var directory = new DirectoryInfo(AppContext.BaseDirectory);

         while (directory != null)
         {
            string candidate = Path.Combine(directory.FullName, "ControlPanel", "Views", "ServerSettingsView.xaml.cs");
            if (File.Exists(candidate))
               return File.ReadAllText(candidate);

            directory = directory.Parent;
         }

         return null;
      }
   }
}
