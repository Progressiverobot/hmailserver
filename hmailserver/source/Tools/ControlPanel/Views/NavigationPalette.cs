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
   /// Ctrl+K command palette: fuzzy-search every page name; the chosen name
   /// is exposed through <see cref="Selected"/>.
   /// </summary>
   public class NavigationPalette : Window
   {
      private const int MaxSettingResults = 12;

      private readonly TextBox searchBox_;
      private readonly ListBox resultsList_;
      private readonly List<string> names_;

      // Display text -> nav page tag, for results that are settings rather than pages.
      private readonly Dictionary<string, string> settingResults_ = new Dictionary<string, string>();

      // Closing the palette takes the focus off it, which raises Deactivated while
      // the close is still running - and a second Close() from that handler throws
      // "Cannot set Visibility ... while a Window is closing". Both ordinary ways
      // of dismissing the palette (choosing a result, pressing Esc) go through
      // Close(), so without this the error dialog appeared almost every time.
      private bool closing_;

      /// <summary>The chosen page NAME, when the user picked a page.</summary>
      public string Selected { get; private set; }

      /// <summary>The nav TAG of the page to open, when the user picked a setting.</summary>
      public string SelectedPageTag { get; private set; }

      public NavigationPalette(Window owner, IEnumerable<string> pageNames)
      {
         names_ = pageNames.ToList();

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
         Deactivated += (s, e) =>
         {
            if (!closing_)
               Close();
         };
         Loaded += (s, e) => { Filter(); searchBox_.Focus(); };
      }

      private void Filter()
      {
         string query = searchBox_.Text.Trim();
         resultsList_.Items.Clear();
         settingResults_.Clear();

         foreach (string name in names_.Where(n => query.Length == 0 || IsSubsequence(query, n)))
            resultsList_.Items.Add(name);

         // Individual settings, so an administrator can search for what they want
         // to change ("delete logs", "LogLevel") rather than having to guess which
         // page it was filed under.
         if (query.Length > 1)
         {
            foreach ((string display, string page) in Services.SettingsSearchIndex.Search(query).Take(MaxSettingResults))
            {
               if (resultsList_.Items.Contains(display))
                  continue;

               settingResults_[display] = page;
               resultsList_.Items.Add(display);
            }
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
         string chosen = resultsList_.SelectedItem as string;

         // A setting result navigates to the page that hosts it; a page result
         // is already the page name the caller expects.
         Selected = chosen != null && settingResults_.TryGetValue(chosen, out string page)
            ? null
            : chosen;

         SelectedPageTag = chosen != null && settingResults_.TryGetValue(chosen, out string tag) ? tag : null;

         Close();
      }

      protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
      {
         // Set before the base call, which is where the window starts tearing down
         // and the focus change that raises Deactivated happens.
         closing_ = true;

         base.OnClosing(e);

         // Nothing cancels the close today, but if anything ever does the palette
         // has to stay dismissable.
         if (e.Cancel)
            closing_ = false;
      }
   }
}
