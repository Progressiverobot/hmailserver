// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// A section of a settings page: a heading, a description under it saying
   /// what the settings in it decide, and the fields (its content - a stack of
   /// <see cref="FieldRow"/>s, as a rule). The heading is a level-2 heading in
   /// the automation tree, under the page's level-1, so a screen reader can walk
   /// a long settings page section by section.
   ///
   /// Named SettingsSection and not Section on purpose: System.Windows.Documents
   /// has a Section, and a dozen views import that namespace for its Hyperlink.
   ///
   /// <code>
   /// &lt;scaffold:SettingsSection Heading="{loc:L 'Greylisting'}" Description="{loc:L '...'}"&gt;
   ///    &lt;StackPanel&gt;&lt;scaffold:FieldRow .../&gt;&lt;/StackPanel&gt;
   /// &lt;/scaffold:SettingsSection&gt;
   /// </code>
   /// </summary>
   public class SettingsSection : ScaffoldContentControl
   {
      static SettingsSection()
      {
         DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingsSection), new FrameworkPropertyMetadata(typeof(SettingsSection)));
      }

      public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
         nameof(Heading), typeof(string), typeof(SettingsSection), new PropertyMetadata(null, OnHeadingChanged, NullWhenEmpty));

      public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
         nameof(Description), typeof(string), typeof(SettingsSection), new PropertyMetadata(null, OnDescriptionChanged, NullWhenEmpty));

      public SettingsSection()
      {
         AutomationProperties.SetHeadingLevel(this, AutomationHeadingLevel.Level2);
      }

      /// <summary>The section's heading.</summary>
      public string Heading
      {
         get => (string)GetValue(HeadingProperty);
         set => SetValue(HeadingProperty, value);
      }

      /// <summary>What the section's settings decide, in a sentence or two.</summary>
      public string Description
      {
         get => (string)GetValue(DescriptionProperty);
         set => SetValue(DescriptionProperty, value);
      }

      protected override AutomationControlType ControlType => AutomationControlType.Group;

      private static void OnHeadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => AutomationProperties.SetName(d, (string)e.NewValue ?? "");

      private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => AutomationProperties.SetHelpText(d, (string)e.NewValue ?? "");
   }
}
