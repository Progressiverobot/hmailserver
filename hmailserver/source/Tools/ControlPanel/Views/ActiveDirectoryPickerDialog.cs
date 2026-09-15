// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
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
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal Active Directory account browser. Lists the forest's domains, searches their
   /// users and returns the selected account(s). Used to fill the AD fields on an account
   /// and to bulk-import members into groups / distribution lists. On the standard
   /// frame: the query row above the list, the status line under it, Select and
   /// Cancel in the footer.
   /// </summary>
   public class ActiveDirectoryPickerDialog : FluentDialogWindow
   {
      private readonly bool multiSelect_;
      private readonly ComboBox domainBox_ = new() { MinWidth = 240 };
      private readonly TextBox searchBox_ = new();
      private readonly ListView list_ = new() { Height = 300 };

      private readonly TextBlock status_ = DialogFields.Note("");

      private readonly Wpf.Ui.Controls.Button okButton_ = new()
      {
         Content = L("_Select"),
         Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
         MinWidth = 88,
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
         Title = L("Browse Active Directory");

         var body = new StackPanel();
         body.Children.Add(BuildQueryRow());

         BuildList();
         body.Children.Add(list_);

         status_.Margin = new Thickness(0, DesignTokens.Space.Sm, 0, 0);
         body.Children.Add(status_);

         okButton_.Click += (s, e) => Accept();

         // Escape closes. Select is deliberately NOT the default button, which
         // the frame would otherwise make it: Enter in the search box means
         // "search" and always has (see BuildQueryRow), and a default button
         // would fire on the same keystroke - so Enter would search and then try
         // to accept a selection the search had just cleared. Enter on the
         // results list is the natural "select" gesture and is reached by tabbing
         // to the list, which is why the list's key handling is left to the
         // ListView.
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => { DialogResult = false; Close(); };

         UseFrame(multiSelect ? L("Select Active Directory accounts") : L("Select an Active Directory account"),
            body, okButton_, cancel, width: 680);
         okButton_.IsDefault = false;

         // After the frame's own handler, which lands on the domain list: the
         // search box is where the typing happens.
         Loaded += (s, e) => { LoadDomains(); searchBox_.Focus(); };
      }

      private FrameworkElement BuildQueryRow()
      {
         var grid = new Grid { Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var domainLabel = new TextBlock
         {
            Text = L("Domain"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0)
         };
         domainLabel.SetResourceReference(StyleProperty, "TextCaption");
         Grid.SetColumn(domainLabel, 0);
         grid.Children.Add(domainLabel);

         domainBox_.Margin = new Thickness(0, 0, DesignTokens.Space.Md, 0);
         AutomationProperties.SetName(domainBox_, L("Domain"));
         Grid.SetColumn(domainBox_, 1);
         grid.Children.Add(domainBox_);

         // The search box has no caption of its own at all - it is a bare box
         // between the domain list and the Search button, so a screen reader
         // announced "edit" and a keyboard user tabbing in had no way to know what
         // it was for. The name says what it does and what an empty box means,
         // because "search with an empty box to list all users" is real behaviour
         // that only the status line mentions.
         AutomationProperties.SetName(searchBox_,
            L("Search for part of an account name. Leave empty to list every user in the domain."));

         // Handled, so the keystroke stops here. Without that, a default button
         // anywhere in this dialog would make one Enter both search and accept.
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
            Content = L("Sea_rch"),
            Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0)
         };
         searchButton.Click += (s, e) => Search();
         Grid.SetColumn(searchButton, 3);
         grid.Children.Add(searchButton);

         return grid;
      }

      private void BuildList()
      {
         list_.SelectionMode = multiSelect_ ? SelectionMode.Extended : SelectionMode.Single;

         var gridView = new GridView();
         gridView.Columns.Add(new GridViewColumn { Header = L("Account"), DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AdUser.SamAccountName)), Width = 160 });
         gridView.Columns.Add(new GridViewColumn { Header = L("Name"), DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AdUser.DisplayName)), Width = 200 });
         gridView.Columns.Add(new GridViewColumn { Header = L("E-mail"), DisplayMemberBinding = new System.Windows.Data.Binding(nameof(AdUser.Email)), Width = 240 });
         list_.View = gridView;

         AutomationProperties.SetName(list_, multiSelect_
            ? L("Matching Active Directory accounts. Select one or more.")
            : L("Matching Active Directory accounts. Select one."));

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
               status_.Text = L("No Active Directory domains could be found in the current forest.");
               return;
            }

            domainBox_.ItemsSource = domains;
            domainBox_.SelectedIndex = 0;
            status_.Text = L("Enter part of a name and press Search, or search with an empty box to list all users.");
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            status_.Text = L("Could not list domains — ") + ServerSession.DescribeComError(ex);
         }
      }

      private async void Search()
      {
         string domain = domainBox_.SelectedItem as string;
         if (string.IsNullOrEmpty(domain))
         {
            status_.Text = L("Select a domain first.");
            return;
         }

         string filter = searchBox_.Text;
         status_.Text = L("Searching ") + domain + "…";
         Mouse.OverrideCursor = Cursors.Wait;
         list_.ItemsSource = null;
         okButton_.IsEnabled = false;

         try
         {
            List<AdUser> users = await Task.Run(() => ActiveDirectoryService.QueryUsers(domain, filter, 1000));
            list_.ItemsSource = users;
            status_.Text = users.Count == 0
               ? F("No matching accounts in {0}.", domain)
               : F("{0} account(s) found{1}.", users.Count, users.Count >= 1000 ? L(" (showing first 1000 — refine your search)") : "");
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            status_.Text = L("Search failed — ") + ServerSession.DescribeComError(ex);
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
