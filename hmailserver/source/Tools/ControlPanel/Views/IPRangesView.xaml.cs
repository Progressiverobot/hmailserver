using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   public partial class IPRangesView : UserControl, IPageLifecycle
   {
      public class RangeRow
      {
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
            int count = (int) ranges.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic range = ranges.Item[i];
               rows.Add(new RangeRow
               {
                  Name = (string) range.Name,
                  LowerIP = (string) range.LowerIP,
                  UpperIP = (string) range.UpperIP,
                  Priority = (int) range.Priority,
                  Smtp = (bool) range.AllowSMTPConnections ? "Yes" : "No",
                  Imap = (bool) range.AllowIMAPConnections ? "Yes" : "No",
                  Pop3 = (bool) range.AllowPOP3Connections ? "Yes" : "No"
               });
               ServerSession.Release(range);
            }
         }
         finally
         {
            ServerSession.Release(ranges);
         }

         RangeGrid.ItemsSource = rows;
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
         catch (Exception ex)
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
            int count = (int) ranges.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic range = ranges.Item[i];
               if ((string) range.Name == row.Name && (string) range.LowerIP == row.LowerIP)
               {
                  range.Delete();
                  ServerSession.Release(range);
                  break;
               }
               ServerSession.Release(range);
            }
         }
         catch (Exception ex)
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
