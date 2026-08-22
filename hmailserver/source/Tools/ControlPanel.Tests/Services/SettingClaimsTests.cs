// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The interface may only claim what the server does.
   ///
   /// This project has already shipped the other outcome twice: "SQL" was
   /// selectable as a log destination and an NCSA log format was selectable as a
   /// line format, for years, while the server read neither value. An
   /// administrator picked one, got no error, and lost the function - which is
   /// worse than the feature being absent, because there is nothing to go looking
   /// for. Both server-side gaps are closed now, and reading the code that closed
   /// them is what turned up the wording still left over: an option naming a
   /// format the server explicitly does not emit, an option crediting that format
   /// to AWStats when it deliberately leaves the AWStats journal alone, an
   /// OpenTelemetry blurb advertising a metrics exporter that does not exist, and
   /// an editable box for a thread priority nothing reads.
   ///
   /// The source-scanning tests below are the ones that fail against the unfixed
   /// tree, and they name the exact strings that were there.
   /// </summary>
   public class SettingClaimsTests
   {
      [Fact]
      public void EveryClaim_HasAKeyAndANote()
      {
         Assert.NotEmpty(SettingClaims.Entries);

         foreach (SettingClaim claim in SettingClaims.Entries)
         {
            Assert.False(string.IsNullOrWhiteSpace(claim.Key), "A claim has no setting key.");
            Assert.False(string.IsNullOrWhiteSpace(claim.Note),
               "'" + claim.Key + "' has no note. A claim with nothing to say is not a claim.");
         }
      }

      [Fact]
      public void ClaimKeys_AreUnique()
      {
         List<string> keys = SettingClaims.Entries.Select(c => c.Key).ToList();

         Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
      }

      /// <summary>
      /// Every claim has to be about a setting the pages actually show, or the note
      /// is written for a control nobody will ever see it beside.
      /// </summary>
      [Fact]
      public void EveryClaim_NamesASettingThatIsOnAPage()
      {
         var indexed = new HashSet<string>(
            SettingsSearchIndex.Entries.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);

         List<string> orphans = SettingClaims.Entries
            .Where(c => !indexed.Contains(c.Key))
            .Select(c => c.Key)
            .ToList();

         Assert.True(orphans.Count == 0,
            "These claims are about settings that no page offers: " + string.Join(", ", orphans));
      }

      /// <summary>
      /// The option the interface offers for the NCSA line format.
      ///
      /// NcsaLogFormatter emits the NCSA *Common* Log Format and says in its own
      /// header that it rejects the "combined" variant, because hMailServer has
      /// nothing to put in the referer and user-agent fields that variant adds. It
      /// also deliberately does not touch the AWStats journal, which has its own
      /// switch three rows above on the same card. The old option text -
      /// "NCSA / combined (AWStats)" - was therefore wrong on both counts, and
      /// wrong in the way that costs an administrator an afternoon: they picked it
      /// to make AWStats work, changed the format of a different file, and AWStats
      /// carried on seeing exactly what it saw before.
      /// </summary>
      [Fact]
      public void LogFormatOptions_DoNotClaimCombinedOrAwstats()
      {
         string ncsa = SettingClaims.LogFormatOptions
            .Single(o => o.Value == SettingClaims.LogFormatNcsa).Label;

         Assert.Contains("NCSA", ncsa, StringComparison.OrdinalIgnoreCase);
         Assert.Contains("Common", ncsa, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("combined", ncsa, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("awstats", ncsa, StringComparison.OrdinalIgnoreCase);
      }

      /// <summary>The enum values have to match the IDL, or the combo writes the wrong thing.</summary>
      [Fact]
      public void LogFormatAndDeviceValues_MatchTheComEnums()
      {
         // eLogOutputFormat: hLogFormatDefault = 1, hLogFormatCSA = 2.
         Assert.Equal(1, SettingClaims.LogFormatDefault);
         Assert.Equal(2, SettingClaims.LogFormatNcsa);

         // eLogDevice: hLogDeviceSQL = 1, hLogDeviceFile = 2.
         Assert.Equal(1, SettingClaims.LogDeviceSql);
         Assert.Equal(2, SettingClaims.LogDeviceFile);

         Assert.Equal(2, SettingClaims.LogFormatOptions.Count);
         Assert.Equal(2, SettingClaims.LogDeviceOptions.Count);

         // Files first: it is the default, and a combo whose first entry is the
         // unusual choice invites a mis-click.
         Assert.Equal(SettingClaims.LogDeviceFile, SettingClaims.LogDeviceOptions[0].Value);
      }

      /// <summary>
      /// The NCSA note has to say the two things that are not obvious from the
      /// option name: what the format can be used for, and that it does not change
      /// the AWStats journal.
      /// </summary>
      [Fact]
      public void LogFormatNote_SaysWhatItDoesAndDoesNotCover()
      {
         string note = SettingClaims.NoteFor("Logging.LogFormat");

         Assert.Contains("AWStats", note, StringComparison.OrdinalIgnoreCase);
         Assert.Contains("separate file", note, StringComparison.OrdinalIgnoreCase);
      }

      /// <summary>
      /// Logger::Render_ tests the NCSA format before it looks at JsonLogging, so
      /// selecting NCSA makes the JSON switch inert. The Logging page offered both
      /// side by side with no hint that one beat the other, which is
      /// indistinguishable, from the outside, from a checkbox that does not work.
      /// </summary>
      [Fact]
      public void JsonLogging_IsDeclaredConditionalAndSaysWhatWins()
      {
         SettingClaim claim = SettingClaims.For("JsonLogging");

         Assert.NotNull(claim);
         Assert.Equal(ClaimKind.Conditional, claim.Kind);
         Assert.Contains("NCSA", claim.Note, StringComparison.OrdinalIgnoreCase);
         Assert.Contains("precedence", claim.Note, StringComparison.OrdinalIgnoreCase);

         // ...and the note must say the value is kept, because the interface
         // disables the checkbox rather than clearing it.
         Assert.Contains("remembered", claim.Note, StringComparison.OrdinalIgnoreCase);
      }

      /// <summary>
      /// This setting is still the TRACES endpoint and must keep saying so.
      ///
      /// It used to read "there is no metrics or logs exporter", which was true when
      /// it was written and stopped being true on 21 August 2026 when both signals
      /// shipped. That is the failure mode this whole class exists to catch, arriving
      /// from the other direction for once: not a claim that overstates what the
      /// server does, but one that understates it, which sends an administrator to
      /// Prometheus for metrics they could have had over OTLP. The assertion now pins
      /// the part that has to stay true - one endpoint per signal - rather than the
      /// absence of a feature, because an absence is exactly what a roadmap turns
      /// into a presence.
      /// </summary>
      [Fact]
      public void OtelClaim_SaysWhichSignalItCarries()
      {
         string note = SettingClaims.NoteFor("OtelEndpoint");

         Assert.Contains("traces", note, StringComparison.OrdinalIgnoreCase);

         // The reader has to be able to find the other two from here.
         Assert.Contains("metrics and logs have", note, StringComparison.OrdinalIgnoreCase);

         // And they must actually exist, or the sentence above is the overclaim.
         Assert.False(string.IsNullOrWhiteSpace(SettingClaims.NoteFor("OtelMetricsEndpoint")),
            "OtelEndpoint points the reader at a metrics endpoint that has no claim of its own.");
         Assert.False(string.IsNullOrWhiteSpace(SettingClaims.NoteFor("OtelLogsEndpoint")),
            "OtelEndpoint points the reader at a logs endpoint that has no claim of its own.");
      }

      /// <summary>
      /// Configuration::GetWorkerThreadPriority has exactly one caller in the whole
      /// tree - the COM getter - and there is no SetThreadPriority call anywhere. The
      /// value round-trips through the settings table and nothing else happens to it.
      /// </summary>
      [Fact]
      public void WorkerThreadPriority_IsDeclaredInert()
      {
         Assert.True(SettingClaims.IsInert("WorkerThreadPriority"));
         Assert.Contains("never reads it", SettingClaims.NoteFor("WorkerThreadPriority"),
            StringComparison.OrdinalIgnoreCase);
      }

      /// <summary>
      /// The label is what the Ctrl+K palette shows, so the palette has to carry the
      /// warning too - somebody who searches for "thread priority", lands on the
      /// Performance page and starts typing has to be told before they type, not
      /// after they save.
      ///
      /// Fails against the unfixed code, where the indexed label was the bare
      /// "Worker thread priority".
      /// </summary>
      [Fact]
      public void InertSetting_IsStillSearchableAndItsLabelSaysSo()
      {
         SettingEntry entry = SettingsSearchIndex.Entries
            .FirstOrDefault(e => string.Equals(e.Key, "WorkerThreadPriority", StringComparison.OrdinalIgnoreCase));

         Assert.True(entry != null,
            "The inert setting dropped out of the search index, so the palette can no longer "
            + "explain it to anyone looking for it.");
         Assert.Equal("performance", entry.Page);
         Assert.Contains("does not use it", entry.Label, StringComparison.OrdinalIgnoreCase);

         // And it is still reachable by the words somebody would type.
         Assert.Contains(SettingsSearchIndex.Search("worker thread priority"),
            m => string.Equals(m.Key, "WorkerThreadPriority", StringComparison.OrdinalIgnoreCase));
      }

      /// <summary>
      /// The OTLP endpoint's own label had to change too, for the same reason: the
      /// palette shows the label, and "OpenTelemetry OTLP endpoint" reads as though
      /// every OTLP signal is on offer.
      /// </summary>
      [Fact]
      public void OtelEndpointLabel_SaysItIsForTraces()
      {
         SettingEntry entry = SettingsSearchIndex.Entries
            .FirstOrDefault(e => string.Equals(e.Key, "OtelEndpoint", StringComparison.OrdinalIgnoreCase));

         Assert.True(entry != null, "OtelEndpoint is no longer in the search index.");
         Assert.Contains("traces", entry.Label, StringComparison.OrdinalIgnoreCase);
      }

      // ---- the withdrawn claims must not come back ---------------------------

      /// <summary>
      /// Reads the settings-page sources and asserts the withdrawn claims are gone.
      ///
      /// Source scanning rather than reflection because these were string literals
      /// in a view: that is precisely why no test could see them for years, and a
      /// test that can see them is the only thing that stops the next one. The same
      /// approach as SettingsSearchIndexTests.Index_MatchesTheSettingsPages.
      ///
      /// Every entry here fails against the unfixed tree, and the string is the one
      /// that was actually there.
      /// </summary>
      [Theory]
      [InlineData("ServerSettingsView.xaml.cs", "NCSA / combined",
         "the interface offered a 'combined' NCSA format the server explicitly does not emit, "
         + "and credited it to AWStats, which that setting does not touch")]
      [InlineData("FeatureSettingsView.xaml.cs", "traces/metrics",
         "the Monitoring card advertised an OpenTelemetry metrics exporter that does not exist - "
         + "only the traces signal is implemented")]
      [InlineData("ServerSettingsView.xaml.cs", "Label = \"Worker thread priority\"",
         "an editable box wrote a thread priority the server never reads")]
      public void WithdrawnClaim_IsNotInTheSettingsPages(string view, string claim, string why)
      {
         string source = ReadView(view);
         if (source == null)
            return;   // sources not available (e.g. running from a packaged drop)

         Assert.False(source.Contains(claim, StringComparison.OrdinalIgnoreCase),
            view + " still contains \"" + claim + "\": " + why + ".");
      }

      /// <summary>
      /// The inert setting must not be offered through an editable control again.
      /// ComInert is the only row type on these pages that writes nothing, so the
      /// key appearing on any other row type means somebody has made it editable.
      /// </summary>
      [Fact]
      public void InertSetting_IsOfferedOnlyThroughTheReadOnlyRow()
      {
         string source = ReadView("ServerSettingsView.xaml.cs");
         if (source == null)
            return;

         int index = source.IndexOf("\"WorkerThreadPriority\"", StringComparison.Ordinal);
         Assert.True(index >= 0, "WorkerThreadPriority is no longer on the Performance page at all.");

         // Look back to the row's own "new " - the type of the row it belongs to.
         int start = source.LastIndexOf("new ", index, StringComparison.Ordinal);
         Assert.True(start >= 0);

         string rowType = source.Substring(start, index - start);
         Assert.Contains("ComInert", rowType);
      }

      /// <summary>
      /// A settings page must not build two tabs with the same name.
      ///
      /// ServerSettingsView writes its pages as Tab("General").Cards.Add(card),
      /// which reads as "put this card on the General tab". The helper used to
      /// APPEND a tab on every call, so two cards written that way produced two tabs
      /// both called General with one card each. The anti-spam page shipped showing
      /// General, General, Sender auth, Host checks, Greylisting, SpamAssassin,
      /// SpamAssassin - and the only route to the quarantine settings was noticing
      /// that the second identically named tab was not the first.
      ///
      /// Source-scanned rather than exercised, for the same reason the withdrawn
      /// claims above are: the page is WPF and this project is deliberately not, so
      /// reading the source is the only way a test can see this at all. It asserts
      /// the helper looks for an existing tab before making one, which is the
      /// property that makes duplicates impossible rather than merely absent today.
      /// </summary>
      [Fact]
      public void TabHelper_ReturnsAnExistingTabRatherThanAppendingADuplicate()
      {
         string source = ReadView("ServerSettingsView.xaml.cs");
         Assert.False(source == null, "ServerSettingsView.xaml.cs could not be found from the test binaries.");

         int helper = source.IndexOf("private TabDef Tab(string header)", StringComparison.Ordinal);
         Assert.True(helper >= 0, "The Tab(string) helper has been renamed or removed.");

         int close = source.IndexOf("      }", helper, StringComparison.Ordinal);
         Assert.True(close > helper);

         string body = source.Substring(helper, close - helper);

         Assert.True(body.Contains("foreach") || body.Contains("FirstOrDefault") || body.Contains("Find("),
            "Tab(string) no longer looks for an existing tab before creating one, so two cards " +
            "asking for the same tab will produce two tabs with the same name again.");
      }

      /// <summary>Walks up from the test binaries to a settings page source.</summary>
      private static string ReadView(string fileName)
      {
         var directory = new DirectoryInfo(AppContext.BaseDirectory);

         while (directory != null)
         {
            string candidate = Path.Combine(directory.FullName, "ControlPanel", "Views", fileName);
            if (File.Exists(candidate))
               return File.ReadAllText(candidate);

            directory = directory.Parent;
         }

         return null;
      }
   }
}
