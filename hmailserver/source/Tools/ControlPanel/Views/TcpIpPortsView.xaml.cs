using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

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

         /// <summary>"Listening", "Not listening", "Server off" or "Unknown".</summary>
         public string ListeningWord { get; set; }

         /// <summary>The full explanation, as the cell's tooltip and its accessible name.</summary>
         public string ListeningTip { get; set; }

         /// <summary>The badge, already shaped and coloured by ShapeMarkVisuals.</summary>
         public FrameworkElement ListeningMark { get; set; }
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

         // Read once per reload, not per row: GetActiveTcpListeners walks the whole
         // TCP table, and a server with twenty port rows would otherwise walk it
         // twenty times for the same answer.
         bool local = ServerSession.IsLocalSession;
         IReadOnlyList<System.Net.IPEndPoint> listeners = local
            ? ListenerProbe.ActiveListeners()
            : System.Array.Empty<System.Net.IPEndPoint>();
         Dictionary<string, bool?> protocolEnabled = LoadProtocolSwitches();

         dynamic ports = ServerSession.Current.Application.Settings.TCPIPPorts;
         try
         {
            int count = (int) ports.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic port = ports.Item[i];
               int security = (int) port.ConnectionSecurity;
               int certId = (int) port.SSLCertificateID;
               string protocol = ProtocolName((int) port.Protocol);
               string address = (string) port.Address;
               int portNumber = (int) port.PortNumber;

               protocolEnabled.TryGetValue(protocol, out bool? enabled);

               ListenerState state = ListenerProbe.StateOf(address, portNumber, enabled, listeners, local);
               StatusPresentation presentation = StatusSemantics.For(ListenerProbe.LevelFor(state));

               rows.Add(new PortRow
               {
                  Id = (int) port.ID,
                  Protocol = protocol,
                  Address = address,
                  Port = portNumber,
                  Security = security >= 0 && security < SecurityNames.Length ? SecurityNames[security] : "#" + security,
                  Certificate = certId > 0 && certNames.TryGetValue(certId, out string n) ? n : "",
                  ListeningWord = ListenerProbe.WordFor(state),
                  ListeningTip = ListenerProbe.WordFor(state) + ". " + ListenerProbe.ExplainFor(state, protocol),
                  ListeningMark = MakeListeningMark(presentation)
               });
               ServerSession.Release(port);
            }
         }
         finally
         {
            ServerSession.Release(ports);
         }

         PortGrid.ItemsSource = rows;
         ListSearch.Apply(PortGrid, SearchBox.Text);
         StatusText.Show(EmptyStatus, rows.Count, null, "No ports configured.");

         ShowListenerSummary(rows, local);
      }

      /// <summary>
      /// One row's badge.
      ///
      /// The colour goes on as a resource reference rather than a resolved brush,
      /// because ThemeTokens republishes the status brushes on every theme change
      /// and a badge handed a brush would stay painted for the previous theme.
      /// ApplyMark is also what knows that a stroke-only mark has to be stroked
      /// rather than filled - a filled cross renders as nothing at all.
      /// </summary>
      private static FrameworkElement MakeListeningMark(StatusPresentation presentation)
      {
         var path = new System.Windows.Shapes.Path
         {
            Width = 10,
            Height = 10,
            Stretch = System.Windows.Media.Stretch.Fill,
            VerticalAlignment = VerticalAlignment.Center
         };

         ShapeMarkVisuals.ApplyMark(path, presentation.Shape, presentation.BrushKey);
         return path;
      }

      /// <summary>
      /// The one line under the list, shown only when it has something to say.
      ///
      /// A per-row badge answers "is this port up"; it does not answer "why has mail
      /// stopped", which is the question somebody opens this page with. A port that
      /// is configured, enabled and not bound is the single most likely cause and
      /// the least visible, so it gets said in words at the bottom of the page as
      /// well as shown in a column.
      /// </summary>
      private void ShowListenerSummary(List<PortRow> rows, bool local)
      {
         if (!local)
         {
            ListenerSummary.Text = "Connected to another host, so whether these ports are being listened on cannot "
                                   + "be read from here - open the Control Panel on the server itself to see it.";
            ListenerSummary.Visibility = Visibility.Visible;
            return;
         }

         var down = rows.FindAll(r => r.ListeningWord == ListenerProbe.WordFor(ListenerState.NotListening));

         if (down.Count == 0)
         {
            ListenerSummary.Visibility = Visibility.Collapsed;
            return;
         }

         var names = new List<string>();
         foreach (PortRow row in down)
            names.Add(row.Protocol + " " + row.Address + ":" + row.Port);

         ListenerSummary.Text =
            (down.Count == 1 ? "One configured port is not being listened on: " : down.Count + " configured ports are not being listened on: ")
            + string.Join(", ", names)
            + ". Connections to them are refused. The usual causes are another program already holding the port, a "
            + "bind address that does not exist on this machine, or - on a TLS port - a certificate the server could "
            + "not load; the server records which, once, in the error log at start-up.";
         ListenerSummary.Visibility = Visibility.Visible;
      }

      /// <summary>
      /// ServiceSMTP / ServicePOP3 / ServiceIMAP, keyed by the same protocol names
      /// the rows carry. A value of null means the switch could not be read, which
      /// makes an unbound port "unknown" rather than "broken" - the difference
      /// between asking somebody to investigate and telling them nothing useful.
      /// </summary>
      private static Dictionary<string, bool?> LoadProtocolSwitches()
      {
         var map = new Dictionary<string, bool?>
         {
            ["SMTP"] = null,
            ["POP3"] = null,
            ["IMAP"] = null
         };

         dynamic settings = ServerSession.Current.Application.Settings;
         try
         {
            map["SMTP"] = (bool) settings.ServiceSMTP;
            map["POP3"] = (bool) settings.ServicePOP3;
            map["IMAP"] = (bool) settings.ServiceIMAP;
         }
         catch (Exception)
         {
         }
         finally
         {
            ServerSession.Release((object) settings);
         }

         return map;
      }

      private void Search_TextChanged(object sender, TextChangedEventArgs e)
         => ListSearch.Apply(PortGrid, SearchBox.Text);

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

      /// <summary>
      /// Settings.TCPIPPorts.SetDefault() over COM: every configured port is replaced
      /// with SMTP 25 + 587, POP3 110 and IMAP 143, no connection security. The COM
      /// call exists since the classic Administrator; this button went missing when
      /// that tool was retired. The failure text from the server is shown verbatim -
      /// it is written for exactly this situation (the old ports are gone even when
      /// writing the new ones failed).
      /// </summary>
      private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
      {
         if (MessageBox.Show(
                "Replace ALL configured ports with the defaults?\n\nSMTP 25 and 587, POP3 110, IMAP 143 - " +
                "without connection security. TLS ports and custom bindings will be removed. The change takes " +
                "effect when the server is restarted.",
                "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic ports = ServerSession.Current.Application.Settings.TCPIPPorts;
         try
         {
            ports.SetDefault();
         }
         catch (Exception ex)
         {
            MessageBox.Show(ex.Message, "Control Panel", MessageBoxButton.OK, MessageBoxImage.Error);
         }
         finally
         {
            ServerSession.Release(ports);
         }

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
