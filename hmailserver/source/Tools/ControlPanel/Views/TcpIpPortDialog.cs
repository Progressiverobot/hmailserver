using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Editor for one TCP/IP port binding, including the SSL certificate to use
   /// (which the inline add-form on the page cannot set).
   /// </summary>
   public class TcpIpPortDialog : Window
   {
      private readonly int portId_;

      private readonly ComboBox protocol_ = new();
      private readonly TextBox address_ = new();
      private readonly TextBox port_ = new();
      private readonly ComboBox security_ = new();
      private readonly ComboBox certificate_ = new();

      public TcpIpPortDialog(Window owner, int portId)
      {
         portId_ = portId;
         Owner = owner;
         Title = "TCP/IP port";
         Width = 460;
         SizeToContent = SizeToContent.Height;
         ResizeMode = ResizeMode.NoResize;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(22) };
         var header = new TextBlock { Text = "Port binding", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(header);

         protocol_.Items.Add(Combo("SMTP", ServerSession.SessionSmtp));
         protocol_.Items.Add(Combo("POP3", ServerSession.SessionPop3));
         protocol_.Items.Add(Combo("IMAP", ServerSession.SessionImap));
         StyleCombo(protocol_);
         panel.Children.Add(Label("Protocol"));
         panel.Children.Add(protocol_);

         panel.Children.Add(Label("Bind address"));
         panel.Children.Add(Input(address_));
         panel.Children.Add(Label("Port"));
         panel.Children.Add(Input(port_));

         security_.Items.Add(Combo("None", 0));
         security_.Items.Add(Combo("SSL/TLS", 1));
         security_.Items.Add(Combo("STARTTLS (optional)", 2));
         security_.Items.Add(Combo("STARTTLS (required)", 3));
         StyleCombo(security_);
         panel.Children.Add(Label("Connection security"));
         panel.Children.Add(security_);

         StyleCombo(certificate_);
         panel.Children.Add(Label("SSL certificate (required for SSL/TLS and STARTTLS)"));
         panel.Children.Add(certificate_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", MinWidth = 80 };
         cancel.Click += (s, e) => Close();
         buttons.Children.Add(save);
         buttons.Children.Add(cancel);
         panel.Children.Add(buttons);

         Content = panel;
         Loaded += (s, e) => Load();
      }

      private void LoadCertificates(int selectedId)
      {
         certificate_.Items.Add(Combo("(none)", 0));
         dynamic certs = ServerSession.Current.Application.Settings.SSLCertificates;
         try
         {
            int count = (int) certs.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic c = certs.Item[i];
               certificate_.Items.Add(Combo((string) c.Name, (int) c.ID));
               ServerSession.Release(c);
            }
         }
         catch (Exception) { }
         finally { ServerSession.Release(certs); }
         SelectCombo(certificate_, selectedId);
      }

      private dynamic FindPort(dynamic ports)
      {
         int count = (int) ports.Count;
         for (int i = 0; i < count; i++)
         {
            dynamic p = ports.Item[i];
            if ((int) p.ID == portId_)
               return p;
            ServerSession.Release(p);
         }
         return null;
      }

      private void Load()
      {
         dynamic ports = ServerSession.Current.Application.Settings.TCPIPPorts;
         try
         {
            dynamic p = FindPort(ports);
            if (p == null) { Close(); return; }
            SelectCombo(protocol_, (int) p.Protocol);
            address_.Text = (string) p.Address ?? "";
            port_.Text = ((int) p.PortNumber).ToString();
            SelectCombo(security_, (int) p.ConnectionSecurity);
            LoadCertificates((int) p.SSLCertificateID);
            ServerSession.Release(p);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load the port: " + ex.Message, "Control Panel");
            Close();
         }
         finally
         {
            ServerSession.Release(ports);
         }
      }

      private void Save()
      {
         if (!int.TryParse(port_.Text.Trim(), out int portNumber) || portNumber <= 0 || portNumber > 65535)
         {
            MessageBox.Show("Enter a valid port number.", "Control Panel");
            return;
         }

         dynamic ports = ServerSession.Current.Application.Settings.TCPIPPorts;
         try
         {
            dynamic p = FindPort(ports);
            if (p == null) { Close(); return; }
            p.Protocol = ComboValue(protocol_);
            p.Address = address_.Text.Trim();
            p.PortNumber = portNumber;
            p.ConnectionSecurity = ComboValue(security_);
            p.SSLCertificateID = ComboValue(certificate_);
            p.Save();
            ServerSession.Release(p);
            Close();
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the port: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(ports);
         }
      }

      // ---- UI helpers ----

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
         box.Margin = new Thickness(0, 0, 0, 4);
         box.Background = System.Windows.Media.Brushes.Transparent;
         box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         return box;
      }

      private static ComboBoxItem Combo(string text, int value) => new() { Content = text, Tag = value };
      private static void StyleCombo(ComboBox combo) { combo.FontSize = 13; combo.Margin = new Thickness(0, 0, 0, 4); }

      private static void SelectCombo(ComboBox combo, int value)
      {
         foreach (ComboBoxItem item in combo.Items)
            if ((int) item.Tag == value) { combo.SelectedItem = item; return; }
         if (combo.Items.Count > 0) combo.SelectedIndex = 0;
      }

      private static int ComboValue(ComboBox combo) => combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : 0;
   }
}
