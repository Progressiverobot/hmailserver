// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Automation.Peers;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// The row above a grid: a search box on the left, a slot beside it for
   /// filters (combo boxes, toggles), and a slot on the right for the actions
   /// that act on the grid (add, delete, refresh). The search box's text is a
   /// two-way property, so a page binds or reads <see cref="SearchText"/> and
   /// listens to <see cref="SearchTextChanged"/> to filter its rows - the same
   /// job the hand-built search boxes on the list pages do today, in one shape.
   ///
   /// <code>
   /// &lt;scaffold:Toolbar SearchPlaceholder="{loc:L 'Search IP ranges'}" SearchTextChanged="Search_Changed"&gt;
   ///    &lt;scaffold:Toolbar.Actions&gt;&lt;StackPanel Orientation="Horizontal"&gt;...&lt;/StackPanel&gt;&lt;/scaffold:Toolbar.Actions&gt;
   /// &lt;/scaffold:Toolbar&gt;
   /// </code>
   /// </summary>
   public class Toolbar : ScaffoldControl
   {
      static Toolbar()
      {
         DefaultStyleKeyProperty.OverrideMetadata(typeof(Toolbar), new FrameworkPropertyMetadata(typeof(Toolbar)));
      }

      public static readonly DependencyProperty SearchTextProperty = DependencyProperty.Register(
         nameof(SearchText), typeof(string), typeof(Toolbar),
         new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSearchTextChanged));

      public static readonly DependencyProperty SearchPlaceholderProperty = DependencyProperty.Register(
         nameof(SearchPlaceholder), typeof(string), typeof(Toolbar), new PropertyMetadata(null, null, NullWhenEmpty));

      public static readonly DependencyProperty ShowSearchProperty = DependencyProperty.Register(
         nameof(ShowSearch), typeof(bool), typeof(Toolbar), new PropertyMetadata(true));

      public static readonly DependencyProperty FiltersProperty = DependencyProperty.Register(
         nameof(Filters), typeof(object), typeof(Toolbar), new PropertyMetadata(null));

      public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
         nameof(Actions), typeof(object), typeof(Toolbar), new PropertyMetadata(null));

      /// <summary>Raised whenever the search text changes, by typing or by code.</summary>
      public event EventHandler SearchTextChanged;

      /// <summary>What is in the search box. Two-way by default.</summary>
      public string SearchText
      {
         get => (string)GetValue(SearchTextProperty);
         set => SetValue(SearchTextProperty, value);
      }

      /// <summary>
      /// The placeholder, which is also the search box's accessible name -
      /// "Search IP ranges" says what the box searches, which is what a screen
      /// reader needs to hear on landing in it.
      /// </summary>
      public string SearchPlaceholder
      {
         get => (string)GetValue(SearchPlaceholderProperty);
         set => SetValue(SearchPlaceholderProperty, value);
      }

      /// <summary>False for a grid that is not worth searching: the box collapses and the filters move left.</summary>
      public bool ShowSearch
      {
         get => (bool)GetValue(ShowSearchProperty);
         set => SetValue(ShowSearchProperty, value);
      }

      /// <summary>The filters beside the search box.</summary>
      public object Filters
      {
         get => GetValue(FiltersProperty);
         set => SetValue(FiltersProperty, value);
      }

      /// <summary>The actions on the right.</summary>
      public object Actions
      {
         get => GetValue(ActionsProperty);
         set => SetValue(ActionsProperty, value);
      }

      protected override AutomationControlType ControlType => AutomationControlType.ToolBar;

      public override void OnApplyTemplate()
      {
         base.OnApplyTemplate();

         // The glyph is put on the box here rather than in the template: a
         // SymbolIcon is a UIElement and can have one parent, so an icon written
         // into the template would be shared by every toolbar in the process.
         if (Part<Wpf.Ui.Controls.TextBox>("PART_Search") is { } search)
            search.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Search24);
      }

      /// <summary>Puts the keyboard in the search box - for a page that opens on its grid.</summary>
      public void FocusSearch()
      {
         Part<Wpf.Ui.Controls.TextBox>("PART_Search")?.Focus();
      }

      private static void OnSearchTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => ((Toolbar)d).SearchTextChanged?.Invoke(d, EventArgs.Empty);
   }
}
