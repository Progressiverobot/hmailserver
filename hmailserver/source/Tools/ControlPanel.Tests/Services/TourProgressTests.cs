// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The record of what this administrator has finished. It comes out of the
   /// registry, where anything at all can happen to it, and the worst thing it
   /// can do is either of two opposites: forget a finished walk and offer it
   /// again for ever, or claim a walk was finished that was abandoned halfway.
   /// </summary>
   public class TourProgressTests
   {
      private static Tour Walk(int version = 1, int steps = 4)
         => new Tour("firstrun", "A walk", "It goes somewhere.", version,
            Enumerable.Range(0, steps).Select(i => new TourStep("domains", "t" + i, "Look at t" + i + ".")).ToList());

      [Fact]
      public void AFinishedTourIsRememberedAndAResumePointIsNot()
      {
         Tour walk = Walk();
         var progress = new TourProgress();

         Assert.False(progress.HasFinished(walk));
         Assert.Equal(0, progress.ResumeAt(walk));

         progress.Left(walk, 2);
         Assert.Equal(2, progress.ResumeAt(walk));
         Assert.False(progress.HasFinished(walk));

         progress.Completed(walk);
         Assert.True(progress.HasFinished(walk));

         // Finishing drops the resume point: coming back to a finished tour and
         // landing two thirds of the way through it teaches nothing.
         Assert.Equal(0, progress.ResumeAt(walk));
      }

      [Fact]
      public void ATourThatHasGrownAStopIsOfferedAgain()
      {
         var progress = new TourProgress();
         progress.Completed(Walk(version: 1));

         Assert.True(progress.HasFinished(Walk(version: 1)));
         Assert.False(progress.HasFinished(Walk(version: 2)));

         // And finishing the new one does not lose the record of the old.
         progress.Completed(Walk(version: 2));
         Assert.True(progress.HasFinished(Walk(version: 2)));
         Assert.True(progress.HasFinished(Walk(version: 1)));
      }

      [Fact]
      public void AResumePointFromAnOlderVersionOfATourIsNotUsed()
      {
         var progress = new TourProgress();
         progress.Left(Walk(version: 1), 3);

         Assert.Equal(3, progress.ResumeAt(Walk(version: 1)));

         // Step 3 of the old tour is not step 3 of the new one, so it is the
         // beginning or nothing.
         Assert.Equal(0, progress.ResumeAt(Walk(version: 2)));
      }

      [Fact]
      public void AResumePointPastTheEndOfTheTourIsNotUsed()
      {
         var progress = new TourProgress();
         progress.Left(Walk(steps: 9), 7);

         // The same tour, shortened: the record still says 7 and there is no 7.
         Assert.Equal(0, progress.ResumeAt(Walk(steps: 4)));
      }

      [Fact]
      public void LeavingOnTheFirstStepRecordsNothingToResume()
      {
         var progress = new TourProgress();
         progress.Left(Walk(), 0);

         Assert.Equal(0, progress.ResumeAt(Walk()));
         Assert.DoesNotContain("@", progress.Serialize());
      }

      [Fact]
      public void ItSurvivesTheRoundTripThroughOneString()
      {
         var progress = new TourProgress();
         progress.Completed(Walk());
         progress.Left(new Tour("second", "Another", "Also somewhere.", 3,
            new List<TourStep> { new TourStep(null, "a", "A."), new TourStep(null, "b", "B."), new TourStep(null, "c", "C.") }), 2);

         TourProgress back = TourProgress.Deserialize(progress.Serialize());

         Assert.True(back.HasFinished(Walk()));
         Assert.Equal(2, back.ResumeAt(new Tour("second", "Another", "Also somewhere.", 3,
            new List<TourStep> { new TourStep(null, "a", "A."), new TourStep(null, "b", "B."), new TourStep(null, "c", "C.") })));
         Assert.Equal(progress.Serialize(), back.Serialize());
      }

      /// <summary>
      /// It is readable in regedit on purpose - a maintainer looking at "the
      /// walk keeps starting itself" should be able to see the state without a
      /// decoder, and delete it without one either.
      /// </summary>
      [Fact]
      public void TheStoredFormIsReadableByAPerson()
      {
         var progress = new TourProgress();
         progress.Completed(Walk(version: 2));

         Assert.Equal("firstrun=2|", progress.Serialize());
      }

      [Theory]
      [InlineData("")]
      [InlineData(null)]
      [InlineData("   ")]
      [InlineData("|")]
      [InlineData("rubbish")]
      [InlineData("firstrun=|firstrun@")]
      [InlineData("firstrun=x|firstrun@yvz")]
      [InlineData("=3|@2v1")]
      [InlineData("firstrun=-1|firstrun@-4v1")]
      [InlineData("|||||")]
      [InlineData("firstrun=1,firstrun=2|firstrun@1v1,firstrun@2v1")]
      public void EveryKindOfDamageIsSurvivableAndSilent(string stored)
      {
         TourProgress progress = TourProgress.Deserialize(stored);

         // Whatever it made of it, asking it anything must not throw, and a
         // tour it does not know about must simply not have been finished.
         Assert.False(progress.HasFinished(new Tour("unknown", "x", "y", 1,
            new List<TourStep> { new TourStep(null, "a", "A.") })));
         Assert.InRange(progress.ResumeAt(Walk()), 0, 3);
         Assert.NotNull(progress.Serialize());
      }

      [Fact]
      public void AnIdForATourThatNoLongerExistsIsKeptRatherThanDropped()
      {
         // This class does not know which ids are live - TourCatalog does - so
         // an id it cannot resolve survives the round trip instead of being
         // quietly deleted from somebody's record.
         TourProgress progress = TourProgress.Deserialize("retired=4|");
         Assert.Contains("retired", progress.Finished);
         Assert.Equal("retired=4|", progress.Serialize());
      }

      [Fact]
      public void ANullTourIsNotAnException()
      {
         var progress = new TourProgress();
         progress.Completed(null);
         progress.Left(null, 3);

         Assert.False(progress.HasFinished(null));
         Assert.Equal(0, progress.ResumeAt(null));
      }
   }
}
