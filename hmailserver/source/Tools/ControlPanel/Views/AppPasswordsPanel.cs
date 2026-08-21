using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// One account's application-specific passwords, embedded in the account editor.
   ///
   /// An app password is a credential for a single mail client, revocable on its own
   /// without changing the password the account holder actually knows. It is the
   /// prerequisite for per-account two-factor authentication, because an IMAP or POP3
   /// client has nowhere to type a code.
   ///
   /// A generated password is shown ONCE, in the card below the list, and there is no
   /// "show" action anywhere on this page - the server stores only a hash, so offering
   /// one would be promising something the store cannot deliver. The list is therefore
   /// about the things it CAN answer: what each credential is for, when it was issued,
   /// and whether it has ever been used. That last column is the useful one: a
   /// credential that has never authenticated is one that can be deleted with no
   /// consequences at all.
   /// </summary>
   public class AppPasswordsPanel : UserControl
   {
      private readonly string domainName_;
      private readonly string address_;

      private readonly ListBox list_ = new()
      {
         FontSize = Typography.Body,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         MinHeight = 160
      };

      private readonly TextBox newName_ = new() { Width = 220, VerticalContentAlignment = VerticalAlignment.Center };
      private readonly TextBlock status_ = new() { FontSize = Typography.Caption, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };

      private readonly Border issuedCard_;
      private readonly TextBox issuedValue_ = new()
      {
         IsReadOnly = true,
         FontFamily = new System.Windows.Media.FontFamily("Consolas"),
         FontSize = 14,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent
      };

      public AppPasswordsPanel(string domainName, string address)
      {
         domainName_ = domainName;
         address_ = address;

         issuedCard_ = new Border { Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
         issuedCard_.SetResourceReference(StyleProperty, "Card");

         Build();
      }

      /// <summary>Loads the list. Called when the account editor opens the tab.</summary>
      public void Reload()
      {
         list_.Items.Clear();

         if (string.IsNullOrEmpty(address_))
         {
            // A new account has no address to hang credentials off yet. The panel is
            // built when the dialog opens, so it does not learn the address the moment
            // the account is saved - say so plainly rather than failing on the first
            // button press.
            status_.Text = "Save the account and reopen it - an app password belongs to an account that exists.";
            newName_.IsEnabled = false;
            return;
         }

         try
         {
            dynamic passwords = OpenCollection();
            try
            {
               int count = (int) passwords.Count;

               for (int i = 0; i < count; i++)
               {
                  dynamic item = passwords[i];
                  try
                  {
                     string lastUsed = (string) item.LastUsedTime;

                     list_.Items.Add(new ListBoxItem
                     {
                        Content = string.Format("{0}{1}   issued {2}   {3}",
                           (string) item.Name,
                           ((bool) item.Active) ? "" : "   (revoked)",
                           (string) item.CreatedTime,
                           string.IsNullOrEmpty(lastUsed) ? "never used" : "last used " + lastUsed),
                        Tag = (int) item.ID
                     });
                  }
                  finally
                  {
                     ServerSession.Release(item);
                  }
               }

               status_.Text = count == 0
                  ? "No app passwords. Create one per mail client, so that losing a device revokes only that device."
                  : count + (count == 1 ? " app password." : " app passwords.");
            }
            finally
            {
               ServerSession.Release(passwords);
            }
         }
         catch (Exception ex)
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private dynamic OpenCollection()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         dynamic domain = domains.ItemByName[domainName_];
         dynamic accounts = domain.Accounts;
         dynamic account = accounts.ItemByAddress[address_];
         dynamic passwords = account.AppPasswords;

         ServerSession.Release(account);
         ServerSession.Release(accounts);
         ServerSession.Release(domain);
         ServerSession.Release(domains);

         return passwords;
      }

      private void Build()
      {
         var root = new StackPanel { Margin = new Thickness(4, 8, 4, 4) };

         var hint = new TextBlock
         {
            Text = "A separate password for one mail client. Losing a laptop then means revoking one line here, "
                 + "rather than changing the account password and reconfiguring every other client.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
         };
         hint.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         root.Children.Add(hint);

         var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
         toolbar.Children.Add(new TextBlock
         {
            Text = "Name",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
         });
         newName_.Text = "";
         toolbar.Children.Add(newName_);
         toolbar.Children.Add(MakeButton("Create", Wpf.Ui.Controls.ControlAppearance.Primary, (_, _) => Create()));
         toolbar.Children.Add(MakeButton("Revoke / restore", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => ToggleActive()));
         toolbar.Children.Add(MakeButton("Delete", Wpf.Ui.Controls.ControlAppearance.Danger, (_, _) => Delete()));
         toolbar.Children.Add(MakeButton("Refresh", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => Reload()));
         root.Children.Add(toolbar);

         var card = new Border { Padding = new Thickness(8) };
         card.SetResourceReference(StyleProperty, "Card");
         card.Child = list_;
         root.Children.Add(card);

         var issued = new StackPanel();
         var issuedTitle = new TextBlock
         {
            Text = "Copy this now - it is not stored and cannot be shown again",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6),
            TextWrapping = TextWrapping.Wrap
         };
         issued.Children.Add(issuedTitle);
         issued.Children.Add(issuedValue_);
         var copy = MakeButton("Copy", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => CopyIssued());
         copy.Margin = new Thickness(0, 8, 0, 0);
         copy.HorizontalAlignment = HorizontalAlignment.Left;
         issued.Children.Add(copy);
         issuedCard_.Child = issued;
         root.Children.Add(issuedCard_);

         status_.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         root.Children.Add(status_);

         Content = root;
      }

      private static Wpf.Ui.Controls.Button MakeButton(string text, Wpf.Ui.Controls.ControlAppearance appearance, RoutedEventHandler onClick)
      {
         var b = new Wpf.Ui.Controls.Button { Content = text, Appearance = appearance, Margin = new Thickness(8, 0, 0, 0), MinWidth = 92 };
         b.Click += onClick;
         return b;
      }

      private void Create()
      {
         if (string.IsNullOrEmpty(address_))
         {
            status_.Text = "Save the account and reopen it first.";
            return;
         }

         string name = newName_.Text.Trim();

         if (name.Length == 0)
         {
            status_.Text = "Give it a name first - it is what you will recognise this credential by when you come to revoke it.";
            return;
         }

         try
         {
            dynamic passwords = OpenCollection();
            try
            {
               dynamic item = passwords.Add();
               try
               {
                  item.Name = name;
                  string clearText = (string) item.Generate();
                  item.Save();

                  issuedValue_.Text = clearText;
                  issuedCard_.Visibility = Visibility.Visible;
                  newName_.Text = "";
               }
               finally
               {
                  ServerSession.Release(item);
               }
            }
            finally
            {
               ServerSession.Release(passwords);
            }

            Reload();
            status_.Text = "Created \"" + name + "\". Enter it in the client as that account's password.";
         }
         catch (Exception ex)
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private int SelectedId()
      {
         return list_.SelectedItem is ListBoxItem item && item.Tag is int id ? id : 0;
      }

      private void ToggleActive()
      {
         int id = SelectedId();
         if (id == 0)
         {
            status_.Text = "Select an app password first.";
            return;
         }

         try
         {
            dynamic passwords = OpenCollection();
            try
            {
               dynamic item = passwords.ItemByDBID[id];
               try
               {
                  item.Active = !(bool) item.Active;
                  item.Save();
               }
               finally
               {
                  ServerSession.Release(item);
               }
            }
            finally
            {
               ServerSession.Release(passwords);
            }

            Reload();
         }
         catch (Exception ex)
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private void Delete()
      {
         int id = SelectedId();
         if (id == 0)
         {
            status_.Text = "Select an app password first.";
            return;
         }

         if (MessageBox.Show(
               "Delete this app password? The client using it will stop connecting immediately.",
               "Delete app password", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         try
         {
            dynamic passwords = OpenCollection();
            try
            {
               passwords.DeleteByDBID(id);
            }
            finally
            {
               ServerSession.Release(passwords);
            }

            Reload();
         }
         catch (Exception ex)
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private void CopyIssued()
      {
         try
         {
            Clipboard.SetText(issuedValue_.Text);
            status_.Text = "Copied.";
         }
         catch (Exception ex)
         {
            status_.Text = "Could not copy: " + ex.Message;
         }
      }
   }
}
