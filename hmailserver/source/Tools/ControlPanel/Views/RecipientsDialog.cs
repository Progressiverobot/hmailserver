using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal editor for the recipients of a distribution list.
   /// </summary>
   public class RecipientsDialog : Window
   {
      private readonly string domainName_;
      private readonly string listAddress_;
      private readonly ListBox listBox_ = new();
      private readonly TextBox addBox_ = new();

      public RecipientsDialog(Window owner, string domainName, string listAddress)
      {
         domainName_ = domainName;
         listAddress_ = listAddress;

         Owner = owner;
         Title = "Recipients - " + listAddress;
         Width = 440;
         Height = 420;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var grid = new Grid { Margin = new Thickness(16) };
         grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         listBox_.BorderThickness = new Thickness(1);
         listBox_.SetResourceReference(Control.BorderBrushProperty, "ControlElevationBorderBrush");
         listBox_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         listBox_.Background = System.Windows.Media.Brushes.Transparent;
         listBox_.FontSize = 13;
         grid.Children.Add(listBox_);

         var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         addBox_.FontSize = 13;
         addBox_.Padding = new Thickness(6);
         addBox_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         addBox_.Background = System.Windows.Media.Brushes.Transparent;
         bottom.Children.Add(addBox_);

         var addButton = new Wpf.Ui.Controls.Button { Content = "Add", Margin = new Thickness(8, 0, 0, 0) };
         addButton.Click += (s, e) => AddRecipient();
         Grid.SetColumn(addButton, 1);
         bottom.Children.Add(addButton);

         var removeButton = new Wpf.Ui.Controls.Button
         {
            Content = "Remove selected",
            Margin = new Thickness(8, 0, 0, 0),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Danger
         };
         removeButton.Click += (s, e) => RemoveSelected();
         Grid.SetColumn(removeButton, 2);
         bottom.Children.Add(removeButton);

         Grid.SetRow(bottom, 1);
         grid.Children.Add(bottom);

         Content = grid;
         Loaded += (s, e) => Reload();
      }

      private dynamic OpenList(dynamic domains)
      {
         dynamic domain = domains.ItemByName[domainName_];
         dynamic lists = domain.DistributionLists;
         int count = (int) lists.Count;
         for (int i = 0; i < count; i++)
         {
            dynamic list = lists.Item[i];
            if ((string) list.Address == listAddress_)
            {
               ServerSession.Release(lists);
               ServerSession.Release(domain);
               return list;
            }
            ServerSession.Release(list);
         }
         ServerSession.Release(lists);
         ServerSession.Release(domain);
         return null;
      }

      private void Reload()
      {
         var rows = new List<string>();
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic list = OpenList(domains);
            if (list != null)
            {
               dynamic recipients = list.Recipients;
               int count = (int) recipients.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic recipient = recipients.Item[i];
                  rows.Add((string) recipient.RecipientAddress);
                  ServerSession.Release(recipient);
               }
               ServerSession.Release(recipients);
               ServerSession.Release(list);
            }
         }
         catch (Exception)
         {
         }
         finally
         {
            ServerSession.Release(domains);
         }

         listBox_.ItemsSource = rows;
      }

      private void AddRecipient()
      {
         string address = addBox_.Text.Trim();
         if (address.Length == 0)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic list = OpenList(domains);
            if (list != null)
            {
               dynamic recipients = list.Recipients;
               dynamic recipient = recipients.Add();
               recipient.RecipientAddress = address;
               recipient.Save();
               ServerSession.Release(recipient);
               ServerSession.Release(recipients);
               ServerSession.Release(list);
            }
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not add the recipient: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }

         addBox_.Text = "";
         Reload();
      }

      private void RemoveSelected()
      {
         string address = listBox_.SelectedItem as string;
         if (address == null)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic list = OpenList(domains);
            if (list != null)
            {
               dynamic recipients = list.Recipients;
               int count = (int) recipients.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic recipient = recipients.Item[i];
                  if ((string) recipient.RecipientAddress == address)
                  {
                     recipient.Delete();
                     ServerSession.Release(recipient);
                     break;
                  }
                  ServerSession.Release(recipient);
               }
               ServerSession.Release(recipients);
               ServerSession.Release(list);
            }
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not remove the recipient: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }

         Reload();
      }
   }
}
