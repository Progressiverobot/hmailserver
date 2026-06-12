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
