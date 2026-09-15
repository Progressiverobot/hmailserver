// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal editor for the recipients of a distribution list, on the standard
   /// frame: the list, the add row under it, what happened in a notice above,
   /// and Close alone in the footer - the three buttons that act on the list
   /// stay beside the list, and the one that ends the dialog stays apart from
   /// them, because putting a "leave" next to a "delete" is how the wrong one
   /// gets pressed.
   /// </summary>
   public class RecipientsDialog : FluentDialogWindow
   {
      private readonly string domainName_;
      private readonly string listAddress_;
      private readonly ListBox listBox_ = new() { Height = 240 };
      private readonly TextBox addBox_ = new();
      private readonly InlineNotice notice_ = DialogFields.Notice();

      public RecipientsDialog(Window owner, string domainName, string listAddress)
      {
         domainName_ = domainName;
         listAddress_ = listAddress;

         Owner = owner;
         Title = L("Recipients - ") + listAddress;

         var body = new StackPanel();
         body.Children.Add(notice_);

         AutomationProperties.SetName(listBox_, F("Recipients of {0}", listAddress));
         body.Children.Add(listBox_);

         var bottom = new Grid { Margin = new Thickness(0, DesignTokens.Space.Md, 0, 0) };
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         // The box has no caption anywhere on the dialog - it is a bare box beside
         // three buttons - so its accessible name says what it is for.
         AutomationProperties.SetName(addBox_, L("E-mail address to add to the list"));

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

         var addButton = new Wpf.Ui.Controls.Button { Content = L("_Add"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         addButton.Click += (s, e) => AddRecipient();
         Grid.SetColumn(addButton, 1);
         bottom.Children.Add(addButton);

         var adButton = new Wpf.Ui.Controls.Button { Content = L("Add from A_D…"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         adButton.Click += (s, e) => ImportFromActiveDirectory();
         Grid.SetColumn(adButton, 2);
         bottom.Children.Add(adButton);

         var removeButton = new Wpf.Ui.Controls.Button
         {
            Content = L("_Remove selected"),
            Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Danger
         };
         removeButton.Click += (s, e) => RemoveSelected();
         Grid.SetColumn(removeButton, 3);
         bottom.Children.Add(removeButton);

         body.Children.Add(bottom);

         // Escape closes; there is no default button, because Enter in the add
         // box means "add" and Enter with a row selected must not close the list.
         var closeButton = new Wpf.Ui.Controls.Button { Content = L("Close"), MinWidth = 88 };
         closeButton.Click += (s, e) => Close();
         AutomationProperties.SetName(closeButton, L("Close the recipient list"));

         UseFrame(F("Recipients of {0}", listAddress), body, null, closeButton, width: 520);
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

         notice_.Hide();
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
            notice_.Show(StatusLevel.Critical, F("Could not add the recipient: {0}", ex.Message));
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

         notice_.Hide();
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
            notice_.Show(StatusLevel.Warning, L("None of the selected accounts have an e-mail address."));
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
            notice_.Show(StatusLevel.Critical, F("Could not add all recipients: {0}", ServerSession.DescribeComError(ex)));
         }
         finally
         {
            ServerSession.Release(domains);
         }

         Reload();
         if (skipped > 0)
            notice_.Show(StatusLevel.Information, F("{0} recipient(s) added. {1} account(s) had no e-mail address and were skipped.", added, skipped));
      }

      private void RemoveSelected()
      {
         string address = listBox_.SelectedItem as string;
         if (address == null)
            return;

         if (!Dialogs.Confirm(F("Remove the recipient {0} from this list?", address), L("Control Panel"), L("_Remove"), destructive: true))
            return;

         notice_.Hide();
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
            notice_.Show(StatusLevel.Critical, F("Could not remove the recipient: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(domains);
         }

         Reload();
      }
   }
}
