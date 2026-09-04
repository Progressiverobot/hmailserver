// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Full tabbed editor for one IP security range — the complete set of
   /// IInterfaceSecurityRange options (connections, relaying, per-direction SMTP
   /// authentication, anti-spam/anti-virus and expiry) that the inline panel does
   /// not expose.
   /// </summary>
   public class IPRangeDialog : FluentDialogWindow
   {
      private readonly int rangeId_;

      // General
      private readonly TextBox name_ = new();
      private readonly TextBox lower_ = new();
      private readonly TextBox upper_ = new();
      private readonly TextBox priority_ = new();

      // Connections
      private readonly CheckBox smtp_ = new() { Content = "Allow SMTP connections", FontSize = Typography.Body };
      private readonly CheckBox imap_ = new() { Content = "Allow IMAP connections", FontSize = Typography.Body };
      private readonly CheckBox pop3_ = new() { Content = "Allow POP3 connections", FontSize = Typography.Body };

      // Relaying
      private readonly CheckBox ll_ = new() { Content = "Local to local", FontSize = Typography.Body };
      private readonly CheckBox lr_ = new() { Content = "Local to external (relay out)", FontSize = Typography.Body };
      private readonly CheckBox rl_ = new() { Content = "External to local", FontSize = Typography.Body };
      private readonly CheckBox rr_ = new() { Content = "External to external (open relay!)", FontSize = Typography.Body };

      // SMTP authentication required
      private readonly CheckBox authLL_ = new() { Content = "Require auth: local to local", FontSize = Typography.Body };
      private readonly CheckBox authLE_ = new() { Content = "Require auth: local to external", FontSize = Typography.Body };
      private readonly CheckBox authEL_ = new() { Content = "Require auth: external to local", FontSize = Typography.Body };
      private readonly CheckBox authEE_ = new() { Content = "Require auth: external to external", FontSize = Typography.Body };
      private readonly CheckBox tlsAuth_ = new() { Content = "Require SSL/TLS when authenticating", FontSize = Typography.Body };

      // Protection + expiry
      private readonly CheckBox spam_ = new() { Content = "Enable anti-spam for this range", FontSize = Typography.Body };
      private readonly CheckBox virus_ = new() { Content = "Enable anti-virus for this range", FontSize = Typography.Body };
      private readonly CheckBox expires_ = new() { Content = "This range expires", FontSize = Typography.Body };
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

         var header = new TextBlock { Text = "IP range", FontSize = Typography.DialogTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 12) };
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
         // Enter saves, Escape cancels. Neither worked before.
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", IsCancel = true };
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
         p.Children.Add(Label("Name", name_));
         p.Children.Add(Input(name_));
         p.Children.Add(Label("Lower IP address", lower_));
         p.Children.Add(Input(lower_));
         p.Children.Add(Label("Upper IP address", upper_));
         p.Children.Add(Input(upper_));
         p.Children.Add(Label("Priority (higher wins when ranges overlap)", priority_));
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
         p.Children.Add(Label("Expiry time (YYYY-MM-DD HH:MM:SS)", expiresTime_));
         p.Children.Add(Input(expiresTime_));
         return Scroll(p);
      }

      private dynamic FindRange(dynamic ranges)
      {
         int count = (int)ranges.Count;
         for (int i = 0; i < count; i++)
         {
            dynamic r = ranges.Item[i];
            if ((int)r.ID == rangeId_)
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

            name_.Text = (string)r.Name ?? "";
            lower_.Text = (string)r.LowerIP ?? "";
            upper_.Text = (string)r.UpperIP ?? "";
            priority_.Text = ((int)r.Priority).ToString();

            smtp_.IsChecked = (bool)r.AllowSMTPConnections;
            imap_.IsChecked = (bool)r.AllowIMAPConnections;
            pop3_.IsChecked = (bool)r.AllowPOP3Connections;

            ll_.IsChecked = (bool)r.AllowDeliveryFromLocalToLocal;
            lr_.IsChecked = (bool)r.AllowDeliveryFromLocalToRemote;
            rl_.IsChecked = (bool)r.AllowDeliveryFromRemoteToLocal;
            rr_.IsChecked = (bool)r.AllowDeliveryFromRemoteToRemote;

            authLL_.IsChecked = (bool)r.RequireSMTPAuthLocalToLocal;
            authLE_.IsChecked = (bool)r.RequireSMTPAuthLocalToExternal;
            authEL_.IsChecked = (bool)r.RequireSMTPAuthExternalToLocal;
            authEE_.IsChecked = (bool)r.RequireSMTPAuthExternalToExternal;
            tlsAuth_.IsChecked = (bool)r.RequireSSLTLSForAuth;

            spam_.IsChecked = (bool)r.EnableSpamProtection;
            virus_.IsChecked = (bool)r.EnableAntiVirus;
            expires_.IsChecked = (bool)r.Expires;
            try { expiresTime_.Text = Convert.ToString(r.ExpiresTime); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { expiresTime_.Text = ""; }

            ServerSession.Release(r);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
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

            r.AllowSMTPConnections = smtp_.IsChecked is true;
            r.AllowIMAPConnections = imap_.IsChecked is true;
            r.AllowPOP3Connections = pop3_.IsChecked is true;

            r.AllowDeliveryFromLocalToLocal = ll_.IsChecked is true;
            r.AllowDeliveryFromLocalToRemote = lr_.IsChecked is true;
            r.AllowDeliveryFromRemoteToLocal = rl_.IsChecked is true;
            r.AllowDeliveryFromRemoteToRemote = rr_.IsChecked is true;

            r.RequireSMTPAuthLocalToLocal = authLL_.IsChecked is true;
            r.RequireSMTPAuthLocalToExternal = authLE_.IsChecked is true;
            r.RequireSMTPAuthExternalToLocal = authEL_.IsChecked is true;
            r.RequireSMTPAuthExternalToExternal = authEE_.IsChecked is true;
            r.RequireSSLTLSForAuth = tlsAuth_.IsChecked is true;

            r.EnableSpamProtection = spam_.IsChecked is true;
            r.EnableAntiVirus = virus_.IsChecked is true;
            r.Expires = expires_.IsChecked is true;
            if (expires_.IsChecked is true && expiresTime_.Text.Trim().Length > 0)
            {
               try { r.ExpiresTime = expiresTime_.Text.Trim(); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
            }

            r.Save();
            ServerSession.Release(r);
            Close();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
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

      /// <summary>
      /// A caption, and - when the editor it captions is passed in - that editor's
      /// accessible name. A TextBlock above a control tells UI Automation nothing,
      /// so the four boxes on the General tab announced themselves as "edit, edit,
      /// edit, edit" on a dialog where two of them are the ends of an IP range.
      /// The checkboxes are already named by their own Content, which is why the
      /// group captions here are passed no editor.
      /// </summary>
      private static TextBlock Label(string text, FrameworkElement editor = null)
      {
         var t = new TextBlock { Text = text, FontSize = Typography.Label, Margin = new Thickness(0, 8, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");

         if (editor != null)
            AutomationProperties.SetName(editor, AccessibleNames.Qualify(text, ""));

         return t;
      }

      private static TextBox Input(TextBox box)
      {
         box.FontSize = Typography.Body;
         box.Padding = new Thickness(6);
         box.Margin = new Thickness(0, 0, 0, 8);
         box.Background = System.Windows.Media.Brushes.Transparent;
         box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         return box;
      }

      private static Border Separator()
      {
         // The theme's own hairline, not a fixed Gray: at 0.3 opacity the old
         // one was near-invisible on the light theme, and theme-blind on all.
         var divider = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 12) };
         divider.SetResourceReference(Border.BackgroundProperty, "ControlElevationBorderBrush");
         return divider;
      }
   }
}
