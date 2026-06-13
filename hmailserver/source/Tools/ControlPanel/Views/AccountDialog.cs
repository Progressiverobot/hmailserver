using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal editor for one account: state, quota, name, password,
   /// forwarding and vacation message.
   /// </summary>
   public class AccountDialog : Window
   {
      private readonly string domainName_;
      private readonly string address_;

      private readonly CheckBox active_ = new() { Content = "Account enabled", FontSize = 13 };
      private readonly TextBox quota_ = new();
      private readonly TextBox firstName_ = new();
      private readonly TextBox lastName_ = new();
      private readonly PasswordBox password_ = new();
      private readonly CheckBox forwardOn_ = new() { Content = "Forward incoming mail", FontSize = 13 };
      private readonly TextBox forwardTo_ = new();
      private readonly CheckBox forwardKeep_ = new() { Content = "Keep original message", FontSize = 13 };
      private readonly CheckBox vacationOn_ = new() { Content = "Send automatic reply (vacation message)", FontSize = 13 };
      private readonly TextBox vacationSubject_ = new();
      private readonly TextBox vacationBody_ = new() { AcceptsReturn = true, Height = 70, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

      public AccountDialog(Window owner, string domainName, string address)
      {
         domainName_ = domainName;
         address_ = address;

         Owner = owner;
         Title = "Account - " + address;
         Width = 480;
         SizeToContent = SizeToContent.Height;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };

         panel.Children.Add(active_);
         panel.Children.Add(Label("Quota (MB, 0 = unlimited)"));
         panel.Children.Add(Input(quota_));
         panel.Children.Add(Label("First name"));
         panel.Children.Add(Input(firstName_));
         panel.Children.Add(Label("Last name"));
         panel.Children.Add(Input(lastName_));
         panel.Children.Add(Label("New password (leave empty to keep current)"));
         password_.FontSize = 13;
         password_.Padding = new Thickness(6);
         password_.Margin = new Thickness(0, 0, 0, 12);
         panel.Children.Add(password_);

         panel.Children.Add(Separator());
         panel.Children.Add(forwardOn_);
         panel.Children.Add(Label("Forward to"));
         panel.Children.Add(Input(forwardTo_));
         forwardKeep_.Margin = new Thickness(0, 0, 0, 12);
         panel.Children.Add(forwardKeep_);

         panel.Children.Add(Separator());
         panel.Children.Add(vacationOn_);
         panel.Children.Add(Label("Reply subject"));
         panel.Children.Add(Input(vacationSubject_));
         panel.Children.Add(Label("Reply message"));
         vacationBody_.FontSize = 13;
         vacationBody_.Padding = new Thickness(6);
         vacationBody_.Margin = new Thickness(0, 0, 0, 16);
         panel.Children.Add(vacationBody_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel" };
         cancel.Click += (s, e) => Close();
         buttons.Children.Add(save);
         buttons.Children.Add(cancel);
         panel.Children.Add(buttons);

         Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 720 };

         Loaded += (s, e) => Load();
      }

      private static TextBlock Label(string text) => new()
      {
         Text = text,
         FontSize = 12.5,
         Margin = new Thickness(0, 6, 0, 4)
      };

      private TextBox Input(TextBox box)
      {
         box.FontSize = 13;
         box.Padding = new Thickness(6);
         box.Margin = new Thickness(0, 0, 0, 8);
         box.Background = System.Windows.Media.Brushes.Transparent;
         box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         return box;
      }

      private static Border Separator() => new()
      {
         Height = 1,
         Margin = new Thickness(0, 10, 0, 12),
         Background = System.Windows.Media.Brushes.Gray,
         Opacity = 0.3
      };

      private dynamic OpenAccount(dynamic domains)
      {
         dynamic domain = domains.ItemByName[domainName_];
         dynamic accounts = domain.Accounts;
         dynamic account = accounts.ItemByAddress[address_];
         ServerSession.Release(accounts);
         ServerSession.Release(domain);
         return account;
      }

      private void Load()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic account = OpenAccount(domains);
            active_.IsChecked = (bool) account.Active;
            quota_.Text = ((int) account.MaxSize).ToString();
            firstName_.Text = (string) account.PersonFirstName ?? "";
            lastName_.Text = (string) account.PersonLastName ?? "";
            forwardOn_.IsChecked = (bool) account.ForwardEnabled;
            forwardTo_.Text = (string) account.ForwardAddress ?? "";
            forwardKeep_.IsChecked = (bool) account.ForwardKeepOriginal;
            vacationOn_.IsChecked = (bool) account.VacationMessageIsOn;
            vacationSubject_.Text = (string) account.VacationSubject ?? "";
            vacationBody_.Text = (string) account.VacationMessage ?? "";
            ServerSession.Release(account);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load the account: " + ex.Message, "Control Panel");
            Close();
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private void Save()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic account = OpenAccount(domains);
            account.Active = active_.IsChecked == true;
            if (int.TryParse(quota_.Text.Trim(), out int quota))
               account.MaxSize = quota;
            account.PersonFirstName = firstName_.Text.Trim();
            account.PersonLastName = lastName_.Text.Trim();
            if (password_.Password.Length > 0)
               account.Password = password_.Password;
            account.ForwardEnabled = forwardOn_.IsChecked == true;
            account.ForwardAddress = forwardTo_.Text.Trim();
            account.ForwardKeepOriginal = forwardKeep_.IsChecked == true;
            account.VacationMessageIsOn = vacationOn_.IsChecked == true;
            account.VacationSubject = vacationSubject_.Text;
            account.VacationMessage = vacationBody_.Text;
            account.Save();
            ServerSession.Release(account);
            Close();
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the account: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }
   }
}
