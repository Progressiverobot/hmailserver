using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

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

      private void ReloadDomains()
      {
         var names = new List<string>();
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            int count = (int) domains.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic domain = domains.Item[i];
               names.Add((string) domain.Name);
               ServerSession.Release(domain);
            }
         }
         finally
         {
            ServerSession.Release(domains);
         }

         DomainList.ItemsSource = names;
         if (names.Count > 0 && DomainList.SelectedIndex < 0)
            DomainList.SelectedIndex = 0;
      }

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
      }

      private dynamic OpenSelectedDomain(dynamic domains)
      {
         string domainName = DomainList.SelectedItem as string;
         return domainName == null ? null : domains.ItemByName[domainName];
      }

      private void ReloadAliases()
      {
         var rows = new List<AliasRow>();
         if (DomainList.SelectedItem != null)
         {
            dynamic domains = ServerSession.Current.Application.Domains;
            try
            {
               dynamic domain = OpenSelectedDomain(domains);
               dynamic aliases = domain.Aliases;
               int count = (int) aliases.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic alias = aliases.Item[i];
                  rows.Add(new AliasRow
                  {
                     Name = (string) alias.Name,
                     Display = (string) alias.Name + "  →  " + (string) alias.Value
                  });
                  ServerSession.Release(alias);
               }
               ServerSession.Release(aliases);
               ServerSession.Release(domain);
            }
            catch (Exception)
            {
            }
            finally
            {
               ServerSession.Release(domains);
            }
         }
         AliasList.ItemsSource = rows;
      }

      private void ReloadDistLists()
      {
         var rows = new List<string>();
         if (DomainList.SelectedItem != null)
         {
            dynamic domains = ServerSession.Current.Application.Domains;
            try
            {
               dynamic domain = OpenSelectedDomain(domains);
               dynamic lists = domain.DistributionLists;
               int count = (int) lists.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic list = lists.Item[i];
                  rows.Add((string) list.Address);
                  ServerSession.Release(list);
               }
               ServerSession.Release(lists);
               ServerSession.Release(domain);
            }
            catch (Exception)
            {
            }
            finally
            {
               ServerSession.Release(domains);
            }
         }
         DistList.ItemsSource = rows;
      }

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

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = OpenSelectedDomain(domains);
            dynamic aliases = domain.Aliases;
            int count = (int) aliases.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic alias = aliases.Item[i];
               if ((string) alias.Name == name)
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
         string domainName = DomainList.SelectedItem as string;
         if (address == null || domainName == null)
            return;

         new RecipientsDialog(Window.GetWindow(this), domainName, address).ShowDialog();
      }

      private void DeleteDistList_Click(object sender, RoutedEventArgs e)
      {
         string address = (sender as FrameworkElement)?.Tag as string;
         if (address == null || DomainList.SelectedItem == null)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = OpenSelectedDomain(domains);
            dynamic lists = domain.DistributionLists;
            int count = (int) lists.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic list = lists.Item[i];
               if ((string) list.Address == address)
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
         string domainName = DomainList.SelectedItem as string;
         if (domainName == null)
         {
            AccountsHeader.Text = "Select a domain";
            AccountList.ItemsSource = null;
            return;
         }

         AccountsHeader.Text = domainName + " - accounts";
         NewAccountBox.Text = "user@" + domainName;

         var addresses = new List<string>();
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = domains.ItemByName[domainName];
            dynamic accounts = domain.Accounts;
            int count = (int) accounts.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic account = accounts.Item[i];
               addresses.Add((string) account.Address);
               ServerSession.Release(account);
            }
            ServerSession.Release(accounts);
            ServerSession.Release(domain);
         }
         finally
         {
            ServerSession.Release(domains);
         }

         AccountList.ItemsSource = addresses;
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
         finally
         {
            ServerSession.Release(domains);
         }

         NewDomainBox.Text = "";
         ReloadDomains();
      }

      private void AddAccount_Click(object sender, RoutedEventArgs e)
      {
         string domainName = DomainList.SelectedItem as string;
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
         string domainName = DomainList.SelectedItem as string;
         if (address == null || domainName == null)
            return;

         new AccountDialog(Window.GetWindow(this), domainName, address).ShowDialog();
         ReloadAccounts();
      }

      private void DeleteAccount_Click(object sender, RoutedEventArgs e)
      {
         string address = (sender as FrameworkElement)?.Tag as string;
         string domainName = DomainList.SelectedItem as string;
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
