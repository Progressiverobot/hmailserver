using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Prompts for a 6-digit two-factor verification code at login.</summary>
   public class TotpPromptDialog : Window
   {
      private readonly TextBox code_ = new()
      {
         FontSize = 18,
         FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
         MaxLength = 6,
         Width = 140,
         Padding = new Thickness(8, 6, 8, 6),
         HorizontalContentAlignment = HorizontalAlignment.Center
      };

      public string Code => code_.Text.Trim();

      public TotpPromptDialog(Window owner)
      {
         Owner = owner;
         Title = "Two-factor authentication";
         Width = 380;
         Height = 200;
         ResizeMode = ResizeMode.NoResize;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };
         var info = new TextBlock
         {
            Text = "Enter the 6-digit code from your authenticator app:",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
         };
         info.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(info);
         panel.Children.Add(code_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
         var ok = new Wpf.Ui.Controls.Button { Content = "OK", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
         ok.Click += (s, e) => { DialogResult = true; Close(); };
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", IsCancel = true };
         cancel.Click += (s, e) => { DialogResult = false; Close(); };
         buttons.Children.Add(ok);
         buttons.Children.Add(cancel);
         panel.Children.Add(buttons);

         Content = panel;
         Loaded += (s, e) => code_.Focus();
         code_.KeyDown += (s, e) => { if (e.Key == Key.Enter) { DialogResult = true; Close(); } };
      }
   }
}
