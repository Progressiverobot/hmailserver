// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // ButtonBase, for the chevron RepeatButtons
using System.Windows.Input;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Makes an overflowing tab strip reachable with a mouse.
   ///
   /// THE DEFECT. The Control Panel replaces the stock WPF tab strip, which wraps
   /// its headers onto several rows, with a single row inside a ScrollViewer whose
   /// scrollbar is hidden. That reads far better - right up to the point where the
   /// tabs no longer fit. AccountDialog has eleven of them. Past the edge of the
   /// dialog they were still there, still focusable with the arrow keys, and
   /// completely unreachable with a mouse: no scrollbar, no buttons, and nothing on
   /// screen to suggest anything had been cut off. A dialog that hides four of its
   /// eleven tabs from anyone who navigates by pointer is not a styling problem.
   ///
   /// THE FIX has three parts, and all three are needed.
   ///
   /// The wheel scrolls the strip horizontally. A vertical wheel gesture is what
   /// people try first on a horizontal strip, and a ScrollViewer with no vertical
   /// range does nothing with it - so the gesture that feels most natural was also
   /// the one that most convincingly said "there is nothing more here".
   ///
   /// Two chevron buttons appear at the ends, but only while there is somewhere to
   /// go, and each disables itself at its own end of the range. Permanently visible
   /// buttons would put furniture on every dialog whose tabs fit, which is most of
   /// them; buttons that are always enabled would claim there is more to see when
   /// there is not.
   ///
   /// Selecting a tab by keyboard scrolls it into view. Arrow-key navigation
   /// already moved selection to tabs that were off screen, so the selected tab
   /// could be one nobody could see - which is a worse state than not being able to
   /// reach it, because the dialog is then showing a page whose tab is invisible.
   ///
   /// It is attached in the TabControl template in App.xaml, so every tabbed dialog
   /// gets it without opting in. Attaching it per dialog is how AccountDialog would
   /// have been fixed and DomainDialog would not.
   /// </summary>
   public static class TabStripScrolling
   {
      /// <summary>
      /// How far one chevron click travels. A shade under a full viewport, so a
      /// tab that straddles the edge is not skipped over entirely - the usual
      /// complaint about page-at-a-time scrolling.
      /// </summary>
      private const double PageFraction = 0.8;

      /// <summary>How far one wheel notch travels.</summary>
      private const double WheelStep = 48.0;

      /// <summary>
      /// Sub-pixel slack. Scroll offsets are doubles and a maxed-out ScrollViewer
      /// routinely lands a fraction short of ScrollableWidth, which would leave
      /// the right-hand chevron enabled at the end of the strip, clickable, and
      /// doing nothing.
      /// </summary>
      private const double Epsilon = 0.5;

      public static readonly DependencyProperty LeftButtonProperty =
         DependencyProperty.RegisterAttached(
            "LeftButton", typeof(ButtonBase), typeof(TabStripScrolling),
            new PropertyMetadata(null, OnButtonChanged));

      public static readonly DependencyProperty RightButtonProperty =
         DependencyProperty.RegisterAttached(
            "RightButton", typeof(ButtonBase), typeof(TabStripScrolling),
            new PropertyMetadata(null, OnButtonChanged));

      public static void SetLeftButton(DependencyObject element, ButtonBase value) =>
         element.SetValue(LeftButtonProperty, value);

      public static ButtonBase GetLeftButton(DependencyObject element) =>
         (ButtonBase) element.GetValue(LeftButtonProperty);

      public static void SetRightButton(DependencyObject element, ButtonBase value) =>
         element.SetValue(RightButtonProperty, value);

      public static ButtonBase GetRightButton(DependencyObject element) =>
         (ButtonBase) element.GetValue(RightButtonProperty);

      private static void OnButtonChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
      {
         if (target is not ScrollViewer viewer)
            return;

         if (args.OldValue is ButtonBase old)
            old.Click -= OnChevronClick;

         if (args.NewValue is ButtonBase added)
         {
            // The handler reads which end it is from the sender's identity, so it
            // needs to be able to get back to the viewer.
            added.Tag = viewer;
            added.Click -= OnChevronClick;
            added.Click += OnChevronClick;
         }

         Attach(viewer);
         Update(viewer);
      }

      private static void Attach(ScrollViewer viewer)
      {
         // Both attached properties are set in the same template, so this runs
         // twice per strip. Unsubscribing first keeps that idempotent rather than
         // scrolling two notches per wheel click.
         viewer.ScrollChanged -= OnScrollChanged;
         viewer.ScrollChanged += OnScrollChanged;

         viewer.PreviewMouseWheel -= OnMouseWheel;
         viewer.PreviewMouseWheel += OnMouseWheel;

         viewer.Loaded -= OnLoaded;
         viewer.Loaded += OnLoaded;
      }

      private static void OnLoaded(object sender, RoutedEventArgs args)
      {
         if (sender is not ScrollViewer viewer)
            return;

         Update(viewer);
         BringSelectedIntoView(viewer);
      }

      private static void OnScrollChanged(object sender, ScrollChangedEventArgs args)
      {
         if (sender is ScrollViewer viewer)
         {
            Update(viewer);

            // ExtentWidthChange fires when tabs are added or removed, which is
            // also when a selection that was comfortably in view can stop being.
            if (Math.Abs(args.ExtentWidthChange) > Epsilon)
               BringSelectedIntoView(viewer);
         }
      }

      private static void OnMouseWheel(object sender, MouseWheelEventArgs args)
      {
         if (sender is not ScrollViewer viewer || viewer.ScrollableWidth <= Epsilon)
            return;

         viewer.ScrollToHorizontalOffset(
            Clamp(viewer.HorizontalOffset - Math.Sign(args.Delta) * WheelStep, viewer.ScrollableWidth));

         // Handled, or the gesture keeps travelling up to whatever scrollable
         // panel the dialog sits in and scrolls the page behind the tabs instead.
         args.Handled = true;
      }

      private static void OnChevronClick(object sender, RoutedEventArgs args)
      {
         if (sender is not ButtonBase button || button.Tag is not ScrollViewer viewer)
            return;

         double step = Math.Max(WheelStep, viewer.ViewportWidth * PageFraction);
         bool left = ReferenceEquals(GetLeftButton(viewer), button);

         viewer.ScrollToHorizontalOffset(
            Clamp(viewer.HorizontalOffset + (left ? -step : step), viewer.ScrollableWidth));
      }

      private static void Update(ScrollViewer viewer)
      {
         bool overflowing = viewer.ScrollableWidth > Epsilon;

         ButtonBase left = GetLeftButton(viewer);
         ButtonBase right = GetRightButton(viewer);

         if (left != null)
         {
            left.Visibility = overflowing ? Visibility.Visible : Visibility.Collapsed;
            left.IsEnabled = overflowing && viewer.HorizontalOffset > Epsilon;
         }

         if (right != null)
         {
            right.Visibility = overflowing ? Visibility.Visible : Visibility.Collapsed;
            right.IsEnabled = overflowing &&
                              viewer.HorizontalOffset < viewer.ScrollableWidth - Epsilon;
         }
      }

      /// <summary>
      /// Scrolls the selected tab into view, so keyboard navigation past the edge
      /// does not leave the dialog showing a page whose tab is off screen.
      /// </summary>
      private static void BringSelectedIntoView(ScrollViewer viewer)
      {
         if (viewer.Content is not Panel panel)
            return;

         foreach (object child in panel.Children)
         {
            if (child is TabItem { IsSelected: true } selected)
            {
               // Deferred: during a Loaded or an extent change the item may not
               // have been arranged yet, and BringIntoView on a zero-sized element
               // scrolls to the wrong place or to nowhere at all.
               selected.Dispatcher.BeginInvoke(
                  System.Windows.Threading.DispatcherPriority.Loaded,
                  new Action(() =>
                  {
                     try
                     {
                        selected.BringIntoView();
                     }
                     catch (Exception)
                     {
                        // A tab removed between the post and the callback. Losing
                        // the scroll position is not worth taking the dialog down.
                     }
                  }));
               return;
            }
         }
      }

      private static double Clamp(double offset, double scrollable)
      {
         if (offset < 0)
            return 0;

         return offset > scrollable ? scrollable : offset;
      }
   }
}
