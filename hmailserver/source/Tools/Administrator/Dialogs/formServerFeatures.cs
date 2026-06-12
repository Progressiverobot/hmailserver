// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Server features dialog: edits the advanced feature switches in
// hMailServer.INI (ARC, MTA-STS, DANE, TLS reporting, REST API, ACME,
// Prometheus metrics, JSON logging) without manual file editing.
//
// The INI file only exists on the server itself, so this dialog is
// available when hMailServer Administrator runs on the same machine.

using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace hMailServer.Administrator.Dialogs
{
   public class formServerFeatures : Form
   {
      private const string IniSection = "Settings";
      private readonly string _iniPath;

      // General
      private CheckBox checkArc;
      private CheckBox checkMtaSts;
      private CheckBox checkDane;
      private CheckBox checkDnssec;
      private CheckBox checkJsonLogging;

      // TLS reporting
      private TextBox textTlsRptFrom;
      private TextBox textTlsRptOrganization;

      // Metrics
      private TextBox textMetricsPort;
      private TextBox textMetricsBind;

      // REST API
      private TextBox textRestPort;
      private TextBox textRestBind;
      private TextBox textRestCert;
      private TextBox textRestKey;

      // ACME
      private CheckBox checkAcme;
      private TextBox textAcmeEmail;
      private TextBox textAcmeDomains;

      // Web services (MTA-STS hosting, autoconfig)
      private CheckBox checkWebServices;
      private TextBox textWebClientHost;

      private Button buttonSave;
      private Button buttonCancel;
      private ToolTip toolTip;

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
         StringBuilder result, int size, string filePath);

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      public formServerFeatures()
      {
         _iniPath = LocateIniFile();

         BuildUi();

         if (_iniPath != null)
            LoadSettings();
      }

      public static string LocateIniFile()
      {
         foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
         {
            try
            {
               using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
               using (RegistryKey key = baseKey.OpenSubKey(@"SOFTWARE\hMailServer"))
               {
                  string installLocation = key?.GetValue("InstallLocation") as string;
                  if (string.IsNullOrEmpty(installLocation))
                     continue;

                  string iniPath = Path.Combine(installLocation, "Bin", "hMailServer.INI");
                  if (File.Exists(iniPath))
                     return iniPath;

                  // Older installations keep the INI in the root folder.
                  iniPath = Path.Combine(installLocation, "hMailServer.INI");
                  if (File.Exists(iniPath))
                     return iniPath;
               }
            }
            catch
            {
               // Try the next registry view.
            }
         }

         return null;
      }

      private string ReadIni(string key, string defaultValue)
      {
         var buffer = new StringBuilder(1024);
         GetPrivateProfileString(IniSection, key, defaultValue, buffer, buffer.Capacity, _iniPath);
         return buffer.ToString();
      }

      private bool WriteIni(string key, string value)
      {
         return WritePrivateProfileString(IniSection, key, value, _iniPath);
      }

      private void LoadSettings()
      {
         checkArc.Checked = ReadIni("ArcSealingEnabled", "0") == "1";
         checkMtaSts.Checked = ReadIni("MtaStsEnabled", "1") == "1";
         checkDane.Checked = ReadIni("DaneEnforcementEnabled", "1") == "1";
         checkDnssec.Checked = ReadIni("DnssecValidationEnabled", "1") == "1";
         checkJsonLogging.Checked = ReadIni("JsonLogging", "0") == "1";

         textTlsRptFrom.Text = ReadIni("TlsRptFromAddress", "");
         textTlsRptOrganization.Text = ReadIni("TlsRptOrganizationName", "hMailServer");

         textMetricsPort.Text = ReadIni("MetricsServerPort", "0");
         textMetricsBind.Text = ReadIni("MetricsServerBindAddress", "127.0.0.1");

         textRestPort.Text = ReadIni("RestApiPort", "0");
         textRestBind.Text = ReadIni("RestApiBindAddress", "127.0.0.1");
         textRestCert.Text = ReadIni("RestApiCertificateFile", "");
         textRestKey.Text = ReadIni("RestApiPrivateKeyFile", "");

         checkAcme.Checked = ReadIni("AcmeEnabled", "0") == "1";
         textAcmeEmail.Text = ReadIni("AcmeContactEmail", "");
         textAcmeDomains.Text = ReadIni("AcmeDomains", "");

         checkWebServices.Checked = ReadIni("WebServicesHttpPort", "0") != "0" || ReadIni("WebServicesHttpsPort", "0") != "0";
         textWebClientHost.Text = ReadIni("AutoconfigClientHost", "");
      }

      private bool ValidateInput()
      {
         if (!int.TryParse(textMetricsPort.Text.Trim(), out int metricsPort) || metricsPort < 0 || metricsPort > 65535)
         {
            MessageBox.Show(this, "The metrics port must be a number between 0 and 65535 (0 = disabled).",
               "hMailServer Administrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
         }

         if (!int.TryParse(textRestPort.Text.Trim(), out int restPort) || restPort < 0 || restPort > 65535)
         {
            MessageBox.Show(this, "The REST API port must be a number between 0 and 65535 (0 = disabled).",
               "hMailServer Administrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
         }

         if (checkAcme.Checked)
         {
            if (textAcmeDomains.Text.Trim().Length == 0)
            {
               MessageBox.Show(this, "Automatic certificates require at least one domain name (for example: mail.example.com).",
                  "hMailServer Administrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
               return false;
            }

            if (textAcmeEmail.Text.Trim().Length == 0 || !textAcmeEmail.Text.Contains("@"))
            {
               MessageBox.Show(this, "Automatic certificates require a valid contact email address (used for expiry notices from Let's Encrypt).",
                  "hMailServer Administrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
               return false;
            }
         }

         if (textTlsRptFrom.Text.Trim().Length > 0 && !textTlsRptFrom.Text.Contains("@"))
         {
            MessageBox.Show(this, "The TLS report sender must be a valid email address hosted on this server.",
               "hMailServer Administrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
         }

         return true;
      }

      private void OnSave(object sender, EventArgs e)
      {
         if (!ValidateInput())
            return;

         try
         {
            bool ok = true;

            ok &= WriteIni("ArcSealingEnabled", checkArc.Checked ? "1" : "0");
            ok &= WriteIni("MtaStsEnabled", checkMtaSts.Checked ? "1" : "0");
            ok &= WriteIni("DaneEnforcementEnabled", checkDane.Checked ? "1" : "0");
            ok &= WriteIni("DnssecValidationEnabled", checkDnssec.Checked ? "1" : "0");
            ok &= WriteIni("JsonLogging", checkJsonLogging.Checked ? "1" : "0");

            ok &= WriteIni("TlsRptFromAddress", textTlsRptFrom.Text.Trim());
            ok &= WriteIni("TlsRptOrganizationName", textTlsRptOrganization.Text.Trim());

            ok &= WriteIni("MetricsServerPort", textMetricsPort.Text.Trim());
            ok &= WriteIni("MetricsServerBindAddress", textMetricsBind.Text.Trim());

            ok &= WriteIni("RestApiPort", textRestPort.Text.Trim());
            ok &= WriteIni("RestApiBindAddress", textRestBind.Text.Trim());
            ok &= WriteIni("RestApiCertificateFile", textRestCert.Text.Trim());
            ok &= WriteIni("RestApiPrivateKeyFile", textRestKey.Text.Trim());

            ok &= WriteIni("AcmeEnabled", checkAcme.Checked ? "1" : "0");
            ok &= WriteIni("AcmeContactEmail", textAcmeEmail.Text.Trim());
            ok &= WriteIni("AcmeDomains", textAcmeDomains.Text.Trim());

            ok &= WriteIni("WebServicesHttpPort", checkWebServices.Checked ? "80" : "0");
            ok &= WriteIni("WebServicesHttpsPort", checkWebServices.Checked ? "443" : "0");
            ok &= WriteIni("AutoconfigClientHost", textWebClientHost.Text.Trim());

            if (!ok)
               throw new IOException("One or more settings could not be written.");
         }
         catch (Exception ex)
         {
            MessageBox.Show(this,
               "The settings could not be saved: " + ex.Message + "\r\n\r\n" +
               "Run hMailServer Administrator as administrator and try again.",
               "hMailServer Administrator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
         }

         OfferServiceRestart();

         DialogResult = DialogResult.OK;
         Close();
      }

      private void OfferServiceRestart()
      {
         DialogResult result = MessageBox.Show(this,
            "The settings have been saved. They take effect the next time the hMailServer service starts.\r\n\r\n" +
            "Restart the hMailServer service now?",
            "hMailServer Administrator", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

         if (result != DialogResult.Yes)
            return;

         try
         {
            Cursor = Cursors.WaitCursor;

            using (var service = new ServiceController("hMailServer"))
            {
               if (service.Status == ServiceControllerStatus.Running)
               {
                  service.Stop();
                  service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
               }

               service.Start();
               service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            }

            MessageBox.Show(this, "The hMailServer service has been restarted.",
               "hMailServer Administrator", MessageBoxButtons.OK, MessageBoxIcon.Information);
         }
         catch (Exception ex)
         {
            MessageBox.Show(this,
               "The service could not be restarted automatically: " + ex.Message + "\r\n\r\n" +
               "Restart the hMailServer service manually (services.msc) to apply the new settings.",
               "hMailServer Administrator", MessageBoxButtons.OK, MessageBoxIcon.Warning);
         }
         finally
         {
            Cursor = Cursors.Default;
         }
      }

      private void BuildUi()
      {
         Text = "Server features";
         FormBorderStyle = FormBorderStyle.FixedDialog;
         MaximizeBox = false;
         MinimizeBox = false;
         StartPosition = FormStartPosition.CenterParent;
         ClientSize = new Size(520, 596);
         Font = new Font("Microsoft Sans Serif", 8.25F);

         if (_iniPath == null)
         {
            var labelUnavailable = new Label
            {
               Text = "hMailServer.INI was not found on this computer.\r\n\r\n" +
                      "These settings can only be changed when hMailServer Administrator runs " +
                      "on the server itself.",
               Location = new Point(16, 16),
               Size = new Size(488, 80)
            };

            var buttonClose = new Button
            {
               Text = "Close",
               DialogResult = DialogResult.Cancel,
               Location = new Point(416, 104),
               Size = new Size(88, 25)
            };

            Controls.Add(labelUnavailable);
            Controls.Add(buttonClose);
            CancelButton = buttonClose;
            ClientSize = new Size(520, 144);
            return;
         }

         int y = 12;

         // --- Email authentication & transport security ---
         var groupSecurity = new GroupBox
         {
            Text = "Email authentication and transport security",
            Location = new Point(12, y),
            Size = new Size(496, 146)
         };

         checkArc = new CheckBox
         {
            Text = "Add ARC seals to delivered email (recommended when forwarding; uses each domain's DKIM key)",
            Location = new Point(14, 22),
            Size = new Size(470, 18)
         };

         checkMtaSts = new CheckBox
         {
            Text = "Honor recipient MTA-STS policies when sending (recommended)",
            Location = new Point(14, 46),
            Size = new Size(470, 18)
         };

         checkDane = new CheckBox
         {
            Text = "Honor recipient DANE/TLSA records when sending (recommended)",
            Location = new Point(14, 70),
            Size = new Size(470, 18)
         };

         checkDnssec = new CheckBox
         {
            Text = "Validate DNSSEC for DANE lookups (recommended; blocks forged TLSA records)",
            Location = new Point(14, 94),
            Size = new Size(470, 18)
         };

         checkJsonLogging = new CheckBox
         {
            Text = "Write logs in JSON format (for log collectors such as Elastic or Loki)",
            Location = new Point(14, 118),
            Size = new Size(470, 18)
         };

         groupSecurity.Controls.AddRange(new Control[] { checkArc, checkMtaSts, checkDane, checkDnssec, checkJsonLogging });
         y += 154;

         // --- TLS reporting ---
         var groupTlsRpt = new GroupBox
         {
            Text = "Outgoing TLS reports (TLS-RPT)",
            Location = new Point(12, y),
            Size = new Size(496, 86)
         };

         groupTlsRpt.Controls.Add(new Label { Text = "Send reports from:", Location = new Point(14, 25), Size = new Size(130, 16) });
         textTlsRptFrom = new TextBox { Location = new Point(150, 22), Size = new Size(330, 21) };

         groupTlsRpt.Controls.Add(new Label { Text = "Organization name:", Location = new Point(14, 53), Size = new Size(130, 16) });
         textTlsRptOrganization = new TextBox { Location = new Point(150, 50), Size = new Size(330, 21) };

         groupTlsRpt.Controls.Add(textTlsRptFrom);
         groupTlsRpt.Controls.Add(textTlsRptOrganization);

         toolTip = new ToolTip();
         toolTip.SetToolTip(textTlsRptFrom, "Example: tlsrpt@yourdomain.com. Leave empty to disable daily TLS reports.");

         y += 94;

         // --- Automatic certificates ---
         var groupAcme = new GroupBox
         {
            Text = "Automatic SSL certificates (Let's Encrypt)",
            Location = new Point(12, y),
            Size = new Size(496, 118)
         };

         checkAcme = new CheckBox
         {
            Text = "Get and renew certificates automatically (port 80 must be reachable from the internet)",
            Location = new Point(14, 22),
            Size = new Size(470, 18)
         };

         groupAcme.Controls.Add(checkAcme);
         groupAcme.Controls.Add(new Label { Text = "Contact email:", Location = new Point(14, 51), Size = new Size(130, 16) });
         textAcmeEmail = new TextBox { Location = new Point(150, 48), Size = new Size(330, 21) };

         groupAcme.Controls.Add(new Label { Text = "Host names:", Location = new Point(14, 79), Size = new Size(130, 16) });
         textAcmeDomains = new TextBox { Location = new Point(150, 76), Size = new Size(330, 21) };

         groupAcme.Controls.Add(textAcmeEmail);
         groupAcme.Controls.Add(textAcmeDomains);

         toolTip.SetToolTip(textAcmeDomains, "Comma-separated, for example: mail.example.com, mail.example.org");

         y += 126;

         // --- Web services ---
         var groupWeb = new GroupBox
         {
            Text = "Web services (MTA-STS policies, mail client autoconfig)",
            Location = new Point(12, y),
            Size = new Size(496, 86)
         };

         checkWebServices = new CheckBox
         {
            Text = "Serve MTA-STS policies, Thunderbird autoconfig and Outlook autodiscover (ports 80/443)",
            Location = new Point(14, 22),
            Size = new Size(470, 18)
         };

         groupWeb.Controls.Add(checkWebServices);
         groupWeb.Controls.Add(new Label { Text = "Mail host name:", Location = new Point(14, 51), Size = new Size(130, 16) });
         textWebClientHost = new TextBox { Location = new Point(150, 48), Size = new Size(330, 21) };
         groupWeb.Controls.Add(textWebClientHost);

         toolTip.SetToolTip(checkWebServices, "Requires DNS records pointing at this server: mta-sts.<domain>, autoconfig.<domain> and autodiscover.<domain>. Add those names to the certificate host names above for HTTPS.");
         toolTip.SetToolTip(textWebClientHost, "The host name mail clients should connect to, for example mail.example.com. Leave empty to use the server's host name.");

         y += 94;

         // --- REST API ---
         var groupRest = new GroupBox
         {
            Text = "REST administration API",
            Location = new Point(12, y),
            Size = new Size(496, 142)
         };

         groupRest.Controls.Add(new Label { Text = "Port (0 = disabled):", Location = new Point(14, 25), Size = new Size(130, 16) });
         textRestPort = new TextBox { Location = new Point(150, 22), Size = new Size(80, 21) };

         groupRest.Controls.Add(new Label { Text = "Listen on address:", Location = new Point(14, 53), Size = new Size(130, 16) });
         textRestBind = new TextBox { Location = new Point(150, 50), Size = new Size(150, 21) };

         groupRest.Controls.Add(new Label { Text = "Certificate file (PEM):", Location = new Point(14, 81), Size = new Size(130, 16) });
         textRestCert = new TextBox { Location = new Point(150, 78), Size = new Size(330, 21) };

         groupRest.Controls.Add(new Label { Text = "Private key file (PEM):", Location = new Point(14, 109), Size = new Size(130, 16) });
         textRestKey = new TextBox { Location = new Point(150, 106), Size = new Size(330, 21) };

         groupRest.Controls.AddRange(new Control[] { textRestPort, textRestBind, textRestCert, textRestKey });

         toolTip.SetToolTip(textRestCert, "Leave empty to automatically use the Let's Encrypt certificate, or to allow plain HTTP on 127.0.0.1 only.");

         y += 150;

         // --- Monitoring ---
         var groupMetrics = new GroupBox
         {
            Text = "Monitoring (Prometheus metrics)",
            Location = new Point(12, y),
            Size = new Size(496, 58)
         };

         groupMetrics.Controls.Add(new Label { Text = "Port (0 = disabled):", Location = new Point(14, 25), Size = new Size(130, 16) });
         textMetricsPort = new TextBox { Location = new Point(150, 22), Size = new Size(80, 21) };

         groupMetrics.Controls.Add(new Label { Text = "Listen on address:", Location = new Point(250, 25), Size = new Size(110, 16) });
         textMetricsBind = new TextBox { Location = new Point(360, 22), Size = new Size(120, 21) };

         groupMetrics.Controls.Add(textMetricsPort);
         groupMetrics.Controls.Add(textMetricsBind);

         y += 66;

         buttonSave = new Button
         {
            Text = "Save...",
            Location = new Point(330, y),
            Size = new Size(85, 25)
         };
         buttonSave.Click += OnSave;

         buttonCancel = new Button
         {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(423, y),
            Size = new Size(85, 25)
         };

         Controls.AddRange(new Control[] { groupSecurity, groupTlsRpt, groupAcme, groupWeb, groupRest, groupMetrics, buttonSave, buttonCancel });

         AcceptButton = buttonSave;
         CancelButton = buttonCancel;

         ClientSize = new Size(520, y + 37);
      }
   }
}
