using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   public partial class TcpIpPortsView : UserControl, IPageLifecycle
   {
      public class PortRow
      {
         public int Id { get; set; }
         public string Protocol { get; set; }
         public string Address { get; set; }
         public int Port { get; set; }
         public string Security { get; set; }
         public string Certificate { get; set; }
      }

      private static readonly string[] SecurityNames = { "None", "SSL/TLS", "STARTTLS (optional)", "STARTTLS (required)" };

      public TcpIpPortsView()
      {
         InitializeComponent();
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private static string ProtocolName(int sessionType) => sessionType switch
      {
         ServerSession.SessionSmtp => "SMTP",
         ServerSession.SessionPop3 => "POP3",
         ServerSession.SessionImap => "IMAP",
         _ => "#" + sessionType
      };

      private void Reload()
      {
         var rows = new List<PortRow>();
         var certNames = LoadCertNames();
         dynamic ports = ServerSession.Current.Application.Settings.TCPIPPorts;
         try
         {
            int count = (int) ports.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic port = ports.Item[i];
               int security = (int) port.ConnectionSecurity;
               int certId = (int) port.SSLCertificateID;
               rows.Add(new PortRow
               {
                  Id = (int) port.ID,
                  Protocol = ProtocolName((int) port.Protocol),
                  Address = (string) port.Address,
                  Port = (int) port.PortNumber,
                  Security = security >= 0 && security < SecurityNames.Length ? SecurityNames[security] : "#" + security,
                  Certificate = certId > 0 && certNames.TryGetValue(certId, out string n) ? n : ""
               });
               ServerSession.Release(port);
            }
         }
         finally
         {
            ServerSession.Release(ports);
         }

         PortGrid.ItemsSource = rows;
      }

      private static Dictionary<int, string> LoadCertNames()
      {
         var map = new Dictionary<int, string>();
         dynamic certs = ServerSession.Current.Application.Settings.SSLCertificates;
         try
         {
            int count = (int) certs.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic c = certs.Item[i];
               map[(int) c.ID] = (string) c.Name;
               ServerSession.Release(c);
            }
         }
         catch (Exception)
         {
         }
         finally
         {
            ServerSession.Release(certs);
         }
         return map;
      }

      private void Edit_Click(object sender, RoutedEventArgs e)
      {
         if (PortGrid.SelectedItem is not PortRow row)
         {
            MessageBox.Show("Select a port first.", "Control Panel");
            return;
         }

         new TcpIpPortDialog(Window.GetWindow(this), row.Id).ShowDialog();
         Reload();
      }

      private void Add_Click(object sender, RoutedEventArgs e)
      {
         if (!int.TryParse(NewPort.Text.Trim(), out int portNumber) || portNumber <= 0 || portNumber > 65535)
         {
            MessageBox.Show("Enter a valid port number.", "Control Panel");
            return;
         }

         int protocol = NewProtocol.SelectedIndex switch
         {
            0 => ServerSession.SessionSmtp,
            1 => ServerSession.SessionPop3,
            _ => ServerSession.SessionImap
         };

         dynamic ports = ServerSession.Current.Application.Settings.TCPIPPorts;
         try
         {
            dynamic port = ports.Add();
            port.Protocol = protocol;
            port.Address = NewAddress.Text.Trim();
            port.PortNumber = portNumber;
            port.ConnectionSecurity = NewSecurity.SelectedIndex;
            port.Save();
            ServerSession.Release(port);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not add the port: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(ports);
         }

         NewPort.Text = "";
         Reload();
      }

      private void Delete_Click(object sender, RoutedEventArgs e)
      {
         if (PortGrid.SelectedItem is not PortRow row)
            return;

         if (MessageBox.Show("Delete the " + row.Protocol + " port " + row.Port + " binding?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic ports = ServerSession.Current.Application.Settings.TCPIPPorts;
         try
         {
            int count = (int) ports.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic port = ports.Item[i];
               if (ProtocolName((int) port.Protocol) == row.Protocol &&
                   (int) port.PortNumber == row.Port &&
                   (string) port.Address == row.Address)
               {
                  port.Delete();
                  ServerSession.Release(port);
                  break;
               }
               ServerSession.Release(port);
            }
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the port: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(ports);
         }

         Reload();
      }
   }
}
