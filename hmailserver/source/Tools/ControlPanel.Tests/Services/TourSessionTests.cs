// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The stepper. Everything here is a way a tour can go wrong that nobody
   /// would notice until they walked it: a step skipped that should not have
   /// been, a tour that stops on a control that is not on the screen, a Back
   /// that closes the tour, a tour somebody abandoned recorded as finished.
   /// None of it needs a window, which is the point of the class being what it
   /// is.
   /// </summary>
   public class TourSessionTests
   {
      private static Tour Walk(params TourStep[] steps)
         => new Tour("test", "A test walk", "It goes nowhere.", 1, new List<TourStep>(steps));

      private static TourStep Step(string target, string until = null, WhenMissing missing = WhenMissing.Skip)
         => new TourStep("domains", target, "Look at " + target + ".", until, missing);

      private static TourSession Open(Tour tour, params string[] present)
      {
         var on = new HashSet<string>(present, StringComparer.Ordinal);
         return new TourSession(tour, 0, step => on.Contains(step.Target), null);
      }

      [Fact]
      public void ItOpensOnItsFirstStepAndWalksToTheEnd()
      {
         Tour tour = Walk(Step("one"), Step("two"), Step("three"));
         TourSession session = Open(tour, "one", "two", "three");

         Assert.True(session.Begin());
         Assert.Equal("one", session.Current.Target);
         Assert.False(session.CanGoBack);

         Assert.True(session.Next());
         Assert.Equal("two", session.Current.Target);
         Assert.True(session.CanGoBack);

         Assert.True(session.Next());
         Assert.Equal("three", session.Current.Target);

         // Past the last step is the end, and the only ending that counts as
         // finished.
         Assert.False(session.Next());
         Assert.Equal(TourEnding.Finished, session.Ending);
         Assert.Null(session.Current);
         Assert.False(session.Running);
      }

      [Fact]
      public void AStepWhoseControlIsNotOnTheScreenIsSkippedRatherThanWaitedOn()
      {
         Tour tour = Walk(Step("one"), Step("gone"), Step("three"));
         TourSession session = Open(tour, "one", "three");

         Assert.True(session.Begin());
         Assert.True(session.Next());
         Assert.Equal("three", session.Current.Target);
         Assert.True(session.Running);
      }

      [Fact]
      public void ATourWhoseFirstStepsAreAllMissingOpensOnTheFirstOneThatIsThere()
      {
         Tour tour = Walk(Step("gone"), Step("also-gone"), Step("three"));
         TourSession session = Open(tour, "three");

         Assert.True(session.Begin());
         Assert.Equal("three", session.Current.Target);
      }

      [Fact]
      public void ATourWithNothingLeftToShowEndsRatherThanPointingAtNothing()
      {
         Tour tour = Walk(Step("gone"), Step("also-gone"));
         TourSession session = Open(tour);

         Assert.False(session.Begin());
         Assert.Equal(TourEnding.Finished, session.Ending);
         Assert.False(string.IsNullOrWhiteSpace(session.Farewell()));
      }

      [Fact]
      public void AStepMarkedEndTourStopsTheWalkAndSaysWhichStepItWas()
      {
         Tour tour = Walk(Step("one"), Step("needed", missing: WhenMissing.EndTour), Step("three"));
         TourSession session = Open(tour, "one", "three");

         Assert.True(session.Begin());
         Assert.False(session.Next());
         Assert.Equal(TourEnding.Missing, session.Ending);
         Assert.Equal("needed", session.MissingStep.Target);

         // And it says so: silence after a tour that stopped on its own is how
         // somebody decides the feature is broken.
         Assert.False(string.IsNullOrWhiteSpace(session.Farewell()));
      }

      [Fact]
      public void BackReturnsToTheLastStepThatWasShownAndNeverClosesTheTour()
      {
         Tour tour = Walk(Step("one"), Step("gone"), Step("three"));
         TourSession session = Open(tour, "one", "three");

         session.Begin();
         session.Next();
         Assert.Equal("three", session.Current.Target);

         // Past the skipped step, not onto it.
         Assert.True(session.Back());
         Assert.Equal("one", session.Current.Target);

         // And Back on the first step stands still rather than ending the tour,
         // which would be a trap: Back is how somebody re-reads a sentence.
         Assert.True(session.Back());
         Assert.Equal("one", session.Current.Target);
         Assert.True(session.Running);
      }

      [Fact]
      public void AConditionThatBecomesTrueWhileTheStepIsShowingMovesTheTourOn()
      {
         Tour tour = Walk(Step("one", until: "done"), Step("two"));
         bool done = false;
         var session = new TourSession(tour, 0, _ => true, condition => condition == "done" && done);

         session.Begin();
         Assert.False(session.Advanced());
         Assert.Equal("one", session.Current.Target);

         done = true;
         Assert.True(session.Advanced());
         Assert.Equal("two", session.Current.Target);

         // The next step has no condition, so nothing more happens by itself.
         Assert.False(session.Advanced());
      }

      /// <summary>
      /// The case that reads wrong if it is got wrong: somebody who already has
      /// a domain opens the walk, and the step about domains vanishes before
      /// they have read it. It is the CHANGE that ends a step, not the state.
      /// </summary>
      [Fact]
      public void AConditionAlreadyTrueWhenTheStepOpensLeavesTheStepShowing()
      {
         Tour tour = Walk(Step("one", until: "done"), Step("two"));
         var session = new TourSession(tour, 0, _ => true, _ => true);

         session.Begin();
         Assert.False(session.Advanced());
         Assert.Equal("one", session.Current.Target);

         // Next still works: a condition never blocks anything.
         Assert.True(session.Next());
         Assert.Equal("two", session.Current.Target);
      }

      [Fact]
      public void LeavingIsNeverFinishing()
      {
         Tour tour = Walk(Step("one"), Step("two"), Step("three"));
         TourSession session = Open(tour, "one", "two", "three");

         session.Begin();
         session.Next();
         session.Leave();

         Assert.Equal(TourEnding.Left, session.Ending);
         Assert.False(session.Running);
         Assert.Equal(1, session.ResumePoint);

         // Nothing moves after the tour has ended.
         Assert.False(session.Next());
         Assert.False(session.Back());
         Assert.False(session.Advanced());
      }

      [Fact]
      public void AFinishedTourResumesAtItsBeginningRatherThanAtItsLastStop()
      {
         Tour tour = Walk(Step("one"), Step("two"));
         TourSession session = Open(tour, "one", "two");

         session.Begin();
         session.Next();
         session.Next();

         Assert.Equal(TourEnding.Finished, session.Ending);
         Assert.Equal(0, session.ResumePoint);
      }

      [Fact]
      public void ItStartsWhereItIsToldToAndClampsAnImpossibleStartingPoint()
      {
         Tour tour = Walk(Step("one"), Step("two"), Step("three"));

         var from = new TourSession(tour, 2, _ => true, null);
         from.Begin();
         Assert.Equal("three", from.Current.Target);

         // A resume point out of range - a record that outlived the tour it
         // describes - lands on the last step rather than throwing.
         var beyond = new TourSession(tour, 99, _ => true, null);
         beyond.Begin();
         Assert.Equal("three", beyond.Current.Target);

         var negative = new TourSession(tour, -5, _ => true, null);
         negative.Begin();
         Assert.Equal("one", negative.Current.Target);
      }

      [Fact]
      public void ItSaysWhereTheReaderIs()
      {
         Tour tour = Walk(Step("one"), Step("two"), Step("three"));
         TourSession session = Open(tour, "one", "two", "three");

         session.Begin();
         Assert.Contains("1", session.Position);
         Assert.Contains("3", session.Position);
      }

      [Fact]
      public void ATourWithoutStepsIsNotAnException()
      {
         Assert.Throws<ArgumentNullException>(() => new TourSession(null, 0, null, null));
      }
   }
}
