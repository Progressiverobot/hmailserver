// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// Recently-visited and most-used pages. The whole type is new, so every test
   /// here fails against the unfixed code, where the palette had no notion of what
   /// this administrator had ever opened and offered all forty-two pages in tree
   /// order every time it was raised.
   ///
   /// Two behaviours are worth more than the rest and are tested hardest: the
   /// history round-trips through one short string (it lives in the registry
   /// beside the window bounds, and a broken value must cost the history and
   /// nothing else), and re-entering the page you are already on is not a visit.
   /// </summary>
   public class PaletteUsageTests
   {
      [Fact]
      public void RecordVisit_PutsThePageAtTheHeadOfRecent()
      {
         var usage = new PaletteUsage();

         usage.RecordVisit("queue");
         usage.RecordVisit("logs");
         usage.RecordVisit("antispam");

         Assert.Equal(new[] { "antispam", "logs", "queue" }, usage.Recent);
      }

      [Fact]
      public void RecordVisit_MovesAReturnVisitBackToTheHead()
      {
         var usage = new PaletteUsage();

         usage.RecordVisit("queue");
         usage.RecordVisit("logs");
         usage.RecordVisit("queue");

         Assert.Equal(new[] { "queue", "logs" }, usage.Recent);
         Assert.Equal(2, usage.VisitCount("queue"));
      }

      /// <summary>
      /// Re-entering the page you are on is not a visit. The Control Panel
      /// re-enters the current page whenever the session reconnects after a
      /// service restart, and the navigation tree can re-raise its own selection;
      /// counting those would let one page's count run away from the rest and
      /// freeze "Most used" on whatever page happened to be open during a restart.
      /// </summary>
      [Fact]
      public void RecordVisit_IgnoresReEnteringTheSamePage()
      {
         var usage = new PaletteUsage();

         usage.RecordVisit("queue");
         usage.RecordVisit("queue");
         usage.RecordVisit("queue");

         Assert.Equal(1, usage.VisitCount("queue"));
         Assert.Equal(new[] { "queue" }, usage.Recent);
      }

      [Fact]
      public void Recent_IsCappedAndKeepsTheNewest()
      {
         var usage = new PaletteUsage();

         for (int i = 0; i < PaletteUsage.MaxRecent + 4; i++)
            usage.RecordVisit("page" + i);

         Assert.Equal(PaletteUsage.MaxRecent, usage.Recent.Count);
         Assert.Equal("page" + (PaletteUsage.MaxRecent + 3), usage.Recent[0]);
         Assert.DoesNotContain("page0", usage.Recent);
         Assert.Equal(usage.Recent.Count, usage.Recent.Distinct(StringComparer.OrdinalIgnoreCase).Count());
      }

      [Fact]
      public void MostUsed_OrdersByVisitCount()
      {
         var usage = new PaletteUsage();

         // Alternated, because a run of visits to one page counts as one visit.
         for (int i = 0; i < 3; i++)
         {
            usage.RecordVisit("queue");
            usage.RecordVisit("logs");
         }
         usage.RecordVisit("about");
         usage.RecordVisit("logs");

         Assert.Equal(new[] { "logs", "queue", "about" }, usage.MostUsed);
      }

      /// <summary>
      /// With a fresh profile almost every count is one, so the tie-break decides
      /// the whole list. Recency is the only signal available; ordering by page key
      /// would make "Most used" a list of the pages whose names begin with A.
      /// </summary>
      [Fact]
      public void MostUsed_BreaksTiesByRecencyRatherThanAlphabetically()
      {
         var usage = new PaletteUsage();

         usage.RecordVisit("about");
         usage.RecordVisit("queue");
         usage.RecordVisit("logs");

         Assert.Equal(new[] { "logs", "queue", "about" }, usage.MostUsed);
      }

      [Fact]
      public void VisitCount_IsZeroForAPageNeverOpened()
      {
         var usage = new PaletteUsage();
         usage.RecordVisit("queue");

         Assert.Equal(0, usage.VisitCount("logs"));
         Assert.Equal(0, usage.VisitCount(null));
      }

      [Fact]
      public void RecordVisit_IgnoresNothing()
      {
         var usage = new PaletteUsage();

         usage.RecordVisit(null);
         usage.RecordVisit("");
         usage.RecordVisit("   ");

         Assert.Empty(usage.Recent);
         Assert.Empty(usage.MostUsed);
      }

      [Fact]
      public void Serialize_RoundTripsCountsAndOrder()
      {
         var usage = new PaletteUsage();
         for (int i = 0; i < 3; i++)
         {
            usage.RecordVisit("queue");
            usage.RecordVisit("logs");
         }
         usage.RecordVisit("about");

         PaletteUsage restored = PaletteUsage.Deserialize(usage.Serialize());

         Assert.Equal(usage.Recent, restored.Recent);
         Assert.Equal(usage.MostUsed, restored.MostUsed);
         Assert.Equal(usage.VisitCount("queue"), restored.VisitCount("queue"));
         Assert.Equal(usage.VisitCount("logs"), restored.VisitCount("logs"));
      }

      /// <summary>
      /// Readable in regedit on purpose: a bug report saying the palette offers
      /// the wrong pages should be diagnosable by looking at the value.
      /// </summary>
      [Fact]
      public void Serialize_IsReadableByAHuman()
      {
         var usage = new PaletteUsage();
         usage.RecordVisit("queue");
         usage.RecordVisit("logs");

         Assert.Equal("logs=1,queue=1|logs,queue", usage.Serialize());
      }

      [Fact]
      public void Serialize_EmptyHistory_RoundTrips()
      {
         PaletteUsage restored = PaletteUsage.Deserialize(new PaletteUsage().Serialize());

         Assert.Empty(restored.Recent);
         Assert.Empty(restored.MostUsed);
      }

      /// <summary>
      /// This value comes out of HKCU, where anything can happen to it. Losing the
      /// history is the correct cost of a damaged value; throwing on start-up
      /// because the list of recently visited pages was corrupt would not be.
      /// </summary>
      [Theory]
      [InlineData(null)]
      [InlineData("")]
      [InlineData("   ")]
      [InlineData("|")]
      [InlineData("garbage")]
      [InlineData("=")]
      [InlineData("=5")]
      [InlineData("queue=")]
      [InlineData("queue=notanumber")]
      [InlineData("queue=-4")]
      [InlineData("queue=0")]
      [InlineData("|||||")]
      [InlineData("queue=1|queue,queue,queue")]
      [InlineData("queue=999999999999")]
      public void Deserialize_SurvivesADamagedValue(string text)
      {
         PaletteUsage usage = PaletteUsage.Deserialize(text);

         // Whatever survived must be internally consistent: no duplicates, and
         // never more entries than the cap.
         Assert.True(usage.Recent.Count <= PaletteUsage.MaxRecent);
         Assert.Equal(usage.Recent.Count, usage.Recent.Distinct(StringComparer.OrdinalIgnoreCase).Count());
         Assert.All(usage.MostUsed, key => Assert.True(usage.VisitCount(key) > 0));
      }

      [Fact]
      public void Deserialize_KeepsWhatItCanFromAPartlyDamagedValue()
      {
         PaletteUsage usage = PaletteUsage.Deserialize("queue=3,rubbish,logs=2,about=x|queue,logs");

         Assert.Equal(3, usage.VisitCount("queue"));
         Assert.Equal(2, usage.VisitCount("logs"));
         Assert.Equal(0, usage.VisitCount("about"));
         Assert.Equal(new[] { "queue", "logs" }, usage.Recent);
      }

      /// <summary>
      /// A Control Panel left open on a monitoring page for months would otherwise
      /// build a count no other page could ever overtake, freezing "Most used"
      /// permanently on one row.
      /// </summary>
      [Fact]
      public void VisitCount_IsClamped()
      {
         PaletteUsage usage = PaletteUsage.Deserialize("queue=999999|queue");

         Assert.Equal(PaletteUsage.MaxVisitCount, usage.VisitCount("queue"));

         usage.RecordVisit("logs");
         usage.RecordVisit("queue");
         Assert.Equal(PaletteUsage.MaxVisitCount, usage.VisitCount("queue"));
      }

      /// <summary>
      /// This class cannot know which page keys are live - the navigation map is
      /// the component that does - so it keeps what it is given and leaves the
      /// filtering to the palette. Dropping unknown keys here would silently lose
      /// history across a release that only renamed a page.
      /// </summary>
      [Fact]
      public void Deserialize_KeepsKeysItDoesNotRecognise()
      {
         PaletteUsage usage = PaletteUsage.Deserialize("aPageWeDeleted=4|aPageWeDeleted");

         Assert.Equal(4, usage.VisitCount("aPageWeDeleted"));
         Assert.Contains("aPageWeDeleted", usage.Recent);
      }
   }
}
