// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The Ctrl+K command palette.
   ///
   /// It used to search page names by character subsequence and setting labels by
   /// substring, which meant it could only answer "what is this thing called".
   /// It now asks <see cref="PaletteSearch"/>, which also answers "I want to
   /// achieve X" and "where was this in the old version", shows the navigation
   /// trail of every result, and offers recently-visited and most-used pages
   /// before anything is typed.
   ///
   /// Two things about the presentation are load-bearing rather than decorative:
   ///
   ///   - Every result names the page it lives on. A palette that silently
   ///     teleports people trains them to keep using the palette; one that says
   ///     "Anti-spam settings, in Spam &amp; virus filtering" teaches the tree and
   ///     is needed less next time. This is the whole difference between the
   ///     palette curing the findability problem and the palette hiding it.
   ///   - The results list contains section captions, which are rows that cannot
   ///     be selected. All selection movement therefore goes through
   ///     <see cref="PaletteSearch.NextSelectable"/> instead of incrementing an
   ///     index, so the arrow keys can never park the highlight on a caption and
   ///     leave Enter doing nothing.
   /// </summary>
   public class NavigationPalette : Window
   {
      private readonly TextBox searchBox_;
      private readonly ListBox resultsList_;
      private readonly TextBlock emptyHint_;
      private readonly PaletteUsage usage_;
      private readonly string currentPage_;

      private IReadOnlyList<PaletteRow> rows_ = Array.Empty<PaletteRow>();

      // Closing the palette takes the focus off it, which raises Deactivated while
      // the close is still running - and a second Close() from that handler throws
      // "Cannot set Visibility ... while a Window is closing". Both ordinary ways
      // of dismissing the palette (choosing a result, pressing Esc) go through
      // Close(), so without this the error dialog appeared almost every time.
      private bool closing_;

      /// <summary>The navigation key of the page to open, or null if nothing was chosen.</summary>
      public string SelectedPage { get; private set; }

      /// <summary>
      /// <paramref name="usage"/> and <paramref name="currentPage"/> shape the
      /// shortcuts offered with an empty query; both may be null.
      /// </summary>
      public NavigationPalette(Window owner, PaletteUsage usage, string currentPage)
      {
         usage_ = usage;
         currentPage_ = currentPage;

         Owner = owner;
         // The application face. The palette is a plain chromeless Window, not
         // a FluentDialogWindow, so it never inherited the face the rest of
         // the app gets from its window base classes.
         FontFamily = new FontFamily(Typography.UiFontFamily);
         WindowStyle = WindowStyle.None;
         AllowsTransparency = true;
         Background = Brushes.Transparent;
         ShowInTaskbar = false;
         // Rows now carry two lines each, so the palette is taller and wider than
         // it was - but it must still fit inside a window sitting at its minimum
         // size (760 x 520), or the results the extra height was for end up off
         // the bottom of the owner.
         Width = Math.Min(620, Math.Max(420, owner.Width - 80));
         Height = Math.Min(460, Math.Max(280, owner.Height - 150));
         WindowStartupLocation = WindowStartupLocation.Manual;
         Left = owner.Left + (owner.Width - Width) / 2;
         Top = owner.Top + 80;
         AutomationProperties.SetName(this, "Search pages, settings and tasks");

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
         panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         searchBox_ = new Wpf.Ui.Controls.TextBox
         {
            FontSize = Typography.SectionHeading,
            Padding = new Thickness(8),
            PlaceholderText = "Search, or say what you want to do - \"stop spam\", \"block an IP\"",
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent
         };
         searchBox_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         searchBox_.SetResourceReference(Control.BorderBrushProperty, "ControlElevationBorderBrush");
         searchBox_.TextChanged += (s, e) => Filter();
         AutomationProperties.SetName(searchBox_, "Search pages, settings and tasks");
         AutomationProperties.SetAutomationId(searchBox_, "palette-search");
         panel.Children.Add(searchBox_);

         resultsList_ = new ListBox
         {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = Typography.ItemTitle,
            Margin = new Thickness(0, 8, 0, 0),
            // The search box keeps the focus for the whole life of the palette so
            // that typing never has to be resumed after arrowing through results;
            // the list is driven from the key handler instead of taking focus.
            Focusable = false
         };
         resultsList_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         resultsList_.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
         AutomationProperties.SetName(resultsList_, "Search results");
         AutomationProperties.SetAutomationId(resultsList_, "palette-results");
         Grid.SetRow(resultsList_, 1);
         panel.Children.Add(resultsList_);

         emptyHint_ = new TextBlock
         {
            FontSize = Typography.Label,
            Margin = new Thickness(4, 14, 4, 4),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
         };
         emptyHint_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         Grid.SetRow(emptyHint_, 1);
         panel.Children.Add(emptyHint_);

         // Spelling out the keys costs one line and removes the guess. The
         // palette is keyboard-only by nature, so a user who cannot see how to
         // drive it from the keyboard cannot use it at all.
         var footer = new TextBlock
         {
            Text = "↑↓ move    Enter open    Esc close",
            FontSize = Typography.Caption,
            Margin = new Thickness(4, 8, 4, 2)
         };
         footer.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
         Grid.SetRow(footer, 2);
         panel.Children.Add(footer);

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
         rows_ = PaletteSearch.Query(searchBox_.Text, usage_, currentPage_);

         resultsList_.Items.Clear();
         foreach (PaletteRow row in rows_)
            resultsList_.Items.Add(BuildRow(row));

         int first = PaletteSearch.FirstSelectable(rows_);
         resultsList_.SelectedIndex = first;
         if (first >= 0)
            resultsList_.ScrollIntoView(resultsList_.Items[first]);

         // "No results" has to say something actionable. The old palette showed
         // an empty box, which reads as a broken search rather than as a query
         // that matched nothing.
         bool anything = first >= 0;
         resultsList_.Visibility = anything ? Visibility.Visible : Visibility.Collapsed;
         emptyHint_.Visibility = anything ? Visibility.Collapsed : Visibility.Visible;
         emptyHint_.Text = "Nothing matched \"" + searchBox_.Text.Trim() + "\". Try what you are trying to achieve " +
            "(\"stop spam\", \"let a device send mail\"), a page name, or an hMailServer.INI key.";
      }

      /// <summary>
      /// A caption row, or a result as two lines: what it is, then where it
      /// lives and what it does.
      /// </summary>
      private ListBoxItem BuildRow(PaletteRow row)
      {
         if (row.Kind == PaletteRowKind.Header)
         {
            var caption = new TextBlock
            {
               Text = row.Title.ToUpperInvariant(),
               FontSize = Typography.Caption,
               FontWeight = FontWeights.SemiBold,
               Margin = new Thickness(2, 8, 2, 2)
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

            return new ListBoxItem
            {
               Content = caption,
               // Both are needed: IsEnabled keeps the mouse and the ListBox's own
               // keyboard navigation off the caption, Focusable keeps it out of
               // the tab order if the list ever becomes focusable again.
               IsEnabled = false,
               Focusable = false,
               Padding = new Thickness(0)
            };
         }

         var stack = new StackPanel();
         stack.Children.Add(new TextBlock
         {
            Text = row.Title,
            FontSize = Typography.Control,
            TextTrimming = TextTrimming.CharacterEllipsis
         });

         string secondary = Describe(row);
         if (secondary.Length > 0)
         {
            var detail = new TextBlock
            {
               Text = secondary,
               FontSize = Typography.Caption,
               TextTrimming = TextTrimming.CharacterEllipsis,
               Margin = new Thickness(0, 1, 0, 0)
            };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            stack.Children.Add(detail);
         }

         var item = new ListBoxItem
         {
            Content = stack,
            Padding = new Thickness(6, 5, 6, 5),
            Tag = row
         };

         // The accessible name is the whole result, because a screen-reader user
         // arrowing through the list gets one announcement per row and the
         // destination is the half that matters.
         AutomationProperties.SetName(item, secondary.Length > 0 ? row.Title + ". " + secondary : row.Title);

         item.MouseLeftButtonUp += (s, e) =>
         {
            resultsList_.SelectedItem = item;
            Accept();
         };

         return item;
      }

      /// <summary>The second line: where the result lives, then what it is for.</summary>
      private static string Describe(PaletteRow row)
      {
         string location = row.Location ?? "";

         switch (row.Kind)
         {
            case PaletteRowKind.Task:
               // Naming the destination page is the point of a task row: the
               // phrase is ours, the page is what the user has to remember.
               location = location.Length > 0 ? "Opens " + location : "";
               break;

            case PaletteRowKind.Page:
               location = location.Length > 0 ? "in " + location : "";
               break;
         }

         string detail = row.Detail ?? "";
         if (location.Length == 0)
            return detail;
         if (detail.Length == 0)
            return location;

         return location + "    " + detail;
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
               Move(1);
               e.Handled = true;
               break;

            case Key.Up:
               Move(-1);
               e.Handled = true;
               break;

            case Key.PageDown:
               // A page is a screenful of rows rather than a fixed count; five
               // moves is close enough and never overshoots past the end because
               // NextSelectable clamps.
               for (int i = 0; i < 5; i++)
                  Move(1);
               e.Handled = true;
               break;

            case Key.PageUp:
               for (int i = 0; i < 5; i++)
                  Move(-1);
               e.Handled = true;
               break;

            case Key.Home:
               // Home and End would otherwise move the caret in the search box,
               // which is the more useful binding while typing - but only while
               // there is something to move within.
               if (searchBox_.Text.Length == 0)
               {
                  Select(PaletteSearch.FirstSelectable(rows_));
                  e.Handled = true;
               }
               break;

            case Key.End:
               if (searchBox_.Text.Length == 0)
               {
                  Select(PaletteSearch.LastSelectable(rows_));
                  e.Handled = true;
               }
               break;
         }
      }

      private void Move(int direction)
         => Select(PaletteSearch.NextSelectable(rows_, resultsList_.SelectedIndex, direction));

      private void Select(int index)
      {
         if (index < 0 || index >= resultsList_.Items.Count)
            return;

         resultsList_.SelectedIndex = index;
         resultsList_.ScrollIntoView(resultsList_.Items[index]);
      }

      private void Accept()
      {
         // Read the chosen row off the container rather than by indexing rows_
         // with the selected index. They do correspond one for one today, but a
         // palette that navigates to the wrong page because the two lists drifted
         // apart would be an unpleasant bug to diagnose, and the Tag is free.
         if (resultsList_.SelectedItem is ListBoxItem container && container.Tag is PaletteRow row && row.IsSelectable)
            SelectedPage = row.Page;

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
