using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Threading.Tasks;
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
   /// certificates (ACME), integrations (REST API, Prometheus metrics,
   /// ManageSieve), authentication (OAuth2, password storage), the DNS
   /// resolver and the public web services listener.
   /// </summary>
   public partial class FeatureSettingsView : UserControl, IPageLifecycle
   {
      public enum Section
      {
         Security,
         Automation,
         Integration,
         Hardening,
         Authentication,
         Dns,
         WebServices
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
                  Blurb = "Discovers and enforces recipient MTA-STS policies before delivering over TLS (RFC 8461). " +
                          "To publish a policy for your own domains, see the Web services & autoconfiguration page.",
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
               SubtitleText.Text = "REST administration API, Prometheus metrics and remote script management. " +
                                   "The public web services listener (autoconfiguration, MTA-STS hosting) is on the " +
                                   "Web services & autoconfiguration page; OAuth2 token authentication is on the " +
                                   "Authentication page.";
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
                  Title = "Monitoring",
                  Blurb = "Prometheus metrics (/metrics), OpenTelemetry traces/metrics export, a slow-query log and JSON-structured log output for log aggregators.",
                  Settings =
                  {
                     new TextSetting { Key = "MetricsServerPort", Default = "0", Label = "Metrics port (0 = disabled)", Placeholder = "9090" },
                     new TextSetting { Key = "MetricsServerBindAddress", Default = "127.0.0.1", Label = "Metrics bind address" },
                     // JsonLogging moved to the Logging page, with the other log settings.
                     new TextSetting { Key = "OtelEndpoint", Label = "OpenTelemetry OTLP endpoint (empty = disabled)", Placeholder = "http://localhost:4318" },
                     new TextSetting { Key = "OtelServiceName", Default = "hmailserver", Label = "OpenTelemetry service name" },
                     new TextSetting { Key = "SlowQueryLogMilliseconds", Default = "0", Label = "Log database queries slower than N ms (0 = off)", Placeholder = "250" }
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
                  // Log retention lives on the Logging page, next to the other
                  // logging settings, so there is exactly one editor per key.
                  Blurb = "Graceful shutdown behaviour for unattended / clustered operation. (Log retention is on the Logging page.)",
                  Settings =
                  {
                     new TextSetting { Key = "ShutdownDrainSeconds", Default = "0", Label = "On stop, wait up to N seconds for active sessions to finish (0 = stop immediately)", Placeholder = "30" }
                  }
               });
               break;

            case Section.Hardening:
               TitleText.Text = "Advanced INI settings";
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
               // Scanner timeouts moved to the scanner they configure: SpamAssassin
               // on the Anti-spam page and ClamAV on the Anti-virus page. The
               // resolver settings moved to Network > DNS resolver.
               cards_.Add(new CardDef
               {
                  Title = "Received headers",
                  Blurb = "Submission identity handling and the diagnostic headers added to received mail. " +
                          "(Where SMTP AUTH is offered is on the Authentication page.)",
                  Settings =
                  {
                     new TextSetting { Key = "AuthUserReplacementIP", Label = "Replace the client IP for authenticated users (empty = keep the real IP)", Placeholder = "e.g. 127.0.0.1" },
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
                     new BoolSetting { Key = "RewriteEnvelopeFromWhenForwarding", Default = false, Label = "Rewrite the envelope sender when forwarding (helps SPF; see also SRS)" }
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
                  Title = "Submission rate limits",
                  Blurb = "Throttle abusive senders submitting to this server. Limits are per minute; 0 disables the limit. " +
                          "(Throttling your own outbound rate to a destination is on the Delivery of e-mail page.)",
                  Settings =
                  {
                     new TextSetting { Key = "MaxSubmissionsPerIPPerMinute", Default = "0", Label = "Max authenticated submissions per client IP per minute (0 = unlimited)", Placeholder = "60" }
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
                  Title = "External fetch & MX attempts",
                  // Retry cadence, jitter and the outbound throttle moved to the
                  // Delivery of e-mail page, next to the retry schedule they modify.
                  Blurb = "Retry cadence and per-destination throttling are on the Delivery of e-mail page.",
                  Settings =
                  {
                     new TextSetting { Key = "MXTriesFactor", Default = "0", Label = "Extra delivery attempts per additional MX host (0 = default)" },
                     new TextSetting { Key = "MaxNumberOfExternalFetchThreads", Default = "15", Label = "Max parallel external POP3 fetch threads" }
                  }
               });
               // Logging detail moved to the Logging page, indexer cadence to
               // Performance > Indexing and message archiving to Advanced &
               // scripting (next to the mirroring address), so each setting sits
               // with the feature it configures rather than on this catch-all page.
               cards_.Add(new CardDef
               {
                  Title = "Server-generated mail",
                  // The server builds mailer-daemon@<domain> from this and puts it
                  // in the From: header of bounces and virus notices; it has no
                  // effect on how or where mail is delivered.
                  Blurb = "Bounces and virus notifications are sent from mailer-daemon@<domain>. This overrides the " +
                          "<domain> part. Left empty, the server uses its own host name, then the local domain the " +
                          "message involves, then this computer's name. It is not a delivery setting and does not " +
                          "change where mail is routed.",
                  Settings =
                  {
                     new TextSetting { Key = "DaemonAddressDomain", Label = "Domain for the mailer-daemon sender address (empty = server host name)", Placeholder = "yourdomain.com" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Low-level tuning",
                  Blurb = "Specialist knobs — leave at the defaults unless you have a specific reason.",
                  Settings =
                  {
                     new TextSetting { Key = "SMTPDMaxSizeDrop", Default = "0", Label = "Drop oversized inbound messages above N bytes mid-transfer (0 = off)" },
                     new BoolSetting { Key = "SAMoveVsCopy", Default = false, Label = "Move (not copy) the message when handing it to SpamAssassin" },
                     new TextSetting { Key = "LoadHeaderReadSize", Default = "4000", Label = "Header read chunk size (bytes)" },
                     new TextSetting { Key = "LoadBodyReadSize", Default = "4000", Label = "Body read chunk size (bytes)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Stored secret protection",
                  Blurb = "When enabled, sensitive values in hMailServer.INI (database password, OAuth/SRS/BATV secrets, password pepper) are encrypted with Windows DPAPI on the next service start. " +
                          "(The password pepper itself is on the Authentication page.)",
                  Settings =
                  {
                     new BoolSetting { Key = "ProtectStoredSecretsWithDPAPI", Default = true, Label = "Protect stored secrets with Windows DPAPI" }
                  }
               });
               break;

            case Section.Authentication:
               TitleText.Text = "Authentication";
               SubtitleText.Text = "How mailbox users prove who they are: external identity providers, how " +
                                   "passwords are stored, and where SMTP AUTH is offered (hMailServer.INI). " +
                                   "Changes take effect after a service restart.";
               cards_.Add(new CardDef
               {
                  Title = "OAuth2 / external identity provider",
                  Blurb = "Accept OAuth2 / OpenID Connect bearer tokens (XOAUTH2) from an external identity provider for IMAP, POP3 and SMTP submission, validated against the issuer's signing key. " +
                          "The mailbox named by the token still has to exist as a local account.",
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
                  Title = "Password storage",
                  Blurb = "How account passwords are hashed, and the weakest stored hash still allowed to log on. " +
                          "Existing passwords keep their current hash until they are next changed.",
                  Settings =
                  {
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
                     },
                     new SecretSetting { Key = "PasswordPepper", Label = "Password pepper — WARNING: set before creating accounts; changing it later invalidates ALL existing passwords", Note = "Server-wide secret mixed into password hashes" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "SMTP authentication",
                  Blurb = "AUTH is normally offered on every SMTP port. List the local TCP ports where it should not be, " +
                          "for example a port 25 that only accepts inbound mail from other servers.",
                  Settings =
                  {
                     new TextSetting { Key = "DisableAUTHList", Label = "Do not offer AUTH on these local TCP ports (comma separated)", Placeholder = "25" }
                  }
               });
               break;

            case Section.Dns:
               TitleText.Text = "DNS resolver";
               SubtitleText.Text = "How this server resolves MX, PTR, SPF, DKIM, DMARC and blacklist lookups " +
                                   "(hMailServer.INI). Changes take effect after a service restart.";
               cards_.Add(new CardDef
               {
                  Title = "Name servers",
                  Blurb = "Which resolver hMailServer queries. Leave empty to use the name servers Windows is configured with. " +
                          "The override takes a single IPv4 address and is used on port 53; DNSSEC validation and its trust " +
                          "anchors are on the Transport security page.",
                  Settings =
                  {
                     new TextSetting { Key = "DNSServer", Label = "Override DNS server (empty = the name servers Windows uses)", Placeholder = "1.1.1.1" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "DNS cache",
                  Blurb = "Keeps DNS answers in memory for their TTL so repeated lookups for the same host do not go out to " +
                          "the network again. It only reduces the number of lookups — it is not a substitute for a local " +
                          "caching resolver on a busy server. Setting an override DNS server above always bypasses the cache.",
                  Settings =
                  {
                     new BoolSetting { Key = "UseDNSCache", Default = true, Label = "Cache DNS lookup results in memory" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "DNS blacklist checks",
                  Blurb = "When during the SMTP conversation blacklist lookups happen. Which blacklists are queried is on the " +
                          "DNS blacklists page.",
                  Settings =
                  {
                     new BoolSetting { Key = "DNSBLChecksAfterMailFrom", Default = true, Label = "Run DNSBL checks after MAIL FROM (rather than at connect)" }
                  }
               });
               break;

            case Section.WebServices:
               TitleText.Text = "Web services & client autoconfiguration";
               SubtitleText.Text = "The built-in HTTP listener that serves mail-client autoconfiguration and MTA-STS " +
                                   "policies for your local domains (hMailServer.INI). Changes take effect after a " +
                                   "service restart.";
               cards_.Add(new CardDef
               {
                  Title = "Listener",
                  Blurb = "Nothing below is served until a port is set here. The server answers on either port, but an " +
                          "MTA-STS policy is only valid over HTTPS and Outlook only accepts autodiscover over HTTPS — " +
                          "so in practice set the HTTPS port and a certificate covering the host names clients use.",
                  Settings =
                  {
                     new TextSetting { Key = "WebServicesHttpPort", Default = "0", Label = "HTTP port (80 to enable, 0 = disabled)" },
                     new TextSetting { Key = "WebServicesHttpsPort", Default = "0", Label = "HTTPS port (443 to enable, 0 = disabled)" },
                     new TextSetting { Key = "WebServicesBindAddress", Default = "0.0.0.0", Label = "Bind address" },
                     new PathSetting { Key = "WebServicesCertificateFile", FileFilter = "PEM/certificate files (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|All files (*.*)|*.*", Label = "TLS certificate file (PEM, optional)", Placeholder = "Falls back to the ACME certificate" },
                     new PathSetting { Key = "WebServicesPrivateKeyFile", FileFilter = "PEM/key files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*", Label = "TLS private key file (PEM, optional)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Client autoconfiguration (autoconfig & autodiscover)",
                  Blurb = "Hands mail clients the right host names, ports and security settings automatically: Thunderbird-style " +
                          "autoconfig and Outlook autodiscover, for every local domain. Point autoconfig.<domain> and " +
                          "autodiscover.<domain> at this server in DNS.",
                  Settings =
                  {
                     new BoolSetting { Key = "AutoconfigEnabled", Default = true, Label = "Thunderbird autoconfig + Outlook autodiscover" },
                     new TextSetting { Key = "AutoconfigClientHost", Label = "Host name clients connect to (empty = server host name)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "MTA-STS policy hosting",
                  Blurb = "Publishes https://mta-sts.<domain>/.well-known/mta-sts.txt so other servers require TLS when they " +
                          "deliver to you. Honouring other domains' policies is on the Transport security page.",
                  Settings =
                  {
                     new BoolSetting { Key = "MtaStsHostingEnabled", Default = true, Label = "Serve MTA-STS policies for local domains" },
                     new TextSetting { Key = "MtaStsPolicyMode", Default = "enforce", Label = "MTA-STS policy mode (enforce / testing / none)" },
                     new TextSetting { Key = "MtaStsPolicyMaxAge", Default = "604800", Label = "Policy max age (seconds; default 604800 = 7 days)" },
                     new TextSetting { Key = "MtaStsPolicyMx", Label = "Policy MX host patterns (empty = derive from each domain's MX)", Placeholder = "mail.yourdomain.com, *.yourdomain.com" }
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

      private async void RestartService()
      {
         StatusText.Text = "Restarting the hMailServer service...";

         string error = await Task.Run(() => TryRestartService());
         if (error != null)
         {
            StatusText.Text = "The service could not be restarted.";
            MessageBox.Show("Could not restart the service: " + error,
               "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
         }

         StatusText.Text = "Service restarted - settings are live.";
         Reattach();
      }

      /// <summary>
      /// Stops and starts the service, returning null on success or a message
      /// describing the failure. Stopping a service needs administrator rights
      /// and the Control Panel runs asInvoker, so a non-elevated session hands
      /// the work to net.exe behind a single UAC prompt instead of failing
      /// with an opaque "Cannot open hMailServer service" error.
      /// </summary>
      private static string TryRestartService()
      {
         try
         {
            bool elevated;
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
               elevated = new System.Security.Principal.WindowsPrincipal(identity)
                  .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            if (elevated)
            {
               using var controller = new ServiceController("hMailServer");
               if (controller.Status != ServiceControllerStatus.Stopped)
               {
                  if (controller.Status == ServiceControllerStatus.Running ||
                      controller.Status == ServiceControllerStatus.Paused)
                     controller.Stop();
                  controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
               }
               controller.Start();
               controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
               return null;
            }

            // "net stop" fails harmlessly when the service is already stopped,
            // so chain with "&" rather than "&&".
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
               FileName = "cmd.exe",
               Arguments = "/c net stop hMailServer & net start hMailServer",
               UseShellExecute = true,
               Verb = "runas",
               WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            using (var process = System.Diagnostics.Process.Start(startInfo))
               process?.WaitForExit();

            using (var controller = new ServiceController("hMailServer"))
               controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            return null;
         }
         catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
         {
            return "the elevation prompt was cancelled.";
         }
         catch (Exception ex)
         {
            return ex.GetBaseException().Message;
         }
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
