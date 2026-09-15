// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using hMailServer.ControlPanel.Services;

// The Control Panel has its own Typography (the type scale, in Services) and
// System.Windows.Documents declares one too. That import is needed here for Run and
// Inlines, so the reference is aliased to the one meant rather than dropping the import.
using Typography = hMailServer.ControlPanel.Services.Typography;
using Card = hMailServer.ControlPanel.Views.Scaffold.Card;
using InlineNotice = hMailServer.ControlPanel.Views.Scaffold.InlineNotice;
using PageHeader = hMailServer.ControlPanel.Views.Scaffold.PageHeader;
using StatusPill = hMailServer.ControlPanel.Views.Scaffold.StatusPill;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// One page that answers "is anything on this server carrying a password in the
   /// clear, and will it still serve a valid certificate next month".
   ///
   /// Neither question has a page today, because neither has an answer that is a
   /// setting. Four pages own the pieces — the ports decide per-listener security,
   /// the certificates page decides what can be presented and when it expires, the
   /// SSL/TLS page decides which versions and ciphers are negotiable, and the IP
   /// ranges page holds the one control that refuses a plaintext password whatever
   /// the port allows. Each is a correct editor. None of them can put the four
   /// together, and the interesting states only exist in the combination: a port set
   /// to use TLS with no certificate assigned does not fall back to plaintext, it
   /// fails to start; the AEAD-ONLY cipher preset leaves TLS 1.0 and 1.1 advertised
   /// with no suite they can use; a certificate that expired last week is still
   /// listed exactly like one that has not.
   ///
   /// Read-only on purpose. It is a diagnosis, not a fifth editor: every row links to
   /// the page that owns the setting, so there is still exactly one place to change
   /// each value — which is also why this page cannot drift out of step with them.
   ///
   /// The judgement all lives in <see cref="TlsPosture"/>, which has no WPF and is
   /// tested directly; this file reads the COM settings into a snapshot and draws it.
   /// </summary>
   public class TlsOverviewView : UserControl, IPageLifecycle
   {
      private readonly StackPanel body_ = new();

      // A read that failed is a warning in the flow of the page, not a grey line
      // at the bottom: this page's whole product is a verdict, and a verdict
      // reached from an incomplete read has to say so before it is believed.
      private readonly InlineNotice readFailure_ = new()
      {
         Level = StatusLevel.Warning,
         Visibility = Visibility.Collapsed
      };

      // Where the values came from - a provenance footnote, the quietest text.
      private readonly TextBlock status_ = new();

      public TlsOverviewView()
      {
         var page = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };

         var refresh = new Wpf.Ui.Controls.Button { Content = L("_Refresh") };
         System.Windows.Automation.AutomationProperties.SetName(refresh, L("Re-read the transport security configuration from the server"));
         System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "tls-overview-refresh");
         refresh.Click += (s, e) => Reload();

         page.Children.Add(new PageHeader
         {
            Title = L("Transport encryption overview"),
            Subtitle = L("What every listener protects, what it presents to prove who it is, and what can still be negotiated. Nothing on this page can be edited — each row opens the page that owns the setting."),
            Actions = refresh
         });

         page.Children.Add(readFailure_);
         page.Children.Add(body_);

         status_.Margin = new Thickness(0, 12, 0, 0);
         status_.SetResourceReference(StyleProperty, "TextCaptionTertiary");
         page.Children.Add(status_);

         var scroller = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
         scroller.SetResourceReference(PaddingProperty, "AppPagePadding");
         Content = scroller;
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      // ---- reading the configuration -----------------------------------------

      private int failedReads_;
      private string firstError_;

      private T Read<T>(Func<T> read)
      {
         try
         {
            return read();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
            return default;
         }
      }

      private TlsPostureConfig ReadConfig()
      {
         failedReads_ = 0;
         firstError_ = null;

         var config = new TlsPostureConfig();
         dynamic settings = null;

         try
         {
            settings = ServerSession.Current.Application.Settings;

            config.Tls10Enabled = Read(() => (bool)settings.TlsVersion10Enabled);
            config.Tls11Enabled = Read(() => (bool)settings.TlsVersion11Enabled);
            config.Tls12Enabled = Read(() => (bool)settings.TlsVersion12Enabled);
            config.Tls13Enabled = Read(() => (bool)settings.TlsVersion13Enabled);
            config.CipherList = Read(() => (string)settings.SslCipherList) ?? "";
            config.VerifyRemoteCertificates = Read(() => (bool)settings.VerifyRemoteSslCertificate);

            // The certificate files live beside the server. Read remotely, the file
            // checks are meaningless - and quietly reporting "missing" for a file
            // that is simply on another machine would be the worst possible answer.
            string host = Read(() => ServerSession.Current.Host) ?? "";
            config.CertificateFilesReadable = string.IsNullOrEmpty(host) || CertificateInspector.SessionIsLocal(host);

            // Certificates first: the listener pass resolves each port's certificate
            // id against what this has already read, rather than asking the server
            // again once per port.
            ReadCertificates(config, settings);
            ReadListeners(config, settings);
            ReadSecurityRanges(config, settings);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object)settings);
         }

         return config;
      }

      /// <summary>
      /// Certificates, with the expiry and file checks the SSL certificates page
      /// already performs. Reused rather than reimplemented so the two pages cannot
      /// disagree about whether a certificate is healthy.
      /// </summary>
      private void ReadCertificates(TlsPostureConfig config, dynamic settings)
      {
         dynamic certificates = null;

         try
         {
            certificates = settings.SSLCertificates;
            int count = (int)certificates.Count;

            for (int i = 0; i < count; i++)
            {
               dynamic certificate = null;

               try
               {
                  certificate = certificates.Item[i];

                  var entry = new TlsCertificate
                  {
                     Id = (int)certificate.ID,
                     Name = (string)certificate.Name ?? ""
                  };

                  if (config.CertificateFilesReadable)
                  {
                     CertificateHealth health = CertificateInspector.Inspect(
                        (string)certificate.CertificateFile,
                        (string)certificate.PrivateKeyFile,
                        true);

                     entry.DaysRemaining = health.DaysRemaining;

                     // Only the findings that stop the certificate working. An
                     // encrypted key with a passphrase configured, for instance, is
                     // fine and must not be reported here as a problem.
                     entry.Problem = FirstProblem(health);
                  }

                  config.Certificates.Add(entry);
               }
               finally
               {
                  ServerSession.Release((object)certificate);
               }
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object)certificates);
         }
      }

      private static string FirstProblem(CertificateHealth health)
      {
         CertificateFinding critical = new[] { health.CertificateFile, health.PrivateKeyFile, health.Pair }
            .FirstOrDefault(finding => finding != null && finding.Level == StatusLevel.Critical);

         return critical?.Detail;
      }

      private void ReadListeners(TlsPostureConfig config, dynamic settings)
      {
         dynamic ports = null;

         try
         {
            ports = settings.TCPIPPorts;
            int count = (int)ports.Count;

            for (int i = 0; i < count; i++)
            {
               dynamic port = null;

               try
               {
                  port = ports.Item[i];

                  // eSessionType: 1 SMTP, 3 POP3, 5 IMAP. Anything else is a session
                  // type IOService refuses to build a server for, so it is named
                  // rather than guessed at.
                  int protocol = (int)port.Protocol;
                  string protocolName = protocol switch
                  {
                     1 => "SMTP", // no-loc
                     3 => "POP3",
                     5 => "IMAP", // no-loc
                     _ => F("Unknown ({0})", protocol)
                  };

                  int certificateId = (int)port.SSLCertificateID;
                  var security = (TlsListenerSecurity)(int)port.ConnectionSecurity;

                  var listener = new TlsListener
                  {
                     Protocol = protocolName,
                     Address = (string)port.Address ?? "",
                     Port = (int)port.PortNumber,
                     Security = security,
                     CertificateName = CertificateNameFor(config, certificateId)
                  };

                  // Marked here, by id, rather than by matching names afterwards:
                  // nothing stops two certificates sharing a name, and "is anything
                  // using this certificate" is the difference between an expired
                  // certificate being critical and being tidy-up.
                  if (security != TlsListenerSecurity.None)
                     MarkInUse(config, certificateId);

                  config.Listeners.Add(listener);
               }
               finally
               {
                  ServerSession.Release((object)port);
               }
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object)ports);
         }
      }

      /// <summary>
      /// The name of the certificate a port refers to, by database id.
      ///
      /// Resolved against the collection this page has already read rather than
      /// asked of the server again once per port. An id that is not in that
      /// collection is a dangling reference, and it comes back empty on purpose: the
      /// listener is then reported as having no certificate, which is exactly what
      /// the server will make of it too - IOService looks the id up through
      /// GetItemByDBID, gets nothing, and hands TCPServer a null certificate.
      /// </summary>
      private static string CertificateNameFor(TlsPostureConfig config, int certificateId)
      {
         if (certificateId <= 0)
            return "";

         TlsCertificate match = config.Certificates.FirstOrDefault(c => c.Id == certificateId);
         return match == null ? "" : match.Name;
      }

      private static void MarkInUse(TlsPostureConfig config, int certificateId)
      {
         TlsCertificate match = config.Certificates.FirstOrDefault(c => c.Id == certificateId);
         if (match != null)
            match.InUse = true;
      }

      private void ReadSecurityRanges(TlsPostureConfig config, dynamic settings)
      {
         dynamic ranges = null;

         try
         {
            ranges = settings.SecurityRanges;
            int count = (int)ranges.Count;
            config.RangesTotal = count;

            for (int i = 0; i < count; i++)
            {
               dynamic range = null;

               try
               {
                  range = ranges.Item[i];

                  if ((bool)range.RequireSSLTLSForAuth)
                     config.RangesRequiringTlsForAuth++;
               }
               finally
               {
                  ServerSession.Release((object)range);
               }
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object)ranges);
         }
      }

      // ---- drawing ------------------------------------------------------------

      private void Reload()
      {
         TlsPostureConfig config = ReadConfig();

         body_.Children.Clear();
         body_.Children.Add(VerdictCard(config));
         body_.Children.Add(ListenersCard(config));
         body_.Children.Add(CertificatesCard(config));
         body_.Children.Add(NegotiationCard(config));
         body_.Children.Add(AuthenticationCard(config));

         IReadOnlyList<TlsPostureNote> notes = TlsPosture.Notes(config);
         if (notes.Count > 0)
            body_.Children.Add(NotesCard(notes));

         status_.Text = L("Read from the server. Values are read again every time this page is opened.");

         readFailure_.Text = failedReads_ == 0
            ? null
            : F("{0} value(s) could not be read — {1} The rows above may be incomplete.", failedReads_, firstError_);
         readFailure_.Visibility = failedReads_ == 0 ? Visibility.Collapsed : Visibility.Visible;
      }

      /// <summary>One section of the page. Named Section and not Card because the
      /// component it builds is called that.</summary>
      private static Card Section(string title, out StackPanel content)
      {
         content = new StackPanel();
         var card = new Card { Title = title, Content = content };
         card.SetResourceReference(MarginProperty, "AppCardGap");
         return card;
      }

      private static Card VerdictCard(TlsPostureConfig config)
      {
         Card card = Section(L("Where this server stands"), out StackPanel content);

         content.Children.Add(Paragraph(TlsPosture.Verdict(config), Typography.Body));

         content.Children.Add(Paragraph(L("Port 25 is judged differently from the rest and deliberately so: the peer there is normally another mail server, and requiring encryption on it refuses mail from senders that cannot offer any. On every other port the peer is a client with a password."),
            Typography.Caption));

         return card;
      }

      private Card ListenersCard(TlsPostureConfig config)
      {
         Card card = Section(L("Listeners"), out StackPanel content);

         var grid = new Grid();
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // protocol + port
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // detail
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // state

         int row = 0;
         foreach (TlsListenerVerdict verdict in TlsPosture.Listeners(config))
         {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddListenerRow(grid, row, verdict);
            row++;
         }

         if (row == 0)
         {
            content.Children.Add(Paragraph(L("No listeners are configured."), Typography.Body));
         }
         else
         {
            content.Children.Add(grid);
         }

         content.Children.Add(PageLink("ports", L("Ports…"), L("Open TCP/IP ports, which owns the per-listener security setting")));

         return card;
      }

      private void AddListenerRow(Grid grid, int row, TlsListenerVerdict verdict)
      {
         TlsListener listener = verdict.Listener;

         var identity = new StackPanel { Margin = new Thickness(0, 6, 16, 6), VerticalAlignment = VerticalAlignment.Top };

         var name = new TextBlock { Text = listener.Protocol + " " + listener.Port };
         name.SetResourceReference(StyleProperty, "TextBodyStrong");

         // The whole row as the accessible name of the one element in it with an
         // automation peer, so a screen reader hears one statement rather than three
         // fragments to reassemble.
         System.Windows.Automation.AutomationProperties.SetName(name,
            F("{0} on port {1}, {2}. {3}", listener.Protocol, listener.Port, verdict.Word, verdict.Detail));

         identity.Children.Add(name);

         var address = new TextBlock { Text = listener.Address };
         address.SetResourceReference(StyleProperty, "TextCaptionTertiary");
         identity.Children.Add(address);

         Grid.SetRow(identity, row);
         Grid.SetColumn(identity, 0);
         grid.Children.Add(identity);

         var text = new StackPanel { Margin = new Thickness(0, 6, 12, 6) };

         var detail = new TextBlock { Text = verdict.Detail, TextWrapping = TextWrapping.Wrap };
         detail.SetResourceReference(StyleProperty, "TextCaption");
         text.Children.Add(detail);

         var certificate = new TextBlock
         {
            Text = string.IsNullOrWhiteSpace(listener.CertificateName)
               ? (listener.Security == TlsListenerSecurity.None ? L("No certificate needed") : L("No certificate assigned"))
               : "Certificate: " + listener.CertificateName,
            TextWrapping = TextWrapping.Wrap
         };
         certificate.SetResourceReference(StyleProperty, "TextCaptionTertiary");
         text.Children.Add(certificate);

         Grid.SetRow(text, row);
         Grid.SetColumn(text, 1);
         grid.Children.Add(text);

         // Colour, shape and word together, so none of the three carries the
         // meaning on its own - which is what the pill is.
         var state = new StatusPill
         {
            Level = verdict.Level,
            Text = verdict.Word,
            MaxWidth = 150,
            Margin = new Thickness(0, 6, 0, 6),
            VerticalAlignment = VerticalAlignment.Top
         };

         Grid.SetRow(state, row);
         Grid.SetColumn(state, 2);
         grid.Children.Add(state);
      }

      private Card CertificatesCard(TlsPostureConfig config)
      {
         Card card = Section(L("Certificates"), out StackPanel content);

         if (config.Certificates.Count == 0)
         {
            content.Children.Add(Paragraph(L("No certificates are configured."), Typography.Body));
         }
         else
         {
            foreach (TlsCertificate certificate in config.Certificates)
            {
               content.Children.Add(CertificateRow(config, certificate));
            }
         }

         if (!config.CertificateFilesReadable)
         {
            content.Children.Add(Paragraph(L("Expiry cannot be read from here: the certificate files are on the server and this Control Panel is connected to it remotely. Everything else on this page comes over COM and is accurate."),
               Typography.Caption));
         }

         content.Children.Add(PageLink("certs", L("Certificates…"), L("Open SSL certificates, which owns the certificate and key file paths")));

         return card;
      }

      private static FrameworkElement CertificateRow(TlsPostureConfig config, TlsCertificate certificate)
      {
         var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

         string detail;
         StatusLevel level;

         if (certificate.Problem != null)
         {
            detail = certificate.Problem;
            level = certificate.InUse ? StatusLevel.Critical : StatusLevel.Warning;
         }
         else if (!config.CertificateFilesReadable || certificate.DaysRemaining == null)
         {
            detail = L("expiry not checked from here");
            level = StatusLevel.Normal;
         }
         else if (certificate.DaysRemaining < 0)
         {
            detail = F("expired {0} day(s) ago", Math.Abs(certificate.DaysRemaining.Value));
            level = certificate.InUse ? StatusLevel.Critical : StatusLevel.Warning;
         }
         else if (certificate.DaysRemaining <= TlsPosture.ExpiryWarningDays)
         {
            detail = F("expires in {0} day(s)", certificate.DaysRemaining.Value);
            level = certificate.InUse ? StatusLevel.Warning : StatusLevel.Information;
         }
         else
         {
            detail = F("expires in {0} day(s)", certificate.DaysRemaining.Value);
            level = StatusLevel.Good;
         }

         StatusPresentation presentation = StatusSemantics.For(level);

         row.Children.Add(new StatusPill
         {
            Level = level,
            Text = presentation.SeverityWord,
            Margin = new Thickness(0, 2, 10, 0),
            VerticalAlignment = VerticalAlignment.Top
         });

         string usage = certificate.InUse ? L("in use by a listener") : L("not used by any listener");

         var text = new TextBlock
         {
            Text = certificate.Name + " — " + detail + ", " + usage,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 820,
            VerticalAlignment = VerticalAlignment.Center
         };
         text.SetResourceReference(StyleProperty, "TextBody");
         System.Windows.Automation.AutomationProperties.SetName(text,
            certificate.Name + ", " + presentation.SeverityWord + ", " + detail + ", " + usage);
         row.Children.Add(text);

         return row;
      }

      private Card NegotiationCard(TlsPostureConfig config)
      {
         Card card = Section(L("What can be negotiated"), out StackPanel content);

         content.Children.Add(Paragraph(F("Protocol versions enabled: {0}", TlsPosture.VersionSummary(config)), Typography.Body));

         content.Children.Add(Paragraph(
            TlsPosture.IsAeadOnlyPreset(config.CipherList)
               ? L("Cipher list: the AEAD-ONLY preset — forward-secret AEAD suites only, with every CBC-mode suite and static-RSA key exchange excluded. This governs TLS 1.2 and below; TLS 1.3 has its own list and is AEAD by construction.")
               : string.IsNullOrWhiteSpace(config.CipherList)
                  ? L("Cipher list: OpenSSL defaults for TLS 1.2 and below. TLS 1.3 has its own separate list.")
                  : F("Cipher list for TLS 1.2 and below: {0}. TLS 1.3 suites are configured separately and are not restricted by this.", config.CipherList),
            Typography.Caption));

         content.Children.Add(PageLink("tls", L("Versions and ciphers…"), L("Open SSL/TLS, which owns the protocol versions and cipher lists")));

         return card;
      }

      private Card AuthenticationCard(TlsPostureConfig config)
      {
         Card card = Section(L("Passwords, and what is allowed to carry them"), out StackPanel content);

         content.Children.Add(Paragraph(
            config.RangesTotal == 0
               ? L("No IP ranges are configured.")
               : config.RangesRequiringTlsForAuth == 0
                  ? F("None of the {0} IP range(s) requires TLS before authentication, so a plaintext password is accepted wherever a port permits one.", config.RangesTotal)
                  : F("{0} of {1} IP range(s) refuse authentication until the session is encrypted.", config.RangesRequiringTlsForAuth, config.RangesTotal),
            Typography.Body));

         content.Children.Add(Paragraph(L("This setting lives on each IP range rather than with the rest of TLS, which is the main reason it goes unnoticed. It is the only control that refuses a plaintext password regardless of what the port allows, so it is what makes an optional-STARTTLS port safe for clients."),
            Typography.Caption));

         content.Children.Add(PageLink("ipranges", L("IP ranges…"), L("Open IP ranges, which owns the require-TLS-for-authentication setting")));

         return card;
      }

      private static Card NotesCard(IReadOnlyList<TlsPostureNote> notes)
      {
         Card card = Section(L("Worth knowing about this configuration"), out StackPanel content);

         // One notice per note, at the note's own level: the component draws the
         // colour, the shape and the severity word, and puts the word first in
         // its accessible name, which is what this card used to build by hand.
         foreach (TlsPostureNote note in notes)
            content.Children.Add(new InlineNotice { Level = note.Level, Text = note.Text });

         return card;
      }

      private static TextBlock Paragraph(string text, double size)
      {
         var block = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) };
         block.SetResourceReference(StyleProperty, size <= Typography.Caption ? "TextCaption" : "TextBody");
         return block;
      }

      private static FrameworkElement PageLink(string page, string caption, string accessibleName)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = caption,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            FontSize = Typography.Caption,
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = accessibleName
         };
         System.Windows.Automation.AutomationProperties.SetName(button, accessibleName);
         System.Windows.Automation.AutomationProperties.SetAutomationId(button, "tls-overview-open-" + page);
         button.Click += (s, e) => (Application.Current?.MainWindow as MainWindow)?.NavigateTo(page);
         return button;
      }
   }
}
