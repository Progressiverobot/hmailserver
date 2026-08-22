// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal Active Directory account browser. Lists the forest's domains, searches their
   /// users and returns the selected account(s). Used to fill the AD fields on an account
   /// and to bulk-import members into groups / distribution lists.
   /// </summary>
   public class ActiveDirectoryPickerDialog : FluentDialogWindow
   {
      private readonly bool multiSelect_;
      private readonly ComboBox domainBox_ = new() { FontSize = Typography.Body, MinWidth = 240, Padding = new Thickness(6) };
      private readonly TextBox searchBox_ = new() { FontSize = Typography.Body, Padding = new Thickness(6) };
      private readonly ListView list_ = new() { FontSize = Typography.Body };

      private readonly TextBlock status_ = new()
      {
         FontSize = Typography.Label,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(2, 8, 2, 0)
      };

      private readonly Wpf.Ui.Controls.Button okButton_ = new()
      {
         Content = "Select",
         Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
         Margin = new Thickness(0, 0, 8, 0),
         IsEnabled = false
      };

      /// <summary>The accounts the user chose (empty when cancelled).</summary>
      public List<AdUser> SelectedUsers { get; } = new();

      /// <summary>The domain the accounts were chosen from.</summary>
      public string SelectedDomain { get; private set; }

      public ActiveDirectoryPickerDialog(Window owner, bool multiSelect)
      {
         multiSelect_ = multiSelect;

         Owner = owner;
         Title = "Browse Active Directory";
         Width = 680;
         Height = 560;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var root = new Grid { Margin = new Thickness(18) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // domain + search
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

         var header = new TextBlock
         {
            Text = multiSelect ? "Select Active Directory accounts" : "Select an Active Directory account",
            FontSize = Typography.DialogTitle,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 12)
         };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetRow(header, 0);
         root.Children.Add(header);

         root.Children.Add(BuildQueryRow());

         BuildList();
         Grid.SetRow(list_, 2);
         root.Children.Add(list_);

         status_.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         Grid.SetRow(status_, 3);
         root.Children.Add(status_);

         var buttons = new StackPanel
         {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
         };
         okButton_.Click += (s, e) => Accept();

         // Escape closes. It did not before, on a dialog that can sit for several
         // seconds on a directory query.
         //
         // Select is deliberately NOT the default button. Enter in the search box
         // means "search" and always has (see BuildQueryRow), and a default button
         // would fire on the same keystroke - so Enter would search and then try to
         // accept a selection the search had just cleared. Enter on the results list
         // is the natural "select" gesture and is reached by tabbing to the list,
         // which is why the list's key handling is left to the ListView.
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", IsCancel = true };
         cancel.Click += (s, e) => { DialogResult = false; Close(); };
         buttons.Children.Add(okButton_);
         buttons.Children.Add(cancel);
         Grid.SetRow(buttons, 4);
         root.Children.Add(buttons);

         Content = root;
         Loaded += (s, e) => LoadDomains();
      }

      private FrameworkElement BuildQueryRow()
      {
         var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var domainLabel = new TextBlock
         {
            Text = "Domain",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = Typography.Body
         };
         domainLabel.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         Grid.SetColumn(domainLabel, 0);
         grid.Children.Add(domainLabel);

         domainBox_.Margin = new Thickness(0, 0, 12, 0);
         AutomationProperties.SetName(domainBox_, "Domain");
         Grid.SetColumn(domainBox_, 1);
         grid.Children.Add(domainBox_);

         searchBox_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         searchBox_.Background = System.Windows.Media.Brushes.Transparent;

         // The search box has no caption of its own at all - it is a bare box
         // between the domain list and the Search button, so a screen reader
         // announced "edit" and a keyboard user tabbing in had no way to know what
         // it was for. The name says what it does and what an empty box means,
         // because "search with an empty box to list all users" is real behaviour
         // that only the status line mentions.
         AutomationProperties.SetName(searchBox_,
            "Search for part of an account name. Leave empty to list every user in the domain.");

         // Handled, so the keystroke stops here. Without that, adding a default
         // button anywhere in this dialog would make one Enter both search and
         // accept.
         searchBox_.KeyDown += (s, e) =>
         {
            if (e.Key != Key.Enter)
               return;

            Search();
            e.Handled = true;
         };
         Grid.SetColumn(searchBox_, 2);
         grid.Children.Add(searchBox_);

         var searchButton = new Wpf.Ui.Controls.Button
         {
            Content = "Search",
            Margin = new Thickness(8, 0, 0, 0)
         };
         searchButton.Click += (s, e) => Search();
         Grid.SetColumn(searchButton, 3);
         grid.Children.Add(searchButton);

         Grid.SetRow(grid, 1);
         return grid;
      }

      private void BuildList()
      {
         list_.SelectionMode = multiSelect_ ? SelectionMode.Extended : SelectionMode.Single;
         list_.BorderThickness = new Thickness(1);
         list_.SetResourceReference(Control.BorderBrushProperty, "ControlElevationBorderBrush");
         list_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         list_.Background = System.Windows.Media.Brushes.Transparent;

         var gridView = new GridView();
         gridView.Columns.Add(new GridViewColumn { Header = "Account", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AdUser.SamAccountName)), Width = 160 });
         gridView.Columns.Add(new GridViewColumn { Header = "Name", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AdUser.DisplayName)), Width = 200 });
         gridView.Columns.Add(new GridViewColumn { Header = "E-mail", DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AdUser.Email)), Width = 240 });
         list_.View = gridView;

         AutomationProperties.SetName(list_, multiSelect_
            ? "Matching Active Directory accounts. Select one or more."
            : "Matching Active Directory accounts. Select one.");

         list_.SelectionChanged += (s, e) => okButton_.IsEnabled = list_.SelectedItems.Count > 0;
         list_.MouseDoubleClick += (s, e) => { if (!multiSelect_ && list_.SelectedItem is AdUser) Accept(); };

         // The keyboard equivalent of the double-click above. Without it the only
         // way to commit a highlighted row was to tab out of the list and back to
         // the Select button - and Enter is what every list in Windows does.
         list_.KeyDown += (s, e) =>
         {
            if (e.Key != Key.Enter || list_.SelectedItems.Count == 0)
               return;

            Accept();
            e.Handled = true;
         };
      }

      private void LoadDomains()
      {
         if (!ActiveDirectoryService.IsAvailable(out string reason))
         {
            status_.Text = reason;
            domainBox_.IsEnabled = false;
            searchBox_.IsEnabled = false;
            return;
         }

         try
         {
            var domains = ActiveDirectoryService.ListDomains();
            if (domains.Count == 0)
            {
               status_.Text = "No Active Directory domains could be found in the current forest.";
               return;
            }

            domainBox_.ItemsSource = domains;
            domainBox_.SelectedIndex = 0;
            status_.Text = "Enter part of a name and press Search, or search with an empty box to list all users.";
         }
         catch (Exception ex)
         {
            status_.Text = "Could not list domains — " + ServerSession.DescribeComError(ex);
         }
      }

      private async void Search()
      {
         string domain = domainBox_.SelectedItem as string;
         if (string.IsNullOrEmpty(domain))
         {
            status_.Text = "Select a domain first.";
            return;
         }

         string filter = searchBox_.Text;
         status_.Text = "Searching " + domain + "\u2026";
         Mouse.OverrideCursor = Cursors.Wait;
         list_.ItemsSource = null;
         okButton_.IsEnabled = false;

         try
         {
            List<AdUser> users = await Task.Run(() => ActiveDirectoryService.QueryUsers(domain, filter, 1000));
            list_.ItemsSource = users;
            status_.Text = users.Count == 0
               ? "No matching accounts in " + domain + "."
               : users.Count + " account(s) found" + (users.Count >= 1000 ? " (showing first 1000 — refine your search)" : "") + ".";
         }
         catch (Exception ex)
         {
            status_.Text = "Search failed — " + ServerSession.DescribeComError(ex);
         }
         finally
         {
            Mouse.OverrideCursor = null;
         }
      }

      private void Accept()
      {
         SelectedUsers.Clear();
         foreach (object item in list_.SelectedItems)
            if (item is AdUser u)
               SelectedUsers.Add(u);

         if (SelectedUsers.Count == 0)
            return;

         SelectedDomain = domainBox_.SelectedItem as string;
         DialogResult = true;
         Close();
      }
   }
}
