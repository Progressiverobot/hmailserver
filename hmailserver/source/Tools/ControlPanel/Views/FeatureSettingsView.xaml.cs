using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using hMailServer.ControlPanel.Services;
using System.Linq;

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
               FontSize = 13,
               MaxWidth = 520,
               MinWidth = 320,
               HorizontalAlignment = HorizontalAlignment.Left
            };
            SetAid(box_, Key);
            panel.Children.Add(box_);
            return panel;
         }

         public override void Save(IniFeatureStore store)
            => store.Write(Key, box_.Text.Trim());
      }

      /// <summary>
      /// A path field: a text box plus a "..." button that opens a file or folder
      /// picker (<see cref="PickFolder"/>) and writes the chosen path back into the box.
      /// </summary>
      private class PathSetting : Setting
      {
         public readonly string Default = "";
         public string Placeholder = "";
         public bool PickFolder;
         public string FileFilter = "All files (*.*)|*.*";
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

            var row = new Grid { Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            box_ = new Wpf.Ui.Controls.TextBox
            {
               Text = store.Read(Key, Default),
               PlaceholderText = Placeholder,
               FontSize = 13,
               HorizontalAlignment = HorizontalAlignment.Stretch
            };
            SetAid(box_, Key);
            Grid.SetColumn(box_, 0);
            row.Children.Add(box_);

            var browse = new Wpf.Ui.Controls.Button
            {
               Content = "\u2026",
               MinWidth = 40,
               Margin = new Thickness(8, 0, 0, 0),
               VerticalAlignment = VerticalAlignment.Bottom,
               ToolTip = PickFolder ? "Browse for a folder" : "Browse for a file"
            };
            SetAid(browse, Key + "Browse");
            browse.Click += (s, e) =>
            {
               string picked = PickFolder
                  ? PathPicker.PickFolder(box_.Text)
                  : PathPicker.PickFile(box_.Text, FileFilter);
               if (picked != null)
                  box_.Text = picked;
            };
            Grid.SetColumn(browse, 1);
            row.Children.Add(browse);

            panel.Children.Add(row);
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

      /// <summary>
      /// A write-only secret field. The current value is never shown (it may be a
      /// DPAPI-protected blob); a placeholder indicates whether one is already set,
      /// and the value is only written when the admin types a new one — so leaving
      /// the field blank keeps the existing secret untouched.
      /// </summary>
      private class SecretSetting : Setting
      {
         public string Note = "";
         private Wpf.Ui.Controls.PasswordBox box_;

         public override FrameworkElement CreateEditor(IniFeatureStore store)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });

            bool hasExisting = !string.IsNullOrEmpty(store.Read(Key, "").Trim());
            box_ = new Wpf.Ui.Controls.PasswordBox
            {
               PlaceholderText = hasExisting
                  ? "A secret is configured — leave blank to keep it"
                  : (string.IsNullOrEmpty(Note) ? "Enter a secret" : Note),
               FontSize = 13,
               MaxWidth = 520,
               MinWidth = 320,
               HorizontalAlignment = HorizontalAlignment.Left
            };
            SetAid(box_, Key);
            panel.Children.Add(box_);
            return panel;
         }

         public override void Save(IniFeatureStore store)
         {
            string entered = box_.Password;
            if (!string.IsNullOrEmpty(entered))
               store.Write(Key, entered);
            // Blank = keep the existing secret.
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
                     new PathSetting { Key = "AcmeCertificateDirectory", PickFolder = true, Label = "Certificate output folder (empty = Data\\ACME)", Placeholder = "Falls back to Data\\ACME" },
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
                     new PathSetting { Key = "RestApiCertificateFile", FileFilter = "PEM/certificate files (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*", Label = "TLS certificate file (PEM, optional)", Placeholder = "Falls back to the ACME certificate" },
                     new PathSetting { Key = "RestApiPrivateKeyFile", FileFilter = "PEM/key files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*", Label = "TLS private key file (PEM, optional)" }
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
                     new PathSetting { Key = "WebServicesCertificateFile", FileFilter = "PEM/certificate files (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*", Label = "TLS certificate file (PEM, optional)", Placeholder = "Falls back to the ACME certificate" },
                     new PathSetting { Key = "WebServicesPrivateKeyFile", FileFilter = "PEM/key files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*", Label = "TLS private key file (PEM, optional)" },
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
                  Blurb = "Prometheus metrics (/metrics), OpenTelemetry traces/metrics export, a slow-query log and JSON-structured log output for log aggregators.",
                  Settings =
                  {
                     new TextSetting { Key = "MetricsServerPort", Default = "0", Label = "Metrics port (0 = disabled)", Placeholder = "9090" },
                     new TextSetting { Key = "MetricsServerBindAddress", Default = "127.0.0.1", Label = "Metrics bind address" },
                     new BoolSetting { Key = "JsonLogging", Default = false, Label = "Write logs as JSON lines" },
                     new TextSetting { Key = "OtelEndpoint", Label = "OpenTelemetry OTLP endpoint (empty = disabled)", Placeholder = "http://localhost:4318" },
                     new TextSetting { Key = "OtelServiceName", Default = "hmailserver", Label = "OpenTelemetry service name" },
                     new TextSetting { Key = "SlowQueryLogMilliseconds", Default = "0", Label = "Log database queries slower than N ms (0 = off)", Placeholder = "250" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "OAuth2 / external token authentication",
                  Blurb = "Accept OAuth2 / OpenID Connect bearer tokens (XOAUTH2) from an external identity provider for IMAP, POP3 and SMTP submission, validated against the issuer's signing key.",
                  Settings =
                  {
                     new BoolSetting { Key = "OAuth2Enabled", Default = false, Label = "Accept OAuth2 bearer tokens (XOAUTH2)" },
                     new BoolSetting { Key = "OAuth2RequireTLS", Default = true, Label = "Require TLS for token authentication" },
                     new TextSetting { Key = "OAuth2Issuer", Label = "Expected token issuer (iss)", Placeholder = "https://login.microsoftonline.com/<tenant>/v2.0" },
                     new TextSetting { Key = "OAuth2Audience", Label = "Expected audience (aud)", Placeholder = "your application / client id" },
                     new TextSetting { Key = "OAuth2AllowedAlgorithms", Default = "RS256", Label = "Allowed signing algorithms (comma separated)", Placeholder = "RS256, ES256" },
                     new TextSetting { Key = "OAuth2UsernameClaim", Default = "email", Label = "Claim that holds the mailbox address", Placeholder = "email" },
                     new PathSetting { Key = "OAuth2PublicKeyFile", FileFilter = "PEM/key files (*.pem;*.crt;*.cer;*.key;*.pub)|*.pem;*.crt;*.cer;*.key;*.pub|All files (*.*)|*.*", Label = "RSA/EC public key file (PEM, for RS*/ES* tokens)", Placeholder = "Path to the issuer's public key" },
                     new SecretSetting { Key = "OAuth2HmacSecret", Label = "Shared HMAC secret (only for HS256/384/512 tokens)", Note = "Only needed for HS* algorithms" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "ManageSieve (RFC 5804)",
                  Blurb = "Lets mail clients upload and manage per-account Sieve filter scripts over TCP. " +
                          "Authentication is SASL PLAIN against the account database; bind to 127.0.0.1 unless fronted by a TLS terminator.",
                  Settings =
                  {
                     new TextSetting { Key = "ManageSieveServerPort", Default = "0", Label = "ManageSieve port (0 = disabled)", Placeholder = "4190" },
                     new TextSetting { Key = "ManageSieveServerBindAddress", Default = "127.0.0.1", Label = "ManageSieve bind address" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Operability",
                  Blurb = "Log retention and graceful shutdown behaviour for unattended / clustered operation.",
                  Settings =
                  {
                     new TextSetting { Key = "LogDeleteDays", Default = "0", Label = "Delete own logs older than N days (0 = keep all)", Placeholder = "30" },
                     new TextSetting { Key = "ShutdownDrainSeconds", Default = "0", Label = "On stop, wait up to N seconds for active sessions to finish (0 = stop immediately)", Placeholder = "30" }
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
                     },
                     new ChoiceSetting
                     {
                        Key = "MinimumAcceptedHashAlgorithm",
                        Default = 0,
                        Label = "Reject logins using a weaker stored hash than",
                        Options = new (int, string)[]
                        {
                           (0, "Accept any stored hash"),
                           (3, "SHA-256 or stronger"),
                           (4, "PBKDF2 or stronger"),
                           (5, "Argon2id only")
                        }
                     }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Message store durability",
                  Blurb = "Crash-durability barrier and an optional integrity scan for the on-disk message store. " +
                          "The defaults preserve the previous behaviour; enabling fsync adds a small per-message cost.",
                  Settings =
                  {
                     new BoolSetting { Key = "MessageStoreFsync", Default = false, Label = "Flush each received message to disk before it is acknowledged (durable, slower)" },
                     new BoolSetting { Key = "MessageStoreConsistencyCheck", Default = false, Label = "Periodically cross-check message rows against files on disk (read-only; writes a report on divergence)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Sender rewriting & bounce protection (SRS / BATV)",
                  Blurb = "SRS rewrites the envelope sender when forwarding so SPF still passes at the next hop; BATV tags outbound envelope senders so forged bounces can be rejected. Both use a server-wide secret.",
                  Settings =
                  {
                     new BoolSetting { Key = "SRSEnabled", Default = false, Label = "Enable Sender Rewriting Scheme (SRS) on forwarded mail" },
                     new SecretSetting { Key = "SRSSecret", Label = "SRS signing secret", Note = "A random server-wide secret" },
                     new BoolSetting { Key = "BATVEnabled", Default = false, Label = "Tag outbound envelope senders with BATV and validate returning bounces" },
                     new SecretSetting { Key = "BATVSecret", Label = "BATV signing secret", Note = "A random server-wide secret" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Submission & delivery rate limits",
                  Blurb = "Throttle abusive senders. Limits are per minute; 0 disables the limit.",
                  Settings =
                  {
                     new TextSetting { Key = "MaxSubmissionsPerIPPerMinute", Default = "0", Label = "Max authenticated submissions per client IP per minute (0 = unlimited)", Placeholder = "60" },
                     new TextSetting { Key = "MaxOutboundPerDestinationPerMinute", Default = "0", Label = "Max outbound messages per destination domain per minute (0 = unlimited)", Placeholder = "100" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Connection timeouts",
                  Blurb = "Idle timeouts (seconds) per protocol. 'Server' = hMailServer accepting connections; 'client' = hMailServer connecting out for delivery / external fetch.",
                  Settings =
                  {
                     new TextSetting { Key = "SMTPDMinTimeout", Default = "10", Label = "SMTP server minimum timeout (s)" },
                     new TextSetting { Key = "SMTPDMaxTimeout", Default = "1800", Label = "SMTP server maximum timeout (s)" },
                     new TextSetting { Key = "SMTPCMinTimeout", Default = "30", Label = "SMTP client minimum timeout (s)" },
                     new TextSetting { Key = "SMTPCMaxTimeout", Default = "600", Label = "SMTP client maximum timeout (s)" },
                     new TextSetting { Key = "POP3DMinTimeout", Default = "10", Label = "POP3 server minimum timeout (s)" },
                     new TextSetting { Key = "POP3DMaxTimeout", Default = "600", Label = "POP3 server maximum timeout (s)" },
                     new TextSetting { Key = "POP3CMinTimeout", Default = "30", Label = "POP3 client minimum timeout (s)" },
                     new TextSetting { Key = "POP3CMaxTimeout", Default = "900", Label = "POP3 client maximum timeout (s)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Delivery & queue tuning",
                  Blurb = "Early-retry behaviour, queue jitter and the external (POP3) fetch worker pool.",
                  Settings =
                  {
                     new TextSetting { Key = "QuickRetries", Default = "0", Label = "Quick early retries before the normal retry schedule (0 = off)" },
                     new TextSetting { Key = "QuickRetriesMinutes", Default = "6", Label = "Minutes between quick retries" },
                     new TextSetting { Key = "QueueRandomnessMinutes", Default = "0", Label = "Random jitter added to retry times (minutes, 0 = off)" },
                     new TextSetting { Key = "MXTriesFactor", Default = "0", Label = "Extra delivery attempts per additional MX host (0 = default)" },
                     new TextSetting { Key = "MaxNumberOfExternalFetchThreads", Default = "15", Label = "Max parallel external POP3 fetch threads" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Logging detail",
                  Blurb = "Low-level logging knobs. The log categories and destination are on the Logging page.",
                  Settings =
                  {
                     new TextSetting { Key = "LogLevel", Default = "9", Label = "Log level / verbosity" },
                     new TextSetting { Key = "MaxLogLineLen", Default = "500", Label = "Maximum characters per log line (minimum 100)" },
                     new BoolSetting { Key = "SepSvcLogs", Default = false, Label = "Write a separate log file per service component" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Search indexing",
                  Blurb = "Background full-text indexer cadence and batch sizes (used by IMAP SEARCH).",
                  Settings =
                  {
                     new TextSetting { Key = "IndexerFullMinutes", Default = "720", Label = "Full re-index interval (minutes)" },
                     new TextSetting { Key = "IndexerFullLimit", Default = "25000", Label = "Messages per full-index pass" },
                     new TextSetting { Key = "IndexerQuickLimit", Default = "1000", Label = "Messages per quick-index pass" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Message archiving",
                  Blurb = "Optionally keep a copy of every message passing through the server.",
                  Settings =
                  {
                     new PathSetting { Key = "ArchiveDir", PickFolder = true, Label = "Archive folder (empty = archiving off)", Placeholder = @"D:\MailArchive" },
                     new BoolSetting { Key = "ArchiveHardLinks", Default = false, Label = "Hard-link archived files instead of copying (same volume only)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Low-level tuning",
                  Blurb = "Specialist knobs — leave at the defaults unless you have a specific reason.",
                  Settings =
                  {
                     new TextSetting { Key = "DaemonAddressDomain", Label = "Domain for system / daemon (postmaster) addresses (empty = first local domain)" },
                     new TextSetting { Key = "SMTPDMaxSizeDrop", Default = "0", Label = "Drop oversized inbound messages above N bytes mid-transfer (0 = off)" },
                     new BoolSetting { Key = "SAMoveVsCopy", Default = false, Label = "Move (not copy) the message when handing it to SpamAssassin" },
                     new BoolSetting { Key = "BackupMessagesDBOnly", Default = false, Label = "Back up message metadata only, not the message files" },
                     new TextSetting { Key = "LoadHeaderReadSize", Default = "4000", Label = "Header read chunk size (bytes)" },
                     new TextSetting { Key = "LoadBodyReadSize", Default = "4000", Label = "Body read chunk size (bytes)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Stored secret protection",
                  Blurb = "When enabled, sensitive values in hMailServer.INI (database password, OAuth/SRS/BATV secrets, password pepper) are encrypted with Windows DPAPI on the next service start.",
                  Settings =
                  {
                     new BoolSetting { Key = "ProtectStoredSecretsWithDPAPI", Default = true, Label = "Protect stored secrets with Windows DPAPI" },
                     new SecretSetting { Key = "PasswordPepper", Label = "Password pepper — WARNING: set before creating accounts; changing it later invalidates ALL existing passwords", Note = "Server-wide secret mixed into password hashes" }
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
               FontSize = Typography.SectionHeading,
               FontWeight = FontWeights.SemiBold,
               Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(new TextBlock
            {
               Text = card.Blurb,
               FontSize = Typography.Caption,
               TextWrapping = TextWrapping.Wrap,
               Opacity = 0.65,
               Margin = new Thickness(0, 0, 0, 14)
            });

            FrameworkElement lastEditor = null;
            foreach (FrameworkElement editor in card.Settings.Select(s => s.CreateEditor(store_)))
            {
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
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not restart the service (try an elevated session): " + ex.Message,
               "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
         }

         StatusText.Text = "Service restarted - settings are live.";
         Reattach();
      }

      /// <summary>
      /// The COM server lives inside the service process, so restarting it
      /// invalidates every interface pointer the Control Panel holds. Rebuild
      /// the session straight away rather than letting the next page fail with
      /// "The RPC server is unavailable". The service registers its COM class
      /// factory a moment before it can serve calls, hence the retries.
      /// </summary>
      private void Reattach()
      {
         ServerSession session = ServerSession.Current;
         if (session == null)
            return;

         Mouse.OverrideCursor = Cursors.Wait;
         try
         {
            if (session.Reconnect(20, TimeSpan.FromMilliseconds(500), out string error))
               return;

            session.Invalidate();
            StatusText.Text = "Service restarted, but the Control Panel could not reconnect.";
            MessageBox.Show(
               "The service was restarted but the Control Panel could not reconnect to it: " + error +
               "\n\nIt will keep trying as you use the application.",
               "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
         }
         finally
         {
            Mouse.OverrideCursor = null;
         }
      }
   }
}
