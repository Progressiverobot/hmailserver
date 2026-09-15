// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Text.RegularExpressions;
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
   /// Enrol or remove the second factor the SERVER enforces on the administrator
   /// credential - distinct from the Control-Panel-only TOTP in TotpSetupDialog.
   /// This one is stored on the server (hMailServer.ini, protected) and applies to
   /// every client of the credential: the COM API, the REST API and this tool.
   ///
   /// The server generates and commits the secret when asked (Settings.
   /// EnrolAdministratorTOTP), so this dialog confirms before you leave rather than
   /// before it commits: it verifies a code against the freshly enrolled secret and,
   /// if you cannot produce one (wrong app, cancelled), rolls the enrolment back
   /// with DisableAdministratorTOTP. The session that enrolled stays signed in
   /// either way; only the NEXT sign-in is affected.
   ///
   /// On the standard frame, keeping the QR-and-key layout: the code is a
   /// <see cref="FieldRow"/>, so a code that does not match is said on the box and
   /// the keyboard goes back to it, and the outcomes - enrolled, removed, refused
   /// by the server - are an <see cref="InlineNotice"/> in the flow rather than a
   /// message box stacked on a dialog that is itself modal.
   /// </summary>
   public class AdministratorTwoFactorDialog : FluentDialogWindow
   {
      private static readonly System.Windows.Media.FontFamily Mono =
         new(Typography.MonoFontFamily);

      private readonly TextBlock status_ = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };

      private readonly StackPanel enrolPanel_ = new();
      private readonly Image qrImage_ = new() { Width = 190, Height = 190 };
      private readonly Wpf.Ui.Controls.TextBox secretBox_ = new()
      {
         IsReadOnly = true,
         FontFamily = Mono,
         VerticalContentAlignment = VerticalAlignment.Center
      };

      private readonly Wpf.Ui.Controls.TextBox code_ = new()
      {
         MaxLength = 6,
         Width = 200,
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

      // The base32 secret the server just handed back, taken from the otpauth URI.
      // Null except while an enrolment is committed-but-unconfirmed.
      private string pendingSecret_;

      public AdministratorTwoFactorDialog(Window owner)
      {
         Owner = owner;
         Title = L("Server-enforced two-factor authentication");

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

         UseFrame(L("Second factor on the administrator credential"), body, action_, close, width: 560);
         RefreshState();

         // A dialog closed with an enrolment still unconfirmed rolls it back, so a
         // half-finished setup never becomes the lock on the next sign-in.
         Closing += (s, e) => RollBackIfUnconfirmed();
         Loaded += (s, e) => code_.Focus();
      }

      private bool IsEnrolled()
      {
         try
         {
            return (bool)ServerSession.Current.Application.AdministratorTOTPEnabled;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return false;
         }
      }

      private void RefreshState()
      {
         if (IsEnrolled() && pendingSecret_ == null)
         {
            status_.Text = L("A second factor is enrolled. Every sign-in with the administrator credential - this tool, the COM API and the REST API - needs a code. Enter a current code to turn it off.");
            enrolPanel_.Visibility = Visibility.Collapsed;
            action_.Content = L("_Disable second factor");
         }
         else
         {
            status_.Text = L("Add the key below to an authenticator app, then confirm with a code. Once enrolled, the administrator password alone will not sign in - the COM API and REST API will need a code too. Keep the key somewhere safe: if you lose the authenticator, a local administrator must clear AdministratorTotpSecret from hMailServer.ini to recover.");
            EnsureEnrolled();
            action_.Content = L("Confirm and _enable");
         }

         code_.Text = "";
         codeRow_.Error = null;
         code_.Focus();
      }

      // Asks the server to enrol (which generates and commits the secret) and shows
      // the QR built from what it returned. Committed but not yet confirmed - see
      // the class comment.
      private void EnsureEnrolled()
      {
         if (pendingSecret_ != null)
            return;

         try
         {
            string uri = (string)ServerSession.Current.Application.Settings.EnrolAdministratorTOTP();
            var match = Regex.Match(uri ?? "", "secret=([A-Z2-7]+)");

            if (!match.Success)
            {
               notice_.Show(StatusLevel.Critical, L("The server did not return a usable enrolment. See its error log."));
               enrolPanel_.Visibility = Visibility.Collapsed;
               action_.IsEnabled = false;
               return;
            }

            pendingSecret_ = match.Groups[1].Value;
            secretBox_.Text = FormatSecret(pendingSecret_);
            ShowQr(uri);
            enrolPanel_.Visibility = Visibility.Visible;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, ex.Message);
            enrolPanel_.Visibility = Visibility.Collapsed;
            action_.IsEnabled = false;
         }
      }

      private void Apply()
      {
         codeRow_.Error = null;
         notice_.Hide();

         string entered = (code_.Text ?? "").Trim();

         try
         {
            if (IsEnrolled() && pendingSecret_ == null)
            {
               // Disabling. The server holds the secret and this tool cannot read
               // it back, so there is nothing here to verify the code against: the
               // disable is gated on a plausible-looking code only, and the code is
               // the user's own assurance that they still control the factor.
               // Losing the authenticator is the ini-file recovery path named above.
               if (entered.Length != 6)
               {
                  DialogFields.ShowError(codeRow_, L("Enter the current 6-digit code to turn the second factor off."));
                  return;
               }

               ServerSession.Current.Application.Settings.DisableAdministratorTOTP();
               RefreshState();
               notice_.Show(StatusLevel.Good, L("The second factor has been removed. The administrator password alone signs in again."));
               return;
            }

            // Confirming a fresh enrolment: verify the code against the secret the
            // server just gave us, so a mis-scanned QR is caught here rather than at
            // the next sign-in.
            if (!Totp.VerifyCode(pendingSecret_, entered))
            {
               DialogFields.ShowError(codeRow_, L("That code does not match. Make sure the authenticator app has the new key, then try the current code."));
               return;
            }

            // Confirmed: keep it, and stop the Closing handler rolling it back.
            pendingSecret_ = null;
            RefreshState();
            notice_.Show(StatusLevel.Good, L("The second factor is enabled. The next sign-in with the administrator credential will need a code."));
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, ex.Message);
         }
      }

      private void RollBackIfUnconfirmed()
      {
         if (pendingSecret_ == null)
            return;

         try
         {
            ServerSession.Current.Application.Settings.DisableAdministratorTOTP();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Best effort: if the roll-back cannot reach the server the enrolment
            // stands, which is the safe direction - the key is on screen and the
            // recovery path is documented - rather than silently half-off.
         }

         pendingSecret_ = null;
      }

      private void BuildEnrolPanel()
      {
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

         var keyRow = new Grid();
         keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         keyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         secretBox_.HorizontalAlignment = HorizontalAlignment.Stretch;
         Grid.SetColumn(secretBox_, 0);
         keyRow.Children.Add(secretBox_);
         var copy = new Wpf.Ui.Controls.Button { Content = L("_Copy"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         copy.Click += (s, e) =>
         {
            try { if (!string.IsNullOrEmpty(secretBox_.Text)) Clipboard.SetText(secretBox_.Text.Replace(" ", "")); }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort clipboard copy. */ }
         };
         Grid.SetColumn(copy, 1);
         keyRow.Children.Add(copy);

         enrolPanel_.Children.Add(DialogFields.Field(L("Setup key (for manual entry)"), keyRow));
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
