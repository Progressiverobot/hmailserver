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
   /// Tabbed editor for one route's full set of options. On the standard frame:
   /// the routed domain is the heading, the five tabs share the height of the
   /// tallest so the window does not resize on every click of the strip, every
   /// field is a <see cref="FieldRow"/>, a number that will not do is said on the
   /// field and brings its tab forward, and everything the server refuses - a
   /// save, an address added or removed - is said in a notice above the tabs
   /// instead of in a message box stacked on top of the dialog.
   /// </summary>
   public class RouteDialog : FluentDialogWindow
   {
      /// <summary>The width this dialog asks the frame for; the tabs are measured against the body inside it.</summary>
      private const double DialogWidth = 560;

      private readonly string domainName_;
      private int routeId_;

      private readonly Wpf.Ui.Controls.TextBox host_ = new();
      private readonly Wpf.Ui.Controls.TextBox port_ = new();
      private FieldRow portRow_;
      private readonly Wpf.Ui.Controls.TextBox description_ = new();
      private readonly Wpf.Ui.Controls.TextBox tries_ = new();
      private FieldRow triesRow_;
      private readonly Wpf.Ui.Controls.TextBox minutes_ = new();
      private FieldRow minutesRow_;
      private readonly CheckBox allAddresses_ = new() { Content = L("Deliver to _all addresses (not only known accounts)") };

      private readonly ListBox addressList_ = new() { Height = 200 };
      private readonly Wpf.Ui.Controls.TextBox newAddress_ = new();

      private readonly ComboBox connSecurity_ = new();
      private readonly CheckBox treatSenderLocal_ = new() { Content = L("Treat sender domain as _local") };
      private readonly CheckBox treatRecipientLocal_ = new() { Content = L("Treat _recipient domain as local") };

      private readonly CheckBox requiresAuth_ = new() { Content = L("Target server requires _authentication") };
      private readonly Wpf.Ui.Controls.TextBox authUser_ = new();
      private readonly hMailServer.ControlPanel.Views.PasswordField authPassword_ = new();

      private readonly InlineNotice notice_ = DialogFields.Notice();

      public RouteDialog(Window owner, string domainName)
      {
         domainName_ = domainName;
         Owner = owner;
         Title = L("Route - ") + domainName;

         var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
         tabs.Items.Add(new TabItem { Header = L("General"), Content = BuildGeneral() });
         tabs.Items.Add(new TabItem { Header = L("Delivery"), Content = BuildDelivery() });
         tabs.Items.Add(new TabItem { Header = L("Addresses"), Content = BuildAddresses() });
         tabs.Items.Add(new TabItem { Header = L("Security"), Content = BuildSecurity() });
         tabs.Items.Add(new TabItem { Header = L("Authentication"), Content = BuildAuth() });

         DialogFields.FitTabs(tabs, DialogFields.BodyWidth(DialogWidth));

         var body = new StackPanel();
         body.Children.Add(notice_);
         body.Children.Add(tabs);

         var save = new Wpf.Ui.Controls.Button { Content = L("_Save"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => Close();

         UseFrame(domainName, body, save, cancel, width: DialogWidth);
         Loaded += (s, e) => Load();
      }

      private ScrollViewer BuildGeneral()
      {
         var p = DialogFields.TabPanel();
         p.Children.Add(Label(L("_Target SMTP host"), host_));
         portRow_ = Label(L("Target SMTP _port"), port_);
         p.Children.Add(portRow_);
         p.Children.Add(Label(L("_Description"), description_));
         return DialogFields.Scroll(p);
      }

      private ScrollViewer BuildDelivery()
      {
         var p = DialogFields.TabPanel();
         triesRow_ = Label(L("_Number of delivery retries"), tries_);
         p.Children.Add(triesRow_);
         minutesRow_ = Label(L("_Minutes between retries"), minutes_);
         p.Children.Add(minutesRow_);
         p.Children.Add(DialogFields.Check(allAddresses_));
         return DialogFields.Scroll(p);
      }

      private ScrollViewer BuildAddresses()
      {
         var p = DialogFields.TabPanel();
         addressList_.DisplayMemberPath = "Address"; // no-loc

         // The list and the box that adds to it are one field: the caption names
         // the list to a screen reader, and the hint says when the list is
         // consulted at all, which used to be a parenthesis inside the caption.
         p.Children.Add(Label(L("Specific addresses to rou_te"), addressList_)
            .WithHint(L("Used when “Deliver to all addresses” is off.")));

         var addRow = new Grid();
         addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         Grid.SetColumn(newAddress_, 0);
         addRow.Children.Add(newAddress_);
         var addBtn = new Wpf.Ui.Controls.Button { Content = L("_Add"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         addBtn.Click += (s, e) => AddAddress();
         Grid.SetColumn(addBtn, 1);
         addRow.Children.Add(addBtn);
         var removeBtn = new Wpf.Ui.Controls.Button { Content = L("_Remove"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         removeBtn.Click += (s, e) => RemoveAddress();
         Grid.SetColumn(removeBtn, 2);
         addRow.Children.Add(removeBtn);
         p.Children.Add(Label(L("Address to a_dd"), addRow));
         return DialogFields.Scroll(p);
      }

      private ScrollViewer BuildSecurity()
      {
         connSecurity_.Items.Add(DialogFields.Combo(L("None"), 0));
         connSecurity_.Items.Add(DialogFields.Combo(L("SSL/TLS"), 1));
         connSecurity_.Items.Add(DialogFields.Combo(L("STARTTLS (optional)"), 2));
         connSecurity_.Items.Add(DialogFields.Combo(L("STARTTLS (required)"), 3));

         var p = DialogFields.TabPanel();
         p.Children.Add(Label(L("_Connection security"), connSecurity_));
         p.Children.Add(DialogFields.Check(treatSenderLocal_));
         p.Children.Add(DialogFields.Check(treatRecipientLocal_));
         return DialogFields.Scroll(p);
      }

      private ScrollViewer BuildAuth()
      {
         var p = DialogFields.TabPanel();
         p.Children.Add(DialogFields.Check(requiresAuth_));
         p.Children.Add(Label(L("_User name"), authUser_));
         p.Children.Add(Label(L("_Password"), authPassword_)
            .WithHint(L("Leave empty to keep the current password.")));
         return DialogFields.Scroll(p);
      }

      private dynamic FindRoute(dynamic routes)
      {
         int count = (int)routes.Count;
         for (int i = 0; i < count; i++)
         {
            dynamic r = routes.Item[i];
            if ((string)r.DomainName == domainName_)
               return r;
            ServerSession.Release(r);
         }
         return null;
      }

      private void Load()
      {
         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            dynamic r = FindRoute(routes);
            if (r == null) { Close(); return; }
            routeId_ = (int)r.ID;
            host_.Text = (string)r.TargetSMTPHost ?? "";
            port_.Text = ((int)r.TargetSMTPPort).ToString();
            description_.Text = (string)r.Description ?? "";
            tries_.Text = ((int)r.NumberOfTries).ToString();
            minutes_.Text = ((int)r.MinutesBetweenTry).ToString();
            allAddresses_.IsChecked = (bool)r.AllAddresses;
            DialogFields.SelectCombo(connSecurity_, (int)r.ConnectionSecurity);
            treatSenderLocal_.IsChecked = (bool)r.TreatSenderAsLocalDomain;
            treatRecipientLocal_.IsChecked = (bool)r.TreatRecipientAsLocalDomain;
            requiresAuth_.IsChecked = (bool)r.RelayerRequiresAuth;
            authUser_.Text = (string)r.RelayerAuthUsername ?? "";
            LoadAddresses(r);
            ServerSession.Release(r);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not load the route: {0}", ex.Message), L("Control Panel"));
            Close();
         }
         finally
         {
            ServerSession.Release(routes);
         }
      }

      private void Save()
      {
         notice_.Hide();
         DialogFields.ClearErrors(portRow_, triesRow_, minutesRow_);

         // Three numbers, each said on its own field rather than in one line at
         // the foot of the dialog: ShowError brings the tab that holds the field
         // forward and puts the keyboard in it, so the number that is wrong is
         // the thing on screen.
         if (!NumericField.TryValidate(port_.Text, L("Target SMTP port"), 1, 65535, out int portV, out bool hasPort, out string error))
         {
            DialogFields.ShowError(portRow_, error);
            return;
         }

         if (!NumericField.TryValidate(tries_.Text, L("Number of delivery retries"), 0, int.MaxValue, out int triesV, out bool hasTries, out error))
         {
            DialogFields.ShowError(triesRow_, error);
            return;
         }

         if (!NumericField.TryValidate(minutes_.Text, L("Minutes between retries"), 0, int.MaxValue, out int minutesV, out bool hasMinutes, out error))
         {
            DialogFields.ShowError(minutesRow_, error);
            return;
         }

         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            dynamic r = FindRoute(routes);
            if (r == null) { Close(); return; }
            r.TargetSMTPHost = host_.Text.Trim();
            if (hasPort) r.TargetSMTPPort = portV;
            r.Description = description_.Text.Trim();
            if (hasTries) r.NumberOfTries = triesV;
            if (hasMinutes) r.MinutesBetweenTry = minutesV;
            r.AllAddresses = allAddresses_.IsChecked is true;
            r.ConnectionSecurity = DialogFields.ComboValue(connSecurity_);
            r.TreatSenderAsLocalDomain = treatSenderLocal_.IsChecked is true;
            r.TreatRecipientAsLocalDomain = treatRecipientLocal_.IsChecked is true;
            r.RelayerRequiresAuth = requiresAuth_.IsChecked is true;
            r.RelayerAuthUsername = authUser_.Text.Trim();
            if (authPassword_.Password.Length > 0)
               r.SetRelayerAuthPassword(authPassword_.Password);
            r.Save();
            ServerSession.Release(r);
            Close();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not save the route: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(routes);
         }
      }

      // ---- addresses ----

      private sealed class AddrItem
      {
         public int Id { get; init; }
         public string Address { get; init; } = "";
         public override string ToString() => Address;
      }

      private void LoadAddresses(dynamic route)
      {
         addressList_.Items.Clear();
         dynamic addresses = route.Addresses;
         try
         {
            int count = (int)addresses.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic a = addresses.Item[i];
               addressList_.Items.Add(new AddrItem { Id = (int)a.ID, Address = (string)a.Address });
               ServerSession.Release(a);
            }
         }
         finally
         {
            ServerSession.Release(addresses);
         }
      }

      private void ReloadAddresses()
      {
         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            dynamic r = FindRoute(routes);
            if (r == null) return;
            LoadAddresses(r);
            ServerSession.Release(r);
         }
         finally
         {
            ServerSession.Release(routes);
         }
      }

      private void AddAddress()
      {
         string addr = newAddress_.Text.Trim();
         if (addr.Length == 0)
            return;

         notice_.Hide();

         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            dynamic r = FindRoute(routes);
            if (r == null) return;
            dynamic addresses = r.Addresses;
            try
            {
               dynamic a = addresses.Add();
               a.Address = addr;
               a.RouteID = routeId_;
               a.Save();
               ServerSession.Release(a);
            }
            finally
            {
               ServerSession.Release(addresses);
            }
            ServerSession.Release(r);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not add the address: {0}", ex.Message));
            return;
         }
         finally
         {
            ServerSession.Release(routes);
         }

         newAddress_.Text = "";
         ReloadAddresses();
      }

      private void RemoveAddress()
      {
         if (addressList_.SelectedItem is not AddrItem item)
            return;

         notice_.Hide();

         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            dynamic r = FindRoute(routes);
            if (r == null) return;
            dynamic addresses = r.Addresses;
            try
            {
               addresses.DeleteByDBID(item.Id);
            }
            finally
            {
               ServerSession.Release(addresses);
            }
            ServerSession.Release(r);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not remove the address: {0}", ex.Message));
            return;
         }
         finally
         {
            ServerSession.Release(routes);
         }

         ReloadAddresses();
      }

      /// <summary>
      /// A caption and its editor as one row, which names the editor to UI
      /// Automation. A TextBlock above a control tells UI Automation nothing, so
      /// without this every field in this dialog announced to a screen reader as
      /// an anonymous "edit".
      /// </summary>
      private static FieldRow Label(string text, FrameworkElement editor) => DialogFields.Field(text, editor);
   }
}
