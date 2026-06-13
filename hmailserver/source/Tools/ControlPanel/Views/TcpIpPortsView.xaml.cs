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
         public string Protocol { get; set; }
         public string Address { get; set; }
         public int Port { get; set; }
         public string Security { get; set; }
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
         dynamic ports = ServerSession.Current.Application.Settings.TCPIPPorts;
         try
         {
            int count = (int) ports.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic port = ports.Item[i];
               int security = (int) port.ConnectionSecurity;
               rows.Add(new PortRow
               {
                  Protocol = ProtocolName((int) port.Protocol),
                  Address = (string) port.Address,
                  Port = (int) port.PortNumber,
                  Security = security >= 0 && security < SecurityNames.Length ? SecurityNames[security] : "#" + security
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
