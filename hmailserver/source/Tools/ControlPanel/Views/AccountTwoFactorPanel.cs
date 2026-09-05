// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using QRCoder;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// One account's second factor, embedded in the account editor.
   ///
   /// Enrolling one has a consequence the page has to state before the button is
   /// pressed rather than after: the account's own password stops authenticating mail
   /// clients. It has to, because IMAP, POP3 and SMTP have nowhere to type a code -
   /// which is exactly why app passwords exist, and why this panel points at that tab
   /// instead of leaving somebody to discover their mail has stopped.
   ///
   /// The QR code is shown once, at enrolment, because that is the only moment the
   /// secret exists outside the server: nothing reads it back afterwards. An
   /// administrator who closes this window without scanning it has to enrol again,
   /// which is the correct trade and is said plainly on the page.
   /// </summary>
   public class AccountTwoFactorPanel : UserControl
   {
      private readonly string domainName_;
      private readonly string address_;

      private readonly TextBlock status_ = new() { TextWrapping = TextWrapping.Wrap, FontSize = Typography.Body };
      private readonly Border enrolCard_;
      private readonly Image qr_ = new() { Width = 190, Height = 190, HorizontalAlignment = HorizontalAlignment.Left };
      private readonly TextBox secret_ = new()
      {
         IsReadOnly = true,
         FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily),
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         TextWrapping = TextWrapping.Wrap
      };

      private readonly Button enrol_;
      private readonly Button disable_;

      public AccountTwoFactorPanel(string domainName, string address)
      {
         domainName_ = domainName;
         address_ = address;

         enrolCard_ = new Border { Padding = new Thickness(12), Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
         enrolCard_.SetResourceReference(StyleProperty, "Card");

         enrol_ = MakeButton("_Enrol a second factor", Wpf.Ui.Controls.ControlAppearance.Primary, (_, _) => Enrol());
         disable_ = MakeButton("_Remove", Wpf.Ui.Controls.ControlAppearance.Danger, (_, _) => Disable());

         Build();
      }

      public void Reload()
      {
         if (string.IsNullOrEmpty(address_))
         {
            status_.Text = "Save the account and reopen it - a second factor belongs to an account that exists.";
            enrol_.IsEnabled = false;
            disable_.IsEnabled = false;
            return;
         }

         try
         {
            dynamic account = OpenAccount();
            try
            {
               bool enabled = (bool)account.TOTPEnabled;

               status_.Text = enabled
                  ? "A second factor is enrolled. This account's own password no longer signs in to mail "
                    + "clients - only its app passwords do. Removing the second factor restores it."
                  : "No second factor. Enrolling one stops this account's password working in mail clients "
                    + "immediately, because IMAP, POP3 and SMTP have nowhere to enter a code - set up an app "
                    + "password on the App passwords tab first, or the mailbox will go quiet.";

               enrol_.IsEnabled = true;
               disable_.IsEnabled = enabled;
            }
            finally
            {
               ServerSession.Release(account);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private dynamic OpenAccount()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         dynamic domain = domains.ItemByName[domainName_];
         dynamic accounts = domain.Accounts;
         dynamic account = accounts.ItemByAddress[address_];

         ServerSession.Release(accounts);
         ServerSession.Release(domain);
         ServerSession.Release(domains);

         return account;
      }

      private void Enrol()
      {
         try
         {
            dynamic account = OpenAccount();
            try
            {
               string uri = (string)account.EnrolTOTP();
               account.Save();

               ShowQr(uri);

               // The manual key, for the case a camera is not available - reading it
               // out of the URI rather than asking the server again, because the
               // server will not answer that question twice.
               int start = uri.IndexOf("secret=", StringComparison.OrdinalIgnoreCase);
               if (start >= 0)
               {
                  start += "secret=".Length;
                  int end = uri.IndexOf('&', start);
                  secret_.Text = end < 0 ? uri.Substring(start) : uri.Substring(start, end - start);
               }

               enrolCard_.Visibility = Visibility.Visible;
            }
            finally
            {
               ServerSession.Release(account);
            }

            Reload();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private void ShowQr(string uri)
      {
         using var generator = new QRCodeGenerator();
         QRCodeData data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
         using var qr = new PngByteQRCode(data);

         var bitmap = new BitmapImage();
         bitmap.BeginInit();
         bitmap.StreamSource = new MemoryStream(qr.GetGraphic(6));
         bitmap.CacheOption = BitmapCacheOption.OnLoad;
         bitmap.EndInit();

         qr_.Source = bitmap;
      }

      private void Disable()
      {
         try
         {
            dynamic account = OpenAccount();
            try
            {
               account.DisableTOTP();
               account.Save();
            }
            finally
            {
               ServerSession.Release(account);
            }

            enrolCard_.Visibility = Visibility.Collapsed;
            qr_.Source = null;
            secret_.Text = "";

            Reload();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private void Build()
      {
         var root = new StackPanel { Margin = new Thickness(4, 8, 4, 4) };

         var hint = new TextBlock
         {
            Text = "A code from an authenticator app, required alongside this account's password. "
                 + "It applies where a code can actually be entered - the Control Panel and the API. Mail "
                 + "clients cannot present one, so once this is on they must use an app password instead.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
         };
         hint.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         root.Children.Add(hint);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
         buttons.Children.Add(enrol_);
         buttons.Children.Add(disable_);
         root.Children.Add(buttons);

         root.Children.Add(status_);

         var enrolContent = new StackPanel();
         enrolContent.Children.Add(new TextBlock
         {
            Text = "Scan this now - it is not stored and cannot be shown again",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap
         });
         enrolContent.Children.Add(qr_);
         enrolContent.Children.Add(new TextBlock
         {
            Text = "Or enter this key by hand:",
            Margin = new Thickness(0, 10, 0, 4)
         });
         enrolContent.Children.Add(secret_);
         enrolCard_.Child = enrolContent;
         root.Children.Add(enrolCard_);

         Content = root;
      }

      private static Button MakeButton(string text, Wpf.Ui.Controls.ControlAppearance appearance, RoutedEventHandler onClick)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = text,
            Appearance = appearance,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 120
         };
         button.Click += onClick;
         return button;
      }
   }
}
