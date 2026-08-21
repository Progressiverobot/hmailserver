using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using System.Linq;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

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
            SubtitleText.Text = "Select a message first.";
            return;
         }

         new MessageViewerDialog(Window.GetWindow(this), row.File).ShowDialog();
      }

      private void Retry_Click(object sender, RoutedEventArgs e)
      {
         if (QueueGrid.SelectedItem is not QueueRow row)
         {
            SubtitleText.Text = "Select a message first.";
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
            SubtitleText.Text = "Delivery retriggered for message " + row.Id + ".";
            Reload();
         }
         catch (Exception ex)
         {
            SubtitleText.Text = "Could not retrigger delivery: " + ex.Message;
         }
      }

      private void Remove_Click(object sender, RoutedEventArgs e)
      {
         if (QueueGrid.SelectedItem is not QueueRow row)
         {
            SubtitleText.Text = "Select a message first.";
            return;
         }

         if (MessageBox.Show("Remove message " + row.Id + " from the queue?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         try
         {
            dynamic queue = ServerSession.Current.Application.GlobalObjects.DeliveryQueue;
            queue.Remove(Convert.ToInt64(row.Id));
            ServerSession.Release(queue);
            SubtitleText.Text = "Message " + row.Id + " removed.";
            Reload();
         }
         catch (Exception ex)
         {
            SubtitleText.Text = "Could not remove the message: " + ex.Message;
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
            ListSearch.Apply(QueueGrid, SearchBox.Text);
            SubtitleText.Text = rows.Count == 0
               ? "The delivery queue is empty."
               : rows.Count + " message(s) waiting for delivery.";
         }
         catch (Exception ex)
         {
            SubtitleText.Text = "Could not read the queue: " + ex.Message;
         }
      }

      private void Search_TextChanged(object sender, TextChangedEventArgs e)
         => ListSearch.Apply(QueueGrid, SearchBox.Text);
   }
}
