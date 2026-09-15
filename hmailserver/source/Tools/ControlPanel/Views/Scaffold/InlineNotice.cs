// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Shapes;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// A notice in the flow of a page: information, success, a warning or a
   /// failure, with the sentence that goes with it and room for one action. It
   /// carries its level in the three channels every status in this application
   /// carries - the colour, the shape and the word from
   /// <see cref="StatusSemantics"/> - so the level survives greyscale, colour
   /// blindness and High Contrast, where the tint behind it goes to zero and the
   /// outline stays.
   ///
   /// It is a polite live region: a notice that appears while the user is
   /// working is announced without cutting them off. Set
   /// <c>AutomationProperties.LiveSetting="Assertive"</c> on one that must
   /// interrupt, as the sign-in failure does.
   ///
   /// <code>
   /// &lt;scaffold:InlineNotice Level="Warning" Text="{loc:L '...'}" /&gt;
   /// </code>
   /// </summary>
   public class InlineNotice : ScaffoldContentControl
   {
      static InlineNotice()
      {
         DefaultStyleKeyProperty.OverrideMetadata(typeof(InlineNotice), new FrameworkPropertyMetadata(typeof(InlineNotice)));
      }

      public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
         nameof(Level), typeof(StatusLevel), typeof(InlineNotice), new PropertyMetadata(StatusLevel.Information, OnPresentationChanged));

      public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
         nameof(Text), typeof(string), typeof(InlineNotice), new PropertyMetadata(null, OnPresentationChanged, NullWhenEmpty));

      public InlineNotice()
      {
         AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
      }

      /// <summary>How much attention the notice asks for. Information by default.</summary>
      public StatusLevel Level
      {
         get => (StatusLevel)GetValue(LevelProperty);
         set => SetValue(LevelProperty, value);
      }

      /// <summary>The sentence. The severity word is drawn before it by the component, so the text need not repeat it.</summary>
      public string Text
      {
         get => (string)GetValue(TextProperty);
         set => SetValue(TextProperty, value);
      }

      protected override AutomationControlType ControlType => AutomationControlType.Text;

      // The name this component last gave itself; a fuller one a page set stays
      // (see StatusPill).
      private string ownName_;

      public override void OnApplyTemplate()
      {
         base.OnApplyTemplate();
         Apply_();
      }

      private static void OnPresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => ((InlineNotice)d).Apply_();

      /// <summary>
      /// Paints the level into the template's parts. Brushes go by resource key,
      /// never by instance: ThemeTokens republishes the status brushes on every
      /// theme change, and a notice holding a resolved brush would keep the old
      /// theme's colour.
      /// </summary>
      private void Apply_()
      {
         StatusPresentation status = StatusSemantics.For(Level);
         string text = Text ?? "";

         // Normal is the level that says nothing, drawn in the neutral grey
         // rather than in StatusSemantics' primary text - the same choice the
         // pill makes.
         string brushKey = Level == StatusLevel.Normal ? "AppNeutralBrush" : status.BrushKey;

         string current = AutomationProperties.GetName(this);
         if (string.IsNullOrEmpty(current) || string.Equals(current, ownName_, System.StringComparison.Ordinal))
         {
            ownName_ = Level == StatusLevel.Normal ? text : status.SeverityWord + ": " + text;
            AutomationProperties.SetName(this, ownName_);
         }

         if (Part<Path>("PART_Mark") is { } mark)
            ShapeMarkVisuals.ApplyMark(mark, status.Shape, brushKey);

         if (Part<TextBlock>("PART_Word") is { } word)
         {
            // Normal is the level that says nothing, so it says nothing here
            // either: a notice at that level is a neutral statement, and the
            // word "Normal" in front of it reads as a status that is not one -
            // which is what a page wave found in a render and had to work
            // around by never using the level. The accessible name has always
            // left the word out at Normal (above); the visual agrees with it.
            word.Visibility = Level == StatusLevel.Normal ? Visibility.Collapsed : Visibility.Visible;
            word.Text = status.SeverityWord;
            word.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
         }

         if (Part<Border>("PART_Tint") is { } tint)
            tint.SetResourceReference(Border.BackgroundProperty, brushKey);

         if (Part<Border>("PART_Frame") is { } frame)
            frame.SetResourceReference(Border.BorderBrushProperty, brushKey);
      }
   }
}
