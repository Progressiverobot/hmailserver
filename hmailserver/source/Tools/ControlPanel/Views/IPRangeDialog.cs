using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Full tabbed editor for one IP security range — the complete set of
   /// IInterfaceSecurityRange options (connections, relaying, per-direction SMTP
   /// authentication, anti-spam/anti-virus and expiry) that the inline panel does
   /// not expose.
   /// </summary>
   public class IPRangeDialog : Window
   {
      private readonly int rangeId_;

      // General
      private readonly TextBox name_ = new();
      private readonly TextBox lower_ = new();
      private readonly TextBox upper_ = new();
      private readonly TextBox priority_ = new();

      // Connections
      private readonly CheckBox smtp_ = new() { Content = "Allow SMTP connections", FontSize = 13 };
      private readonly CheckBox imap_ = new() { Content = "Allow IMAP connections", FontSize = 13 };
      private readonly CheckBox pop3_ = new() { Content = "Allow POP3 connections", FontSize = 13 };

      // Relaying
      private readonly CheckBox ll_ = new() { Content = "Local to local", FontSize = 13 };
      private readonly CheckBox lr_ = new() { Content = "Local to external (relay out)", FontSize = 13 };
      private readonly CheckBox rl_ = new() { Content = "External to local", FontSize = 13 };
      private readonly CheckBox rr_ = new() { Content = "External to external (open relay!)", FontSize = 13 };

      // SMTP authentication required
      private readonly CheckBox authLL_ = new() { Content = "Require auth: local to local", FontSize = 13 };
      private readonly CheckBox authLE_ = new() { Content = "Require auth: local to external", FontSize = 13 };
      private readonly CheckBox authEL_ = new() { Content = "Require auth: external to local", FontSize = 13 };
      private readonly CheckBox authEE_ = new() { Content = "Require auth: external to external", FontSize = 13 };
      private readonly CheckBox tlsAuth_ = new() { Content = "Require SSL/TLS when authenticating", FontSize = 13 };

      // Protection + expiry
      private readonly CheckBox spam_ = new() { Content = "Enable anti-spam for this range", FontSize = 13 };
      private readonly CheckBox virus_ = new() { Content = "Enable anti-virus for this range", FontSize = 13 };
      private readonly CheckBox expires_ = new() { Content = "This range expires", FontSize = 13 };
      private readonly TextBox expiresTime_ = new();

      public IPRangeDialog(Window owner, int rangeId)
      {
         rangeId_ = rangeId;
         Owner = owner;
         Title = "IP range";
         Width = 560;
         Height = 560;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var root = new Grid { Margin = new Thickness(18) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var header = new TextBlock { Text = "IP range", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 12) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetRow(header, 0);
         root.Children.Add(header);

         var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
         tabs.Items.Add(new TabItem { Header = "General", Content = BuildGeneral() });
         tabs.Items.Add(new TabItem { Header = "Connections", Content = BuildConnections() });
         tabs.Items.Add(new TabItem { Header = "Relaying", Content = BuildRelaying() });
         tabs.Items.Add(new TabItem { Header = "Require auth", Content = BuildAuth() });
         tabs.Items.Add(new TabItem { Header = "Protection", Content = BuildProtection() });
         Grid.SetRow(tabs, 1);
         root.Children.Add(tabs);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel" };
         cancel.Click += (s, e) => Close();
         buttons.Children.Add(save);
         buttons.Children.Add(cancel);
         Grid.SetRow(buttons, 2);
         root.Children.Add(buttons);

         Content = root;
         Loaded += (s, e) => Load();
      }

      private ScrollViewer BuildGeneral()
      {
         var p = Panel();
         p.Children.Add(Label("Name"));
         p.Children.Add(Input(name_));
         p.Children.Add(Label("Lower IP address"));
         p.Children.Add(Input(lower_));
         p.Children.Add(Label("Upper IP address"));
         p.Children.Add(Input(upper_));
         p.Children.Add(Label("Priority (higher wins when ranges overlap)"));
         p.Children.Add(Input(priority_));
         return Scroll(p);
      }

      private ScrollViewer BuildConnections()
      {
         var p = Panel();
         p.Children.Add(smtp_);
         p.Children.Add(imap_);
         p.Children.Add(pop3_);
         return Scroll(p);
      }

      private ScrollViewer BuildRelaying()
      {
         var p = Panel();
         p.Children.Add(Label("Which deliveries are allowed from this range"));
         p.Children.Add(ll_);
         p.Children.Add(lr_);
         p.Children.Add(rl_);
         p.Children.Add(rr_);
         return Scroll(p);
      }

      private ScrollViewer BuildAuth()
      {
         var p = Panel();
         p.Children.Add(Label("Require SMTP authentication for each delivery direction"));
         p.Children.Add(authLL_);
         p.Children.Add(authLE_);
         p.Children.Add(authEL_);
         p.Children.Add(authEE_);
         p.Children.Add(Separator());
         p.Children.Add(tlsAuth_);
         return Scroll(p);
      }

      private ScrollViewer BuildProtection()
      {
         var p = Panel();
         p.Children.Add(spam_);
         p.Children.Add(virus_);
         p.Children.Add(Separator());
         p.Children.Add(expires_);
         p.Children.Add(Label("Expiry time (YYYY-MM-DD HH:MM:SS)"));
         p.Children.Add(Input(expiresTime_));
         return Scroll(p);
      }

      private dynamic FindRange(dynamic ranges)
      {
         int count = (int) ranges.Count;
         for (int i = 0; i < count; i++)
         {
            dynamic r = ranges.Item[i];
            if ((int) r.ID == rangeId_)
               return r;
            ServerSession.Release(r);
         }
         return null;
      }

      private void Load()
      {
         dynamic ranges = ServerSession.Current.Application.Settings.SecurityRanges;
         try
         {
            dynamic r = FindRange(ranges);
            if (r == null) { Close(); return; }

            name_.Text = (string) r.Name ?? "";
            lower_.Text = (string) r.LowerIP ?? "";
            upper_.Text = (string) r.UpperIP ?? "";
            priority_.Text = ((int) r.Priority).ToString();

            smtp_.IsChecked = (bool) r.AllowSMTPConnections;
            imap_.IsChecked = (bool) r.AllowIMAPConnections;
            pop3_.IsChecked = (bool) r.AllowPOP3Connections;

            ll_.IsChecked = (bool) r.AllowDeliveryFromLocalToLocal;
            lr_.IsChecked = (bool) r.AllowDeliveryFromLocalToRemote;
            rl_.IsChecked = (bool) r.AllowDeliveryFromRemoteToLocal;
            rr_.IsChecked = (bool) r.AllowDeliveryFromRemoteToRemote;

            authLL_.IsChecked = (bool) r.RequireSMTPAuthLocalToLocal;
            authLE_.IsChecked = (bool) r.RequireSMTPAuthLocalToExternal;
            authEL_.IsChecked = (bool) r.RequireSMTPAuthExternalToLocal;
            authEE_.IsChecked = (bool) r.RequireSMTPAuthExternalToExternal;
            tlsAuth_.IsChecked = (bool) r.RequireSSLTLSForAuth;

            spam_.IsChecked = (bool) r.EnableSpamProtection;
            virus_.IsChecked = (bool) r.EnableAntiVirus;
            expires_.IsChecked = (bool) r.Expires;
            try { expiresTime_.Text = Convert.ToString(r.ExpiresTime); } catch { expiresTime_.Text = ""; }

            ServerSession.Release(r);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load the range: " + ex.Message, "Control Panel");
            Close();
         }
         finally
         {
            ServerSession.Release(ranges);
         }
      }

      private void Save()
      {
         dynamic ranges = ServerSession.Current.Application.Settings.SecurityRanges;
         try
         {
            dynamic r = FindRange(ranges);
            if (r == null) { Close(); return; }

            r.Name = name_.Text.Trim();
            if (lower_.Text.Trim().Length > 0) r.LowerIP = lower_.Text.Trim();
            if (upper_.Text.Trim().Length > 0) r.UpperIP = upper_.Text.Trim();
            if (int.TryParse(priority_.Text.Trim(), out int prio)) r.Priority = prio;

            r.AllowSMTPConnections = smtp_.IsChecked == true;
            r.AllowIMAPConnections = imap_.IsChecked == true;
            r.AllowPOP3Connections = pop3_.IsChecked == true;

            r.AllowDeliveryFromLocalToLocal = ll_.IsChecked == true;
            r.AllowDeliveryFromLocalToRemote = lr_.IsChecked == true;
            r.AllowDeliveryFromRemoteToLocal = rl_.IsChecked == true;
            r.AllowDeliveryFromRemoteToRemote = rr_.IsChecked == true;

            r.RequireSMTPAuthLocalToLocal = authLL_.IsChecked == true;
            r.RequireSMTPAuthLocalToExternal = authLE_.IsChecked == true;
            r.RequireSMTPAuthExternalToLocal = authEL_.IsChecked == true;
            r.RequireSMTPAuthExternalToExternal = authEE_.IsChecked == true;
            r.RequireSSLTLSForAuth = tlsAuth_.IsChecked == true;

            r.EnableSpamProtection = spam_.IsChecked == true;
            r.EnableAntiVirus = virus_.IsChecked == true;
            r.Expires = expires_.IsChecked == true;
            if (expires_.IsChecked == true && expiresTime_.Text.Trim().Length > 0)
            {
               try { r.ExpiresTime = expiresTime_.Text.Trim(); } catch (Exception) { }
            }

            r.Save();
            ServerSession.Release(r);
            Close();
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the range: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(ranges);
         }
      }

      // ---- UI helpers ----

      private static StackPanel Panel() => new() { Margin = new Thickness(4, 12, 4, 4) };
      private static ScrollViewer Scroll(StackPanel p) => new() { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

      private static TextBlock Label(string text)
      {
         var t = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }

      private static TextBox Input(TextBox box)
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
         Margin = new Thickness(0, 12, 0, 12),
         Background = System.Windows.Media.Brushes.Gray,
         Opacity = 0.3
      };
   }
}
