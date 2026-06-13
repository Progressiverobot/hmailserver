using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Enable or disable two-factor authentication for the admin login. Generates
   /// a new secret, shows it plus an otpauth:// URI for the authenticator app, and
   /// confirms with a verification code. Writing the secret requires the Control
   /// Panel to run elevated (the secret lives under HKLM).
   /// </summary>
   public class TotpSetupDialog : Window
   {
      private readonly TextBlock status_ = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
      private readonly TextBox secret_ = new() { IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 13 };
      private readonly TextBox uri_ = new() { IsReadOnly = true, FontSize = 12 };
      private readonly TextBox code_ = new() { MaxLength = 6, Width = 130, FontSize = 15, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas") };
      private readonly Wpf.Ui.Controls.Button action_ = new() { Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };

      private readonly TextBlock secretLabel_ = Label("Secret (enter manually in your authenticator app):");
      private readonly TextBlock uriLabel_ = Label("Or add this otpauth:// URI as a QR code:");

      private string pendingSecret_;

      public TotpSetupDialog(Window owner)
      {
         Owner = owner;
         Title = "Two-factor authentication setup";
         Width = 540;
         Height = 420;
         ResizeMode = ResizeMode.NoResize;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };
         status_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(status_);

         panel.Children.Add(secretLabel_);
         StyleBox(secret_);
         panel.Children.Add(secret_);

         panel.Children.Add(uriLabel_);
         StyleBox(uri_);
         panel.Children.Add(uri_);

         panel.Children.Add(Label("Verification code from your authenticator app:"));
         code_.Padding = new Thickness(6);
         code_.Margin = new Thickness(0, 0, 0, 8);
         code_.HorizontalAlignment = HorizontalAlignment.Left;
         code_.Background = System.Windows.Media.Brushes.Transparent;
         code_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(code_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
         action_.Click += (s, e) => Apply();
         action_.Margin = new Thickness(0, 0, 8, 0);
         var close = new Wpf.Ui.Controls.Button { Content = "Close", IsCancel = true };
         close.Click += (s, e) => Close();
         buttons.Children.Add(action_);
         buttons.Children.Add(close);
         panel.Children.Add(buttons);

         Content = panel;
         RefreshState();
      }

      private void RefreshState()
      {
         if (TotpManager.IsConfigured())
         {
            status_.Text = "Two-factor authentication is ENABLED. Enter a valid code to disable it.";
            secret_.Text = "";
            uri_.Text = "";
            secretLabel_.Visibility = Visibility.Collapsed;
            secret_.Visibility = Visibility.Collapsed;
            uriLabel_.Visibility = Visibility.Collapsed;
            uri_.Visibility = Visibility.Collapsed;
            action_.Content = "Disable two-factor authentication";
            pendingSecret_ = null;
         }
         else
         {
            status_.Text = "Two-factor authentication is DISABLED. Add the secret below to your authenticator app, then confirm with a code.";
            pendingSecret_ = Totp.GenerateSecret();
            secret_.Text = pendingSecret_;
            uri_.Text = Totp.BuildOtpAuthUri("hMailServer Control Panel", pendingSecret_);
            secretLabel_.Visibility = Visibility.Visible;
            secret_.Visibility = Visibility.Visible;
            uriLabel_.Visibility = Visibility.Visible;
            uri_.Visibility = Visibility.Visible;
            action_.Content = "Enable two-factor authentication";
         }

         code_.Text = "";
      }

      private void Apply()
      {
         try
         {
            if (TotpManager.IsConfigured())
            {
               if (!Totp.VerifyCode(TotpManager.ReadSecret(), code_.Text))
               {
                  MessageBox.Show("The verification code is incorrect.", Title);
                  return;
               }

               TotpManager.RemoveSecret();
               MessageBox.Show("Two-factor authentication has been disabled.", Title);
            }
            else
            {
               if (!Totp.VerifyCode(pendingSecret_, code_.Text))
               {
                  MessageBox.Show("The verification code is incorrect. Make sure your authenticator app is set up with the new secret.", Title);
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

      private static TextBlock Label(string text)
      {
         var t = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }

      private static void StyleBox(TextBox box)
      {
         box.Padding = new Thickness(6);
         box.Margin = new Thickness(0, 0, 0, 8);
         box.Background = System.Windows.Media.Brushes.Transparent;
         box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
      }
   }
}
