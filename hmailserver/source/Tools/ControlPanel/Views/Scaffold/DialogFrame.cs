// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// The standard shape of a dialog's content: a heading with an optional
   /// description, a body that scrolls when the dialog has reached the height
   /// it may have, and a footer with the primary button first and the secondary
   /// after it (the Windows order, as every dialog footer here already has), with
   /// room on the footer's left for a note. <see cref="FluentDialogWindow.UseFrame"/>
   /// builds one, makes it the window's content and sizes the window to it.
   /// </summary>
   public class DialogFrame : ScaffoldControl
   {
      static DialogFrame()
      {
         DefaultStyleKeyProperty.OverrideMetadata(typeof(DialogFrame), new FrameworkPropertyMetadata(typeof(DialogFrame)));
      }

      public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
         nameof(Heading), typeof(string), typeof(DialogFrame), new PropertyMetadata(null, OnHeadingChanged, NullWhenEmpty));

      public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
         nameof(Description), typeof(string), typeof(DialogFrame), new PropertyMetadata(null, null, NullWhenEmpty));

      public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
         nameof(Body), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

      public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
         nameof(Footer), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

      public static readonly DependencyProperty PrimaryButtonProperty = DependencyProperty.Register(
         nameof(PrimaryButton), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

      public static readonly DependencyProperty SecondaryButtonProperty = DependencyProperty.Register(
         nameof(SecondaryButton), typeof(object), typeof(DialogFrame), new PropertyMetadata(null));

      /// <summary>The dialog's heading, in the subtitle size. The window title says the same, shorter.</summary>
      public string Heading
      {
         get => (string)GetValue(HeadingProperty);
         set => SetValue(HeadingProperty, value);
      }

      /// <summary>A line under the heading saying what the dialog decides.</summary>
      public string Description
      {
         get => (string)GetValue(DescriptionProperty);
         set => SetValue(DescriptionProperty, value);
      }

      /// <summary>The fields. Scrolls past the dialog's maximum height.</summary>
      public object Body
      {
         get => GetValue(BodyProperty);
         set => SetValue(BodyProperty, value);
      }

      /// <summary>A note or a check box at the footer's left, before the buttons.</summary>
      public object Footer
      {
         get => GetValue(FooterProperty);
         set => SetValue(FooterProperty, value);
      }

      /// <summary>The button that does the thing - Save, Add, Apply. Takes Enter.</summary>
      public object PrimaryButton
      {
         get => GetValue(PrimaryButtonProperty);
         set => SetValue(PrimaryButtonProperty, value);
      }

      /// <summary>The button that does not - Cancel, Close. Takes Escape.</summary>
      public object SecondaryButton
      {
         get => GetValue(SecondaryButtonProperty);
         set => SetValue(SecondaryButtonProperty, value);
      }

      protected override AutomationControlType ControlType => AutomationControlType.Pane;

      private static void OnHeadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => AutomationProperties.SetName(d, (string)e.NewValue ?? "");
   }
}
