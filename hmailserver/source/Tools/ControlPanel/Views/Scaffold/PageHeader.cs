// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// The top of every page: the title, an optional subtitle under it, an
   /// optional status pill beside the title, and a slot on the right for the
   /// page's actions (refresh, add, save). It is the page's level-1 heading -
   /// screen readers navigate by headings, and a page without one has no
   /// landmark to jump to - so the heading level, the name and the help text
   /// are on the component itself, where its automation peer reports them.
   ///
   /// <code>
   /// &lt;scaffold:PageHeader Title="{loc:L 'Dashboard'}" Subtitle="{loc:L '...'}"&gt;
   ///    &lt;scaffold:PageHeader.Actions&gt;&lt;ui:Button .../&gt;&lt;/scaffold:PageHeader.Actions&gt;
   /// &lt;/scaffold:PageHeader&gt;
   /// </code>
   /// </summary>
   public class PageHeader : ScaffoldControl
   {
      static PageHeader()
      {
         DefaultStyleKeyProperty.OverrideMetadata(typeof(PageHeader), new FrameworkPropertyMetadata(typeof(PageHeader)));
      }

      public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
         nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata(null, OnTitleChanged, NullWhenEmpty));

      public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
         nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata(null, OnSubtitleChanged, NullWhenEmpty));

      public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
         nameof(Actions), typeof(object), typeof(PageHeader), new PropertyMetadata(null));

      public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
         nameof(Status), typeof(object), typeof(PageHeader), new PropertyMetadata(null));

      public PageHeader()
      {
         AutomationProperties.SetHeadingLevel(this, AutomationHeadingLevel.Level1);
      }

      /// <summary>The page title - the same words as its navigation entry.</summary>
      public string Title
      {
         get => (string)GetValue(TitleProperty);
         set => SetValue(TitleProperty, value);
      }

      /// <summary>One line under the title saying what the page shows or when it was last refreshed. Null hides the line.</summary>
      public string Subtitle
      {
         get => (string)GetValue(SubtitleProperty);
         set => SetValue(SubtitleProperty, value);
      }

      /// <summary>The page's actions, right-aligned - usually a horizontal StackPanel of buttons.</summary>
      public object Actions
      {
         get => GetValue(ActionsProperty);
         set => SetValue(ActionsProperty, value);
      }

      /// <summary>An optional status beside the title - a <see cref="StatusPill"/>, as a rule.</summary>
      public object Status
      {
         get => GetValue(StatusProperty);
         set => SetValue(StatusProperty, value);
      }

      protected override AutomationControlType ControlType => AutomationControlType.Text;

      private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => AutomationProperties.SetName(d, (string)e.NewValue ?? "");

      private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => AutomationProperties.SetHelpText(d, (string)e.NewValue ?? "");
   }
}
