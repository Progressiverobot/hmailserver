// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;

namespace hMailServer.ControlPanel.Views.Scaffold
{
   /// <summary>
   /// What a grid or list shows when it has nothing to show: a glyph, one
   /// sentence that says so and what to do about it, and the action that does
   /// it. An empty box reads as broken; silence reads as "never loaded". Shown
   /// in the grid's place, never over a grid with rows in it.
   ///
   /// <code>
   /// &lt;scaffold:EmptyState Icon="Globe24" Text="{loc:L 'No domains yet. Add the first one to start receiving mail.'}"&gt;
   ///    &lt;scaffold:EmptyState.Action&gt;&lt;ui:Button Content="{loc:L '_Add domain'}" .../&gt;&lt;/scaffold:EmptyState.Action&gt;
   /// &lt;/scaffold:EmptyState&gt;
   /// </code>
   /// </summary>
   public class EmptyState : ScaffoldControl
   {
      static EmptyState()
      {
         DefaultStyleKeyProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(typeof(EmptyState)));
      }

      public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
         nameof(Icon), typeof(Wpf.Ui.Controls.SymbolRegular), typeof(EmptyState),
         new PropertyMetadata(Wpf.Ui.Controls.SymbolRegular.Empty));

      public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
         nameof(Text), typeof(string), typeof(EmptyState), new PropertyMetadata(null, OnTextChanged, NullWhenEmpty));

      public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
         nameof(Action), typeof(object), typeof(EmptyState), new PropertyMetadata(null));

      /// <summary>The Fluent glyph above the sentence; <see cref="Wpf.Ui.Controls.SymbolRegular.Empty"/> shows none.</summary>
      public Wpf.Ui.Controls.SymbolRegular Icon
      {
         get => (Wpf.Ui.Controls.SymbolRegular)GetValue(IconProperty);
         set => SetValue(IconProperty, value);
      }

      /// <summary>One sentence: what is empty and what to do next.</summary>
      public string Text
      {
         get => (string)GetValue(TextProperty);
         set => SetValue(TextProperty, value);
      }

      /// <summary>The action that fills the emptiness - a button, as a rule.</summary>
      public object Action
      {
         get => GetValue(ActionProperty);
         set => SetValue(ActionProperty, value);
      }

      protected override AutomationControlType ControlType => AutomationControlType.Text;

      private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
         => AutomationProperties.SetName(d, (string)e.NewValue ?? "");
   }
}
