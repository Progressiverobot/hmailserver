// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// What a screen reader calls a settings editor.
   ///
   /// Against the unfixed code there was no name at all: the two table-driven
   /// settings pages and the generated property dialog each put the wording in a
   /// TextBlock above the control and set only an AutomationId, which is for test
   /// automation and is never spoken. So every text box, number box, combo and
   /// password box on those pages - roughly 240 of them - was announced as a bare
   /// "edit" or "combo box". These tests pin the two decisions that fix it: a name
   /// exists, and a name that would be ambiguous is qualified with the card it
   /// belongs to rather than left to collide.
   /// </summary>
   public class AccessibleNamesTests
   {
      private static IReadOnlyList<string> Resolve(params (string Label, string Group)[] rows)
      {
         return AccessibleNames.Resolve(
            rows.Select(r => new LabelledEditor(r.Label, r.Group, r.Label)).ToList());
      }

      [Fact]
      public void UniqueLabel_IsAnnouncedAsWritten()
      {
         IReadOnlyList<string> names = Resolve(
            ("Spam mark threshold (score)", "Thresholds & actions"),
            ("Spam delete threshold (score)", "Thresholds & actions"));

         Assert.Equal(new[] { "Spam mark threshold (score)", "Spam delete threshold (score)" }, names);
      }

      /// <summary>
      /// The rule, on the smallest case: two cards, one shared label. The shipped
      /// occurrence is the Protocols page below - "Host" and "Port" do repeat, but
      /// on two different pages (SpamAssassin on Anti-spam, ClamAV on Anti-virus),
      /// which is deliberately not qualified because a reader is only ever on one
      /// page at a time.
      /// </summary>
      [Fact]
      public void DuplicatedLabel_IsQualifiedWithItsCard()
      {
         IReadOnlyList<string> names = Resolve(
            ("Host", "SpamAssassin"),
            ("Port", "SpamAssassin"),
            ("Host", "ClamAV (network daemon)"),
            ("Port", "ClamAV (network daemon)"));

         Assert.Equal(
            new[] { "SpamAssassin: Host", "SpamAssassin: Port", "ClamAV: Host", "ClamAV: Port" },
            names);
      }

      /// <summary>
      /// The parenthetical gloss on a card title is there for the reader's benefit
      /// and would be repeated on every control inside the card. The name in front
      /// of the bracket is the part that distinguishes one card from another.
      /// </summary>
      [Fact]
      public void CardTitleParenthetical_IsNotRepeatedOnEveryControl()
      {
         Assert.Equal("ClamWin", AccessibleNames.GroupPrefix("ClamWin (local executable)"));
         Assert.Equal("ManageSieve", AccessibleNames.GroupPrefix("ManageSieve (RFC 5804)"));
         Assert.Equal("Auto-ban", AccessibleNames.GroupPrefix("Auto-ban"));
         Assert.Equal("", AccessibleNames.GroupPrefix(null));
      }

      /// <summary>
      /// The shipped case, and the whole reason this resolves a page at a time. The
      /// Protocols page carries this exact wording three times - once on the SMTP
      /// card, once on IMAP, once on POP3 - and the same is true of "Welcome banner
      /// (empty = default)". They are the only labels that repeat within a page on
      /// either settings view, which was counted rather than assumed. Qualifying has
      /// to look across the whole page: per card it would find nothing, because each
      /// card carries the label only once.
      /// </summary>
      [Fact]
      public void SameLabelInThreeCards_IsQualifiedInAllThree()
      {
         IReadOnlyList<string> names = Resolve(
            ("Max simultaneous connections (0 = unlimited)", "SMTP"),
            ("Max simultaneous connections (0 = unlimited)", "IMAP"),
            ("Max simultaneous connections (0 = unlimited)", "POP3"));

         Assert.Equal(
            new[]
            {
               "SMTP: Max simultaneous connections (0 = unlimited)",
               "IMAP: Max simultaneous connections (0 = unlimited)",
               "POP3: Max simultaneous connections (0 = unlimited)"
            },
            names);
      }

      /// <summary>
      /// "SpamAssassin: Use SpamAssassin" is worse than "Use SpamAssassin". A label
      /// that already carries its card's name is qualified enough.
      /// </summary>
      [Fact]
      public void LabelThatAlreadyNamesItsCard_IsNotQualifiedTwice()
      {
         IReadOnlyList<string> names = Resolve(
            ("Use SpamAssassin", "SpamAssassin"),
            ("Use SpamAssassin", "Anti-spam"));

         Assert.Equal("Use SpamAssassin", names[0]);
         Assert.Equal("Anti-spam: Use SpamAssassin", names[1]);
      }

      /// <summary>
      /// A duplicated label with no card to qualify against gets the label. There is
      /// nothing better available, and a name is still better than none.
      /// </summary>
      [Fact]
      public void DuplicatedLabelWithNoCard_KeepsTheLabel()
      {
         IReadOnlyList<string> names = Resolve(("Port", ""), ("Port", null));

         Assert.Equal(new[] { "Port", "Port" }, names);
      }

      /// <summary>
      /// Spoken, "Host:" is "Host colon". Some labels are written with a trailing
      /// colon because they read as a caption on screen.
      /// </summary>
      [Fact]
      public void TrailingColonAndWhitespace_AreNotSpoken()
      {
         IReadOnlyList<string> names = Resolve(("  Host:  ", "SpamAssassin"));

         Assert.Equal("Host", names[0]);
      }

      /// <summary>
      /// An empty name must never be set on a control. Buttons and checkboxes are
      /// named by their own content, and overriding that with a blank string would
      /// take a working name away - so the resolver returns "" and the callers
      /// treat it as "leave this one alone".
      /// </summary>
      [Fact]
      public void EditorWithNoLabel_GetsNoName()
      {
         IReadOnlyList<string> names = AccessibleNames.Resolve(new List<LabelledEditor>
         {
            new LabelledEditor(null, "Greylisting", "AntiSpam.GreyListingEnabled"),
            new LabelledEditor("", "Greylisting", "AntiSpam.GreyListingEnabled"),
            new LabelledEditor("   ", "Greylisting", null)
         });

         Assert.Equal(new[] { "", "", "" }, names);
      }

      [Fact]
      public void Resolve_AlwaysReturnsOneNamePerEditor()
      {
         Assert.Empty(AccessibleNames.Resolve(null));
         Assert.Empty(AccessibleNames.Resolve(new List<LabelledEditor>()));

         var editors = new List<LabelledEditor>
         {
            new LabelledEditor("Host", "SpamAssassin", "AntiSpam.SpamAssassinHost"),
            null
         };

         IReadOnlyList<string> names = AccessibleNames.Resolve(editors);

         Assert.Equal(editors.Count, names.Count);
         Assert.All(names, name => Assert.NotNull(name));
      }

      /// <summary>
      /// A colon, because screen readers pause on it: "SpamAssassin: Host" is heard
      /// as two pieces of information rather than as one odd phrase. Pinned because
      /// changing it would change every qualified announcement in the application.
      /// </summary>
      [Fact]
      public void Qualifier_IsSeparatedByAColon()
      {
         Assert.Equal(": ", AccessibleNames.Separator);
         Assert.Equal("SpamAssassin: Host", AccessibleNames.Qualify("Host", "SpamAssassin"));
      }

      /// <summary>
      /// Which labels actually repeat within a page, measured from the generated
      /// index rather than asserted from memory.
      ///
      /// Two things depend on this. It keeps the tests above from being about a
      /// hypothetical - the Protocols page really does carry the same two labels
      /// three times each - and it fails when a new in-page duplicate appears, which
      /// is the moment somebody has to decide whether the two settings should be
      /// worded differently or the card title is doing the distinguishing. Either
      /// answer is fine; not noticing is not.
      /// </summary>
      [Fact]
      public void OnlyTheProtocolsPage_RepeatsALabelWithinItself()
      {
         List<string> duplicated = SettingsSearchIndex.Entries
            .GroupBy(e => e.Page + "|" + e.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.First().Page + ": " + g.First().Label + " (x" + g.Count() + ")")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

         Assert.Equal(
            new[]
            {
               "protocols: Max simultaneous connections (0 = unlimited) (x3)",
               "protocols: Welcome banner (empty = default) (x3)"
            },
            duplicated);
      }
   }
}
