using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Ctrl+K command palette: fuzzy-search every page and jump to it.
   /// </summary>
   public class CommandPalette : Window
   {
      private readonly TextBox searchBox_;
      private readonly ListBox resultsList_;
      private readonly List<KeyValuePair<string, RadioButton>> items_;

      public CommandPalette(Window owner, IEnumerable<RadioButton> navItems)
      {
         Owner = owner;
         WindowStyle = WindowStyle.None;
         AllowsTransparency = true;
         Background = Brushes.Transparent;
         ShowInTaskbar = false;
         Width = 520;
         Height = 360;
         WindowStartupLocation = WindowStartupLocation.Manual;
         Left = owner.Left + (owner.Width - Width) / 2;
         Top = owner.Top + 90;

         items_ = navItems
            .Select(item => new KeyValuePair<string, RadioButton>(item.Content?.ToString() ?? "", item))
            .Where(pair => pair.Key.Length > 0)
            .ToList();

         var root = new Border
         {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10)
         };
         root.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
         root.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");

         var panel = new Grid();
         panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         searchBox_ = new TextBox
         {
            FontSize = 15,
            Padding = new Thickness(8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent
         };
         searchBox_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         searchBox_.SetResourceReference(Control.BorderBrushProperty, "ControlElevationBorderBrush");
         searchBox_.TextChanged += (s, e) => Filter();
         panel.Children.Add(searchBox_);

         resultsList_ = new ListBox
         {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 14,
            Margin = new Thickness(0, 8, 0, 0)
         };
         resultsList_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         resultsList_.MouseUp += (s, e) => Accept();
         Grid.SetRow(resultsList_, 1);
         panel.Children.Add(resultsList_);

         root.Child = panel;
         Content = root;

         PreviewKeyDown += OnKey;
         Deactivated += (s, e) => Close();
         Loaded += (s, e) => { Filter(); searchBox_.Focus(); };
      }

      private void Filter()
      {
         string query = searchBox_.Text.Trim();
         resultsList_.Items.Clear();

         foreach (var pair in items_)
         {
            if (query.Length == 0 || IsSubsequence(query, pair.Key))
               resultsList_.Items.Add(pair.Key);
         }

         if (resultsList_.Items.Count > 0)
            resultsList_.SelectedIndex = 0;
      }

      private static bool IsSubsequence(string query, string text)
      {
         int index = 0;
         foreach (char c in query)
         {
            index = text.IndexOf(char.ToLowerInvariant(c).ToString(),
               index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
               return false;
            index++;
         }
         return true;
      }

      private void OnKey(object sender, KeyEventArgs e)
      {
         switch (e.Key)
         {
            case Key.Escape:
               Close();
               e.Handled = true;
               break;
            case Key.Enter:
               Accept();
               e.Handled = true;
               break;
            case Key.Down:
               if (resultsList_.SelectedIndex < resultsList_.Items.Count - 1)
                  resultsList_.SelectedIndex++;
               e.Handled = true;
               break;
            case Key.Up:
               if (resultsList_.SelectedIndex > 0)
                  resultsList_.SelectedIndex--;
               e.Handled = true;
               break;
         }
      }

      private void Accept()
      {
         string selected = resultsList_.SelectedItem as string;
         if (selected != null)
         {
            var target = items_.FirstOrDefault(pair => pair.Key == selected).Value;
            if (target != null && target.IsEnabled)
               target.IsChecked = true;
         }
         Close();
      }
   }
}
