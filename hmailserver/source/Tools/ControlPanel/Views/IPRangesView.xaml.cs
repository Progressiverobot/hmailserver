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
   public partial class IPRangesView : UserControl, IPageLifecycle
   {
      public class RangeRow
      {
         public int Id { get; set; }
         public string Name { get; set; }
         public string LowerIP { get; set; }
         public string UpperIP { get; set; }
         public int Priority { get; set; }
         public string Smtp { get; set; }
         public string Imap { get; set; }
         public string Pop3 { get; set; }
      }

      public IPRangesView()
      {
         InitializeComponent();
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private void Reload()
      {
         var rows = new List<RangeRow>();
         dynamic ranges = ServerSession.Current.Application.Settings.SecurityRanges;
         try
         {
            int count = (int)ranges.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic range = ranges.Item[i];
               rows.Add(new RangeRow
               {
                  Id = (int)range.ID,
                  Name = (string)range.Name,
                  LowerIP = (string)range.LowerIP,
                  UpperIP = (string)range.UpperIP,
                  Priority = (int)range.Priority,
                  Smtp = (bool)range.AllowSMTPConnections ? "Yes" : "No",
                  Imap = (bool)range.AllowIMAPConnections ? "Yes" : "No",
                  Pop3 = (bool)range.AllowPOP3Connections ? "Yes" : "No"
               });
               ServerSession.Release(range);
            }
         }
         finally
         {
            ServerSession.Release(ranges);
         }

         RangeGrid.ItemsSource = rows;
         ListSearch.Apply(RangeGrid, SearchBox.Text);
         StatusText.Show(EmptyStatus, rows.Count, null, "No IP ranges defined yet.");
      }

      private void Search_TextChanged(object sender, TextChangedEventArgs e)
         => ListSearch.Apply(RangeGrid, SearchBox.Text);

      private void RangeGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
      {
         if (RangeGrid.SelectedItem is not RangeRow row)
         {
            PermHeader.Text = "Permissions (select a range)";
            SavePermsButton.IsEnabled = false;
            return;
         }

         dynamic ranges = ServerSession.Current.Application.Settings.SecurityRanges;
         try
         {
            dynamic range = FindRange(ranges, row);
            if (range == null)
               return;

            PermHeader.Text = "Permissions - " + row.Name;
            PermSmtp.IsChecked = (bool)range.AllowSMTPConnections;
            PermImap.IsChecked = (bool)range.AllowIMAPConnections;
            PermPop3.IsChecked = (bool)range.AllowPOP3Connections;
            PermTlsAuth.IsChecked = (bool)range.RequireSSLTLSForAuth;
            PermLL.IsChecked = (bool)range.AllowDeliveryFromLocalToLocal;
            PermLR.IsChecked = (bool)range.AllowDeliveryFromLocalToRemote;
            PermRL.IsChecked = (bool)range.AllowDeliveryFromRemoteToLocal;
            PermRR.IsChecked = (bool)range.AllowDeliveryFromRemoteToRemote;
            SavePermsButton.IsEnabled = true;
            ServerSession.Release(range);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            SavePermsButton.IsEnabled = false;
         }
         finally
         {
            ServerSession.Release(ranges);
         }
      }

      private static dynamic FindRange(dynamic ranges, RangeRow row)
      {
         int count = (int)ranges.Count;
         for (int i = 0; i < count; i++)
         {
            dynamic range = ranges.Item[i];
            if ((string)range.Name == row.Name && (string)range.LowerIP == row.LowerIP)
               return range;
            ServerSession.Release(range);
         }
         return null;
      }

      private void SavePerms_Click(object sender, RoutedEventArgs e)
      {
         if (RangeGrid.SelectedItem is not RangeRow row)
            return;

         dynamic ranges = ServerSession.Current.Application.Settings.SecurityRanges;
         try
         {
            dynamic range = FindRange(ranges, row);
            if (range == null)
               return;

            range.AllowSMTPConnections = PermSmtp.IsChecked is true;
            range.AllowIMAPConnections = PermImap.IsChecked is true;
            range.AllowPOP3Connections = PermPop3.IsChecked is true;
            range.RequireSSLTLSForAuth = PermTlsAuth.IsChecked is true;
            range.AllowDeliveryFromLocalToLocal = PermLL.IsChecked is true;
            range.AllowDeliveryFromLocalToRemote = PermLR.IsChecked is true;
            range.AllowDeliveryFromRemoteToLocal = PermRL.IsChecked is true;
            range.AllowDeliveryFromRemoteToRemote = PermRR.IsChecked is true;
            range.Save();
            ServerSession.Release(range);
            PermHeader.Text = "Permissions - " + row.Name + " (saved)";
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not save the permissions: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(ranges);
         }

         Reload();
      }

      private void Edit_Click(object sender, RoutedEventArgs e)
      {
         if (RangeGrid.SelectedItem is not RangeRow row)
         {
            MessageBox.Show("Select a range first.", "Control Panel");
            return;
         }

         new IPRangeDialog(Window.GetWindow(this), row.Id).ShowDialog();
         Reload();
      }

      private void Add_Click(object sender, RoutedEventArgs e)
      {
         string name = NewName.Text.Trim();
         string lower = NewLower.Text.Trim();
         string upper = NewUpper.Text.Trim();

         if (name.Length == 0 || lower.Length == 0 || upper.Length == 0)
         {
            MessageBox.Show("Name, lower IP and upper IP are required.", "Control Panel");
            return;
         }

         if (!int.TryParse(NewPriority.Text.Trim(), out int priority))
            priority = 15;

         dynamic ranges = ServerSession.Current.Application.Settings.SecurityRanges;
         try
         {
            dynamic range = ranges.Add();
            range.Name = name;
            range.LowerIP = lower;
            range.UpperIP = upper;
            range.Priority = priority;
            range.AllowSMTPConnections = true;
            range.AllowIMAPConnections = true;
            range.AllowPOP3Connections = true;
            range.Save();
            ServerSession.Release(range);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not add the range: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(ranges);
         }

         NewName.Text = NewLower.Text = NewUpper.Text = "";
         Reload();
      }

      /// <summary>
      /// Settings.SecurityRanges.SetDefault() over COM: every range is replaced with
      /// the standard two - "My computer" (127.0.0.1, priority 30) and "Internet"
      /// (0.0.0.0-255.255.255.255, priority 10). Another classic-Administrator action
      /// that lost its button when that tool was retired. The server's failure text
      /// is shown verbatim; it explains that the old ranges are gone even when
      /// writing the new ones failed, which is the state an administrator most needs
      /// to know about.
      /// </summary>
      private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
      {
         if (MessageBox.Show(
                "Replace ALL IP ranges with the defaults?\n\nEvery custom range - including any auto-ban " +
                "entries - will be removed and replaced with 'My computer' (127.0.0.1) and 'Internet' " +
                "(everything else). This changes who may connect and relay, immediately.",
                "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic ranges = ServerSession.Current.Application.Settings.SecurityRanges;
         try
         {
            ranges.SetDefault();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(ex.Message, "Control Panel", MessageBoxButton.OK, MessageBoxImage.Error);
         }
         finally
         {
            ServerSession.Release(ranges);
         }

         Reload();
      }

      private void Delete_Click(object sender, RoutedEventArgs e)
      {
         if (RangeGrid.SelectedItem is not RangeRow row)
            return;

         if (MessageBox.Show("Delete IP range '" + row.Name + "'?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic ranges = ServerSession.Current.Application.Settings.SecurityRanges;
         try
         {
            int count = (int)ranges.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic range = ranges.Item[i];
               if ((string)range.Name == row.Name && (string)range.LowerIP == row.LowerIP)
               {
                  range.Delete();
                  ServerSession.Release(range);
                  break;
               }
               ServerSession.Release(range);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not delete the range: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(ranges);
         }

         Reload();
      }
   }
}
