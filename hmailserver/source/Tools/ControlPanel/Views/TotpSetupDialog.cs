// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using QRCoder;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Enable or disable two-factor authentication for the admin login. Generates
   /// a new secret, renders a scannable QR code plus the manual key, and confirms
   /// with a verification code. Writing the secret requires the Control Panel to
   /// run elevated (the secret lives under HKLM).
   ///
   /// On the standard frame, keeping the QR-and-key layout it needs: the sentence
   /// that says where the setting stands is body text at the top, the code is a
   /// <see cref="FieldRow"/> so a wrong code is said on the box the reader must
   /// retype rather than in a message box over the dialog, and everything else
   /// the attempt has to say - enabled, disabled, not elevated - is an
   /// <see cref="InlineNotice"/> in the flow, which a screen reader hears when it
   /// appears. Nothing is confirmed with a modal on top of a modal any more.
   /// </summary>
   public class TotpSetupDialog : FluentDialogWindow
   {
      private static readonly System.Windows.Media.FontFamily Mono =
         new(Typography.MonoFontFamily);

      private readonly TextBlock status_ = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };

      // Enrolment block (QR + manual key), shown only while enabling.
      private readonly StackPanel enrolPanel_ = new();
      private readonly Image qrImage_ = new() { Width = 190, Height = 190 };
      private readonly Wpf.Ui.Controls.TextBox secret_ = new()
      {
         IsReadOnly = true,
         FontFamily = Mono,
         VerticalContentAlignment = VerticalAlignment.Center
      };

      private readonly Wpf.Ui.Controls.TextBox code_ = new()
      {
         MaxLength = 6,
         Width = 200,
         // 28 is the ramp's Title rung, and what the login prompt
         // (TotpPromptDialog) uses for the same six digits.
         FontSize = 28,
         FontFamily = Mono,
         PlaceholderText = "000000",
         HorizontalAlignment = HorizontalAlignment.Left,
         HorizontalContentAlignment = HorizontalAlignment.Center
      };

      private readonly FieldRow codeRow_;
      private readonly InlineNotice notice_ = DialogFields.Notice();

      private readonly Wpf.Ui.Controls.Button action_ =
         new() { Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };

      private string pendingSecret_;

      public TotpSetupDialog(Window owner)
      {
         Owner = owner;
         Title = L("Two-factor authentication setup");

         status_.SetResourceReference(FrameworkElement.StyleProperty, "TextBody");

         BuildEnrolPanel();
         codeRow_ = DialogFields.Field(L("Verification code from your authenticator app"), code_);

         var body = new StackPanel();
         body.Children.Add(status_);
         body.Children.Add(notice_);
         body.Children.Add(enrolPanel_);
         body.Children.Add(codeRow_);

         action_.Click += (s, e) => Apply();
         var close = new Wpf.Ui.Controls.Button { Content = L("Close"), MinWidth = 88 };
         close.Click += (s, e) => Close();

         UseFrame(L("Two-factor authentication"), body, action_, close, width: 560);
         RefreshState();
         Loaded += (s, e) => code_.Focus();
      }

      private void BuildEnrolPanel()
      {
         // Top row: QR code (white card) on the left, instructions on the right.
         var grid = new Grid { Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         // White whatever the theme, and deliberately not a token: a QR code is
         // read by a camera, and a dark-on-dark or inverted code does not scan.
         var qrCard = new Border
         {
            Background = System.Windows.Media.Brushes.White,
            CornerRadius = new CornerRadius(DesignTokens.Radius.Card),
            Padding = new Thickness(DesignTokens.Space.Sm),
            VerticalAlignment = VerticalAlignment.Top,
            Child = qrImage_
         };
         Grid.SetColumn(qrCard, 0);
         grid.Children.Add(qrCard);

         var right = new StackPanel { Margin = new Thickness(DesignTokens.Space.Lg, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
         right.Children.Add(StepText(L("1.  Scan this QR code with an authenticator app (Microsoft Authenticator, Google Authenticator, Authy, 1Password…).")));
         right.Children.Add(StepText(L("2.  Or enter the setup key shown below by hand.")));
         Grid.SetColumn(right, 1);
         grid.Children.Add(right);

         enrolPanel_.Children.Add(grid);

         // Full-width key row so the grouped base32 key is never clipped.
         var keyRow = new Grid();
         keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         secret_.HorizontalAlignment = HorizontalAlignment.Stretch;
         Grid.SetColumn(secret_, 0);
         keyRow.Children.Add(secret_);
         var copy = new Wpf.Ui.Controls.Button { Content = L("_Copy"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         copy.Click += (s, e) =>
         {
            try { if (!string.IsNullOrEmpty(secret_.Text)) Clipboard.SetText(secret_.Text.Replace(" ", "")); }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
         };
         Grid.SetColumn(copy, 1);
         keyRow.Children.Add(copy);

         enrolPanel_.Children.Add(DialogFields.Field(L("Setup key (for manual entry)"), keyRow));
      }

      private void RefreshState()
      {
         if (TotpManager.IsConfigured())
         {
            status_.Text = L("Two-factor authentication is currently enabled. Enter a valid code to turn it off.");
            enrolPanel_.Visibility = Visibility.Collapsed;
            action_.Content = L("_Disable two-factor authentication");
            pendingSecret_ = null;
         }
         else
         {
            status_.Text = L("Two-factor authentication is currently disabled. Add it to your authenticator app, then confirm with a code.");
            pendingSecret_ = Totp.GenerateSecret();
            secret_.Text = FormatSecret(pendingSecret_);
            ShowQr(Totp.BuildOtpAuthUri("hMailServer Control Panel", pendingSecret_)); // no-loc: the issuer name inside the otpauth URI
            enrolPanel_.Visibility = Visibility.Visible;
            action_.Content = L("_Enable two-factor authentication");
         }

         code_.Text = "";
         codeRow_.Error = null;
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
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            qrImage_.Source = null;
         }
      }

      private void Apply()
      {
         codeRow_.Error = null;
         notice_.Hide();

         try
         {
            string entered = (code_.Text ?? "").Trim();

            if (TotpManager.IsConfigured())
            {
               // A code that does not match is a field that is wrong, so it is
               // said on the field and the keyboard goes back to it.
               if (!Totp.VerifyCode(TotpManager.ReadSecret(), entered))
               {
                  DialogFields.ShowError(codeRow_, L("The verification code is incorrect."));
                  return;
               }

               TotpManager.RemoveSecret();
               RefreshState();
               notice_.Show(StatusLevel.Good, L("Two-factor authentication has been disabled."));
            }
            else
            {
               if (!Totp.VerifyCode(pendingSecret_, entered))
               {
                  DialogFields.ShowError(codeRow_, L("The verification code is incorrect. Make sure your authenticator app is set up with the new key."));
                  return;
               }

               TotpManager.SaveSecret(pendingSecret_);
               RefreshState();
               notice_.Show(StatusLevel.Good, L("Two-factor authentication has been enabled. The next connection will require a verification code."));
            }
         }
         catch (UnauthorizedAccessException)
         {
            notice_.Show(StatusLevel.Critical, L("Changing two-factor authentication settings requires administrator rights. Restart the Control Panel as an administrator and try again."));
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, ex.Message);
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

      private static TextBlock StepText(string text)
      {
         var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Sm) };
         t.SetResourceReference(FrameworkElement.StyleProperty, "TextCaption");
         return t;
      }
   }
}
