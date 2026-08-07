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
   /// Enable or disable two-factor authentication for the admin login. Generates
   /// a new secret, renders a scannable QR code plus the manual key, and confirms
   /// with a verification code. Writing the secret requires the Control Panel to
   /// run elevated (the secret lives under HKLM).
   /// </summary>
   public class TotpSetupDialog : Window
   {
      private static readonly System.Windows.Media.FontFamily Mono =
         new("Cascadia Mono, Consolas");

      private readonly TextBlock status_ = new()
      {
         FontSize = Typography.Body,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(0, 0, 0, 16)
      };

      // Enrolment block (QR + manual key), shown only while enabling.
      private readonly StackPanel enrolPanel_ = new() { Margin = new Thickness(0, 0, 0, 4) };
      private readonly Image qrImage_ = new() { Width = 190, Height = 190 };
      private readonly Wpf.Ui.Controls.TextBox secret_ = new()
      {
         IsReadOnly = true,
         FontFamily = Mono,
         FontSize = Typography.Body,
         VerticalContentAlignment = VerticalAlignment.Center
      };

      private readonly Wpf.Ui.Controls.TextBox code_ = new()
      {
         MaxLength = 6,
         Width = 200,
         FontSize = 26,
         FontFamily = Mono,
         PlaceholderText = "000000",
         HorizontalAlignment = HorizontalAlignment.Left,
         HorizontalContentAlignment = HorizontalAlignment.Center
      };

      private readonly Wpf.Ui.Controls.Button action_ =
         new() { Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };

      private string pendingSecret_;

      public TotpSetupDialog(Window owner)
      {
         Owner = owner;
         Title = "Two-factor authentication setup";
         Width = 560;
         Height = 610;
         MinWidth = 520;
         MinHeight = 560;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(22) };

         var header = new TextBlock
         {
            Text = "Two-factor authentication",
            FontSize = Typography.DialogTitle,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
         };
         header.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(header);

         status_.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         panel.Children.Add(status_);

         BuildEnrolPanel();
         panel.Children.Add(enrolPanel_);

         panel.Children.Add(Label("Verification code from your authenticator app"));
         code_.Margin = new Thickness(0, 0, 0, 4);
         panel.Children.Add(code_);

         var buttons = new StackPanel
         {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
         };
         action_.Click += (s, e) => Apply();
         action_.Margin = new Thickness(0, 0, 8, 0);
         var close = new Wpf.Ui.Controls.Button { Content = "Close", IsCancel = true };
         close.Click += (s, e) => Close();
         buttons.Children.Add(action_);
         buttons.Children.Add(close);
         panel.Children.Add(buttons);

         Content = panel;
         RefreshState();
         Loaded += (s, e) => code_.Focus();
      }

      private void BuildEnrolPanel()
      {
         // Top row: QR code (white card) on the left, instructions on the right.
         var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         var qrCard = new Border
         {
            Background = System.Windows.Media.Brushes.White,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            VerticalAlignment = VerticalAlignment.Top,
            Child = qrImage_
         };
         Grid.SetColumn(qrCard, 0);
         grid.Children.Add(qrCard);

         var right = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
         right.Children.Add(StepText("1.  Scan this QR code with an authenticator app (Microsoft Authenticator, Google Authenticator, Authy, 1Password…)."));
         right.Children.Add(StepText("2.  Or enter the setup key shown below by hand."));
         Grid.SetColumn(right, 1);
         grid.Children.Add(right);

         enrolPanel_.Children.Add(grid);

         // Full-width key row so the grouped base32 key is never clipped.
         enrolPanel_.Children.Add(Label("Setup key (for manual entry)"));
         var keyRow = new Grid();
         keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         secret_.HorizontalAlignment = HorizontalAlignment.Stretch;
         Grid.SetColumn(secret_, 0);
         keyRow.Children.Add(secret_);
         var copy = new Wpf.Ui.Controls.Button { Content = "Copy", Margin = new Thickness(8, 0, 0, 0) };
         copy.Click += (s, e) =>
         {
            try { if (!string.IsNullOrEmpty(secret_.Text)) Clipboard.SetText(secret_.Text.Replace(" ", "")); }
            catch (Exception) { }
         };
         Grid.SetColumn(copy, 1);
         keyRow.Children.Add(copy);
         enrolPanel_.Children.Add(keyRow);
      }

      private void RefreshState()
      {
         if (TotpManager.IsConfigured())
         {
            status_.Text = "Two-factor authentication is currently ENABLED. Enter a valid code to turn it off.";
            enrolPanel_.Visibility = Visibility.Collapsed;
            action_.Content = "Disable two-factor authentication";
            pendingSecret_ = null;
         }
         else
         {
            status_.Text = "Two-factor authentication is currently DISABLED. Add it to your authenticator app, then confirm with a code.";
            pendingSecret_ = Totp.GenerateSecret();
            secret_.Text = FormatSecret(pendingSecret_);
            ShowQr(Totp.BuildOtpAuthUri("hMailServer Control Panel", pendingSecret_));
            enrolPanel_.Visibility = Visibility.Visible;
            action_.Content = "Enable two-factor authentication";
         }

         code_.Text = "";
         code_.Focus();
      }

      private void ShowQr(string uri)
      {
         try
         {
            using var generator = new QRCodeGenerator();
            QRCodeData data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
            using var qr = new PngByteQRCode(data);
            byte[] png = qr.GetGraphic(8);

            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(png))
            {
               bmp.BeginInit();
               bmp.CacheOption = BitmapCacheOption.OnLoad;
               bmp.StreamSource = ms;
               bmp.EndInit();
            }
            bmp.Freeze();
            qrImage_.Source = bmp;
         }
         catch (Exception)
         {
            qrImage_.Source = null;
         }
      }

      private void Apply()
      {
         try
         {
            string entered = (code_.Text ?? "").Trim();

            if (TotpManager.IsConfigured())
            {
               if (!Totp.VerifyCode(TotpManager.ReadSecret(), entered))
               {
                  MessageBox.Show("The verification code is incorrect.", Title);
                  return;
               }

               TotpManager.RemoveSecret();
               MessageBox.Show("Two-factor authentication has been disabled.", Title);
            }
            else
            {
               if (!Totp.VerifyCode(pendingSecret_, entered))
               {
                  MessageBox.Show("The verification code is incorrect. Make sure your authenticator app is set up with the new key.", Title);
                  return;
               }

               TotpManager.SaveSecret(pendingSecret_);
               MessageBox.Show("Two-factor authentication has been enabled. The next connection will require a verification code.", Title);
            }

            RefreshState();
         }
         catch (UnauthorizedAccessException)
         {
            MessageBox.Show("Changing two-factor authentication settings requires administrator rights. Restart the Control Panel as an administrator and try again.", Title);
         }
         catch (Exception ex)
         {
            MessageBox.Show(ex.Message, Title);
         }
      }

      // Groups the base32 secret into 4-character blocks for readable manual entry.
      private static string FormatSecret(string secret)
      {
         if (string.IsNullOrEmpty(secret))
            return secret;
         var sb = new System.Text.StringBuilder();
         for (int i = 0; i < secret.Length; i++)
         {
            if (i > 0 && i % 4 == 0)
               sb.Append(' ');
            sb.Append(secret[i]);
         }
         return sb.ToString();
      }

      private static TextBlock Label(string text)
      {
         var t = new TextBlock { Text = text, FontSize = Typography.Label, Margin = new Thickness(0, 6, 0, 6) };
         t.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }

      private static TextBlock StepText(string text)
      {
         var t = new TextBlock
         {
            Text = text,
            FontSize = Typography.Label,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
         };
         t.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }
   }
}
