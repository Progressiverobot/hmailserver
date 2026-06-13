using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Data-driven settings pages backed by the COM Settings object.
   /// Property paths are dotted ("AntiSpam.SpamMarkThreshold") and resolved
   /// against app.Settings via late binding, covering protocols, delivery,
   /// anti-spam, anti-virus, TLS, auto-ban and logging.
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
         Advanced
      }

      private abstract class ComSetting
      {
         public string Path;   // dotted path under app.Settings
         public string Label;
         public abstract FrameworkElement CreateEditor(object value);
         public abstract object ReadEditor();
      }

      private class ComBool : ComSetting
      {
         private CheckBox box_;

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
         private Wpf.Ui.Controls.TextBox box_;

         public override FrameworkElement CreateEditor(object value)
         {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = Label, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
            box_ = new Wpf.Ui.Controls.TextBox
            {
               Text = Convert.ToString(value) ?? "",
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
            return long.TryParse(text, out long number) ? number : 0L;
         }
      }

      private class CardDef
      {
         public string Title;
         public string Blurb;
         public List<ComSetting> Settings = new();
      }

      private readonly Section section_;
      private List<CardDef> cards_;

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

      private void BuildDefinition()
      {
         cards_ = new List<CardDef>();

         switch (section_)
         {
            case Section.Protocols:
               TitleText.Text = "Protocols";
               SubtitleText.Text = "Which services this server runs, connection limits and greetings.";
               cards_.Add(new CardDef
               {
                  Title = "Services",
                  Blurb = "Enable or disable the protocol servers. Changes apply after pressing Save.",
                  Settings =
                  {
                     new ComBool { Path = "ServiceSMTP", Label = "SMTP server" },
                     new ComBool { Path = "ServiceIMAP", Label = "IMAP server" },
                     new ComBool { Path = "ServicePOP3", Label = "POP3 server" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "SMTP",
                  Settings =
                  {
                     new ComText { Path = "HostName", Label = "Host name (HELO/EHLO greeting)" },
                     new ComText { Path = "MaxSMTPConnections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true },
                     new ComText { Path = "MaxMessageSize", Label = "Max message size (KB, 0 = unlimited)", Numeric = true },
                     new ComText { Path = "MaxSMTPRecipientsInBatch", Label = "Max recipients per message", Numeric = true },
                     new ComText { Path = "WelcomeSMTP", Label = "Welcome banner (empty = default)" },
                     new ComBool { Path = "AllowSMTPAuthPlain", Label = "Allow plain-text authentication (AUTH PLAIN/LOGIN)" },
                     new ComBool { Path = "DenyMailFromNull", Label = "Reject empty sender addresses (MAIL FROM:<>)" },
                     new ComBool { Path = "AllowIncorrectLineEndings", Label = "Allow incorrect line endings" },
                     new ComBool { Path = "DisconnectInvalidClients", Label = "Disconnect clients sending too many invalid commands" },
                     new ComText { Path = "MaxNumberOfInvalidCommands", Label = "Invalid command limit", Numeric = true }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "IMAP",
                  Settings =
                  {
                     new ComText { Path = "MaxIMAPConnections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true },
                     new ComText { Path = "WelcomeIMAP", Label = "Welcome banner (empty = default)" },
                     new ComBool { Path = "IMAPIdleEnabled", Label = "IDLE (push mail)" },
                     new ComBool { Path = "IMAPQuotaEnabled", Label = "QUOTA" },
                     new ComBool { Path = "IMAPSortEnabled", Label = "SORT" },
                     new ComBool { Path = "IMAPACLEnabled", Label = "ACL (shared folder permissions)" },
                     new ComText { Path = "IMAPPublicFolderName", Label = "Public folder name" },
                     new ComText { Path = "IMAPMasterUser", Label = "Master user (empty = disabled)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "POP3",
                  Settings =
                  {
                     new ComText { Path = "MaxPOP3Connections", Label = "Max simultaneous connections (0 = unlimited)", Numeric = true },
                     new ComText { Path = "WelcomePOP3", Label = "Welcome banner (empty = default)" }
                  }
               });
               break;

            case Section.Delivery:
               TitleText.Text = "Delivery";
               SubtitleText.Text = "Outbound delivery behavior, retries and smart-host relaying.";
               cards_.Add(new CardDef
               {
                  Title = "Delivery of e-mail",
                  Settings =
                  {
                     new ComText { Path = "SMTPNoOfTries", Label = "Number of delivery retries", Numeric = true },
                     new ComText { Path = "SMTPMinutesBetweenTry", Label = "Minutes between retries", Numeric = true },
                     new ComText { Path = "MaxNumberOfMXHosts", Label = "Max MX hosts to try (0 = all)", Numeric = true },
                     new ComText { Path = "SMTPDeliveryBindToIP", Label = "Bind outbound connections to IP (empty = any)" },
                     new ComBool { Path = "AddDeliveredToHeader", Label = "Add Delivered-To header" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "SMTP relayer (smart host)",
                  Blurb = "Route all outbound mail through another SMTP server instead of delivering directly.",
                  Settings =
                  {
                     new ComText { Path = "SMTPRelayer", Label = "Relay host name (empty = direct delivery)" },
                     new ComText { Path = "SMTPRelayerPort", Label = "Port", Numeric = true },
                     new ComBool { Path = "SMTPRelayerRequiresAuthentication", Label = "Relay requires authentication" },
                     new ComText { Path = "SMTPRelayerUsername", Label = "User name" },
                     new ComText { Path = "SMTPRelayerConnectionSecurity", Label = "Connection security (0=None 1=SSL/TLS 2=STARTTLS-optional 3=STARTTLS-required)", Numeric = true }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Rules",
                  Settings =
                  {
                     new ComText { Path = "RuleLoopLimit", Label = "Rule loop limit", Numeric = true }
                  }
               });
               break;

            case Section.AntiSpam:
               TitleText.Text = "Anti-spam";
               SubtitleText.Text = "Score-based spam filtering: SPF, DKIM, DMARC, host checks, greylisting and SpamAssassin.";
               cards_.Add(new CardDef
               {
                  Title = "Thresholds & actions",
                  Settings =
                  {
                     new ComText { Path = "AntiSpam.SpamMarkThreshold", Label = "Spam mark threshold (score)", Numeric = true },
                     new ComText { Path = "AntiSpam.SpamDeleteThreshold", Label = "Spam delete threshold (score)", Numeric = true },
                     new ComBool { Path = "AntiSpam.AddHeaderSpam", Label = "Add X-hMailServer-Spam header" },
                     new ComBool { Path = "AntiSpam.AddHeaderReason", Label = "Add X-hMailServer-Reason header" },
                     new ComBool { Path = "AntiSpam.PrependSubject", Label = "Prepend text to subject" },
                     new ComText { Path = "AntiSpam.PrependSubjectText", Label = "Subject prefix" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Sender authentication",
                  Settings =
                  {
                     new ComBool { Path = "AntiSpam.UseSPF", Label = "Check SPF" },
                     new ComText { Path = "AntiSpam.UseSPFScore", Label = "SPF failure score", Numeric = true },
                     new ComBool { Path = "AntiSpam.DKIMVerificationEnabled", Label = "Verify DKIM signatures" },
                     new ComText { Path = "AntiSpam.DKIMVerificationFailureScore", Label = "DKIM failure score", Numeric = true },
                     new ComBool { Path = "AntiSpam.DMARCEnabled", Label = "Evaluate DMARC policies" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Connecting host checks",
                  Settings =
                  {
                     new ComBool { Path = "AntiSpam.CheckHostInHelo", Label = "Check host in HELO" },
                     new ComText { Path = "AntiSpam.CheckHostInHeloScore", Label = "HELO check score", Numeric = true },
                     new ComBool { Path = "AntiSpam.CheckPTR", Label = "Check PTR record" },
                     new ComText { Path = "AntiSpam.CheckPTRScore", Label = "PTR check score", Numeric = true },
                     new ComBool { Path = "AntiSpam.UseMXChecks", Label = "Check sender MX records" },
                     new ComText { Path = "AntiSpam.UseMXChecksScore", Label = "MX check score", Numeric = true }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Greylisting",
                  Settings =
                  {
                     new ComBool { Path = "AntiSpam.GreyListingEnabled", Label = "Enable greylisting" },
                     new ComText { Path = "AntiSpam.GreyListingInitialDelay", Label = "Initial delay (minutes)", Numeric = true },
                     new ComText { Path = "AntiSpam.GreyListingInitialDelete", Label = "Delete unconfirmed after (hours)", Numeric = true },
                     new ComText { Path = "AntiSpam.GreyListingFinalDelete", Label = "Delete confirmed after (days)", Numeric = true },
                     new ComBool { Path = "AntiSpam.GreyListingOnMailFromMX", Label = "Bypass when sender matches MX" },
                     new ComBool { Path = "AntiSpam.GreylistingOnSPFSuccess", Label = "Bypass on SPF success" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "SpamAssassin",
                  Settings =
                  {
                     new ComBool { Path = "AntiSpam.SpamAssassinEnabled", Label = "Use SpamAssassin" },
                     new ComText { Path = "AntiSpam.SpamAssassinHost", Label = "Host" },
                     new ComText { Path = "AntiSpam.SpamAssassinPort", Label = "Port", Numeric = true },
                     new ComBool { Path = "AntiSpam.SpamAssassinMergeScore", Label = "Merge SpamAssassin score into hMailServer score" },
                     new ComText { Path = "AntiSpam.SpamAssassinScore", Label = "Score when not merging", Numeric = true }
                  }
               });
               break;

            case Section.AntiVirus:
               TitleText.Text = "Anti-virus";
               SubtitleText.Text = "Virus scanning of received messages.";
               cards_.Add(new CardDef
               {
                  Title = "ClamAV (network daemon)",
                  Settings =
                  {
                     new ComBool { Path = "AntiVirus.ClamAVEnabled", Label = "Scan with clamd" },
                     new ComText { Path = "AntiVirus.ClamAVHost", Label = "Host" },
                     new ComText { Path = "AntiVirus.ClamAVPort", Label = "Port", Numeric = true }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "ClamWin (local executable)",
                  Settings =
                  {
                     new ComBool { Path = "AntiVirus.ClamWinEnabled", Label = "Scan with ClamWin" },
                     new ComText { Path = "AntiVirus.ClamWinExecutable", Label = "clamscan.exe path" },
                     new ComText { Path = "AntiVirus.ClamWinDBFolder", Label = "Database folder" }
                  }
               });
               break;

            case Section.Tls:
               TitleText.Text = "SSL / TLS";
               SubtitleText.Text = "Protocol versions and cipher configuration for encrypted connections.";
               cards_.Add(new CardDef
               {
                  Title = "Protocol versions",
                  Blurb = "TLS 1.2 and 1.3 are the recommended baseline; older versions exist only for legacy clients.",
                  Settings =
                  {
                     new ComBool { Path = "TlsVersion10Enabled", Label = "TLS 1.0 (legacy)" },
                     new ComBool { Path = "TlsVersion11Enabled", Label = "TLS 1.1 (legacy)" },
                     new ComBool { Path = "TlsVersion12Enabled", Label = "TLS 1.2" },
                     new ComBool { Path = "TlsVersion13Enabled", Label = "TLS 1.3" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Ciphers & verification",
                  Settings =
                  {
                     new ComText { Path = "SslCipherList", Label = "Cipher list (OpenSSL format)" },
                     new ComBool { Path = "TlsOptionPreferServerCiphersEnabled", Label = "Prefer server cipher order" },
                     new ComBool { Path = "TlsOptionPrioritizeChaChaEnabled", Label = "Prioritize ChaCha20 on mobile clients" },
                     new ComBool { Path = "VerifyRemoteSslCertificate", Label = "Verify remote certificates when delivering" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Auto-ban",
                  Blurb = "Temporarily blocks IP addresses after repeated failed logons.",
                  Settings =
                  {
                     new ComBool { Path = "AutoBanOnLogonFailure", Label = "Enable auto-ban" },
                     new ComText { Path = "MaxInvalidLogonAttempts", Label = "Max invalid logon attempts", Numeric = true },
                     new ComText { Path = "MaxInvalidLogonAttemptsWithin", Label = "...within (minutes)", Numeric = true },
                     new ComText { Path = "AutoBanMinutes", Label = "Ban duration (minutes)", Numeric = true }
                  }
               });
               break;

            case Section.Logging:
               TitleText.Text = "Logging";
               SubtitleText.Text = "What the server writes to its log files (viewable on the Live logs page).";
               cards_.Add(new CardDef
               {
                  Title = "Log categories",
                  Settings =
                  {
                     new ComBool { Path = "Logging.Enabled", Label = "Logging enabled" },
                     new ComBool { Path = "Logging.LogApplication", Label = "Application events" },
                     new ComBool { Path = "Logging.LogSMTP", Label = "SMTP conversations" },
                     new ComBool { Path = "Logging.LogIMAP", Label = "IMAP conversations" },
                     new ComBool { Path = "Logging.LogPOP3", Label = "POP3 conversations" },
                     new ComBool { Path = "Logging.LogTCPIP", Label = "TCP/IP activity" },
                     new ComBool { Path = "Logging.LogDebug", Label = "Debug messages" },
                     new ComBool { Path = "Logging.AWStatsEnabled", Label = "AWStats-compatible log" },
                     new ComBool { Path = "Logging.KeepFilesOpen", Label = "Keep log files open (performance)" }
                  }
               });
               break;

            case Section.Advanced:
               TitleText.Text = "Performance & scripting";
               SubtitleText.Text = "Thread pools, mirroring and the server-side scripting engine.";
               cards_.Add(new CardDef
               {
                  Title = "Performance",
                  Blurb = "Thread pool sizing. Defaults suit most installations; raise for very busy servers.",
                  Settings =
                  {
                     new ComText { Path = "MaxDeliveryThreads", Label = "Max delivery threads", Numeric = true },
                     new ComText { Path = "MaxAsynchronousThreads", Label = "Max asynchronous task threads", Numeric = true },
                     new ComText { Path = "TCPIPThreads", Label = "TCP/IP threads", Numeric = true },
                     new ComText { Path = "WorkerThreadPriority", Label = "Worker thread priority", Numeric = true }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Mirroring",
                  Blurb = "Sends a copy of every message passing through the server to one address (compliance archiving).",
                  Settings =
                  {
                     new ComText { Path = "MirrorEMailAddress", Label = "Mirror address (empty = disabled)" }
                  }
               });
               cards_.Add(new CardDef
               {
                  Title = "Scripting",
                  Blurb = "Runs event scripts (OnAcceptMessage, OnDeliveryStart...) from the Events folder. The script engine reloads when you save.",
                  Settings =
                  {
                     new ComBool { Path = "Scripting.Enabled", Label = "Enable server-side event scripts" },
                     new ComText { Path = "Scripting.Language", Label = "Language (VBScript or JScript)" }
                  }
               });
               break;
         }
      }

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
         => owner.GetType().InvokeMember(name,
            System.Reflection.BindingFlags.GetProperty, null, owner, null);

      private static void SetProperty(object owner, string name, object value)
         => owner.GetType().InvokeMember(name,
            System.Reflection.BindingFlags.SetProperty, null, owner, new[] { value });

      private void BuildUi()
      {
         CardsPanel.Children.Clear();

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
               Margin = new Thickness(0, 0, 0, string.IsNullOrEmpty(card.Blurb) ? 12 : 4)
            });
            if (!string.IsNullOrEmpty(card.Blurb))
            {
               panel.Children.Add(new TextBlock
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
               object value;
               try
               {
                  object owner = ResolveOwner(setting.Path, out string property);
                  value = GetProperty(owner, property);
               }
               catch (Exception)
               {
                  continue; // property unavailable on this server version
               }

               FrameworkElement editor = setting.CreateEditor(value);
               editor.Margin = new Thickness(0, 0, 0, 12);
               panel.Children.Add(editor);
               lastEditor = editor;
            }

            if (lastEditor != null)
               lastEditor.Margin = new Thickness(0, 0, 0, 2);

            border.Child = panel;
            CardsPanel.Children.Add(border);
         }

         StatusText.Text = "Values read from the server.";
      }

      private void Reload_Click(object sender, RoutedEventArgs e) => BuildUi();

      private void Save_Click(object sender, RoutedEventArgs e)
      {
         int saved = 0, failed = 0;

         foreach (CardDef card in cards_)
         {
            foreach (ComSetting setting in card.Settings)
            {
               try
               {
                  object owner = ResolveOwner(setting.Path, out string property);
                  object newValue = setting.ReadEditor();
                  if (newValue is long number)
                     SetProperty(owner, property, (int) number);
                  else
                     SetProperty(owner, property, newValue);
                  saved++;
               }
               catch (Exception)
               {
                  failed++;
               }
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
