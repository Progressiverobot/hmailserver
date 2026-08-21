using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Editor for one TCP/IP port binding, including the SSL certificate to use
   /// (which the inline add-form on the page cannot set) and the client
   /// certificate (mutual TLS) policy, which nothing else in the interface
   /// could set at all.
   /// </summary>
   public class TcpIpPortDialog : FluentDialogWindow
   {
      private readonly int portId_;

      private readonly ComboBox protocol_ = new();
      private readonly TextBox address_ = new();
      private readonly TextBox port_ = new();
      private readonly ComboBox security_ = new();
      private readonly ComboBox certificate_ = new();
      private readonly ComboBox clientCertPolicy_ = new();
      private readonly TextBox clientCertCaFile_ = new();
      private readonly TextBlock clientCertWarning_ = new();

      public TcpIpPortDialog(Window owner, int portId)
      {
         portId_ = portId;
         Owner = owner;
         Title = "TCP/IP port";
         Width = 520;
         SizeToContent = SizeToContent.Height;
         ResizeMode = ResizeMode.NoResize;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(22) };
         var header = new TextBlock { Text = "Port binding", FontSize = Typography.DialogTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(header);

         protocol_.Items.Add(Combo("SMTP", ServerSession.SessionSmtp));
         protocol_.Items.Add(Combo("POP3", ServerSession.SessionPop3));
         protocol_.Items.Add(Combo("IMAP", ServerSession.SessionImap));
         StyleCombo(protocol_);
         panel.Children.Add(Label("Protocol", protocol_));
         panel.Children.Add(protocol_);

         panel.Children.Add(Label("Bind address", address_));
         panel.Children.Add(Input(address_));
         panel.Children.Add(Label("Port", port_));
         panel.Children.Add(Input(port_));

         security_.Items.Add(Combo("None", 0));
         security_.Items.Add(Combo("SSL/TLS", 1));
         security_.Items.Add(Combo("STARTTLS (optional)", 2));
         security_.Items.Add(Combo("STARTTLS (required)", 3));
         StyleCombo(security_);
         panel.Children.Add(Label("Connection security", security_));
         panel.Children.Add(security_);

         StyleCombo(certificate_);
         panel.Children.Add(Label("SSL certificate (required for SSL/TLS and STARTTLS)", certificate_));
         panel.Children.Add(certificate_);

         // Client certificates (mutual TLS), per port. The three options are the
         // three values of ClientCertificatePolicy in SocketConstants.h, spelled
         // out as what each one does to a connection.
         clientCertPolicy_.Items.Add(Combo("Off", 0));
         clientCertPolicy_.Items.Add(Combo("Request (verify and log, never refuse)", 1));
         clientCertPolicy_.Items.Add(Combo("Require (refuse a connection without a trusted certificate)", 2));
         StyleCombo(clientCertPolicy_);
         panel.Children.Add(Label("Client certificate policy (mutual TLS)", clientCertPolicy_));
         AutomationProperties.SetHelpText(clientCertPolicy_,
            "Request asks every client for a certificate, verifies and logs one if it is offered, and never " +
            "refuses the connection - use it to inventory which clients would survive Require before enforcing " +
            "it. Require refuses the connection unless the client presents a certificate that chains to the " +
            "CA bundle below.");
         panel.Children.Add(clientCertPolicy_);

         panel.Children.Add(Label("CA certificate bundle (PEM) that client certificates must chain to", clientCertCaFile_));
         var caRow = new Grid();
         caRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         caRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         Input(clientCertCaFile_);
         Grid.SetColumn(clientCertCaFile_, 0);
         caRow.Children.Add(clientCertCaFile_);
         var caBrowse = new Wpf.Ui.Controls.Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 4), VerticalAlignment = VerticalAlignment.Top };
         AutomationProperties.SetAutomationId(caBrowse, "ClientCertCaBrowse");
         AutomationProperties.SetName(caBrowse, "Browse for a CA certificate bundle file");
         caBrowse.Click += (s, e) =>
         {
            string file = PathPicker.PickFile(clientCertCaFile_.Text,
               "PEM/certificate files (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*");
            if (file != null)
               clientCertCaFile_.Text = file;
         };
         Grid.SetColumn(caBrowse, 1);
         caRow.Children.Add(caBrowse);
         panel.Children.Add(caRow);

         TextBlock caNote = Note(
            "hMailServer does not create or manage client certificates. The certificate authority and the " +
            "client certificates themselves must be produced outside hMailServer (for example with OpenSSL " +
            "or an internal PKI) - the server only trusts the CA bundle it is given here.");
         AutomationProperties.SetHelpText(clientCertCaFile_, caNote.Text);
         panel.Children.Add(caNote);

         // Inline validation for the combinations the server refuses to save, so
         // the dialog says so while the user is still choosing rather than
         // relaying a COM error after Save. The state is carried by the text
         // itself (present or absent), never by colour, and the live setting
         // makes a screen reader announce it when it appears.
         clientCertWarning_.FontSize = Typography.Caption;
         clientCertWarning_.TextWrapping = TextWrapping.Wrap;
         clientCertWarning_.Margin = new Thickness(0, 6, 0, 0);
         clientCertWarning_.Visibility = Visibility.Collapsed;
         clientCertWarning_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         AutomationProperties.SetLiveSetting(clientCertWarning_, AutomationLiveSetting.Polite);
         panel.Children.Add(clientCertWarning_);

         security_.SelectionChanged += (s, e) => UpdateClientCertificateValidation();
         clientCertPolicy_.SelectionChanged += (s, e) => UpdateClientCertificateValidation();
         clientCertCaFile_.TextChanged += (s, e) => UpdateClientCertificateValidation();

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
         // Enter saves and Escape cancels. Neither did anything here: a dialog whose
         // only way out is the mouse or Alt+F4 is a dialog a keyboard user is stuck
         // in, and the two properties that fix it are the ones the account and domain
         // dialogs already set.
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80, IsDefault = true };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
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
            SelectCombo(clientCertPolicy_, (int) p.ClientCertificatePolicy);
            clientCertCaFile_.Text = (string) p.ClientCertificateCAFile ?? "";
            UpdateClientCertificateValidation();
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

         // The server refuses these combinations too (PersistentTCPIPPort), so
         // this is a courtesy, not the enforcement: a COM save error is still
         // handled below in case the dialog and the server ever disagree.
         string clientCertError = ClientCertificateValidationError();
         if (clientCertError != null)
         {
            UpdateClientCertificateValidation();
            MessageBox.Show(clientCertError, "Control Panel");
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
            p.ClientCertificatePolicy = ComboValue(clientCertPolicy_);
            p.ClientCertificateCAFile = clientCertCaFile_.Text.Trim();
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

      // ---- client certificate validation ----

      /// <summary>
      /// The combinations PersistentTCPIPPort::SaveObject refuses, checked while
      /// the user is still choosing. The wording explains the refusal rather than
      /// just naming it, because each of these is a configuration that LOOKS like
      /// security and enforces nothing. Must stay in step with that file.
      /// </summary>
      private string ClientCertificateValidationError()
      {
         int policy = ComboValue(clientCertPolicy_);
         if (policy == 0)
            return null;

         int security = ComboValue(security_);

         // A client certificate is only ever exchanged during a TLS handshake,
         // so on a plaintext port the policy could never run.
         if (security == 0)
            return "Client certificates can only be requested or required on a port that uses SSL/TLS or " +
                   "STARTTLS. On a port with no connection security there is no TLS handshake, so no client " +
                   "would ever be asked for a certificate.";

         // Without trust anchors, Require rejects every client (nothing chains
         // to an empty CA set) and Request verifies nothing while looking
         // configured.
         if (clientCertCaFile_.Text.Trim().Length == 0)
            return "A CA certificate bundle file must be specified when client certificates are requested or " +
                   "required. Without one, Require would reject every client and Request would verify nothing.";

         // On an optional-STARTTLS port a client that never issues STARTTLS is
         // never asked for a certificate at all, so Require there is a lock on
         // an open door.
         if (policy == 2 && security == 2)
            return "Require cannot be combined with STARTTLS (optional): a client that simply never issues " +
                   "STARTTLS is never asked for a certificate, so the requirement would not be enforced. " +
                   "Use SSL/TLS or STARTTLS (required).";

         return null;
      }

      private void UpdateClientCertificateValidation()
      {
         string error = ClientCertificateValidationError();
         clientCertWarning_.Text = error == null ? "" : "Cannot save: " + error;
         clientCertWarning_.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
      }

      // ---- UI helpers ----

      /// <summary>
      /// A caption, and - when the editor it captions is passed in - the editor's
      /// accessible name.
      ///
      /// A TextBlock placed above a control tells UI Automation nothing: this dialog
      /// binds a network port and announced itself to a screen reader as "combo box,
      /// edit, edit, combo box, combo box". That is the same defect
      /// <see cref="AccessibleNames"/> was written for on the generated settings
      /// pages, and the hand-built dialogs were never done. The cleaning rule comes
      /// from there too, so "Host:" is not read out as "Host colon".
      /// </summary>
      private static TextBlock Label(string text, FrameworkElement editor = null)
      {
         var t = new TextBlock { Text = text, FontSize = Typography.Label, Margin = new Thickness(0, 8, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");

         if (editor != null)
            AutomationProperties.SetName(editor, AccessibleNames.Qualify(text, ""));

         return t;
      }

      /// <summary>
      /// A wrapped caption for a statement that belongs on screen rather than in
      /// a tooltip - the CA-provenance note is the kind of thing an administrator
      /// has to read before they go looking for a "generate" button that does
      /// not exist. Callers attach the same text to the control as accessible
      /// help text so it is heard with the editor, not after it.
      /// </summary>
      private static TextBlock Note(string text)
      {
         var t = new TextBlock { Text = text, FontSize = Typography.Caption, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }

      private static TextBox Input(TextBox box)
      {
         box.FontSize = Typography.Body;
         box.Padding = new Thickness(6);
         box.Margin = new Thickness(0, 0, 0, 4);
         box.Background = System.Windows.Media.Brushes.Transparent;
         box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         return box;
      }

      private static ComboBoxItem Combo(string text, int value) => new() { Content = text, Tag = value };
      private static void StyleCombo(ComboBox combo) { combo.FontSize = Typography.Body; combo.Margin = new Thickness(0, 0, 0, 4); }

      private static void SelectCombo(ComboBox combo, int value)
      {
         foreach (ComboBoxItem item in combo.Items)
            if ((int) item.Tag == value) { combo.SelectedItem = item; return; }
         if (combo.Items.Count > 0) combo.SelectedIndex = 0;
      }

      private static int ComboValue(ComboBox combo) => combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : 0;
   }
}
