using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Tabbed editor for one route's full set of options.</summary>
   public class RouteDialog : Window
   {
      private readonly string domainName_;
      private int routeId_;

      private readonly TextBox host_ = new();
      private readonly TextBox port_ = new();
      private readonly TextBox description_ = new();
      private readonly TextBox tries_ = new();
      private readonly TextBox minutes_ = new();
      private readonly CheckBox allAddresses_ = new() { Content = "Deliver to all addresses (not only known accounts)", FontSize = 13 };

      private readonly ListBox addressList_ = new() { Height = 200, FontSize = 13 };
      private readonly TextBox newAddress_ = new();

      private readonly ComboBox connSecurity_ = new();
      private readonly CheckBox treatSenderLocal_ = new() { Content = "Treat sender domain as local", FontSize = 13 };
      private readonly CheckBox treatRecipientLocal_ = new() { Content = "Treat recipient domain as local", FontSize = 13 };

      private readonly CheckBox requiresAuth_ = new() { Content = "Target server requires authentication", FontSize = 13 };
      private readonly TextBox authUser_ = new();
      private readonly PasswordBox authPassword_ = new();

      public RouteDialog(Window owner, string domainName)
      {
         domainName_ = domainName;
         Owner = owner;
         Title = "Route - " + domainName;
         Width = 560;
         Height = 560;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var root = new Grid { Margin = new Thickness(18) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var header = new TextBlock { Text = domainName, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 12) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetRow(header, 0);
         root.Children.Add(header);

         var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
         tabs.Items.Add(new TabItem { Header = "General", Content = BuildGeneral() });
         tabs.Items.Add(new TabItem { Header = "Delivery", Content = BuildDelivery() });
         tabs.Items.Add(new TabItem { Header = "Addresses", Content = BuildAddresses() });
         tabs.Items.Add(new TabItem { Header = "Security", Content = BuildSecurity() });
         tabs.Items.Add(new TabItem { Header = "Authentication", Content = BuildAuth() });
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
         var p = TabPanel();
         p.Children.Add(Label("Target SMTP host"));
         p.Children.Add(Input(host_));
         p.Children.Add(Label("Target SMTP port"));
         p.Children.Add(Input(port_));
         p.Children.Add(Label("Description"));
         p.Children.Add(Input(description_));
         return Scroll(p);
      }

      private ScrollViewer BuildDelivery()
      {
         var p = TabPanel();
         p.Children.Add(Label("Number of delivery retries"));
         p.Children.Add(Input(tries_));
         p.Children.Add(Label("Minutes between retries"));
         p.Children.Add(Input(minutes_));
         p.Children.Add(allAddresses_);
         return Scroll(p);
      }

      private ScrollViewer BuildAddresses()
      {
         var p = TabPanel();
         p.Children.Add(Label("Specific addresses to route (used when \u201cDeliver to all addresses\u201d is off)"));
         addressList_.DisplayMemberPath = "Address";
         p.Children.Add(addressList_);

         var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
         newAddress_.Width = 320;
         Input(newAddress_);
         newAddress_.Margin = new Thickness(0, 0, 8, 0);
         var addBtn = new Wpf.Ui.Controls.Button { Content = "Add", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
         addBtn.Click += (s, e) => AddAddress();
         var removeBtn = new Wpf.Ui.Controls.Button { Content = "Remove" };
         removeBtn.Click += (s, e) => RemoveAddress();
         addRow.Children.Add(newAddress_);
         addRow.Children.Add(addBtn);
         addRow.Children.Add(removeBtn);
         p.Children.Add(addRow);
         return Scroll(p);
      }

      private ScrollViewer BuildSecurity()
      {
         connSecurity_.Items.Add(Combo("None", 0));
         connSecurity_.Items.Add(Combo("SSL/TLS", 1));
         connSecurity_.Items.Add(Combo("STARTTLS (optional)", 2));
         connSecurity_.Items.Add(Combo("STARTTLS (required)", 3));
         connSecurity_.FontSize = 13;
         connSecurity_.Margin = new Thickness(0, 0, 0, 8);

         var p = TabPanel();
         p.Children.Add(Label("Connection security"));
         p.Children.Add(connSecurity_);
         p.Children.Add(treatSenderLocal_);
         p.Children.Add(treatRecipientLocal_);
         return Scroll(p);
      }

      private ScrollViewer BuildAuth()
      {
         var p = TabPanel();
         p.Children.Add(requiresAuth_);
         p.Children.Add(Label("User name"));
         p.Children.Add(Input(authUser_));
         p.Children.Add(Label("Password (leave empty to keep current)"));
         authPassword_.FontSize = 13;
         authPassword_.Padding = new Thickness(6);
         authPassword_.Margin = new Thickness(0, 0, 0, 8);
         p.Children.Add(authPassword_);
         return Scroll(p);
      }

      private dynamic FindRoute(dynamic routes)
      {
         int count = (int) routes.Count;
         for (int i = 0; i < count; i++)
         {
            dynamic r = routes.Item[i];
            if ((string) r.DomainName == domainName_)
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
            routeId_ = (int) r.ID;
            host_.Text = (string) r.TargetSMTPHost ?? "";
            port_.Text = ((int) r.TargetSMTPPort).ToString();
            description_.Text = (string) r.Description ?? "";
            tries_.Text = ((int) r.NumberOfTries).ToString();
            minutes_.Text = ((int) r.MinutesBetweenTry).ToString();
            allAddresses_.IsChecked = (bool) r.AllAddresses;
            SelectCombo(connSecurity_, (int) r.ConnectionSecurity);
            treatSenderLocal_.IsChecked = (bool) r.TreatSenderAsLocalDomain;
            treatRecipientLocal_.IsChecked = (bool) r.TreatRecipientAsLocalDomain;
            requiresAuth_.IsChecked = (bool) r.RelayerRequiresAuth;
            authUser_.Text = (string) r.RelayerAuthUsername ?? "";
            LoadAddresses(r);
            ServerSession.Release(r);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load the route: " + ex.Message, "Control Panel");
            Close();
         }
         finally
         {
            ServerSession.Release(routes);
         }
      }

      private void Save()
      {
         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            dynamic r = FindRoute(routes);
            if (r == null) { Close(); return; }
            r.TargetSMTPHost = host_.Text.Trim();
            if (int.TryParse(port_.Text.Trim(), out int port)) r.TargetSMTPPort = port;
            r.Description = description_.Text.Trim();
            if (int.TryParse(tries_.Text.Trim(), out int t)) r.NumberOfTries = t;
            if (int.TryParse(minutes_.Text.Trim(), out int m)) r.MinutesBetweenTry = m;
            r.AllAddresses = allAddresses_.IsChecked == true;
            int cs = ComboValue(connSecurity_);
            r.ConnectionSecurity = cs;
            r.TreatSenderAsLocalDomain = treatSenderLocal_.IsChecked == true;
            r.TreatRecipientAsLocalDomain = treatRecipientLocal_.IsChecked == true;
            r.RelayerRequiresAuth = requiresAuth_.IsChecked == true;
            r.RelayerAuthUsername = authUser_.Text.Trim();
            if (authPassword_.Password.Length > 0)
               r.SetRelayerAuthPassword(authPassword_.Password);
            r.Save();
            ServerSession.Release(r);
            Close();
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the route: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(routes);
         }
      }

      // ---- UI helpers ----

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
            int count = (int) addresses.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic a = addresses.Item[i];
               addressList_.Items.Add(new AddrItem { Id = (int) a.ID, Address = (string) a.Address });
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not add the address: " + ex.Message, "Control Panel");
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not remove the address: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(routes);
         }

         ReloadAddresses();
      }

      private static StackPanel TabPanel() => new() { Margin = new Thickness(4, 12, 4, 4) };
      private static ScrollViewer Scroll(StackPanel panel) => new() { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

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

      private static ComboBoxItem Combo(string text, int value) => new() { Content = text, Tag = value };

      private static void SelectCombo(ComboBox combo, int value)
      {
         foreach (ComboBoxItem item in combo.Items)
            if ((int) item.Tag == value) { combo.SelectedItem = item; return; }
      }

      private static int ComboValue(ComboBox combo) => combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : 0;
   }
}
