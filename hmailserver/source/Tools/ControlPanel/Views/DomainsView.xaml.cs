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
   public partial class DomainsView : UserControl, IPageLifecycle
   {
      public DomainsView()
      {
         InitializeComponent();
      }

      public void OnEnter() => ReloadDomains();

      public void OnLeave()
      {
      }

      /// <summary>
      /// A domain in the left-hand list. The template dims an inactive domain and
      /// appends the word "inactive"; ToString carries the same word to a screen
      /// reader, because a ListViewItem's accessible name falls back to ToString()
      /// on the bound object.
      /// </summary>
      public class DomainRow
      {
         public string Name { get; set; }
         public bool Active { get; set; }
         public override string ToString() => Active ? Name : Name + " (inactive)";
      }

      /// <summary>Same shape for the account list.</summary>
      public class AccountRow
      {
         public string Address { get; set; }
         public bool Active { get; set; }
         public override string ToString() => Active ? Address : Address + " (inactive)";
      }

      private string SelectedDomainName() => (DomainList.SelectedItem as DomainRow)?.Name;

      private void ReloadDomains()
      {
         string previousSelection = SelectedDomainName();

         var rows = new List<DomainRow>();
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            int count = (int)domains.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic domain = domains.Item[i];
               rows.Add(new DomainRow { Name = (string)domain.Name, Active = (bool)domain.Active });
               ServerSession.Release(domain);
            }
         }
         finally
         {
            ServerSession.Release(domains);
         }

         DomainList.ItemsSource = rows;
         ListSearch.Apply(DomainList, DomainSearch.Text);

         // Replacing ItemsSource clears the selection; put it back on the same
         // domain when it still exists, so a reload does not yank the accounts
         // pane over to whatever happens to be first.
         if (previousSelection != null)
            DomainList.SelectedItem = rows.Find(r => r.Name == previousSelection);

         if (rows.Count > 0 && DomainList.SelectedIndex < 0)
            DomainList.SelectedIndex = 0;
      }

      private void DomainSearch_TextChanged(object sender, TextChangedEventArgs e)
         => ListSearch.Apply(DomainList, DomainSearch.Text);

      private void AccountSearch_TextChanged(object sender, TextChangedEventArgs e)
         => ListSearch.Apply(AccountList, AccountSearch.Text);

      private void DomainList_SelectionChanged(object sender, SelectionChangedEventArgs e)
      {
         ReloadAccounts();
         ReloadAliases();
         ReloadDistLists();
      }

      public class AliasRow
      {
         public string Name { get; set; }
         public string Display { get; set; }

         // The alias list is a ListView with an ItemTemplate, so the visible text
         // comes from the Display binding - but a ListViewItem's accessible name
         // falls back to ToString() on the bound object. Without this a screen
         // reader announces the type name for every alias. The domain, account and
         // distribution lists are bound to plain strings and never had the problem.
         public override string ToString() => Display;
      }

      private dynamic OpenSelectedDomain(dynamic domains)
      {
         string domainName = SelectedDomainName();
         return domainName == null ? null : domains.ItemByName[domainName];
      }

      private void ReloadAliases()
      {
         var rows = new List<AliasRow>();
         string error = null;
         if (DomainList.SelectedItem != null)
         {
            dynamic domains = ServerSession.Current.Application.Domains;
            try
            {
               dynamic domain = OpenSelectedDomain(domains);
               dynamic aliases = domain.Aliases;
               int count = (int)aliases.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic alias = aliases.Item[i];
                  rows.Add(new AliasRow
                  {
                     Name = (string)alias.Name,
                     Display = (string)alias.Name + "  →  " + (string)alias.Value
                  });
                  ServerSession.Release(alias);
               }
               ServerSession.Release(aliases);
               ServerSession.Release(domain);
            }
            catch (Exception ex)
            {
               error = ServerSession.DescribeComError(ex);
            }
            finally
            {
               ServerSession.Release(domains);
            }
         }
         AliasList.ItemsSource = rows;
         SetListStatus(AliasStatus, rows.Count, error,
            DomainList.SelectedItem == null ? "Select a domain to see its aliases." : "No aliases for this domain.",
            "Couldn't load aliases: ");
      }

      private void ReloadDistLists()
      {
         var rows = new List<string>();
         string error = null;
         if (DomainList.SelectedItem != null)
         {
            dynamic domains = ServerSession.Current.Application.Domains;
            try
            {
               dynamic domain = OpenSelectedDomain(domains);
               dynamic lists = domain.DistributionLists;
               int count = (int)lists.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic list = lists.Item[i];
                  rows.Add((string)list.Address);
                  ServerSession.Release(list);
               }
               ServerSession.Release(lists);
               ServerSession.Release(domain);
            }
            catch (Exception ex)
            {
               error = ServerSession.DescribeComError(ex);
            }
            finally
            {
               ServerSession.Release(domains);
            }
         }
         DistList.ItemsSource = rows;
         SetListStatus(DistStatus, rows.Count, error,
            DomainList.SelectedItem == null ? "Select a domain to see its distribution lists." : "No distribution lists for this domain.",
            "Couldn't load distribution lists: ");
      }

      // Shows a centered empty/error placeholder over a list when it has no rows.
      private static void SetListStatus(TextBlock status, int rowCount, string error, string emptyText, string errorPrefix)
         => StatusText.Show(status, rowCount, error, emptyText, errorPrefix);

      private void AddAlias_Click(object sender, RoutedEventArgs e)
      {
         string name = NewAliasName.Text.Trim();
         string value = NewAliasValue.Text.Trim();
         if (DomainList.SelectedItem == null || name.Length == 0 || value.Length == 0)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = OpenSelectedDomain(domains);
            dynamic aliases = domain.Aliases;
            dynamic alias = aliases.Add();
            alias.Name = name;
            alias.Value = value;
            alias.Active = true;
            alias.Save();
            ServerSession.Release(alias);
            ServerSession.Release(aliases);
            ServerSession.Release(domain);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not create the alias: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(domains);
         }

         NewAliasName.Text = NewAliasValue.Text = "";
         ReloadAliases();
      }

      private void DeleteAlias_Click(object sender, RoutedEventArgs e)
      {
         string name = (sender as FrameworkElement)?.Tag as string;
         if (name == null || DomainList.SelectedItem == null)
            return;

         if (MessageBox.Show("Delete the alias " + name + "?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = OpenSelectedDomain(domains);
            dynamic aliases = domain.Aliases;
            int count = (int)aliases.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic alias = aliases.Item[i];
               if ((string)alias.Name == name)
               {
                  alias.Delete();
                  ServerSession.Release(alias);
                  break;
               }
               ServerSession.Release(alias);
            }
            ServerSession.Release(aliases);
            ServerSession.Release(domain);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the alias: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }

         ReloadAliases();
      }

      private void AddDistList_Click(object sender, RoutedEventArgs e)
      {
         string address = NewDistAddress.Text.Trim();
         if (DomainList.SelectedItem == null || address.Length == 0)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = OpenSelectedDomain(domains);
            dynamic lists = domain.DistributionLists;
            dynamic list = lists.Add();
            list.Address = address;
            list.Active = true;
            list.Save();
            ServerSession.Release(list);
            ServerSession.Release(lists);
            ServerSession.Release(domain);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not create the list: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(domains);
         }

         NewDistAddress.Text = "";
         ReloadDistLists();
      }

      private void EditRecipients_Click(object sender, RoutedEventArgs e)
      {
         string address = (sender as FrameworkElement)?.Tag as string;
         string domainName = SelectedDomainName();
         if (address == null || domainName == null)
            return;

         new RecipientsDialog(Window.GetWindow(this), domainName, address).ShowDialog();
      }

      private void EditDistList_Click(object sender, RoutedEventArgs e)
      {
         string address = (sender as FrameworkElement)?.Tag as string;
         string domainName = SelectedDomainName();
         if (address == null || domainName == null)
            return;

         new DistributionListDialog(Window.GetWindow(this), domainName, address).ShowDialog();
         ReloadDistLists();
      }

      private void DeleteDistList_Click(object sender, RoutedEventArgs e)
      {
         string address = (sender as FrameworkElement)?.Tag as string;
         if (address == null || DomainList.SelectedItem == null)
            return;

         if (MessageBox.Show("Delete the distribution list " + address + "?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = OpenSelectedDomain(domains);
            dynamic lists = domain.DistributionLists;
            int count = (int)lists.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic list = lists.Item[i];
               if ((string)list.Address == address)
               {
                  list.Delete();
                  ServerSession.Release(list);
                  break;
               }
               ServerSession.Release(list);
            }
            ServerSession.Release(lists);
            ServerSession.Release(domain);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the list: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }

         ReloadDistLists();
      }

      private void ReloadAccounts()
      {
         string domainName = SelectedDomainName();
         if (domainName == null)
         {
            AccountsHeader.Text = "Select a domain";
            AccountList.ItemsSource = null;
            return;
         }

         AccountsHeader.Text = domainName + " - accounts";
         NewAccountBox.Text = "user@" + domainName;

         var rows = new List<AccountRow>();
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = domains.ItemByName[domainName];
            dynamic accounts = domain.Accounts;
            int count = (int)accounts.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic account = accounts.Item[i];
               rows.Add(new AccountRow { Address = (string)account.Address, Active = (bool)account.Active });
               ServerSession.Release(account);
            }
            ServerSession.Release(accounts);
            ServerSession.Release(domain);
         }
         finally
         {
            ServerSession.Release(domains);
         }

         AccountList.ItemsSource = rows;
         ListSearch.Apply(AccountList, AccountSearch.Text);
      }

      private void EditDomain_Click(object sender, RoutedEventArgs e)
      {
         string name = (sender as FrameworkElement)?.Tag as string;
         if (name == null)
            return;

         new DomainDialog(Window.GetWindow(this), name).ShowDialog();
         ReloadDomains();
      }

      /// <summary>
      /// Deletes a domain and everything under it. The confirmation counts what is
      /// about to go, because "Delete example.com?" reads as removing a list row,
      /// and what actually happens is that every mailbox and every stored message
      /// in the domain is destroyed.
      /// </summary>
      private void DeleteDomain_Click(object sender, RoutedEventArgs e)
      {
         string name = (sender as FrameworkElement)?.Tag as string;
         if (name == null)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            int accountCount;
            int domainId;
            dynamic domain = domains.ItemByName[name];
            try
            {
               domainId = (int)domain.ID;
               dynamic accounts = domain.Accounts;
               accountCount = (int)accounts.Count;
               ServerSession.Release(accounts);
            }
            finally
            {
               ServerSession.Release(domain);
            }

            string counted = accountCount == 1 ? "its 1 account" : "its " + accountCount + " accounts";

            if (MessageBox.Show(
                   "Delete the domain " + name + "?\n\nThis permanently removes " + counted + ", every " +
                   "message stored in them, and all of the domain's aliases and distribution lists. " +
                   "There is no undo.",
                   "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
               return;

            domains.DeleteByDBID(domainId);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the domain: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }

         ReloadDomains();
      }

      private void AddDomain_Click(object sender, RoutedEventArgs e)
      {
         string name = NewDomainBox.Text.Trim();
         if (name.Length == 0 || !name.Contains('.'))
         {
            MessageBox.Show("Enter a valid domain name.", "Control Panel");
            return;
         }

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = domains.Add();
            domain.Name = name;
            domain.Active = true;
            domain.Save();
            ServerSession.Release(domain);
         }
         catch (Exception ex)
         {
            // Without this, a duplicate name - the likeliest failure - took the
            // whole window down instead of saying so.
            MessageBox.Show("Could not create the domain: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(domains);
         }

         NewDomainBox.Text = "";
         ReloadDomains();
      }

      private void GenerateAccountPassword_Click(object sender, RoutedEventArgs e)
      {
         string pw = Services.PasswordGenerator.Generate(16);
         NewAccountPassword.Password = pw;
         try { Clipboard.SetText(pw); } catch (Exception) { }
         Services.Toast.Info("Generated password copied to clipboard \u2014 reveal it with the eye icon.", "Password");
      }

      private void AddAccount_Click(object sender, RoutedEventArgs e)
      {
         string domainName = SelectedDomainName();
         string address = NewAccountBox.Text.Trim();
         string password = NewAccountPassword.Password;

         if (domainName == null || address.Length == 0 || password.Length == 0)
         {
            MessageBox.Show("Address and password are required.", "Control Panel");
            return;
         }

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = domains.ItemByName[domainName];
            dynamic accounts = domain.Accounts;
            dynamic account = accounts.Add();
            account.Address = address;
            account.Password = password;
            account.Active = true;
            account.Save();
            ServerSession.Release(account);
            ServerSession.Release(accounts);
            ServerSession.Release(domain);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not create the account: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(domains);
         }

         NewAccountPassword.Password = "";
         ReloadAccounts();
      }

      private void EditAccount_Click(object sender, RoutedEventArgs e)
      {
         string address = (sender as FrameworkElement)?.Tag as string;
         string domainName = SelectedDomainName();
         if (address == null || domainName == null)
            return;

         new AccountDialog(Window.GetWindow(this), domainName, address).ShowDialog();
         ReloadAccounts();
      }

      private void DeleteAccount_Click(object sender, RoutedEventArgs e)
      {
         string address = (sender as FrameworkElement)?.Tag as string;
         string domainName = SelectedDomainName();
         if (address == null || domainName == null)
            return;

         if (MessageBox.Show("Delete " + address + "?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = domains.ItemByName[domainName];
            dynamic accounts = domain.Accounts;
            dynamic account = accounts.ItemByAddress[address];
            account.Delete();
            ServerSession.Release(account);
            ServerSession.Release(accounts);
            ServerSession.Release(domain);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the account: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }

         ReloadAccounts();
      }
   }
}
