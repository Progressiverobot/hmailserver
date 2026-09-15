// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Full tabbed editor for one IP security range — the complete set of
   /// IInterfaceSecurityRange options (connections, relaying, per-direction SMTP
   /// authentication, anti-spam/anti-virus and expiry) that the inline panel does
   /// not expose. On the standard frame: five tabs sized to the tallest of them
   /// so the window does not jump on every click of the strip, every field a
   /// <see cref="FieldRow"/>, a priority or an expiry that will not parse said on
   /// the field itself - which also brings its tab to the front - and a save the
   /// server refuses said in a notice above the tabs.
   /// </summary>
   public class IPRangeDialog : FluentDialogWindow
   {
      /// <summary>The width this dialog asks the frame for; the tabs are measured against the body inside it.</summary>
      private const double DialogWidth = 560;

      private readonly int rangeId_;

      // General
      private readonly TextBox name_ = new();
      private readonly TextBox lower_ = new();
      private readonly TextBox upper_ = new();
      private readonly TextBox priority_ = new();
      private FieldRow priorityRow_;

      // Connections
      private readonly CheckBox smtp_ = new() { Content = L("Allow SM_TP connections") };
      private readonly CheckBox imap_ = new() { Content = L("Allow _IMAP connections") };
      private readonly CheckBox pop3_ = new() { Content = L("Allow _POP3 connections") };

      // Relaying
      private readonly CheckBox ll_ = new() { Content = L("_Local to local") };
      private readonly CheckBox lr_ = new() { Content = L("Local to _external (relay out)") };
      private readonly CheckBox rl_ = new() { Content = L("E_xternal to local") };
      private readonly CheckBox rr_ = new() { Content = L("External to external (_open relay!)") };

      // SMTP authentication required
      private readonly CheckBox authLL_ = new() { Content = L("Require auth: _local to local") };
      private readonly CheckBox authLE_ = new() { Content = L("Require auth: local to _external") };
      private readonly CheckBox authEL_ = new() { Content = L("Require auth: e_xternal to local") };
      private readonly CheckBox authEE_ = new() { Content = L("Require auth: external to exte_rnal") };
      private readonly CheckBox tlsAuth_ = new() { Content = L("Require SSL/_TLS when authenticating") };

      // Protection + expiry
      private readonly CheckBox spam_ = new() { Content = L("Enable _anti-spam for this range") };
      private readonly CheckBox virus_ = new() { Content = L("Enable anti-_virus for this range") };
      private readonly CheckBox expires_ = new() { Content = L("This range _expires") };
      private readonly TextBox expiresTime_ = new();
      private FieldRow expiresRow_;

      private readonly InlineNotice notice_ = DialogFields.Notice();

      public IPRangeDialog(Window owner, int rangeId)
      {
         rangeId_ = rangeId;
         Owner = owner;
         Title = L("IP range");

         var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
         tabs.Items.Add(new TabItem { Header = L("General"), Content = BuildGeneral() });
         tabs.Items.Add(new TabItem { Header = L("Connections"), Content = BuildConnections() });
         tabs.Items.Add(new TabItem { Header = L("Relaying"), Content = BuildRelaying() });
         tabs.Items.Add(new TabItem { Header = L("Require auth"), Content = BuildAuth() });
         tabs.Items.Add(new TabItem { Header = L("Protection"), Content = BuildProtection() });

         // One height for every tab, taken from the tallest: a window sized to
         // its content would otherwise grow and shrink with each click on the
         // strip, which is the tabbed dialog's version of a jumping layout.
         DialogFields.FitTabs(tabs, DialogFields.BodyWidth(DialogWidth));

         var body = new StackPanel();
         body.Children.Add(notice_);
         body.Children.Add(tabs);

         // Enter saves, Escape cancels; the frame wires both.
         var save = new Wpf.Ui.Controls.Button { Content = L("_Save"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => Close();

         UseFrame(L("IP range"), body, save, cancel, width: DialogWidth);
         Loaded += (s, e) => Load();
      }

      private ScrollViewer BuildGeneral()
      {
         var p = DialogFields.TabPanel();
         p.Children.Add(Label(L("_Name"), name_));
         p.Children.Add(Label(L("_Lower IP address"), lower_));
         p.Children.Add(Label(L("_Upper IP address"), upper_));
         priorityRow_ = Label(L("_Priority (higher wins when ranges overlap)"), priority_);
         p.Children.Add(priorityRow_);
         return DialogFields.Scroll(p);
      }

      private ScrollViewer BuildConnections()
      {
         var p = DialogFields.TabPanel();
         p.Children.Add(DialogFields.Check(smtp_));
         p.Children.Add(DialogFields.Check(imap_));
         p.Children.Add(DialogFields.Check(pop3_));
         return DialogFields.Scroll(p);
      }

      private ScrollViewer BuildRelaying()
      {
         var p = DialogFields.TabPanel();
         p.Children.Add(DialogFields.Caption(L("Which deliveries are allowed from this range")));
         p.Children.Add(DialogFields.Check(ll_));
         p.Children.Add(DialogFields.Check(lr_));
         p.Children.Add(DialogFields.Check(rl_));
         p.Children.Add(DialogFields.Check(rr_));
         return DialogFields.Scroll(p);
      }

      private ScrollViewer BuildAuth()
      {
         var p = DialogFields.TabPanel();
         p.Children.Add(DialogFields.Caption(L("Require SMTP authentication for each delivery direction")));
         p.Children.Add(DialogFields.Check(authLL_));
         p.Children.Add(DialogFields.Check(authLE_));
         p.Children.Add(DialogFields.Check(authEL_));
         p.Children.Add(DialogFields.Check(authEE_));
         p.Children.Add(DialogFields.Separator());
         p.Children.Add(DialogFields.Check(tlsAuth_));
         return DialogFields.Scroll(p);
      }

      private ScrollViewer BuildProtection()
      {
         var p = DialogFields.TabPanel();
         p.Children.Add(DialogFields.Check(spam_));
         p.Children.Add(DialogFields.Check(virus_));
         p.Children.Add(DialogFields.Separator());
         p.Children.Add(DialogFields.Check(expires_));
         expiresRow_ = Label(L("Expiry _time (YYYY-MM-DD HH:MM:SS)"), expiresTime_);
         p.Children.Add(expiresRow_);
         return DialogFields.Scroll(p);
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
            MessageBox.Show(F("Could not load the range: {0}", ex.Message), L("Control Panel"));
            Close();
         }
         finally
         {
            ServerSession.Release(ranges);
         }
      }

      private void Save()
      {
         notice_.Hide();
         DialogFields.ClearErrors(priorityRow_, expiresRow_);

         // A priority that will not parse used to be dropped on the floor: the
         // dialog closed, the range kept its old priority and nothing said so.
         // It is a field that is wrong, so the field says so.
         int? priority = null;
         string priorityText = priority_.Text.Trim();
         if (priorityText.Length > 0)
         {
            if (!int.TryParse(priorityText, out int parsed))
            {
               DialogFields.ShowError(priorityRow_, L("Enter a whole number."));
               return;
            }

            priority = parsed;
         }

         dynamic ranges = ServerSession.Current.Application.Settings.SecurityRanges;
         try
         {
            dynamic r = FindRange(ranges);
            if (r == null) { Close(); return; }

            r.Name = name_.Text.Trim();
            if (lower_.Text.Trim().Length > 0) r.LowerIP = lower_.Text.Trim();
            if (upper_.Text.Trim().Length > 0) r.UpperIP = upper_.Text.Trim();
            if (priority.HasValue) r.Priority = priority.Value;

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
               // The server parses the date itself and refuses what it cannot
               // read. That refusal used to be swallowed - the range saved with
               // "expires" set and no expiry time, which never expires - so it
               // now stops the save on the field that caused it.
               try
               {
                  r.ExpiresTime = expiresTime_.Text.Trim();
               }
               catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
               {
                  ServerSession.Release(r);
                  DialogFields.ShowError(expiresRow_, L("Enter the expiry as YYYY-MM-DD HH:MM:SS."));
                  return;
               }
            }

            r.Save();
            ServerSession.Release(r);
            Close();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not save the range: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(ranges);
         }
      }

      /// <summary>
      /// A caption and its editor as one row, which names the editor to UI
      /// Automation. A TextBlock above a control tells UI Automation nothing, so
      /// the four boxes on the General tab announced themselves as "edit, edit,
      /// edit, edit" on a dialog where two of them are the ends of an IP range.
      /// The check boxes are named by their own Content, which is why the group
      /// captions here are <see cref="DialogFields.Caption"/> and not rows.
      /// </summary>
      private static FieldRow Label(string text, FrameworkElement editor) => DialogFields.Field(text, editor);
   }
}
