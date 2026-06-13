using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Tabbed, data-driven settings pages backed by the COM Settings object.
   /// Mirrors the classic Administrator layout: each section is a TabControl
   /// whose tabs group related cards. Property paths are dotted
   /// ("AntiSpam.SpamMarkThreshold") and resolved against app.Settings.
   /// </summary>
   public partial class ServerSettingsView : UserControl, IPageLifecycle
   {
      public enum Section
      {
         Protocols,
         Delivery,
         AntiSpam,
         AntiVirus,
         Tls,
         Logging,
         Performance,
         Advanced
      }

      // ---- editor model ------------------------------------------------------

      private abstract class ComSetting
      {
         public string Path;   // dotted path under app.Settings
         public string Label;
         public virtual bool WantsInitialValue => true;
         public abstract FrameworkElement CreateEditor(object value);
         public abstract object ReadEditor();

         public virtual void Write(object owner, string property)
         {
            object value = ReadEditor();
            if (value is long n)
               SetProperty(owner, property, (int) n);
            else
               SetProperty(owner, property, value);
         }
      }

      private class ComBool : ComSetting
      {
         private CheckBox box_;

         /// <summary>The created checkbox (for cross-field dependency wiring).</summary>
         public CheckBox Box => box_;

         public override FrameworkElement CreateEditor(object value)
         {
            box_ = new CheckBox { Content = Label, IsChecked = value is bool b && b, FontSize = 13.5 };
            return box_;
         }

         public override object ReadEditor() => box_.IsChecked == true;
      }

      private class ComText : ComSetting
      {
         public bool Numeric;
         public int Divisor = 1;   // numeric display scaling (e.g. hours stored, days shown)
         private TextBox box_;

         /// <summary>Current text in the editor (for live test buttons).</summary>
         public string CurrentText => box_?.Text?.Trim() ?? "";
         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
            object shown = value;
            if (Numeric && Divisor > 1 && value != null)
            {
               try { shown = Convert.ToInt64(value) / Divisor; } catch (Exception) { shown = value; }
            }
            box_ = new TextBox
            {
               Text = Convert.ToString(shown) ?? "",
               FontSize = 13,
               MaxWidth = 520,
               HorizontalAlignment = HorizontalAlignment.Left,
               MinWidth = 320
            };
            panel.Children.Add(box_);
            return panel;
         }

         public override object ReadEditor()
         {
            string text = box_.Text.Trim();
            if (!Numeric)
               return text;
            long number = long.TryParse(text, out long v) ? v : 0L;
            return number * Divisor;
         }
      }

      private class ComCombo : ComSetting
      {
         public (int Value, string Label)[] Options;
         private ComboBox combo_;

         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
            combo_ = new ComboBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left, FontSize = 13 };
            int sel;
            try { sel = Convert.ToInt32(value); } catch (Exception) { sel = 0; }
            foreach ((int v, string l) in Options)
            {
               var item = new ComboBoxItem { Content = l, Tag = v };
               combo_.Items.Add(item);
               if (v == sel)
                  combo_.SelectedItem = item;
            }
            if (combo_.SelectedItem == null && combo_.Items.Count > 0)
               combo_.SelectedIndex = 0;
            panel.Children.Add(combo_);
            return panel;
         }

         public override object ReadEditor()
            => combo_.SelectedItem is ComboBoxItem cbi ? (int) cbi.Tag : 0;
      }

      private class ComPassword : ComSetting
      {
         public string MethodName;   // e.g. SetSMTPRelayerPassword
         private PasswordBox box_;
         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
            box_ = new PasswordBox
            {
               FontSize = 13,
               MinWidth = 320,
               MaxWidth = 520,
               Padding = new Thickness(6),
               HorizontalAlignment = HorizontalAlignment.Left
            };
            panel.Children.Add(box_);
            return panel;
         }

         public override object ReadEditor() => box_.Password;

         public override void Write(object owner, string property)
         {
            string pw = box_.Password;
            if (string.IsNullOrEmpty(pw))
               return;   // leave the current password unchanged
            owner.GetType().InvokeMember(MethodName, BindingFlags.InvokeMethod, null, owner, new object[] { pw });
         }
      }

      /// <summary>A non-persistent action button (e.g. "Test connection") with a result line.</summary>
      private class ComAction : ComSetting
      {
         public string ButtonText;
         public Func<(bool ok, string text)> Action;
         private System.Windows.Controls.TextBlock result_;
         public override bool WantsInitialValue => false;

         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            var btn = new Wpf.Ui.Controls.Button
            {
               Content = ButtonText,
               Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
               HorizontalAlignment = HorizontalAlignment.Left
            };
            result_ = new System.Windows.Controls.TextBlock
            {
               FontSize = 12,
               Margin = new Thickness(0, 8, 0, 0),
               TextWrapping = TextWrapping.Wrap
            };
            btn.Click += (s, e) =>
            {
               try
               {
                  (bool ok, string text) r = Action();
                  result_.Text = r.text;
                  result_.Foreground = r.ok
                     ? System.Windows.Media.Brushes.MediumSeaGreen
                     : System.Windows.Media.Brushes.IndianRed;
               }
               catch (Exception ex)
               {
                  result_.Text = "Test failed: " + ex.Message;
                  result_.Foreground = System.Windows.Media.Brushes.IndianRed;
               }
            };
            panel.Children.Add(btn);
            panel.Children.Add(result_);
            return panel;
         }

         public override object ReadEditor() => null;
         public override void Write(object owner, string property) { }
      }

      private class CardDef
      {
         public string Title;
         public string Blurb;
         public List<ComSetting> Settings = new();
      }

      private class TabDef
      {
         public string Header;
         public List<CardDef> Cards = new();
      }

      // ---- enum option tables ------------------------------------------------

      private static readonly (int, string)[] ConnSecurity =
      {
         (0, "None"), (1, "SSL/TLS"), (2, "STARTTLS (optional)"), (3, "STARTTLS (required)")
      };

      private static readonly (int, string)[] AntivirusAction =
      {
         (0, "Delete entire e-mail"), (1, "Delete infected attachments only")
      };

      private readonly Section section_;
      private List<TabDef> tabs_;
      private string diag_;
      private int failedReads_;
      private Action afterBuildUi_;
      public ServerSettingsView(Section section)
      {
         InitializeComponent();
         section_ = section;
         BuildDefinition();
      }

      public void OnEnter() => BuildUi();

      public void OnLeave()
      {
      }

      // ---- definitions -------------------------------------------------------

      private TabDef Tab(string header)
      {
         var t = new TabDef { Header = header };
         tabs_.Add(t);
         return t;
      }

      private static CardDef Card(string title, string blurb = null)
         => new() { Title = title, Blurb = blurb };

      private void BuildDefinition()
      {
         tabs_ = new List<TabDef>();

         switch (section_)
         {
            case Section.Protocols: BuildProtocols(); break;
            case Section.Delivery: BuildDelivery(); break;
            case Section.AntiSpam: BuildAntiSpam(); break;
            case Section.AntiVirus: BuildAntiVirus(); break;
            case Section.Tls: BuildTls(); break;
            case Section.Logging: BuildLogging(); break;
            case Section.Performance: BuildPerformance(); break;
            case Section.Advanced: BuildAdvanced(); break;
         }
      }

      private void BuildProtocols()
      {
         TitleText.Text = "Protocols";
         SubtitleText.Text = "Which services this server runs, connection limits and greetings.";

         var services = Card("Services", "Enable or disable the protocol servers. Changes apply after pressing Save.");
         services.Settings.Add(new ComBool { Path = "ServiceSMTP", Label = "SMTP server" });
         services.Settings.Add(new ComBool { Path = "ServiceIMAP", Label = "IMAP server" });
         services.Settings.Add(new ComBool { Path = "ServicePOP3", Label = "POP3 server" });
         Tab("Services").Cards.Add(services);

         var smtp = Card("SMTP");
         smtp.Settings.Add(new ComText { Path = "HostName", Label = "Host name (HELO/EHLO greeting)" });
         smtp.Settings.Add(new ComText { Path = "MaxSMTPConnections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true });
         smtp.Settings.Add(new ComText { Path = "MaxMessageSize", Label = "Max message size (KB, 0 = unlimited)", Numeric = true });
         smtp.Settings.Add(new ComText { Path = "MaxSMTPRecipientsInBatch", Label = "Max recipients per message", Numeric = true });
         smtp.Settings.Add(new ComText { Path = "WelcomeSMTP", Label = "Welcome banner (empty = default)" });
         smtp.Settings.Add(new ComBool { Path = "AllowSMTPAuthPlain", Label = "Allow plain-text authentication (AUTH PLAIN/LOGIN)" });
         smtp.Settings.Add(new ComBool { Path = "DenyMailFromNull", Label = "Reject empty sender addresses (MAIL FROM:<>)" });
         smtp.Settings.Add(new ComBool { Path = "AllowIncorrectLineEndings", Label = "Allow incorrect line endings" });
         smtp.Settings.Add(new ComBool { Path = "DisconnectInvalidClients", Label = "Disconnect clients sending too many invalid commands" });
         smtp.Settings.Add(new ComText { Path = "MaxNumberOfInvalidCommands", Label = "Invalid command limit", Numeric = true });
         Tab("SMTP").Cards.Add(smtp);

         var imap = Card("IMAP");
         imap.Settings.Add(new ComText { Path = "MaxIMAPConnections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true });
         imap.Settings.Add(new ComText { Path = "WelcomeIMAP", Label = "Welcome banner (empty = default)" });
         imap.Settings.Add(new ComBool { Path = "IMAPIdleEnabled", Label = "IDLE (push mail)" });
         imap.Settings.Add(new ComBool { Path = "IMAPQuotaEnabled", Label = "QUOTA" });
         imap.Settings.Add(new ComBool { Path = "IMAPSortEnabled", Label = "SORT" });
         imap.Settings.Add(new ComBool { Path = "IMAPACLEnabled", Label = "ACL (shared folder permissions)" });
         imap.Settings.Add(new ComBool { Path = "IMAPSASLPlainEnabled", Label = "Allow SASL PLAIN authentication" });
         imap.Settings.Add(new ComBool { Path = "IMAPSASLInitialResponseEnabled", Label = "Allow SASL initial client response" });
         imap.Settings.Add(new ComText { Path = "IMAPPublicFolderName", Label = "Public folder name" });
         imap.Settings.Add(new ComText { Path = "IMAPMasterUser", Label = "Master user (empty = disabled)" });
         imap.Settings.Add(new ComText { Path = "IMAPHierarchyDelimiter", Label = "Folder hierarchy delimiter" });
         Tab("IMAP").Cards.Add(imap);

         var pop3 = Card("POP3");
         pop3.Settings.Add(new ComText { Path = "MaxPOP3Connections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true });
         pop3.Settings.Add(new ComText { Path = "WelcomePOP3", Label = "Welcome banner (empty = default)" });
         Tab("POP3").Cards.Add(pop3);
      }

      private void BuildDelivery()
      {
         TitleText.Text = "Delivery of e-mail";
         SubtitleText.Text = "Outbound delivery behavior, retries and smart-host relaying.";

         var del = Card("Delivery of e-mail");
         del.Settings.Add(new ComText { Path = "SMTPNoOfTries", Label = "Number of delivery retries", Numeric = true });
         del.Settings.Add(new ComText { Path = "SMTPMinutesBetweenTry", Label = "Minutes between retries", Numeric = true });
         del.Settings.Add(new ComText { Path = "MaxNumberOfMXHosts", Label = "Max MX hosts to try (0 = all)", Numeric = true });
         del.Settings.Add(new ComText { Path = "SMTPDeliveryBindToIP", Label = "Bind outbound connections to IP (empty = any)" });
         del.Settings.Add(new ComCombo { Path = "SMTPConnectionSecurity", Label = "Outbound delivery security (after MX lookup)", Options = ConnSecurity });
         del.Settings.Add(new ComBool { Path = "AddDeliveredToHeader", Label = "Add Delivered-To header" });
         Tab("Delivery").Cards.Add(del);

         var relay = Card("SMTP relayer (smart host)", "Route all outbound mail through another SMTP server instead of delivering directly.");
         relay.Settings.Add(new ComText { Path = "SMTPRelayer", Label = "Relay host name (empty = direct delivery)" });
         relay.Settings.Add(new ComText { Path = "SMTPRelayerPort", Label = "Port", Numeric = true });
         relay.Settings.Add(new ComCombo { Path = "SMTPRelayerConnectionSecurity", Label = "Connection security", Options = ConnSecurity });
         relay.Settings.Add(new ComBool { Path = "SMTPRelayerRequiresAuthentication", Label = "Relay requires authentication" });
         relay.Settings.Add(new ComText { Path = "SMTPRelayerUsername", Label = "User name" });
         relay.Settings.Add(new ComPassword { Path = "SetSMTPRelayerPassword", MethodName = "SetSMTPRelayerPassword", Label = "Password (leave empty to keep current)" });
         Tab("Relayer").Cards.Add(relay);

         var rules = Card("Rules");
         rules.Settings.Add(new ComText { Path = "RuleLoopLimit", Label = "Rule loop limit", Numeric = true });
         Tab("Rules").Cards.Add(rules);
      }

      private void BuildAntiSpam()
      {
         TitleText.Text = "Anti-spam";
         SubtitleText.Text = "Score-based spam filtering: SPF, DKIM, DMARC, host checks, greylisting and SpamAssassin.";

         var general = Card("Thresholds & actions");
         general.Settings.Add(new ComText { Path = "AntiSpam.SpamMarkThreshold", Label = "Spam mark threshold (score)", Numeric = true });
         general.Settings.Add(new ComText { Path = "AntiSpam.SpamDeleteThreshold", Label = "Spam delete threshold (score)", Numeric = true });
         general.Settings.Add(new ComBool { Path = "AntiSpam.AddHeaderSpam", Label = "Add X-hMailServer-Spam header" });
         general.Settings.Add(new ComBool { Path = "AntiSpam.AddHeaderReason", Label = "Add X-hMailServer-Reason header" });
         general.Settings.Add(new ComBool { Path = "AntiSpam.PrependSubject", Label = "Prepend text to subject" });
         general.Settings.Add(new ComText { Path = "AntiSpam.PrependSubjectText", Label = "Subject prefix" });
         general.Settings.Add(new ComText { Path = "AntiSpam.MaximumMessageSize", Label = "Max message size to spam-scan (KB, 0 = unlimited)", Numeric = true });
         Tab("General").Cards.Add(general);

         var auth = Card("Sender authentication");
         auth.Settings.Add(new ComBool { Path = "AntiSpam.UseSPF", Label = "Check SPF" });
         auth.Settings.Add(new ComText { Path = "AntiSpam.UseSPFScore", Label = "SPF failure score", Numeric = true });
         auth.Settings.Add(new ComBool { Path = "AntiSpam.DKIMVerificationEnabled", Label = "Verify DKIM signatures" });
         auth.Settings.Add(new ComText { Path = "AntiSpam.DKIMVerificationFailureScore", Label = "DKIM failure score", Numeric = true });
         auth.Settings.Add(new ComBool { Path = "AntiSpam.DMARCEnabled", Label = "Evaluate DMARC policies" });
         auth.Settings.Add(new ComText { Path = "AntiSpam.DMARCFailureScore", Label = "DMARC failure score", Numeric = true });
         Tab("Sender auth").Cards.Add(auth);

         var host = Card("Connecting host checks");
         host.Settings.Add(new ComBool { Path = "AntiSpam.CheckHostInHelo", Label = "Check host in HELO" });
         host.Settings.Add(new ComText { Path = "AntiSpam.CheckHostInHeloScore", Label = "HELO check score", Numeric = true });
         host.Settings.Add(new ComBool { Path = "AntiSpam.CheckPTR", Label = "Check PTR record" });
         host.Settings.Add(new ComText { Path = "AntiSpam.CheckPTRScore", Label = "PTR check score", Numeric = true });
         host.Settings.Add(new ComBool { Path = "AntiSpam.UseMXChecks", Label = "Check sender MX records" });
         host.Settings.Add(new ComText { Path = "AntiSpam.UseMXChecksScore", Label = "MX check score", Numeric = true });
         Tab("Host checks").Cards.Add(host);

         var grey = Card("Greylisting", "Temporarily rejects mail from unknown senders; legitimate servers retry and pass.");
         grey.Settings.Add(new ComBool { Path = "AntiSpam.GreyListingEnabled", Label = "Enable greylisting" });
         grey.Settings.Add(new ComText { Path = "AntiSpam.GreyListingInitialDelay", Label = "Initial delay (minutes)", Numeric = true });
         grey.Settings.Add(new ComText { Path = "AntiSpam.GreyListingInitialDelete", Label = "Delete unconfirmed after (days)", Numeric = true, Divisor = 24 });
         grey.Settings.Add(new ComText { Path = "AntiSpam.GreyListingFinalDelete", Label = "Delete confirmed after (days)", Numeric = true, Divisor = 24 });
         grey.Settings.Add(new ComBool { Path = "AntiSpam.BypassGreylistingOnMailFromMX", Label = "Bypass when sender matches MX" });
         grey.Settings.Add(new ComBool { Path = "AntiSpam.BypassGreylistingOnSPFSuccess", Label = "Bypass on SPF success" });
         grey.Settings.Add(new ComAction
         {
            Path = "AntiSpam.GreyListingEnabled",
            ButtonText = "Clear greylisting triplets",
            Action = () =>
            {
               dynamic a = ServerSession.Current.Application.Settings.AntiSpam;
               try { a.ClearGreyListingTriplets(); return (true, "Greylisting triplets cleared."); }
               finally { ServerSession.Release((object) a); }
            }
         });
         Tab("Greylisting").Cards.Add(grey);

         var sa = Card("SpamAssassin");
         sa.Settings.Add(new ComBool { Path = "AntiSpam.SpamAssassinEnabled", Label = "Use SpamAssassin" });
         var saHost = new ComText { Path = "AntiSpam.SpamAssassinHost", Label = "Host" };
         var saPort = new ComText { Path = "AntiSpam.SpamAssassinPort", Label = "Port", Numeric = true };
         sa.Settings.Add(saHost);
         sa.Settings.Add(saPort);
         sa.Settings.Add(new ComBool { Path = "AntiSpam.SpamAssassinMergeScore", Label = "Merge SpamAssassin score into hMailServer score" });
         sa.Settings.Add(new ComText { Path = "AntiSpam.SpamAssassinScore", Label = "Score when not merging", Numeric = true });
         sa.Settings.Add(new ComAction
         {
            Path = "AntiSpam.SpamAssassinEnabled",
            ButtonText = "Test SpamAssassin connection",
            Action = () =>
            {
               string host = saHost.CurrentText;
               int.TryParse(saPort.CurrentText, out int port);
               return TestSpamAssassin(host, port);
            }
         });
         Tab("SpamAssassin").Cards.Add(sa);
      }

      private void BuildAntiVirus()
      {
         TitleText.Text = "Anti-virus";
         SubtitleText.Text = "Virus scanning and attachment blocking of received messages.";

         var general = Card("Action & notifications");
         general.Settings.Add(new ComCombo { Path = "AntiVirus.Action", Label = "When a virus is found", Options = AntivirusAction });
         general.Settings.Add(new ComBool { Path = "AntiVirus.NotifySender", Label = "Notify sender" });
         general.Settings.Add(new ComBool { Path = "AntiVirus.NotifyReceiver", Label = "Notify receiver" });
         general.Settings.Add(new ComText { Path = "AntiVirus.MaximumMessageSize", Label = "Max message size to virus-scan (KB, 0 = unlimited)", Numeric = true });
         general.Settings.Add(new ComBool { Path = "AntiVirus.EnableAttachmentBlocking", Label = "Enable attachment blocking (manage list on the Blocked attachments page)" });
         Tab("General").Cards.Add(general);

         var clamav = Card("ClamAV (network daemon)");
         clamav.Settings.Add(new ComBool { Path = "AntiVirus.ClamAVEnabled", Label = "Scan with clamd" });
         clamav.Settings.Add(new ComText { Path = "AntiVirus.ClamAVHost", Label = "Host" });
         clamav.Settings.Add(new ComText { Path = "AntiVirus.ClamAVPort", Label = "Port", Numeric = true });
         Tab("ClamAV").Cards.Add(clamav);

         var clamwin = Card("ClamWin (local executable)");
         clamwin.Settings.Add(new ComBool { Path = "AntiVirus.ClamWinEnabled", Label = "Scan with ClamWin" });
         clamwin.Settings.Add(new ComText { Path = "AntiVirus.ClamWinExecutable", Label = "clamscan.exe path" });
         clamwin.Settings.Add(new ComText { Path = "AntiVirus.ClamWinDBFolder", Label = "Database folder" });
         Tab("ClamWin").Cards.Add(clamwin);

         var custom = Card("Custom scanner", "Run an external command; a configured return value indicates an infected message.");
         custom.Settings.Add(new ComBool { Path = "AntiVirus.CustomScannerEnabled", Label = "Use a custom virus scanner" });
         custom.Settings.Add(new ComText { Path = "AntiVirus.CustomScannerExecutable", Label = "Executable" });
         custom.Settings.Add(new ComText { Path = "AntiVirus.CustomScannerReturnValue", Label = "Return value for infected", Numeric = true });
         Tab("Custom").Cards.Add(custom);
      }

      private void BuildTls()
      {
         TitleText.Text = "SSL / TLS";
         SubtitleText.Text = "Protocol versions, cipher configuration and brute-force protection.";

         var ver = Card("Protocol versions", "TLS 1.2 and 1.3 are the recommended baseline; older versions exist only for legacy clients.");
         var tls10 = new ComBool { Path = "TlsVersion10Enabled", Label = "TLS 1.0 (legacy)" };
         var tls11 = new ComBool { Path = "TlsVersion11Enabled", Label = "TLS 1.1 (legacy)" };
         var tls12 = new ComBool { Path = "TlsVersion12Enabled", Label = "TLS 1.2" };
         var tls13 = new ComBool { Path = "TlsVersion13Enabled", Label = "TLS 1.3" };
         ver.Settings.Add(tls10);
         ver.Settings.Add(tls11);
         ver.Settings.Add(tls12);
         ver.Settings.Add(tls13);
         Tab("Protocol versions").Cards.Add(ver);

         var ciph = Card("Ciphers & verification");
         var preferServer = new ComBool { Path = "TlsOptionPreferServerCiphersEnabled", Label = "Prefer server cipher order" };
         var chacha = new ComBool { Path = "TlsOptionPrioritizeChaChaEnabled", Label = "Prioritize ChaCha20 on mobile clients" };
         ciph.Settings.Add(new ComText { Path = "SslCipherList", Label = "Cipher list (OpenSSL format)" });
         ciph.Settings.Add(preferServer);
         ciph.Settings.Add(chacha);
         ciph.Settings.Add(new ComBool { Path = "VerifyRemoteSslCertificate", Label = "Verify remote certificates when delivering" });
         Tab("Ciphers").Cards.Add(ciph);

         // ChaCha prioritization only takes effect when the server chooses the
         // cipher order and a modern TLS version is enabled. Reflect that
         // dependency live in the UI instead of letting it silently no-op.
         afterBuildUi_ = () => WireChaChaDependency(preferServer, chacha, tls12, tls13);

         var ban = Card("Auto-ban", "Temporarily blocks IP addresses after repeated failed logons.");
         ban.Settings.Add(new ComBool { Path = "AutoBanOnLogonFailure", Label = "Enable auto-ban" });
         ban.Settings.Add(new ComText { Path = "MaxInvalidLogonAttempts", Label = "Max invalid logon attempts", Numeric = true });
         ban.Settings.Add(new ComText { Path = "MaxInvalidLogonAttemptsWithin", Label = "...within (minutes)", Numeric = true });
         ban.Settings.Add(new ComText { Path = "AutoBanMinutes", Label = "Ban duration (minutes)", Numeric = true });
         ban.Settings.Add(new ComAction
         {
            Path = "AutoBanOnLogonFailure",
            ButtonText = "Clear logon-failure list",
            Action = () =>
            {
               dynamic s = ServerSession.Current.Application.Settings;
               try { s.ClearLogonFailureList(); return (true, "Logon-failure list cleared."); }
               finally { ServerSession.Release((object) s); }
            }
         });
         Tab("Auto-ban").Cards.Add(ban);
      }

      private void BuildLogging()
      {
         TitleText.Text = "Logging";
         SubtitleText.Text = "What the server writes to its log files (viewable on the Live logs page).";

         var log = Card("Log categories");
         log.Settings.Add(new ComBool { Path = "Logging.Enabled", Label = "Logging enabled" });
         log.Settings.Add(new ComBool { Path = "Logging.LogApplication", Label = "Application events" });
         log.Settings.Add(new ComBool { Path = "Logging.LogSMTP", Label = "SMTP conversations" });
         log.Settings.Add(new ComBool { Path = "Logging.LogIMAP", Label = "IMAP conversations" });
         log.Settings.Add(new ComBool { Path = "Logging.LogPOP3", Label = "POP3 conversations" });
         log.Settings.Add(new ComBool { Path = "Logging.LogTCPIP", Label = "TCP/IP activity" });
         log.Settings.Add(new ComBool { Path = "Logging.LogDebug", Label = "Debug messages" });
         log.Settings.Add(new ComBool { Path = "Logging.AWStatsEnabled", Label = "AWStats-compatible log" });
         log.Settings.Add(new ComBool { Path = "Logging.KeepFilesOpen", Label = "Keep log files open (performance)" });
         Tab("Logging").Cards.Add(log);
      }

      private void BuildPerformance()
      {
         TitleText.Text = "Performance";
         SubtitleText.Text = "Thread pools, in-memory caches and message indexing.";

         var threads = Card("Threads", "Thread pool sizing. Defaults suit most installations; raise for very busy servers.");
         threads.Settings.Add(new ComText { Path = "MaxDeliveryThreads", Label = "Max delivery threads", Numeric = true });
         threads.Settings.Add(new ComText { Path = "MaxAsynchronousThreads", Label = "Max asynchronous task threads", Numeric = true });
         threads.Settings.Add(new ComText { Path = "TCPIPThreads", Label = "TCP/IP threads", Numeric = true });
         threads.Settings.Add(new ComText { Path = "WorkerThreadPriority", Label = "Worker thread priority", Numeric = true });
         Tab("Threads").Cards.Add(threads);

         var cache = Card("Cache", "Caches domain/account/alias lookups in memory to reduce database round-trips. TTL in seconds.");
         cache.Settings.Add(new ComBool { Path = "Cache.Enabled", Label = "Enable caching" });
         cache.Settings.Add(new ComText { Path = "Cache.DomainCacheTTL", Label = "Domain cache TTL (seconds)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.AccountCacheTTL", Label = "Account cache TTL (seconds)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.AliasCacheTTL", Label = "Alias cache TTL (seconds)", Numeric = true });
         cache.Settings.Add(new ComText { Path = "Cache.DistributionListCacheTTL", Label = "Distribution-list cache TTL (seconds)", Numeric = true });
         Tab("Cache").Cards.Add(cache);

         var index = Card("Message indexing", "Builds a search index so IMAP SEARCH and the web client are faster.");
         index.Settings.Add(new ComBool { Path = "MessageIndexing.Enabled", Label = "Enable message indexing" });
         Tab("Indexing").Cards.Add(index);
      }

      private void BuildAdvanced()
      {
         TitleText.Text = "Advanced";
         SubtitleText.Text = "Server-wide defaults, archiving mirror and the scripting engine.";

         var general = Card("General");
         general.Settings.Add(new ComText { Path = "DefaultDomain", Label = "Default domain (for unqualified logons)" });
         general.Settings.Add(new ComBool { Path = "IPv6PreferredEnabled", Label = "Prefer IPv6 when delivering" });
         general.Settings.Add(new ComPassword { Path = "SetAdministratorPassword", MethodName = "SetAdministratorPassword", Label = "New main administration password (leave empty to keep current)" });
         Tab("General").Cards.Add(general);

         var mirror = Card("Mirroring", "Sends a copy of every message passing through the server to one address (compliance archiving).");
         mirror.Settings.Add(new ComText { Path = "MirrorEMailAddress", Label = "Mirror address (empty = disabled)" });
         Tab("Mirroring").Cards.Add(mirror);

         var script = Card("Scripting", "Runs event scripts (OnAcceptMessage, OnDeliveryStart...) from the Events folder. The script engine reloads when you save.");
         script.Settings.Add(new ComBool { Path = "Scripting.Enabled", Label = "Enable server-side event scripts" });
         script.Settings.Add(new ComText { Path = "Scripting.Language", Label = "Language (VBScript or JScript)" });
         Tab("Scripting").Cards.Add(script);
      }

      // ---- COM resolution ----------------------------------------------------

      private static object ResolveOwner(string path, out string property)
      {
         dynamic current = ServerSession.Current.Application.Settings;
         string[] parts = path.Split('.');
         for (int i = 0; i < parts.Length - 1; i++)
            current = GetProperty(current, parts[i]);
         property = parts[^1];
         return current;
      }

      private static object GetProperty(object owner, string name)
         => owner.GetType().InvokeMember(name, BindingFlags.GetProperty, null, owner, null);

      private static void SetProperty(object owner, string name, object value)
         => owner.GetType().InvokeMember(name, BindingFlags.SetProperty, null, owner, new[] { value });

      // ---- UI ----------------------------------------------------------------

      private void BuildUi()
      {
         SettingsTabs.Items.Clear();
         diag_ = null;
         failedReads_ = 0;

         foreach (TabDef tab in tabs_)
         {
            var panel = new StackPanel { Margin = new Thickness(2, 4, 14, 4) };

            foreach (CardDef card in tab.Cards)
            {
               var border = new Border { Margin = new Thickness(0, 0, 0, 12) };
               border.SetResourceReference(StyleProperty, "Card");

               var inner = new StackPanel();
               inner.Children.Add(new TextBlock
               {
                  Text = card.Title,
                  FontSize = 15,
                  FontWeight = FontWeights.SemiBold,
                  Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(card.Blurb) ? 12 : 4)
               });
               if (!string.IsNullOrEmpty(card.Blurb))
               {
                  inner.Children.Add(new TextBlock
                  {
                     Text = card.Blurb,
                     FontSize = 12,
                     TextWrapping = TextWrapping.Wrap,
                     Opacity = 0.65,
                     Margin = new Thickness(0, 0, 0, 14)
                  });
               }

               FrameworkElement lastEditor = null;
               foreach (ComSetting setting in card.Settings)
               {
                  object value = null;
                  if (setting.WantsInitialValue)
                  {
                     try
                     {
                        object owner = ResolveOwner(setting.Path, out string property);
                        value = GetProperty(owner, property);
                     }
                     catch (Exception ex)
                     {
                        failedReads_++;
                        diag_ ??= ex.Message;
                        continue;   // value could not be read; skip this editor
                     }
                  }

                  FrameworkElement editor = setting.CreateEditor(value);
                  editor.Margin = new Thickness(0, 0, 0, 12);
                  inner.Children.Add(editor);
                  lastEditor = editor;
               }

               if (lastEditor != null)
                  lastEditor.Margin = new Thickness(0, 0, 0, 2);

               border.Child = inner;
               panel.Children.Add(border);
            }

            var scroll = new ScrollViewer
            {
               VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
               Content = panel
            };
            SettingsTabs.Items.Add(new TabItem { Header = tab.Header, Content = scroll });
         }

         if (SettingsTabs.Items.Count > 0)
            SettingsTabs.SelectedIndex = 0;

         StatusText.Text = failedReads_ == 0
            ? "Values read from the server."
            : failedReads_ + " setting(s) could not be read: " + diag_;

         afterBuildUi_?.Invoke();
      }

      // ---- live test / dependency helpers ------------------------------------

      private static (bool ok, string text) TestSpamAssassin(string host, int port)
      {
         if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return (false, "Enter a host name and port first.");

         dynamic antispam = ServerSession.Current.Application.Settings.AntiSpam;
         try
         {
            object[] args = { host, port, "" };
            object ret = ((object) antispam).GetType().InvokeMember(
               "TestSpamAssassinConnection", BindingFlags.InvokeMethod, null, (object) antispam, args);
            bool ok = ret is bool b && b;
            string msg = args.Length > 2 ? args[2] as string : null;
            if (string.IsNullOrEmpty(msg))
               msg = ok ? "Connection succeeded." : "Connection failed.";
            return (ok, msg);
         }
         finally
         {
            ServerSession.Release((object) antispam);
         }
      }

      private static void WireChaChaDependency(ComBool preferServer, ComBool chacha, ComBool tls12, ComBool tls13)
      {
         if (preferServer?.Box == null || chacha?.Box == null || tls12?.Box == null || tls13?.Box == null)
            return;

         void Update()
         {
            bool eligible = preferServer.Box.IsChecked == true
               && (tls12.Box.IsChecked == true || tls13.Box.IsChecked == true);
            chacha.Box.IsEnabled = eligible;
            chacha.Box.ToolTip = eligible
               ? null
               : "Requires 'Prefer server cipher order' and TLS 1.2 or 1.3 to be enabled.";
         }

         void Handler(object s, RoutedEventArgs e) => Update();
         preferServer.Box.Checked += Handler;
         preferServer.Box.Unchecked += Handler;
         tls12.Box.Checked += Handler;
         tls12.Box.Unchecked += Handler;
         tls13.Box.Checked += Handler;
         tls13.Box.Unchecked += Handler;
         Update();
      }

      private void Reload_Click(object sender, RoutedEventArgs e) => BuildUi();

      private void Save_Click(object sender, RoutedEventArgs e)
      {
         int saved = 0, failed = 0;

         foreach (TabDef tab in tabs_)
         foreach (CardDef card in tab.Cards)
         foreach (ComSetting setting in card.Settings)
         {
            try
            {
               object owner = ResolveOwner(setting.Path, out string property);
               setting.Write(owner, property);
               saved++;
            }
            catch (Exception)
            {
               failed++;
            }
         }

         StatusText.Text = failed == 0
            ? "Saved " + saved + " settings at " + DateTime.Now.ToLongTimeString() + " - applied immediately."
            : "Saved " + saved + " settings, " + failed + " could not be written.";

         // Reload the script engine after scripting changes.
         if (section_ == Section.Advanced)
         {
            try
            {
               dynamic scripting = ServerSession.Current.Application.Settings.Scripting;
               scripting.Reload();
               ServerSession.Release(scripting);
            }
            catch (Exception)
            {
            }
         }
      }
   }
}
