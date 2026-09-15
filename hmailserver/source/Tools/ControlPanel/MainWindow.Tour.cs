// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using hMailServer.ControlPanel.Services;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel
{
   /// <summary>
   /// The shell's half of a tour: opening the page a step is on, finding the
   /// control by its automation id, keeping the ring on it while the page moves
   /// underneath, answering the questions a step waits on, and remembering what
   /// this administrator has finished.
   ///
   /// The decisions - which step is showing, what Next and Back do, when the
   /// tour ended and why - are all in <see cref="TourSession"/>, which knows
   /// nothing about WPF and is tested without it. What is here is only the part
   /// that genuinely needs a window.
   /// </summary>
   public partial class MainWindow
   {
      /// <summary>
      /// Where the record of finished tours and resume points is kept: beside
      /// the window bounds, the palette's history and the sidebar's collapsed
      /// state, under the same key, because it is the same kind of thing - a
      /// per-user convenience worth nothing on another machine and never
      /// required for the application to work.
      /// </summary>
      private const string TourProgressValue = "TourProgress"; // no-loc

      /// <summary>
      /// How often the ring is put back where the control is. The page under a
      /// tour goes on living - it scrolls, it reloads its list, the window
      /// resizes - and a ring left where the control used to be is worse than
      /// no ring. Five times a second is under the threshold at which a reader
      /// notices the ring lagging, and a transform of one element is nothing.
      /// </summary>
      private static readonly TimeSpan TourTick = TimeSpan.FromMilliseconds(200);

      /// <summary>
      /// Every fifteenth tick - three seconds - the step's condition is asked
      /// about. It is one COM call on a step that has a condition at all, and
      /// none on a step that has not.
      /// </summary>
      private const int TicksPerConditionCheck = 15;

      private TourProgress tourProgress_ = new();
      private TourSession tour_;
      private DispatcherTimer tourTimer_;
      private int tourTicks_;

      // Where the keyboard was when the tour started. A tour that does not put
      // it back has taken something from the reader that it never asked for.
      private IInputElement focusBeforeTour_;

      /// <summary>True while a tour is running, which is what arms Escape and F6.</summary>
      public bool TourRunning => tour_ != null && tour_.Running;

      /// <summary>Whether this administrator has finished the first-run walk at its current version.</summary>
      public bool HasFinishedFirstRun => tourProgress_.HasFinished(TourCatalog.Find(TourCatalog.FirstRun));

      /// <summary>
      /// Starts a tour, at the step this administrator left it on. Called by the
      /// Welcome page's button, by the Ctrl+K palette and by a page header's
      /// help button; safe to call when one is already running, which restarts
      /// it rather than nesting two.
      /// </summary>
      public void StartTour(string tourId) => StartTour(tourId, -1);

      /// <summary>
      /// The same, forced to a step. <paramref name="startAt"/> below zero means
      /// "where they left off", which is what every entry point but a page's own
      /// help button wants.
      /// </summary>
      public void StartTour(string tourId, int startAt)
      {
         Tour tour = TourCatalog.Find(tourId);
         if (tour == null || !connected_)
            return;

         // Not recorded and not announced: this is not somebody leaving a
         // tour, it is somebody starting one, and the resume point they are
         // about to be taken to is the one already stored.
         if (tour_ != null)
            EndTour(record: false);

         focusBeforeTour_ = Keyboard.FocusedElement;
         tour_ = new TourSession(tour, startAt >= 0 ? startAt : tourProgress_.ResumeAt(tour), StepIsOnScreen_, ConditionMet_);

         if (!tour_.Begin())
         {
            FinishTour_();
            return;
         }

         tourTicks_ = 0;
         tourTimer_ ??= MakeTourTimer_();
         tourTimer_.Start();
         ShowTourStep_();
      }

      /// <summary>
      /// Takes the tour off the screen. <paramref name="record"/> writes where
      /// the reader got to; a sign-out or a lost connection passes false,
      /// because a tour interrupted by the server going away is not a tour
      /// somebody chose to leave.
      /// </summary>
      public void EndTour(bool record)
      {
         tourTimer_?.Stop();
         TourLayer.Hide();

         if (tour_ == null)
            return;

         if (record)
         {
            tour_.Leave();
            tourProgress_.Left(tour_.Tour, tour_.ResumePoint);
            SaveTourProgress_();
            Services.Toast.Info(tour_.Farewell(), L(tour_.Tour.Name));
         }

         tour_ = null;
         RestoreFocusAfterTour_();
      }

      /// <summary>
      /// The shell's handler for a page header's help button. Starts the tour
      /// that visits the page showing, at its first stop on that page, so the
      /// button teaches THIS page rather than restarting a walk somewhere else.
      /// </summary>
      private void OnPageHelpRequested_(object sender, RoutedEventArgs e)
      {
         Tour tour = TourCatalog.ForPage(currentPage_, out int startAt);
         if (tour != null)
            StartTour(tour.Id, startAt);
      }

      /// <summary>
      /// A page has been opened: give its header the tour that visits it, if
      /// one does. Done by the shell rather than by the page, so a page joins a
      /// tour by being named in the catalogue and not by being edited - and a
      /// page that leaves one loses its help button the same way.
      /// </summary>
      private void OfferPageTour_(object page, string key)
      {
         if (page is not DependencyObject root)
            return;

         Tour tour = TourCatalog.ForPage(key);

         // After layout: a header inside a template that has not been applied
         // is not in the visual tree to be found.
         Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
         {
            if (!ReferenceEquals(ContentHost?.Content, page))
               return;

            foreach (Views.Scaffold.PageHeader header in FindHeaders_(root))
               header.TourId = tour?.Id;
         }));
      }

      private static IEnumerable<Views.Scaffold.PageHeader> FindHeaders_(DependencyObject root)
      {
         var queue = new Queue<DependencyObject>();
         queue.Enqueue(root);

         while (queue.Count > 0)
         {
            DependencyObject node = queue.Dequeue();
            if (node is Views.Scaffold.PageHeader header)
               yield return header;

            int children = VisualTreeHelper.GetChildrenCount(node);
            for (int at = 0; at < children; at++)
               queue.Enqueue(VisualTreeHelper.GetChild(node, at));
         }
      }

      /// <summary>
      /// The page changed, or came back with new data. Re-resolves the step's
      /// control: a page that reloads its list replaces the very element the
      /// ring is drawn around, and without this the tour would point at a
      /// control that is no longer in the tree.
      /// </summary>
      private void TourPageChanged_()
      {
         if (!TourRunning)
            return;

         // After the page has been laid out, not before: a control that has not
         // been measured is not in the visual tree yet, and a step would be
         // skipped for being "missing" on a page that does have it.
         Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
         {
            if (TourRunning)
               ShowTourStep_();
         }));
      }

      /// <summary>
      /// Escape and F6 while a tour runs. Escape ends it only when the keyboard
      /// is inside the tour's own card - everywhere else Escape still belongs to
      /// the page, and a tour that ate it would be exactly the "in the way of
      /// the keyboard" the feature promises not to be. F6 is the platform's
      /// "next pane": it is how somebody reaches the tour's buttons from
      /// wherever they are.
      /// </summary>
      private bool HandleTourKey_(KeyEventArgs e)
      {
         if (!TourRunning)
            return false;

         if (e.Key == Key.F6 && Keyboard.Modifiers == ModifierKeys.None)
         {
            TourLayer.FocusCard();
            return true;
         }

         if (e.Key == Key.Escape && TourLayer.HasFocusWithin)
         {
            EndTour(record: true);
            return true;
         }

         return false;
      }

      private DispatcherTimer MakeTourTimer_()
      {
         var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TourTick };
         timer.Tick += (s, e) =>
         {
            if (!TourRunning)
            {
               timer.Stop();
               return;
            }

            TourLayer.Reposition();

            if (++tourTicks_ % TicksPerConditionCheck != 0)
               return;

            if (tour_.Advanced())
            {
               if (tour_.Running)
                  ShowTourStep_();
               else
                  FinishTour_();
            }
         };
         return timer;
      }

      /// <summary>Draws the step that is current: its page, its control, its sentence.</summary>
      private void ShowTourStep_()
      {
         TourStep step = tour_?.Current;
         if (step == null)
            return;

         FrameworkElement target = ResolveStepTarget_(step);
         if (target == null)
         {
            // It was there when the step was chosen and it is not now - a list
            // that reloaded, a card that collapsed. Let the session decide
            // again rather than drawing a ring around nothing.
            if (tour_.Next())
               ShowTourStep_();
            else
               FinishTour_();
            return;
         }

         TourLayer.ShowStep(target, L(tour_.Tour.Name), tour_.Position, L(step.Sentence),
            tour_.CanGoBack, tour_.Index == tour_.Tour.Steps.Count - 1);
      }

      /// <summary>The tour ran out of steps, or could not show one it needed. Say which, and record it.</summary>
      private void FinishTour_()
      {
         tourTimer_?.Stop();
         TourLayer.Hide();

         if (tour_ == null)
            return;

         if (tour_.Ending == TourEnding.Finished)
            tourProgress_.Completed(tour_.Tour);
         else
            tourProgress_.Left(tour_.Tour, tour_.ResumePoint);

         SaveTourProgress_();
         Services.Toast.Info(tour_.Farewell(), L(tour_.Tour.Name));
         tour_ = null;
         RestoreFocusAfterTour_();
      }

      private void RestoreFocusAfterTour_()
      {
         IInputElement was = focusBeforeTour_;
         focusBeforeTour_ = null;

         if (was is UIElement element && element.IsVisible && element.Focusable)
            Keyboard.Focus(was);
      }

      /// <summary>
      /// Whether a step can be shown: opens the page it names and looks for its
      /// control. This is a question with a side effect - the page opens - and
      /// that is unavoidable: a control on a page nobody has opened is not in
      /// any visual tree to be found in. The navigation is marked automatic, so
      /// walking a tour does not rewrite "Most used" in the palette.
      /// </summary>
      private bool StepIsOnScreen_(TourStep step) => ResolveStepTarget_(step) != null;

      private FrameworkElement ResolveStepTarget_(TourStep step)
      {
         if (step == null || string.IsNullOrEmpty(step.Target))
            return null;

         if (!string.IsNullOrEmpty(step.Page) && !string.Equals(step.Page, currentPage_, StringComparison.OrdinalIgnoreCase))
         {
            bool was = automaticNavigation_;
            automaticNavigation_ = true;
            try
            {
               NavigateTo(step.Page);
            }
            finally
            {
               automaticNavigation_ = was;
            }
         }

         try
         {
            // Forces the measure and arrange the page has not had yet, so the
            // control exists to be found on the very first look rather than one
            // dispatcher turn later.
            UpdateLayout();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            return null;
         }

         return FindByAutomationId_(this, step.Target);
      }

      /// <summary>
      /// The element carrying an automation id, anywhere in the window. Breadth
      /// first, so the outermost match wins - a page and a control inside it
      /// could in principle carry the same id, and a ring round the page would
      /// be the less wrong of the two.
      /// </summary>
      private static FrameworkElement FindByAutomationId_(DependencyObject root, string id)
      {
         var queue = new Queue<DependencyObject>();
         queue.Enqueue(root);

         while (queue.Count > 0)
         {
            DependencyObject node = queue.Dequeue();

            if (node is FrameworkElement element && element.IsVisible &&
                string.Equals(AutomationProperties.GetAutomationId(element), id, StringComparison.Ordinal))
            {
               return element;
            }

            int children = VisualTreeHelper.GetChildrenCount(node);
            for (int at = 0; at < children; at++)
               queue.Enqueue(VisualTreeHelper.GetChild(node, at));
         }

         return null;
      }

      /// <summary>
      /// Whether the thing a step is waiting for has happened, asked of the
      /// server rather than of the page: a domain added in a dialog, an account
      /// created, a listener given TLS, a certificate installed. Every one of
      /// them is a count, and a server that cannot be reached answers no -
      /// which leaves the step where it is, which is right, because a tour must
      /// never move on because a call failed.
      /// </summary>
      private bool ConditionMet_(string condition)
      {
         ServerSession session = ServerSession.Current;
         if (session == null || string.IsNullOrEmpty(condition))
            return false;

         try
         {
            switch (condition)
            {
               case "domain-exists":
                  return AnyDomain_(session, accounts: false);

               case "account-exists":
                  return AnyDomain_(session, accounts: true);

               case "tls-listener":
                  return AnySecureListener_(session);

               case "certificate-exists":
                  return Count_(session.Application.Settings.SSLCertificates) > 0;

               default:
                  return false;
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            return false;
         }
      }

      /// <summary>
      /// Whether there is a domain at all, or - with <paramref name="accounts"/>
      /// - a domain with a mailbox in it. Each COM object is released as the
      /// pages do: the walk runs every three seconds while a step waits, and a
      /// leaked interface pointer per tick would be a leak with a clock on it.
      /// </summary>
      private static bool AnyDomain_(ServerSession session, bool accounts)
      {
         dynamic domains = session.Application.Domains;
         try
         {
            int count = (int)domains.Count;
            if (!accounts)
               return count > 0;

            for (int at = 0; at < count; at++)
            {
               dynamic domain = domains.Item[at];
               try
               {
                  if (Count_(domain.Accounts) > 0)
                     return true;
               }
               finally
               {
                  ServerSession.Release(domain);
               }
            }

            return false;
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      /// <summary>Whether any listener offers TLS - SSL/TLS on the port, or STARTTLS on it.</summary>
      private static bool AnySecureListener_(ServerSession session)
      {
         dynamic ports = session.Application.Settings.TCPIPPorts;
         try
         {
            int count = (int)ports.Count;
            for (int at = 0; at < count; at++)
            {
               dynamic port = ports.Item[at];
               try
               {
                  if ((int)port.ConnectionSecurity != 0)
                     return true;
               }
               finally
               {
                  ServerSession.Release(port);
               }
            }

            return false;
         }
         finally
         {
            ServerSession.Release(ports);
         }
      }

      private static int Count_(dynamic collection)
      {
         try
         {
            return (int)collection.Count;
         }
         finally
         {
            ServerSession.Release(collection);
         }
      }

      /// <summary>
      /// Reads the record of finished tours. Anything at all can be in a
      /// registry value, so a damaged one costs the record and nothing else -
      /// being offered a tour a second time is the whole consequence.
      /// </summary>
      private void LoadTourProgress_()
      {
         try
         {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            tourProgress_ = TourProgress.Deserialize(key?.GetValue(TourProgressValue) as string);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            tourProgress_ = new TourProgress();
         }
      }

      private void SaveTourProgress_()
      {
         try
         {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue(TourProgressValue, tourProgress_.Serialize());
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }
      }
   }
}
