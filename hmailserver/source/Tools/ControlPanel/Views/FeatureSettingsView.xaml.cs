using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Data-driven settings pages for the hMailServer.INI feature switches:
   /// transport security (DANE, DNSSEC, MTA-STS, ARC, TLS-RPT), automatic
   /// certificates (ACME) and integrations (REST API, web services,
   /// Prometheus metrics, JSON logging).
   /// </summary>
   public partial class FeatureSettingsView : UserControl, IPageLifecycle
   {
      public enum Section
      {
         Security,
         Automation,
         Integration,
         Hardening
      }

      private abstract class Setting
      {
         public string Key;
         public string Label;
         public abstract FrameworkElement CreateEditor(IniFeatureStore store);
         public abstract void Save(IniFeatureStore store);

         protected static void SetAid(FrameworkElement element, string id)
         {
            if (element != null && !string.IsNullOrEmpty(id))
               System.Windows.Automation.AutomationProperties.SetAutomationId(element, id);
         }
      }

      private class BoolSetting : Setting
      {
         public bool Default;
         private CheckBox box_;

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            box_ = new CheckBox
            {
               Content = Label,
               IsChecked = store.ReadBool(Key, Default),
               FontSize = 13.5
            };
            SetAid(box_, Key);
            return box_;
         }

         public override void Save(IniFeatureStore store)
            => store.WriteBool(Key, box_.IsChecked == true);
      }

      private class TextSetting : Setting
      {
         public string Default = "";
         public string Placeholder = "";
         private Wpf.Ui.Controls.TextBox box_;

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
               Text = Label,
               FontSize = 13,
               Margin = new Thickness(0, 0, 0, 4)
            });
            box_ = new Wpf.Ui.Controls.TextBox
            {
               Text = store.Read(Key, Default),
               PlaceholderText = Placeholder,
               FontSize = 13
            };
            SetAid(box_, Key);
            panel.Children.Add(box_);
            return panel;
         }

         public override void Save(IniFeatureStore store)
            => store.Write(Key, box_.Text.Trim());
      }

      private class ChoiceSetting : Setting
      {
         public int Default;
         public (int Value, string Label)[] Options;
         private ComboBox combo_;

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
               Text = Label,
               FontSize = 13,
               Margin = new Thickness(0, 0, 0, 4)
            });

            combo_ = new ComboBox { FontSize = 13, MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
            if (!int.TryParse(store.Read(Key, Default.ToString()), out int current))
               current = Default;
            foreach ((int value, string label) in Options)
            {
               var item = new ComboBoxItem { Content = label, Tag = value };
               combo_.Items.Add(item);
               if (value == current)
                  combo_.SelectedItem = item;
            }
            if (combo_.SelectedItem == null && combo_.Items.Count > 0)
               combo_.SelectedIndex = 0;

            SetAid(combo_, Key);
            panel.Children.Add(combo_);
            return panel;
         }

         public override void Save(IniFeatureStore store)
         {
            int value = combo_.SelectedItem is ComboBoxItem cbi ? (int) cbi.Tag : Default;
            store.Write(Key, value.ToString());
         }
      }

      private class CardDef
      {
         public string Title;
         public string Blurb;
         public List<Setting> Settings = new();
      }

      private readonly Section section_;
      private readonly IniFeatureStore store_ = new();
      private List<CardDef> cards_;

      public FeatureSettingsView(Section section)
      {
         InitializeComponent();
         section_ = section;
         BuildDefinition();
         BuildUi();
      }

      public void OnEnter()
      {
         // The page instance is cached by MainWindow, so re-read the INI from disk
         // on every navigation. This keeps the editors in sync with values changed
         // elsewhere (a prior save, another tool, or a hand edit) instead of showing
         // stale data captured when the page was first constructed.
         BuildDefinition();
         BuildUi();
      }

      public void OnLeave()
      {
      }

      private void BuildDefinition()
      {
         cards_ = new List<CardDef>();

         switch (section_)
         {
            case Section.Security:
               TitleText.Text = "Transport security";
               SubtitleText.Text = "Outbound mail authentication and encryption policies (hMailServer.INI). " +
                                   "Changes take effect after a service restart.";
               cards_.Add(new CardDef
               {
                  Title = "DANE & DNSSEC",
                  Blurb = "Validates recipient TLSA records with in-process DNSSEC and blocks delivery over forged chains (RFC 7672).",
                  Settings =
                  {
                     new BoolSetting { Key = "DaneEnforcementEnabled", Default = true, Label = "Honor recipient DANE/TLSA records when sending" },
                     new BoolSetting { Key = "DnssecValidationEnabled", Default = true, Label = "Validate DNSSEC for DANE and SPF/DKIM/DMARC lookups" },
                     new TextSetting { Key = "DnssecTrustAnchors", Label = "Trust anchor override (tag alg digesttype hex; ...)", Placeholder = "Leave empty for the built-in root anchors" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "MTA-STS",
                  Blurb = "Discovers and enforces recipient MTA-STS policies before delivering over TLS (RFC 8461).",
                  Settings =
                  {
                     new BoolSetting { Key = "MtaStsEnabled", Default = true, Label = "Honor recipient MTA-STS policies when sending" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "ARC sealing",
                  Blurb = "Adds ARC seals to forwarded mail so downstream servers can trust original authentication results (RFC 8617).",
                  Settings =
                  {
                     new BoolSetting { Key = "ArcSealingEnabled", Default = false, Label = "Seal forwarded messages with the domain's DKIM key" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "TLS reporting (TLS-RPT)",
                  Blurb = "Sends daily aggregate reports about TLS connection failures to recipient domains (RFC 8460).",
                  Settings =
                  {
                     new TextSetting { Key = "TlsRptFromAddress", Label = "Report sender address (empty = disabled)", Placeholder = "tlsrpt@yourdomain.com" },
                     new TextSetting { Key = "TlsRptOrganizationName", Default = "hMailServer", Label = "Organization name in reports" }
                  }
               });
               break;

            case Section.Automation:
               TitleText.Text = "Automatic certificates (ACME)";
               SubtitleText.Text = "Built-in Let's Encrypt integration: certificates are issued, renewed, " +
                                   "assigned to TLS ports and hot-reloaded automatically.";
               cards_.Add(new CardDef
               {
                  Title = "ACME (Let's Encrypt)",
                  Blurb = "Issued certificates are stored in Data\\ACME and assigned to TLS ports without a restart. " +
                          "Key reuse keeps published DANE TLSA records valid across renewals.",
                  Settings =
                  {
                     new BoolSetting { Key = "AcmeEnabled", Default = false, Label = "Issue and renew certificates automatically" },
                     new TextSetting { Key = "AcmeContactEmail", Label = "Contact e-mail (CA expiry notices)", Placeholder = "admin@yourdomain.com" },
                     new TextSetting { Key = "AcmeDomains", Label = "Host names for the certificate (comma separated)", Placeholder = "mail.yourdomain.com, mta-sts.yourdomain.com" },
                     new TextSetting { Key = "AcmeDirectoryUrl", Default = "https://acme-v02.api.letsencrypt.org/directory", Label = "ACME directory URL" },
                     new TextSetting { Key = "AcmeHttpPort", Default = "80", Label = "Port for http-01 challenges" },
                     new TextSetting { Key = "AcmeCertificateDirectory", Label = "Certificate output folder (empty = Data\\ACME)", Placeholder = "Falls back to Data\\ACME" },
                     new BoolSetting { Key = "AcmeReuseKey", Default = true, Label = "Reuse the private key across renewals (keeps DANE TLSA records valid)" }
                  }
               });
               break;

            case Section.Integration:
               TitleText.Text = "API & monitoring";
               SubtitleText.Text = "REST administration API, web services hosting, Prometheus metrics and structured logging.";
               cards_.Add(new CardDef
               {
                  Title = "REST administration API + Web Control Deck",
                  Blurb = "JSON API under /api/v1 plus the browser-based Control Deck at the listener root. " +
                          "HTTP Basic authentication with the administrator password; TLS required unless bound to 127.0.0.1.",
                  Settings =
                  {
                     new TextSetting { Key = "RestApiPort", Default = "0", Label = "Port (0 = disabled)", Placeholder = "8045" },
                     new TextSetting { Key = "RestApiBindAddress", Default = "127.0.0.1", Label = "Bind address" },
                     new TextSetting { Key = "RestApiCertificateFile", Label = "TLS certificate file (PEM, optional)", Placeholder = "Falls back to the ACME certificate" },
                     new TextSetting { Key = "RestApiPrivateKeyFile", Label = "TLS private key file (PEM, optional)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Web services (MTA-STS hosting & client autoconfig)",
                  Blurb = "Hosts https://mta-sts.<domain>/.well-known/mta-sts.txt, Thunderbird autoconfig and Outlook autodiscover for all local domains.",
                  Settings =
                  {
                     new TextSetting { Key = "WebServicesHttpPort", Default = "0", Label = "HTTP port (80 to enable, 0 = disabled)" },
                     new TextSetting { Key = "WebServicesHttpsPort", Default = "0", Label = "HTTPS port (443 to enable, 0 = disabled)" },
                     new TextSetting { Key = "WebServicesBindAddress", Default = "0.0.0.0", Label = "Bind address" },
                     new TextSetting { Key = "WebServicesCertificateFile", Label = "TLS certificate file (PEM, optional)", Placeholder = "Falls back to the ACME certificate" },
                     new TextSetting { Key = "WebServicesPrivateKeyFile", Label = "TLS private key file (PEM, optional)" },
                     new BoolSetting { Key = "MtaStsHostingEnabled", Default = true, Label = "Serve MTA-STS policies for local domains" },
                     new TextSetting { Key = "MtaStsPolicyMode", Default = "enforce", Label = "MTA-STS policy mode (enforce / testing / none)" },
                     new TextSetting { Key = "MtaStsPolicyMaxAge", Default = "604800", Label = "Policy max age (seconds; default 604800 = 7 days)" },
                     new TextSetting { Key = "MtaStsPolicyMx", Label = "Policy MX host patterns (empty = derive from each domain's MX)", Placeholder = "mail.yourdomain.com, *.yourdomain.com" },
                     new BoolSetting { Key = "AutoconfigEnabled", Default = true, Label = "Thunderbird autoconfig + Outlook autodiscover" },
                     new TextSetting { Key = "AutoconfigClientHost", Label = "Host name clients connect to (empty = server host name)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Monitoring",
                  Blurb = "Prometheus metrics endpoint (/metrics) and JSON-structured log output for log aggregators.",
                  Settings =
                  {
                     new TextSetting { Key = "MetricsServerPort", Default = "0", Label = "Metrics port (0 = disabled)", Placeholder = "9090" },
                     new TextSetting { Key = "MetricsServerBindAddress", Default = "127.0.0.1", Label = "Metrics bind address" },
                     new BoolSetting { Key = "JsonLogging", Default = false, Label = "Write logs as JSON lines" }
                  }
               });
               break;

            case Section.Hardening:
               TitleText.Text = "Advanced hardening";
               SubtitleText.Text = "Lower-level hMailServer.INI [Settings] knobs that are not exposed on the other " +
                                   "pages. The defaults are safe; change these only with a specific reason. " +
                                   "Changes take effect after a service restart.";
               cards_.Add(new CardDef
               {
                  Title = "Greylisting",
                  Blurb = "Behaviour while a greylisting triplet is still within its expiration window.",
                  Settings =
                  {
                     new BoolSetting { Key = "GreylistingEnabledDuringRecordExpiration", Default = true, Label = "Keep greylisting active during the record-expiration window" },
                     new TextSetting { Key = "GreylistingRecordExpirationInterval", Default = "240", Label = "Record expiration interval (minutes, default 240)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Scanner timeouts",
                  Blurb = "Per-message minimum and maximum wait for the external SpamAssassin and ClamAV scanners (seconds).",
                  Settings =
                  {
                     new TextSetting { Key = "SAMinTimeout", Default = "30", Label = "SpamAssassin minimum timeout (s)" },
                     new TextSetting { Key = "SAMaxTimeout", Default = "90", Label = "SpamAssassin maximum timeout (s)" },
                     new TextSetting { Key = "ClamMinTimeout", Default = "15", Label = "ClamAV minimum timeout (s)" },
                     new TextSetting { Key = "ClamMaxTimeout", Default = "90", Label = "ClamAV maximum timeout (s)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "DNS",
                  Blurb = "Resolver behaviour for MX / SPF / DNSBL lookups.",
                  Settings =
                  {
                     new BoolSetting { Key = "UseDNSCache", Default = true, Label = "Cache DNS lookups in-process" },
                     new TextSetting { Key = "DNSServer", Label = "Override DNS server (empty = system resolvers)", Placeholder = "1.1.1.1" },
                     new BoolSetting { Key = "DNSBLChecksAfterMailFrom", Default = true, Label = "Run DNSBL checks after MAIL FROM (rather than at connect)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Authentication & received headers",
                  Blurb = "Submission identity handling and the diagnostic headers added to received mail.",
                  Settings =
                  {
                     new TextSetting { Key = "AuthUserReplacementIP", Label = "Replace the client IP for authenticated users (empty = keep the real IP)", Placeholder = "e.g. 127.0.0.1" },
                     new TextSetting { Key = "DisableAUTHList", Label = "Disable AUTH for these IPs / ranges (semicolon separated)", Placeholder = "203.0.113.0/24; 198.51.100.7" },
                     new BoolSetting { Key = "AddXAuthUserHeader", Default = false, Label = "Add an X-AuthUser header with the authenticated account" },
                     new BoolSetting { Key = "AddXAuthUserIP", Default = true, Label = "Include the client IP in the X-AuthUser header" },
                     new BoolSetting { Key = "AddXOriginalRcptTo", Default = false, Label = "Add an X-OriginalRcptTo header" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Other",
                  Blurb = "Miscellaneous hardening knobs.",
                  Settings =
                  {
                     new TextSetting { Key = "BlockedIPHoldSeconds", Default = "0", Label = "Hold a blocked connection open before dropping it (seconds, 0 = drop immediately)" },
                     new BoolSetting { Key = "RewriteEnvelopeFromWhenForwarding", Default = false, Label = "Rewrite the envelope sender when forwarding (helps SPF; see also SRS)" },
                     new ChoiceSetting
                     {
                        Key = "PreferredHashAlgorithm",
                        Default = 4,
                        Label = "Password hash for new or changed account passwords",
                        Options = new (int, string)[]
                        {
                           (5, "Argon2id (strongest)"),
                           (4, "PBKDF2 (default)"),
                           (3, "SHA-256"),
                           (2, "MD5 (legacy)"),
                           (1, "Blowfish (legacy)")
                        }
                     }
                  }
               });
               break;
         }
      }

      private void BuildUi()
      {
         CardsPanel.Children.Clear();

         if (!store_.IsAvailable)
         {
            SubtitleText.Text = "hMailServer.INI was not found on this machine. " +
                                "These settings can only be edited on the server itself.";
            SaveButton.IsEnabled = false;
            return;
         }

         foreach (CardDef card in cards_)
         {
            var border = new Border { Margin = new Thickness(0, 0, 0, 12) };
            border.SetResourceReference(StyleProperty, "Card");

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
               Text = card.Title,
               FontSize = 15,
               FontWeight = FontWeights.SemiBold,
               Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(new TextBlock
            {
               Text = card.Blurb,
               FontSize = 12,
               TextWrapping = TextWrapping.Wrap,
               Opacity = 0.65,
               Margin = new Thickness(0, 0, 0, 14)
            });

            FrameworkElement lastEditor = null;
            foreach (Setting setting in card.Settings)
            {
               FrameworkElement editor = setting.CreateEditor(store_);
               editor.Margin = new Thickness(0, 0, 0, 12);
               panel.Children.Add(editor);
               lastEditor = editor;
            }

            if (lastEditor != null)
               lastEditor.Margin = new Thickness(0, 0, 0, 2);

            border.Child = panel;
            CardsPanel.Children.Add(border);
         }

         StatusText.Text = "Editing " + store_.IniPath;
      }

      private void Reload_Click(object sender, RoutedEventArgs e)
      {
         BuildDefinition();
         BuildUi();
      }

      private void Save_Click(object sender, RoutedEventArgs e)
      {
         try
         {
            foreach (CardDef card in cards_)
               foreach (Setting setting in card.Settings)
                  setting.Save(store_);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save: " + ex.Message, "Control Panel",
               MessageBoxButton.OK, MessageBoxImage.Error);
            return;
         }

         StatusText.Text = "Saved " + DateTime.Now.ToLongTimeString() + " - restart the service to apply.";

         if (MessageBox.Show(
                "Settings saved. The hMailServer service must be restarted for the changes to take effect.\n\nRestart it now?",
                "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
         {
            RestartService();
         }
      }

      private void RestartService()
      {
         try
         {
            using var controller = new ServiceController("hMailServer");
            if (controller.Status == ServiceControllerStatus.Running)
            {
               controller.Stop();
               controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
            }
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            StatusText.Text = "Service restarted - settings are live.";
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not restart the service (try an elevated session): " + ex.Message,
               "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
         }
      }
   }
}
