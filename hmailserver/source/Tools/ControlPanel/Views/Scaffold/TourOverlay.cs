// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using hMailServer.ControlPanel.Services;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// What a tour looks like: a ring around the control the step points at, and
   /// a small card beside it with the sentence and Back, Next and Skip.
   ///
   /// Built in code rather than templated in Scaffold.xaml, because it is not
   /// laid out in a page - it floats over one, positioned from the target's own
   /// bounds - and a template cannot express that. Everything else about it
   /// follows the design system: the tokens for spacing and radius, the theme's
   /// brushes by key so they follow a theme change, and the level's shape as
   /// well as its colour.
   ///
   /// Three properties of it are the feature, not decoration:
   ///
   /// * **It never blocks the product.** The overlay is a <see cref="Canvas"/>
   ///   with no background, so it is hit-testable only where the card actually
   ///   is; every pixel of the page underneath - including the control being
   ///   pointed at - stays clickable while the tour runs.
   /// * **It never takes the keyboard.** Nothing here is focused when a step
   ///   opens. The card's buttons are ordinary tab stops that a reader reaches
   ///   when they want them, and the shell gives them F6; the page keeps the
   ///   focus it had. They are also the only captions in the application with
   ///   no Alt key, deliberately: this card floats over whichever page the step
   ///   is on, so any key it claimed would sooner or later be a key that page
   ///   already owns, and an access key that does the wrong thing on one page
   ///   in forty-seven is worse than none. F6 reaches the card, Tab moves
   ///   inside it, Enter presses, Escape leaves.
   /// * **The highlight is not colour alone.** A solid ring in the accent and a
   ///   dashed ring inside it in the text colour, so it survives greyscale,
   ///   colour blindness and High Contrast - where the accent becomes the
   ///   system highlight and the dashes are what still separate the ring from
   ///   an ordinary focus rectangle.
   /// </summary>
   public sealed class TourOverlay : Canvas
   {
      private const double CardWidth = 340;
      private const double Gap = DesignTokens.Space.Md;

      private readonly Border ring_ = new();
      private readonly Rectangle dashes_ = new();
      private readonly Border card_ = new();
      private readonly TextBlock tourName_ = new();
      private readonly TextBlock position_ = new();
      private readonly TextBlock sentence_ = new();
      private readonly Wpf.Ui.Controls.Button back_ = new();
      private readonly Wpf.Ui.Controls.Button next_ = new();
      private readonly Wpf.Ui.Controls.Button finish_ = new();
      private readonly Wpf.Ui.Controls.Button skip_ = new();

      private FrameworkElement target_;

      public TourOverlay()
      {
         // Nothing here is a tab stop until a step is showing, and the canvas
         // itself never is: it covers the page, and a focusable element the
         // size of the page would swallow a Tab press from anywhere.
         Focusable = false;
         Visibility = Visibility.Collapsed;

         ring_.BorderThickness = new Thickness(2);
         ring_.CornerRadius = new CornerRadius(DesignTokens.Radius.Control);
         ring_.IsHitTestVisible = false;
         ring_.SetResourceReference(Border.BorderBrushProperty, "AppBrandBrush");

         dashes_.StrokeThickness = 1;
         dashes_.StrokeDashArray = new DoubleCollection(new[] { 3.0, 3.0 });
         dashes_.RadiusX = DesignTokens.Radius.Control;
         dashes_.RadiusY = DesignTokens.Radius.Control;
         dashes_.IsHitTestVisible = false;
         dashes_.SetResourceReference(Shape.StrokeProperty, "TextFillColorPrimaryBrush");

         tourName_.SetResourceReference(StyleProperty, "TextCaption");
         position_.SetResourceReference(StyleProperty, "TextCaption");
         sentence_.TextWrapping = TextWrapping.Wrap;
         sentence_.Margin = new Thickness(0, DesignTokens.Space.Sm, 0, DesignTokens.Space.Md);
         sentence_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

         back_.Content = L("Back");
         back_.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
         back_.Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0);
         back_.Click += (s, e) => Back?.Invoke();

         next_.Content = L("Next");
         next_.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
         next_.Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0);
         next_.Click += (s, e) => Next?.Invoke();

         // A button of its own rather than Next re-captioned: a control whose
         // caption changes under the reader is one a screen reader has already
         // announced by the old name. Only one of the two is ever visible.
         finish_.Content = L("Finish");
         finish_.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
         finish_.Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0);
         finish_.Visibility = Visibility.Collapsed;
         finish_.Click += (s, e) => Next?.Invoke();

         skip_.Content = L("Skip the tour");
         skip_.Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent;
         skip_.Click += (s, e) => Skip?.Invoke();

         var head = new Grid();
         head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         head.Children.Add(tourName_);
         Grid.SetColumn(position_, 1);
         head.Children.Add(position_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal };
         buttons.Children.Add(back_);
         buttons.Children.Add(next_);
         buttons.Children.Add(finish_);
         buttons.Children.Add(skip_);

         var body = new StackPanel();
         body.Children.Add(head);
         body.Children.Add(sentence_);
         body.Children.Add(buttons);

         card_.Width = CardWidth;
         card_.Padding = new Thickness(DesignTokens.Space.Lg);
         card_.CornerRadius = new CornerRadius(DesignTokens.Radius.Card);
         card_.BorderThickness = new Thickness(1);
         card_.Child = body;
         card_.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
         card_.SetResourceReference(Border.BorderBrushProperty, "AppBrandBrush");

         // The card is the live region: a screen reader hears the step's
         // sentence when it changes without the tour having to steal focus to
         // make that happen.
         AutomationProperties.SetLiveSetting(card_, AutomationLiveSetting.Polite);

         Children.Add(ring_);
         Children.Add(dashes_);
         Children.Add(card_);
      }

      /// <summary>Raised by the card's buttons. The shell owns what they mean.</summary>
      public event Action Next;

      public event Action Back;

      public event Action Skip;

      /// <summary>True while a step is showing.</summary>
      public bool Showing => Visibility == Visibility.Visible;

      /// <summary>
      /// Shows a step: the ring around <paramref name="target"/>, the card
      /// beside it. <paramref name="lastStep"/> makes Next read <em>Finish</em>,
      /// because a Next on the last step of a tour promises one more stop that
      /// does not exist.
      /// </summary>
      public void ShowStep(FrameworkElement target, string tourName, string position, string sentence,
         bool canGoBack, bool lastStep)
      {
         target_ = target;
         tourName_.Text = tourName ?? "";
         position_.Text = position ?? "";
         sentence_.Text = sentence ?? "";
         back_.Visibility = canGoBack ? Visibility.Visible : Visibility.Collapsed;
         next_.Visibility = lastStep ? Visibility.Collapsed : Visibility.Visible;
         finish_.Visibility = lastStep ? Visibility.Visible : Visibility.Collapsed;
         AutomationProperties.SetName(card_, (tourName ?? "") + ". " + (position ?? "") + ". " + (sentence ?? ""));

         Visibility = Visibility.Visible;
         target?.BringIntoView();
         Reposition();
         Announce(sentence);
      }

      /// <summary>Takes the tour off the screen. Safe to call when nothing is showing.</summary>
      public void Hide()
      {
         target_ = null;
         Visibility = Visibility.Collapsed;
      }

      /// <summary>Puts the keyboard on the card - what F6 does while a tour runs.</summary>
      public void FocusCard() => (next_.IsVisible ? (Control)next_ : finish_.IsVisible ? finish_ : skip_).Focus();

      /// <summary>True when the focus is somewhere inside the card, which is what makes Escape the tour's.</summary>
      public bool HasFocusWithin => card_.IsKeyboardFocusWithin;

      /// <summary>
      /// Puts the ring where the target is now and the card where there is room
      /// for it. Called on a timer while a step shows, because the page under
      /// the tour goes on living: it scrolls, it reloads, the window resizes,
      /// and a ring left behind would be pointing at nothing.
      /// </summary>
      public void Reposition()
      {
         if (!Showing)
            return;

         if (target_ == null || !target_.IsVisible || ActualWidth <= 0)
         {
            ring_.Visibility = Visibility.Collapsed;
            dashes_.Visibility = Visibility.Collapsed;
            return;
         }

         Rect bounds;
         try
         {
            GeneralTransform transform = target_.TransformToVisual(this);
            bounds = transform.TransformBounds(new Rect(0, 0, target_.ActualWidth, target_.ActualHeight));
         }
         catch (InvalidOperationException)
         {
            // The target left the visual tree between the timer tick and here -
            // a page reloading its list under the tour. The step is still the
            // step; the ring simply has nothing to draw until it comes back.
            ring_.Visibility = Visibility.Collapsed;
            dashes_.Visibility = Visibility.Collapsed;
            return;
         }

         bounds.Inflate(DesignTokens.Space.Xs, DesignTokens.Space.Xs);
         ring_.Visibility = Visibility.Visible;
         dashes_.Visibility = Visibility.Visible;
         Place(ring_, bounds);
         ring_.Width = bounds.Width;
         ring_.Height = bounds.Height;
         dashes_.Width = Math.Max(0, bounds.Width - 6);
         dashes_.Height = Math.Max(0, bounds.Height - 6);
         Place(dashes_, new Rect(bounds.X + 3, bounds.Y + 3, dashes_.Width, dashes_.Height));

         // The laid-out height where there is one; a measure only on the first
         // step, before the card has ever been arranged. Measuring on every
         // tick would invalidate layout five times a second for nothing.
         double height = card_.ActualHeight;
         if (height <= 0)
         {
            card_.Measure(new Size(CardWidth, double.PositiveInfinity));
            height = card_.DesiredSize.Height;
         }

         // Under the target if it fits, above it if not, and clamped into the
         // overlay either way: a card half off the bottom of the window is a
         // sentence nobody reads.
         double top = bounds.Bottom + Gap;
         if (top + height > ActualHeight)
            top = Math.Max(0, bounds.Top - Gap - height);
         top = Math.Max(0, Math.Min(top, Math.Max(0, ActualHeight - height)));

         double left = Math.Min(bounds.Left, Math.Max(0, ActualWidth - CardWidth));
         SetLeft(card_, Math.Max(0, left));
         SetTop(card_, top);
      }

      private static void Place(UIElement element, Rect at)
      {
         SetLeft(element, at.X);
         SetTop(element, at.Y);
      }

      /// <summary>
      /// Says the sentence to a screen reader without moving the focus. The
      /// live region above covers a reader that is watching the card; this
      /// covers one that is not, and it is the whole reason a tour is usable
      /// without sight.
      /// </summary>
      private void Announce(string sentence)
      {
         if (string.IsNullOrEmpty(sentence))
            return;

         try
         {
            AutomationPeer peer = UIElementAutomationPeer.FromElement(card_) ?? UIElementAutomationPeer.CreatePeerForElement(card_);
            peer?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
               AutomationNotificationProcessing.MostRecent, sentence, "hMailServerTour");
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            // No automation client listening, or a peer that cannot be made.
            // A tour that threw because nothing was listening would be worse
            // than one that is quiet.
         }
      }
   }
}
