// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using System.Linq;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   public partial class QueueView : UserControl, IPageLifecycle
   {
      public class QueueRow
      {
         public string Id { get; set; }
         public string Created { get; set; }
         public string From { get; set; }
         public string Recipients { get; set; }
         public string NextTry { get; set; }
         public string Tries { get; set; }
         public string File { get; set; }
      }

      public QueueView()
      {
         InitializeComponent();
         // The three act on the selected message; enabled only while one is selected.
         hMailServer.ControlPanel.Services.SelectionGate.Bind(QueueGrid, ViewButton, RetryButton, RemoveButton);
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

      private void View_Click(object sender, RoutedEventArgs e)
      {
         if (QueueGrid.SelectedItem is not QueueRow row)
         {
            ShowNotice_(StatusLevel.Information, L("Select a message first."));
            return;
         }

         new MessageViewerDialog(Window.GetWindow(this), row.File).ShowDialog();
      }

      private void Retry_Click(object sender, RoutedEventArgs e)
      {
         if (QueueGrid.SelectedItem is not QueueRow row)
         {
            ShowNotice_(StatusLevel.Information, L("Select a message first."));
            return;
         }

         try
         {
            dynamic queue = ServerSession.Current.Application.GlobalObjects.DeliveryQueue;
            // Message ids are 64-bit on the server; narrowing to Int32 threw
            // OverflowException on long-lived installations (Remove already used Int64).
            queue.ResetDeliveryTime(Convert.ToInt64(row.Id));
            queue.StartDelivery();
            ServerSession.Release(queue);
            Reload();
            ShowNotice_(StatusLevel.Good, F("Delivery retriggered for message {0}.", row.Id));
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            ShowNotice_(StatusLevel.Critical, F("Could not retrigger delivery: {0}", ex.Message));
         }
      }

      private void Remove_Click(object sender, RoutedEventArgs e)
      {
         if (QueueGrid.SelectedItem is not QueueRow row)
         {
            ShowNotice_(StatusLevel.Information, L("Select a message first."));
            return;
         }

         if (MessageBox.Show(F("Remove message {0} from the queue?", row.Id), L("Control Panel"),
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         try
         {
            dynamic queue = ServerSession.Current.Application.GlobalObjects.DeliveryQueue;
            queue.Remove(Convert.ToInt64(row.Id));
            ServerSession.Release(queue);
            Reload();
            ShowNotice_(StatusLevel.Good, F("Message {0} removed.", row.Id));
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            ShowNotice_(StatusLevel.Critical, F("Could not remove the message: {0}", ex.Message));
         }
      }

      private void Reload()
      {
         try
         {
            var snap = ServerSession.Current.ReadStatus(includeQueueRows: true);

            var rows = new List<QueueRow>();
            // Tab-separated: id, created, from, recipients, next try, file, locked, tries
            foreach (string[] columns in snap.QueueRows
                                            .Select(line => line.Split('\t'))
                                            .Where(c => c.Length >= 8))
            {

               rows.Add(new QueueRow
               {
                  Id = columns[0],
                  Created = columns[1],
                  From = string.IsNullOrWhiteSpace(columns[2]) ? "<>" : columns[2],
                  Recipients = columns[3],
                  NextTry = columns[4],
                  File = columns[5],
                  Tries = columns[7]
               });
            }

            QueueGrid.ItemsSource = rows;
            ListSearch.Apply(QueueGrid, SearchBar.SearchText);
            StatusText.Show(EmptyStatus, Notice, rows.Count, null, L("The delivery queue is empty."));
            Header.Subtitle = rows.Count == 0
               ? L("Messages waiting for delivery.")
               : F("{0} message(s) waiting for delivery.", rows.Count);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            ShowNotice_(StatusLevel.Critical, F("Could not read the queue: {0}", ex.Message));
         }
      }

      /// <summary>What the last action said, at its level: a confirmation in
      /// green, a failure in red, each with its shape and word.</summary>
      private void ShowNotice_(StatusLevel level, string text)
      {
         Notice.Level = level;
         Notice.Text = text;
         Notice.Visibility = Visibility.Visible;
      }

      private void Search_TextChanged(object sender, EventArgs e)
         => ListSearch.Apply(QueueGrid, SearchBar.SearchText);
   }
}
