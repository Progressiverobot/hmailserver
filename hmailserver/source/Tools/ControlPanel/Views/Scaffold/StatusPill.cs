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
   /// A status as a capsule: the shape and the colour of its level
   /// (<see cref="StatusSemantics"/>) and a short word - "Connected", "Backlog",
   /// "Expires in 12 days". The shell's server status is one; a page header's
   /// status is one; a grid cell that says the state of a row is one. Its
   /// accessible name leads with the severity word, so "Warning: Backlog" is
   /// heard where "Backlog" is seen beside a triangle.
   ///
   /// <code>
   /// &lt;scaffold:StatusPill Level="Good" Text="{loc:L 'Connected'}" /&gt;
   /// </code>
   /// </summary>
   public class StatusPill : ScaffoldControl
   {
      static StatusPill()
      {
         DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusPill), new FrameworkPropertyMetadata(typeof(StatusPill)));
      }

      public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
         nameof(Level), typeof(StatusLevel), typeof(StatusPill), new PropertyMetadata(StatusLevel.Normal, OnPresentationChanged));

      public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
         nameof(Text), typeof(string), typeof(StatusPill), new PropertyMetadata(null, OnPresentationChanged, NullWhenEmpty));

      /// <summary>The level, which decides the colour and the shape.</summary>
      public StatusLevel Level
      {
         get => (StatusLevel)GetValue(LevelProperty);
         set => SetValue(LevelProperty, value);
      }

      /// <summary>The word or two on the pill.</summary>
      public string Text
      {
         get => (string)GetValue(TextProperty);
         set => SetValue(TextProperty, value);
      }

      protected override AutomationControlType ControlType => AutomationControlType.Text;

      // The name this component last gave itself. A page that sets a fuller
      // sentence of its own - the shell's "Connected to host as user" - keeps
      // it: the component only replaces a name it wrote.
      private string ownName_;

      public override void OnApplyTemplate()
      {
         base.OnApplyTemplate();
         Apply_();
      }

      private static void OnPresentationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => ((StatusPill)d).Apply_();

      private void Apply_()
      {
         StatusPresentation status = StatusSemantics.For(Level);
         string text = Text ?? "";

         // Normal is the level that says nothing, and it is drawn in the neutral
         // grey rather than in StatusSemantics' primary text: a capsule outlined
         // in the text colour reads as the loudest thing on the bar.
         string brushKey = Level == StatusLevel.Normal ? "AppNeutralBrush" : status.BrushKey;

         string current = AutomationProperties.GetName(this);
         if (string.IsNullOrEmpty(current) || string.Equals(current, ownName_, System.StringComparison.Ordinal))
         {
            ownName_ = Level == StatusLevel.Normal ? text : status.SeverityWord + ": " + text;
            AutomationProperties.SetName(this, ownName_);
         }

         if (Part<Path>("PART_Mark") is { } mark)
            ShapeMarkVisuals.ApplyMark(mark, status.Shape, brushKey);

         if (Part<TextBlock>("PART_Text") is { } label)
            label.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

         if (Part<Border>("PART_Frame") is { } frame)
            frame.SetResourceReference(Border.BorderBrushProperty, brushKey);
      }
   }
}
