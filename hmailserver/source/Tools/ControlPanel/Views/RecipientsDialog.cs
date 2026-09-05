// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal editor for the recipients of a distribution list.
   /// </summary>
   public class RecipientsDialog : FluentDialogWindow
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
         listBox_.FontSize = Typography.Body;
         AutomationProperties.SetName(listBox_, "Recipients of " + listAddress);
         grid.Children.Add(listBox_);

         var bottom = new Grid { Margin = new Thickness(0, 12, 0, 0) };
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         addBox_.FontSize = Typography.Body;
         addBox_.Padding = new Thickness(6);
         addBox_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         addBox_.Background = System.Windows.Media.Brushes.Transparent;

         // The box has no caption anywhere on the dialog - it is a bare box beside
         // three buttons - so a screen reader announced it as "edit".
         AutomationProperties.SetName(addBox_, "E-mail address to add to the list");

         // Enter adds. Handled, so the keystroke cannot also reach a default button
         // if one is ever added here.
         addBox_.KeyDown += (s, e) =>
         {
            if (e.Key != Key.Enter)
               return;

            AddRecipient();
            e.Handled = true;
         };
         bottom.Children.Add(addBox_);

         var addButton = new Wpf.Ui.Controls.Button { Content = "_Add", Margin = new Thickness(8, 0, 0, 0) };
         addButton.Click += (s, e) => AddRecipient();
         Grid.SetColumn(addButton, 1);
         bottom.Children.Add(addButton);

         var adButton = new Wpf.Ui.Controls.Button { Content = "Add from A_D\u2026", Margin = new Thickness(8, 0, 0, 0) };
         adButton.Click += (s, e) => ImportFromActiveDirectory();
         Grid.SetColumn(adButton, 2);
         bottom.Children.Add(adButton);

         var removeButton = new Wpf.Ui.Controls.Button
         {
            Content = "_Remove selected",
            Margin = new Thickness(8, 0, 0, 0),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Danger
         };
         removeButton.Click += (s, e) => RemoveSelected();
         Grid.SetColumn(removeButton, 3);
         bottom.Children.Add(removeButton);

         Grid.SetRow(bottom, 1);
         grid.Children.Add(bottom);

         // This dialog had no closing button of any kind, and nothing marked
         // IsCancel, so the only ways out were the title-bar X and Alt+F4: a
         // keyboard user who opened it could operate every control in it and could
         // not leave it. Escape now closes it, and there is a visible Close for
         // anybody who was looking for one.
         //
         // A row of its own rather than beside Add / Add from AD / Remove selected,
         // because those three act on the list and this one ends the dialog -
         // putting a "leave" next to a "delete" is how the wrong one gets pressed.
         var closeButton = new Wpf.Ui.Controls.Button
         {
            Content = "Close",
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            IsCancel = true
         };
         closeButton.Click += (s, e) => Close();
         AutomationProperties.SetName(closeButton, "Close the recipient list");

         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         Grid.SetRow(closeButton, 2);
         grid.Children.Add(closeButton);

         Content = grid;
         Loaded += (s, e) => Reload();
      }

      private dynamic OpenList(dynamic domains)
      {
         dynamic domain = domains.ItemByName[domainName_];
         dynamic lists = domain.DistributionLists;
         int count = (int)lists.Count;
         for (int i = 0; i < count; i++)
         {
            dynamic list = lists.Item[i];
            if ((string)list.Address == listAddress_)
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
               int count = (int)recipients.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic recipient = recipients.Item[i];
                  rows.Add((string)recipient.RecipientAddress);
                  ServerSession.Release(recipient);
               }
               ServerSession.Release(recipients);
               ServerSession.Release(list);
            }
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
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
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
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

      private void ImportFromActiveDirectory()
      {
         var picker = new ActiveDirectoryPickerDialog(this, multiSelect: true);
         if (picker.ShowDialog() != true || picker.SelectedUsers.Count == 0)
            return;

         var emails = new List<string>();
         int skipped = 0;
         foreach (AdUser u in picker.SelectedUsers)
         {
            if (!string.IsNullOrWhiteSpace(u.Email))
               emails.Add(u.Email.Trim());
            else
               skipped++;
         }

         if (emails.Count == 0)
         {
            MessageBox.Show("None of the selected accounts have an e-mail address.", "Control Panel");
            return;
         }

         int added = 0;
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic list = OpenList(domains);
            if (list != null)
            {
               dynamic recipients = list.Recipients;
               foreach (string address in emails)
               {
                  dynamic recipient = recipients.Add();
                  recipient.RecipientAddress = address;
                  recipient.Save();
                  ServerSession.Release(recipient);
                  added++;
               }
               ServerSession.Release(recipients);
               ServerSession.Release(list);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not add all recipients: " + ServerSession.DescribeComError(ex), "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }

         Reload();
         if (skipped > 0)
            MessageBox.Show(added + " recipient(s) added. " + skipped + " account(s) had no e-mail address and were skipped.", "Control Panel");
      }

      private void RemoveSelected()
      {
         string address = listBox_.SelectedItem as string;
         if (address == null)
            return;

         if (MessageBox.Show("Remove the recipient " + address + " from this list?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic list = OpenList(domains);
            if (list != null)
            {
               dynamic recipients = list.Recipients;
               int count = (int)recipients.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic recipient = recipients.Item[i];
                  if ((string)recipient.RecipientAddress == address)
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
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
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
