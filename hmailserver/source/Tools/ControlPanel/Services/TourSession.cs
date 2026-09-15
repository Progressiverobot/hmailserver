// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>Why a tour stopped.</summary>
   public enum TourEnding
   {
      /// <summary>It is still running.</summary>
      Running,

      /// <summary>The last step was passed. This is the only ending that records the tour as finished.</summary>
      Finished,

      /// <summary>The reader left it - Skip, Escape, or signing out.</summary>
      Left,

      /// <summary>
      /// A step the rest of the tour needs was not on the screen. The tour says
      /// so rather than pointing at nothing, because a tour that silently ends
      /// is indistinguishable from one that crashed.
      /// </summary>
      Missing
   }

   /// <summary>
   /// The stepper. Given a tour, where to start, and two questions it can ask
   /// the surface - "is this control on the screen" and "has this happened
   /// yet" - it decides which step is showing, what Next and Back do, and when
   /// and why the tour ended.
   ///
   /// All of it is here, WPF-free, for the reason <see cref="ConnectionStatus"/>
   /// and <see cref="SettingsPageStatus"/> are: this is the part that can be
   /// wrong in a way nobody notices - a step skipped that should not have been,
   /// a tour that hangs on a control that is not there, a finished record
   /// written for a tour somebody abandoned - and none of it needs a window to
   /// be tested.
   ///
   /// The two questions are delegates rather than an interface because the
   /// three surfaces answer them in three different ways and none of them wants
   /// to implement an interface to say "yes, that button is on the screen".
   /// </summary>
   public sealed class TourSession
   {
      private readonly Func<TourStep, bool> present_;
      private readonly Func<string, bool> satisfied_;

      // What the current step's condition said when the step opened. A
      // condition that was ALREADY true when the reader arrived does not end
      // the step: somebody who has a domain already should read the sentence
      // about domains and press Next, not watch the step vanish before they
      // have read it. It is the change from false to true - the reader doing
      // the thing - that moves the tour on.
      private bool conditionAtEntry_;

      /// <summary>
      /// <paramref name="present"/> is asked whether a step's control is on the
      /// screen; <paramref name="satisfied"/> whether a named condition has
      /// happened. Either may be null, which is a surface that can answer
      /// neither: every control then counts as present and no condition ever
      /// fires, which is the behaviour a bare unit test wants.
      ///
      /// The whole step goes to <paramref name="present"/> rather than just the
      /// automation id, because finding the control means opening the page it
      /// is on first, and only the step knows which page that is.
      /// </summary>
      public TourSession(Tour tour, int startAt, Func<TourStep, bool> present, Func<string, bool> satisfied)
      {
         Tour = tour ?? throw new ArgumentNullException(nameof(tour));
         present_ = present ?? (_ => true);
         satisfied_ = satisfied ?? (_ => false);
         Index = Math.Max(0, Math.Min(startAt, Tour.Steps.Count - 1));
         Ending = TourEnding.Running;
      }

      /// <summary>The tour being walked.</summary>
      public Tour Tour { get; }

      /// <summary>The index of the step showing. Meaningless once <see cref="Ending"/> is not Running.</summary>
      public int Index { get; private set; }

      /// <summary>Why the tour stopped, or Running.</summary>
      public TourEnding Ending { get; private set; }

      /// <summary>The step whose target could not be found, when <see cref="Ending"/> is Missing.</summary>
      public TourStep MissingStep { get; private set; }

      /// <summary>True while there is a step on the screen.</summary>
      public bool Running => Ending == TourEnding.Running;

      /// <summary>The step showing, or null once the tour has ended.</summary>
      public TourStep Current => Running && Index >= 0 && Index < Tour.Steps.Count ? Tour.Steps[Index] : null;

      /// <summary>True when there is a step before this one that can be shown.</summary>
      public bool CanGoBack => Running && PreviousShowable(Index) >= 0;

      /// <summary>
      /// Where the reader is, for the callout: "Step 3 of 7".
      ///
      /// Counted over the whole tour rather than over the steps this
      /// installation will actually be shown, and the numbers therefore skip
      /// where a step is skipped. That is deliberate: whether a control is on
      /// the screen is only knowable once the page holding it is open, so a
      /// denominator of "the steps you will see" cannot be computed without
      /// opening every page first, and one that shrinks while somebody walks
      /// is a worse lie than a number that jumps.
      /// </summary>
      public string Position => F("Step {0} of {1}", Index + 1, Tour.Steps.Count);

      /// <summary>
      /// Opens the tour on the first step that can be shown from where it
      /// starts. Returns false when it ended immediately, which the caller must
      /// handle: a tour whose very first target is missing has nothing to draw.
      /// </summary>
      public bool Begin() => Settle(Index);

      /// <summary>Moves to the next step that can be shown. False once the tour has ended.</summary>
      public bool Next()
      {
         if (!Running)
            return false;

         if (Index + 1 >= Tour.Steps.Count)
         {
            Ending = TourEnding.Finished;
            return false;
         }

         return Settle(Index + 1);
      }

      /// <summary>
      /// Moves back to the previous step that can be shown. Standing still when
      /// there is none, rather than ending the tour: Back is how somebody
      /// re-reads a sentence, and a Back that closed the tour would be a trap.
      /// </summary>
      public bool Back()
      {
         if (!Running)
            return false;

         int previous = PreviousShowable(Index);
         if (previous < 0)
            return true;

         Index = previous;
         conditionAtEntry_ = ConditionOf(Tour.Steps[Index]);
         return true;
      }

      /// <summary>The reader left. Never records the tour as finished.</summary>
      public void Leave()
      {
         if (Running)
            Ending = TourEnding.Left;
      }

      /// <summary>
      /// Asks whether the thing the current step was waiting for has happened,
      /// and moves on if it has. The surface calls it after anything that could
      /// have changed the answer - a dialog closing, a list reloading. It never
      /// blocks anything: a step with no condition, or one whose condition was
      /// already true when the reader arrived, simply stays until Next.
      /// </summary>
      public bool Advanced()
      {
         if (!Running || Current == null || string.IsNullOrEmpty(Current.Until))
            return false;

         if (conditionAtEntry_ || !ConditionOf(Current))
            return false;

         Next();
         return true;
      }

      /// <summary>
      /// The sentence the reader is left with. Every ending says something:
      /// silence after a tour that stopped on its own is how somebody decides
      /// the feature is broken.
      /// </summary>
      public string Farewell()
      {
         switch (Ending)
         {
            case TourEnding.Finished:
               return F("That is the end of {0}. You can start it again whenever you like.", L(Tour.Name));
            case TourEnding.Missing:
               return F("{0} stops here: the next thing it points at is not on this screen.", L(Tour.Name));
            case TourEnding.Left:
               return F("{0} closed. It picks up where you left off.", L(Tour.Name));
            default:
               return "";
         }
      }

      /// <summary>
      /// The step to record as the resume point: where the reader is if they
      /// left, and the start if the tour ran out of steps to show - coming back
      /// to a finished tour and landing on its last stop teaches nothing.
      /// </summary>
      public int ResumePoint => Ending == TourEnding.Left ? Index : 0;

      /// <summary>
      /// From <paramref name="from"/> onwards, the first step whose target is
      /// on the screen, ending the tour when a step that may not be skipped is
      /// not. Forwards only: going back never skips past a missing step, it
      /// stands still, so Back has its own walk below.
      /// </summary>
      private bool Settle(int from)
      {
         for (int at = from; at < Tour.Steps.Count; at++)
         {
            TourStep step = Tour.Steps[at];
            if (Shows(step))
            {
               Index = at;
               conditionAtEntry_ = ConditionOf(step);
               return true;
            }

            if (step.WhenMissing == WhenMissing.EndTour)
            {
               Index = at;
               MissingStep = step;
               Ending = TourEnding.Missing;
               return false;
            }
         }

         Ending = TourEnding.Finished;
         return false;
      }

      /// <summary>The nearest earlier step that can be shown, or -1.</summary>
      private int PreviousShowable(int from)
      {
         for (int at = from - 1; at >= 0; at--)
         {
            if (Shows(Tour.Steps[at]))
               return at;
         }

         return -1;
      }

      private bool Shows(TourStep step) => step != null && !string.IsNullOrEmpty(step.Target) && present_(step);

      private bool ConditionOf(TourStep step) => step != null && !string.IsNullOrEmpty(step.Until) && satisfied_(step.Until);
   }
}
