// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The accounts that belong to one security group.
   ///
   /// This existed nowhere in the Control Panel, and its absence made the whole
   /// feature unusable rather than merely awkward: a group could be created on the
   /// Groups page and granted rights on a public folder in
   /// <see cref="FolderPermissionsDialog"/>, but no account could ever be put into
   /// it, so the permission it was granted applied to nobody. Filling it in
   /// required writing a COM script - which is precisely the shape AGENTS.md's GUI
   /// rule exists to stop, since most administrators never write one.
   ///
   /// Members are stored as (group, account) rows rather than as names, so the
   /// list here is built by resolving each member's AccountID back to an address.
   /// A member whose account has since been deleted therefore has nothing to
   /// resolve; it is shown as an orphan rather than hidden, because a row that
   /// silently vanishes from a permissions list is the sort of thing an
   /// administrator only discovers when someone cannot read a folder.
   /// </summary>
   public class GroupMembersDialog : FluentDialogWindow
   {
      private readonly int groupId_;
      private readonly string groupName_;

      private readonly ListBox list_ = new() { FontSize = Typography.Body, Height = 260, Margin = new Thickness(0, 0, 0, 12) };

      /// <summary>Member row database ids, parallel to <see cref="list_"/>.</summary>
      private readonly List<int> memberIds_ = new();

      private readonly TextBlock status_ = new();

      public GroupMembersDialog(Window owner, int groupId, string groupName)
      {
         groupId_ = groupId;
         groupName_ = groupName ?? "";

         Owner = owner;
         Title = "Members - " + groupName_;
         Width = 520;
         SizeToContent = SizeToContent.Height;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         ResizeMode = ResizeMode.NoResize;
         SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };

         var header = new TextBlock
         {
            Text = groupName_,
            FontSize = Typography.DialogTitle,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
         };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(header);

         var blurb = new TextBlock
         {
            Text = "A group exists so that several accounts can be given the same rights at once - "
                   + "grant the group access to a public folder under Public folders, and every account "
                   + "listed here gets that access. An empty group grants nothing to anyone.",
            FontSize = Typography.Label,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
         };
         blurb.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         panel.Children.Add(blurb);

         System.Windows.Automation.AutomationProperties.SetName(list_, "Accounts in the group " + groupName_);
         System.Windows.Automation.AutomationProperties.SetAutomationId(list_, "group-members-list");
         panel.Children.Add(list_);

         status_.FontSize = Typography.Caption;
         status_.TextWrapping = TextWrapping.Wrap;
         status_.Margin = new Thickness(0, 0, 0, 12);
         status_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         panel.Children.Add(status_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

         var add = new Wpf.Ui.Controls.Button
         {
            Content = "Add account…",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 110
         };
         System.Windows.Automation.AutomationProperties.SetName(add, "Add an account to this group");
         System.Windows.Automation.AutomationProperties.SetAutomationId(add, "group-members-add");
         add.Click += (s, e) => AddMember();
         buttons.Children.Add(add);

         var remove = new Wpf.Ui.Controls.Button
         {
            Content = "Remove",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Danger,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 90
         };
         System.Windows.Automation.AutomationProperties.SetName(remove, "Remove the selected account from this group");
         System.Windows.Automation.AutomationProperties.SetAutomationId(remove, "group-members-remove");
         remove.Click += (s, e) => RemoveSelected();
         buttons.Children.Add(remove);

         // IsCancel only, and deliberately not the default button: Enter with a row
         // selected must not re-open the add prompt.
         var close = new Wpf.Ui.Controls.Button { Content = "Close", MinWidth = 80, IsCancel = true };
         System.Windows.Automation.AutomationProperties.SetName(close, "Close the group members window");
         close.Click += (s, e) => Close();
         buttons.Children.Add(close);

         panel.Children.Add(buttons);

         Content = panel;
         Loaded += (s, e) => Reload();
      }

      /// <summary>The group this dialog edits, or null when it has been deleted underneath us.</summary>
      private dynamic OpenGroup()
      {
         dynamic groups = ServerSession.Current.Application.Settings.Groups;
         return groups.ItemByDBID[groupId_];
      }

      private void Reload()
      {
         list_.Items.Clear();
         memberIds_.Clear();
         status_.Text = "";

         try
         {
            dynamic group = OpenGroup();
            dynamic members = group.Members;

            int count = (int)members.Count;

            for (int i = 0; i < count; i++)
            {
               dynamic member = members[i];

               int memberId = (int)member.ID;
               string label;

               // Account is a COM property that resolves the stored AccountID. It can
               // legitimately fail when the account has been deleted since the member
               // row was written, which is exactly the case worth showing rather than
               // swallowing.
               try
               {
                  dynamic account = member.Account;
                  label = account == null ? null : (string)account.Address;
               }
               catch
               {
                  label = null;
               }

               if (string.IsNullOrWhiteSpace(label))
                  label = "(account " + (int)member.AccountID + " no longer exists)";

               memberIds_.Add(memberId);
               list_.Items.Add(label);
            }

            status_.Text = count == 0
               ? "This group has no accounts in it, so any permission granted to it currently applies to nobody."
               : count + (count == 1 ? " account is a member." : " accounts are members.");
         }
         catch (Exception ex)
         {
            status_.Text = "The group members could not be read: " + ServerSession.DescribeComError(ex);
         }
      }

      /// <summary>Every account on the server, as "address" strings, for the picker.</summary>
      private List<string> ReadAllAccountAddresses()
      {
         var addresses = new List<string>();

         dynamic domains = null;
         try
         {
            domains = ServerSession.Current.Application.Domains;
            int domainCount = (int)domains.Count;

            for (int d = 0; d < domainCount; d++)
            {
               dynamic domain = null;
               dynamic accounts = null;

               try
               {
                  domain = domains[d];
                  accounts = domain.Accounts;
                  int accountCount = (int)accounts.Count;

                  for (int a = 0; a < accountCount; a++)
                     addresses.Add((string)accounts[a].Address);
               }
               finally
               {
                  if (accounts != null)
                     ServerSession.Release(accounts);
                  if (domain != null)
                     ServerSession.Release(domain);
               }
            }
         }
         finally
         {
            if (domains != null)
               ServerSession.Release(domains);
         }

         addresses.Sort(StringComparer.OrdinalIgnoreCase);
         return addresses;
      }

      private void AddMember()
      {
         try
         {
            List<string> candidates = ReadAllAccountAddresses();

            if (candidates.Count == 0)
            {
               MessageBox.Show("There are no accounts on this server to add.", "Control Panel");
               return;
            }

            string address = PickAccountDialog.Pick(this, candidates);
            if (string.IsNullOrWhiteSpace(address))
               return;

            dynamic account = ServerSession.Current.Application.Domains.ItemByAddress[address].Accounts.ItemByAddress[address];

            dynamic group = OpenGroup();
            dynamic members = group.Members;

            // Already a member? Adding a duplicate row would grant nothing extra and
            // leave two rows to remove later, so it is refused with an explanation
            // rather than silently ignored.
            int existing = (int)members.Count;
            int accountId = (int)account.ID;

            for (int i = 0; i < existing; i++)
            {
               if ((int)members[i].AccountID == accountId)
               {
                  MessageBox.Show(address + " is already a member of this group.", "Control Panel");
                  return;
               }
            }

            // Add() already stamps the group id from the collection it belongs to, so
            // only the account and the Save are needed here.
            dynamic member = members.Add();
            member.AccountID = accountId;
            member.Save();

            Reload();
         }
         catch (Exception ex)
         {
            MessageBox.Show("The account could not be added to the group: " + ServerSession.DescribeComError(ex), "Control Panel");
         }
      }

      private void RemoveSelected()
      {
         int index = list_.SelectedIndex;
         if (index < 0 || index >= memberIds_.Count)
            return;

         string label = (string)list_.Items[index];

         if (MessageBox.Show("Remove " + label + " from " + groupName_ + "?\r\n\r\n"
                             + "Any access this group has been granted stops applying to that account.",
                             "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
         {
            return;
         }

         try
         {
            dynamic group = OpenGroup();
            group.Members.DeleteByDBID(memberIds_[index]);
            Reload();
         }
         catch (Exception ex)
         {
            MessageBox.Show("The account could not be removed from the group: " + ServerSession.DescribeComError(ex), "Control Panel");
         }
      }
   }

   /// <summary>
   /// Picks one address from a list. A list rather than a free-text box because a
   /// group member is stored as an account id: a typed address that does not exist
   /// cannot be stored at all, so offering a text box would only produce an error
   /// after the fact.
   /// </summary>
   internal static class PickAccountDialog
   {
      public static string Pick(Window owner, List<string> addresses)
      {
         var dlg = new FluentDialogWindow
         {
            Owner = owner,
            Title = "Add account to group",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
         };
         dlg.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };

         var label = new TextBlock
         {
            Text = "Account to add",
            FontSize = Typography.Label,
            Margin = new Thickness(0, 0, 0, 6)
         };
         label.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         panel.Children.Add(label);

         var combo = new ComboBox { FontSize = Typography.Body, Margin = new Thickness(0, 0, 0, 12), IsEditable = false };
         foreach (string address in addresses)
            combo.Items.Add(address);
         combo.SelectedIndex = 0;
         System.Windows.Automation.AutomationProperties.SetName(combo, "Account to add to the group");
         System.Windows.Automation.AutomationProperties.SetAutomationId(combo, "group-members-pick");
         panel.Children.Add(combo);

         string result = null;

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

         var ok = new Wpf.Ui.Controls.Button
         {
            Content = "Add",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 80,
            IsDefault = true
         };
         ok.Click += (s, e) => { result = combo.SelectedItem as string; dlg.DialogResult = true; dlg.Close(); };
         buttons.Children.Add(ok);

         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
         cancel.Click += (s, e) => dlg.Close();
         buttons.Children.Add(cancel);

         panel.Children.Add(buttons);
         dlg.Content = panel;

         combo.Loaded += (s, e) => combo.Focus();

         return dlg.ShowDialog() == true ? result : null;
      }
   }
}
