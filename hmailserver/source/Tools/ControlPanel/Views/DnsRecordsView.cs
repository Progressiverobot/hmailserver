// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using hMailServer.ControlPanel.Services;

// The Control Panel has its own Typography (the type scale, in Services) and
// System.Windows.Documents declares one too. That import is needed here for Run and
// Inlines, so the reference is aliased to the one meant rather than dropping the import.
using Typography = hMailServer.ControlPanel.Services.Typography;

// This file needs System.IO (the DKIM key file is read to derive the public key) and
// System.Windows.Shapes (the status marks), and both declare a Path. The alias picks
// the drawing one; the file APIs used here (File) are unambiguous.
using Path = System.Windows.Shapes.Path;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The DNS records this server needs, per domain, each one exact and checkable.
   ///
   /// An administrator's DNS provider asks for a type, a host and a value; this page
   /// produces all three for SPF, DKIM, DMARC, MTA-STS and TLS-RPT, in separate
   /// selectable boxes with their own copy buttons, and a Check button per record
   /// that says whether what is actually published matches. The model is the DKIM
   /// rotation flow in <see cref="DomainDialog"/>: generate what can be generated,
   /// show exactly what to publish, verify it on the wire, and gate the dangerous
   /// next step (here: switching DKIM signing on) on that verification passing.
   ///
   /// Every generated value is read from the server's own configuration or code,
   /// never invented:
   ///
   ///   SPF      - the server only CHECKS SPF on incoming mail (SMTP/SPF/SPF.cpp,
   ///              vendored RMSPF.cpp, records beginning "v=spf1"). The suggested
   ///              record names this server's outbound identity: the HELO/EHLO host
   ///              name, which is Settings.HostName (Utilities::ComputerName falls
   ///              back to the Windows computer name when it is empty).
   ///   DKIM     - the record value is derived from the domain's configured private
   ///              key file, the same derivation DkimKeyGenerator uses when a key
   ///              is first made (the p= tag is the key's SubjectPublicKeyInfo).
   ///   DMARC    - the server's own evaluator (Common/AntiSpam/DMARC/DMARC.cpp)
   ///              queries _dmarc.&lt;domain&gt; and accepts records beginning
   ///              "v=DMARC1"; the starting policy offered is p=none.
   ///   MTA-STS  - the policy file is served by the server itself
   ///              (Common/Util/WebServicesServer.cpp, HandleMtaStsPolicy_): HTTPS
   ///              only per RFC 8461 section 3.3, for hosted active domains, built
   ///              from MtaStsPolicyMode / MtaStsPolicyMaxAge / MtaStsPolicyMx in
   ///              hMailServer.INI. There is no file to upload; what this page can
   ///              do is show what will be served, and fetch what IS served.
   ///   TLS-RPT  - the record solicits reports from OTHER servers to a mailbox of
   ///              the administrator's choosing. Separately, this server's own
   ///              report sending is inert until TlsRptFromAddress is set
   ///              (SMTP/TlsRptReporterTask.cpp discards each completed day unsent
   ///              while it is empty).
   ///   PTR      - not publishable by the administrator at all: the owner of the
   ///              IP address sets it. The page shows the HELO name the PTR should
   ///              agree with and says who to ask, and does not pretend to check
   ///              what it cannot (the server's public IP is not knowable from here).
   ///
   /// Honesty rules, in force everywhere on this page: a value that depends on
   /// facts this panel cannot see (the public IP, the administrator's other mail
   /// senders) is labelled as an assumption, the danger of copying it blindly is
   /// said at the copy button and not in a footnote, and a mismatch is never
   /// reported without showing what IS published - the administrator cannot fix a
   /// difference they cannot see. The four-way verdict (not published / published
   /// but different / published and correct / lookup failed) follows
   /// <see cref="DnsTxtLookup"/>, whose whole purpose is keeping those apart.
   ///
   /// DNS lookups and the MTA-STS policy fetch run off the UI thread; each blocks
   /// for the resolver's or the HTTP client's full timeout, and the missing-record
   /// case - the slowest - is the common one right after publishing.
   /// </summary>
   public class DnsRecordsView : UserControl, IPageLifecycle
   {
      private readonly ComboBox domainCombo_ = new() { FontSize = Typography.Body, MinWidth = 280, Margin = new Thickness(8, 0, 0, 0) };
      private readonly StackPanel body_ = new();
      private readonly TextBlock status_ = new();

      private List<DomainInfo> domains_ = new();
      private string serverHostName_ = "";
      private bool serverHostNameKnown_;
      private int failedReads_;
      private string firstError_;

      /// <summary>Shared by the MTA-STS policy fetches. Redirects are NOT followed,
      /// because RFC 8461 section 3.3 forbids senders from following them - a
      /// policy behind a redirect is a policy senders never see.</summary>
      private static readonly HttpClient http_ = new(new HttpClientHandler { AllowAutoRedirect = false })
      {
         Timeout = TimeSpan.FromSeconds(10)
      };

      public DnsRecordsView()
      {
         var page = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };

         var header = new Grid();
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var heading = new StackPanel();
         var title = new TextBlock { Text = "DNS records" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         heading.Children.Add(title);

         var subtitle = new TextBlock
         {
            Text = "The records this server needs published in each domain's DNS zone: type, host and value, ready to copy "
                   + "into your DNS provider's console, with a check for whether each one is really live. Checks use this "
                   + "machine's Windows resolver - the same one the DKIM rotation check uses."
         };
         subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
         heading.Children.Add(subtitle);
         header.Children.Add(heading);

         var refresh = new Wpf.Ui.Controls.Button
         {
            Content = "_Refresh",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 4, 0, 0)
         };
         System.Windows.Automation.AutomationProperties.SetName(refresh, "Re-read the domains and server configuration");
         System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "dns-records-refresh");
         refresh.Click += (s, e) => Reload();
         Grid.SetColumn(refresh, 1);
         header.Children.Add(refresh);

         page.Children.Add(header);

         var domainRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
         var domainLabel = new TextBlock
         {
            Text = "Domain:",
            FontSize = Typography.Body,
            VerticalAlignment = VerticalAlignment.Center
         };
         domainRow.Children.Add(domainLabel);
         System.Windows.Automation.AutomationProperties.SetName(domainCombo_, "Domain whose DNS records are shown");
         System.Windows.Automation.AutomationProperties.SetAutomationId(domainCombo_, "dns-records-domain");
         domainCombo_.SelectionChanged += (s, e) => BuildCards();
         domainRow.Children.Add(domainCombo_);
         page.Children.Add(domainRow);

         page.Children.Add(body_);

         status_.FontSize = Typography.Caption;
         status_.Margin = new Thickness(0, 6, 0, 0);
         status_.TextWrapping = TextWrapping.Wrap;
         status_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         page.Children.Add(status_);

         Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      // ---- reading the configuration -------------------------------------------

      /// <summary>What this page needs to know about one hosted domain. Copied out
      /// of COM into plain strings up front, so nothing apartment-bound is ever
      /// touched from the background threads the checks run on.</summary>
      private sealed class DomainInfo
      {
         public string Name = "";
         public bool Active;
         public string Postmaster = "";
         public bool DkimEnabled;
         public string DkimSelector = "";
         public string DkimKeyFile = "";
         public string DkimSecondarySelector = "";
      }

      private void Reload()
      {
         failedReads_ = 0;
         firstError_ = null;

         string previous = SelectedDomainName();

         domains_ = ReadDomains();
         ReadServerHostName();

         domainCombo_.Items.Clear();
         foreach (DomainInfo domain in domains_)
         {
            domainCombo_.Items.Add(new ComboBoxItem
            {
               Content = domain.Name + (domain.Active ? "" : " (inactive)"),
               Tag = domain.Name,
               FontSize = Typography.Body
            });
         }

         if (domainCombo_.Items.Count > 0)
         {
            int select = 0;
            for (int i = 0; i < domainCombo_.Items.Count; i++)
               if (string.Equals((string)((ComboBoxItem)domainCombo_.Items[i]).Tag, previous, StringComparison.OrdinalIgnoreCase))
                  select = i;
            domainCombo_.SelectedIndex = select;   // fires BuildCards
         }
         else
         {
            BuildCards();
         }

         status_.Text = failedReads_ == 0
            ? "Configuration read from the server. Records are re-derived every time this page is opened; press Check to query DNS."
            : failedReads_ + " value(s) could not be read — " + firstError_ + " The cards below may be incomplete.";
      }

      private string SelectedDomainName()
         => domainCombo_.SelectedItem is ComboBoxItem item ? (string)item.Tag : null;

      private DomainInfo SelectedDomain()
      {
         string name = SelectedDomainName();
         return name == null ? null : domains_.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
      }

      private List<DomainInfo> ReadDomains()
      {
         var result = new List<DomainInfo>();
         dynamic domains = null;
         try
         {
            domains = ServerSession.Current.Application.Domains;
            int count = (int)domains.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic domain = domains.Item[i];
               try
               {
                  result.Add(new DomainInfo
                  {
                     Name = ((string)domain.Name ?? "").Trim(),
                     Active = (bool)domain.Active,
                     Postmaster = ((string)domain.Postmaster ?? "").Trim(),
                     DkimEnabled = (bool)domain.DKIMSignEnabled,
                     DkimSelector = ((string)domain.DKIMSelector ?? "").Trim(),
                     DkimKeyFile = ((string)domain.DKIMPrivateKeyFile ?? "").Trim(),
                     DkimSecondarySelector = ReadSecondarySelector(domain)
                  });
               }
               finally
               {
                  ServerSession.Release((object)domain);
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
            ServerSession.Release((object)domains);
         }
         return result;
      }

      /// <summary>Read defensively, like DomainDialog does: against an older server
      /// that predates the rotation API this property does not exist, and the rest
      /// of the page must keep working without it.</summary>
      private static string ReadSecondarySelector(dynamic domain)
      {
         try
         {
            return ((string)domain.DKIMSecondarySelector ?? "").Trim();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return "";
         }
      }

      /// <summary>
      /// The name this server uses to identify itself when delivering outbound
      /// mail: Settings.HostName. Utilities::ComputerName (Common/Util/Utilities.cpp)
      /// returns it for the HELO/EHLO greeting, falling back to the Windows
      /// computer name when it is empty - and a bare computer name is almost never
      /// a public DNS name, which several records below care about.
      /// </summary>
      private void ReadServerHostName()
      {
         serverHostName_ = "";
         serverHostNameKnown_ = false;

         dynamic settings = null;
         try
         {
            settings = ServerSession.Current.Application.Settings;
            serverHostName_ = ((string)settings.HostName ?? "").Trim();
            serverHostNameKnown_ = true;
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
      }

      /// <summary>
      /// File and INI checks only mean anything on the machine the server runs on:
      /// IniFeatureStore locates hMailServer.INI through the local registry and
      /// service table, and the DKIM key file path is a path on the SERVER's disk.
      /// Connected to a remote host, those degrade to "cannot tell" instead.
      /// </summary>
      private static bool IsLocalServer()
      {
         string host = ServerSession.Current != null ? ServerSession.Current.Host : null;
         return string.IsNullOrWhiteSpace(host) || host == "localhost" || host == "127.0.0.1" || host == "::1";
      }

      // ---- building the cards ---------------------------------------------------

      private void BuildCards()
      {
         body_.Children.Clear();

         DomainInfo domain = SelectedDomain();

         if (domain == null)
         {
            var none = new TextBlock
            {
               Text = "No domains are configured, so there are no per-domain records to produce. Add a domain first.",
               FontSize = Typography.Body,
               TextWrapping = TextWrapping.Wrap,
               Margin = new Thickness(0, 16, 0, 0)
            };
            body_.Children.Add(none);
            body_.Children.Add(PageLink("domains", "Open Domains…", "Open Domains, where domains are added"));
         }
         else
         {
            body_.Children.Add(SpfCard(domain));
            body_.Children.Add(DkimCard(domain));
            body_.Children.Add(DmarcCard(domain));
            body_.Children.Add(MtaStsCard(domain));
            body_.Children.Add(TlsRptCard(domain));
         }

         // Server-wide, not per-domain: one IP, one PTR, one HELO name.
         body_.Children.Add(PtrCard());
      }

      // ---- SPF -------------------------------------------------------------------

      /// <summary>
      /// The suggested SPF record. The server's outbound identity is its HELO name
      /// (Settings.HostName); when that is set, "a:&lt;name&gt;" authorises its
      /// address and "mx" authorises whatever the domain's MX records point at.
      /// When it is not set, the server greets with its Windows computer name -
      /// not a public DNS name - so no a: mechanism can honestly be offered and
      /// the suggestion falls back to the MX hosts alone.
      ///
      /// "~all" (softfail) rather than "-all" on purpose: this page cannot know
      /// the administrator's other senders, and a hard fail on an incomplete list
      /// makes receivers reject legitimate mail outright.
      /// </summary>
      private string SuggestedSpf()
         => serverHostName_.Length > 0 ? "v=spf1 mx a:" + serverHostName_ + " ~all" : "v=spf1 mx ~all";

      private Border SpfCard(DomainInfo domain)
      {
         Border card = Card("SPF — which machines may send mail as " + domain.Name, out StackPanel content);

         content.Children.Add(Paragraph(
            "This server only CHECKS SPF on incoming mail; publishing the record is for everyone else - it tells "
            + "receiving servers which machines are allowed to send mail claiming to be from " + domain.Name + ".",
            Typography.Caption));

         string suggested = SuggestedSpf();

         string provenance;
         if (!serverHostNameKnown_)
            provenance = "The server's host name could not be read, so the suggestion authorises only the domain's MX hosts (\"mx\").";
         else if (serverHostName_.Length > 0)
            provenance = "\"mx\" authorises whatever " + domain.Name + "'s MX records point at; \"a:" + serverHostName_
                         + "\" authorises the address of the host name this server announces in HELO/EHLO "
                         + "(Delivery of e-mail > Host name). \"~all\" soft-fails everything else - deliberately softer than \"-all\" "
                         + "until you are sure the list is complete.";
         else
            provenance = "The server's Host name (Delivery of e-mail) is EMPTY, so it announces its Windows computer name in "
                         + "HELO/EHLO - not a public DNS name - and no \"a:\" mechanism can honestly be offered. The suggestion "
                         + "authorises only the domain's MX hosts. Set the host name first; several records on this page need it.";

         content.Children.Add(Paragraph(provenance, Typography.Caption));

         RecordBlock block = BuildRecordBlock(content, "dns-records-spf", "SPF",
            host: domain.Name,
            hostNote: "the domain itself (many DNS consoles write the zone apex as \"@\")",
            value: suggested,
            copyWarning: "This suggestion cannot know your other senders. If anything else sends mail as " + domain.Name
                         + " - a newsletter service, a website contact form, a hosted mailbox provider - copying this record "
                         + "as-is makes that mail fail SPF at every receiver that checks it. Add those senders to the record first.");

         block.Check.Click += async (s, e) => await CheckSpf(block, domain.Name, suggested);

         return card;
      }

      private async Task CheckSpf(RecordBlock block, string domainName, string suggested)
      {
         await RunCheck(block, domainName, () =>
         {
            DnsTxtLookup.LookupResult result = DnsTxtLookup.Query(domainName);
            return (Action)(() =>
            {
               if (result.Status == DnsTxtLookup.LookupStatus.Failed)
               {
                  SetLookupFailed(block, domainName, result.Error);
                  return;
               }

               // RFC 7208 (and the vendored RMSPF.cpp): an SPF record is the TXT
               // record whose value begins "v=spf1".
               List<string> spf = result.Records.Where(IsSpfRecord).ToList();

               if (spf.Count == 0)
               {
                  if (result.Records.Count > 0)
                     block.SetResult(StatusLevel.Warning, "Not published",
                        result.Records.Count + " TXT record(s) exist at " + domainName + ", but none begins \"v=spf1\", "
                        + "so receivers see no SPF policy at all." + DescribeFound(result.Records));
                  else
                     block.SetResult(StatusLevel.Warning, "Not published", NotFoundAdvice(domainName));
               }
               else if (spf.Count > 1)
               {
                  block.SetResult(StatusLevel.Warning, "Published, but duplicated",
                     spf.Count + " records beginning \"v=spf1\" exist at " + domainName + ". RFC 7208 makes that a permanent "
                     + "SPF error at every receiver - worse than no record. Merge them into one." + DescribeFound(spf));
               }
               else if (DnsTxtLookup.Matches(spf[0], suggested))
               {
                  block.SetResult(StatusLevel.Good, "Published and correct",
                     "The record at " + domainName + " matches the suggestion.");
               }
               else
               {
                  // Never "wrong": this page cannot know the administrator's other
                  // senders, so a difference from the suggestion may be deliberate
                  // and better. Show what is published so they can judge.
                  block.SetResult(StatusLevel.Information, "Published, differs from the suggestion",
                     "An SPF record is live at " + domainName + " and it is not the suggested one. That may well be deliberate - "
                     + "the suggestion cannot know your other senders. Published: " + spf[0]
                     + " — just confirm everything that really sends mail as " + domainName + " is covered.");
               }
            });
         });
      }

      private static bool IsSpfRecord(string record)
      {
         string trimmed = (record ?? "").Trim();
         return trimmed.Equals("v=spf1", StringComparison.OrdinalIgnoreCase)
             || trimmed.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase);
      }

      // ---- DKIM ------------------------------------------------------------------

      private Border DkimCard(DomainInfo domain)
      {
         Border card = Card("DKIM — the public key receivers verify " + domain.Name + "'s signatures against", out StackPanel content);

         bool configured = domain.DkimSelector.Length > 0 && domain.DkimKeyFile.Length > 0;

         if (!configured)
         {
            if (domain.DkimSelector.Length > 0 || domain.DkimKeyFile.Length > 0)
            {
               // Half-configured: one of the pair is set. Generating over the top
               // of it could clobber a key the administrator meant to keep, so
               // this page points at the editor that owns the pair instead.
               content.Children.Add(Paragraph(
                  "DKIM is half-configured: the " + (domain.DkimSelector.Length == 0 ? "selector" : "private key file")
                  + " is missing, so there is no record to derive. Complete the pair on the domain's DKIM tab.",
                  Typography.Body));
               content.Children.Add(PageLink("domains", "Open Domains…",
                  "Open Domains, where the DKIM selector and key file are configured"));
               return card;
            }

            content.Children.Add(Paragraph(
               "No DKIM key is configured for " + domain.Name + " yet. Generate one here: the private key is saved to a file "
               + "of your choosing on this machine, the selector and key file are stored on the domain, and signing stays OFF "
               + "until the DNS record below has been published and a check on this page has seen it - switching signing on "
               + "before then would sign mail with a key the world cannot look up.",
               Typography.Body));

            var selectorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
            var selectorBox = new TextBox
            {
               Text = "dkim",
               FontSize = Typography.Body,
               MinWidth = 180,
               Padding = new Thickness(6),
               VerticalAlignment = VerticalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetName(selectorBox, "Selector name for the new DKIM key");
            System.Windows.Automation.AutomationProperties.SetAutomationId(selectorBox, "dns-records-dkim-selector");
            selectorRow.Children.Add(selectorBox);

            var generate = new Wpf.Ui.Controls.Button { Content = "_Generate key pair…", Margin = new Thickness(8, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(generate, "Generate a DKIM key pair for " + domain.Name);
            System.Windows.Automation.AutomationProperties.SetAutomationId(generate, "dns-records-dkim-generate");
            generate.Click += (s, e) => GenerateDkim(domain.Name, selectorBox.Text.Trim());
            selectorRow.Children.Add(generate);
            content.Children.Add(selectorRow);

            return card;
         }

         string host = domain.DkimSelector + "._domainkey." + domain.Name;

         // The value is derived from the private key file - the p= tag is the key
         // pair's SubjectPublicKeyInfo, the same record DkimKeyGenerator emits when
         // a key is first made - so no secret leaves this process. Null when the
         // file cannot be read from this machine (typically remote administration:
         // the path is on the server's disk), in which case the check degrades to
         // presence-only and says so, exactly like the rotation flow does.
         string expected = DeriveDkimRecord(domain.DkimKeyFile);

         content.Children.Add(Paragraph(
            "Derived from the domain's configured key: selector \"" + domain.DkimSelector + "\", private key file "
            + domain.DkimKeyFile + ". Signing is currently "
            + (domain.DkimEnabled ? "ON." : "OFF - it can be switched on below once the record is confirmed published.")
            + (domain.DkimEnabled && expected == null
               ? " The key file could not be read from this machine, so the record value cannot be shown here; the domain's DKIM tab showed it when the key was made."
               : ""),
            Typography.Caption));

         RecordBlock block = BuildRecordBlock(content, "dns-records-dkim", "DKIM",
            host: host,
            hostNote: null,
            value: expected ?? "(The private key file could not be read from this machine, so the record value cannot be "
                               + "derived here. Run this page on the server itself, or use the domain's DKIM tab.)",
            copyWarning: null,
            valueCopyable: expected != null);

         if (domain.DkimSecondarySelector.Length > 0)
         {
            content.Children.Add(Paragraph(
               "A key rotation is staged (selector \"" + domain.DkimSecondarySelector + "\"). The staged record and the "
               + "promote step are managed from the domain's DKIM tab, which gates promotion on its own DNS check; it is "
               + "deliberately not repeated here.",
               Typography.Caption));
         }

         // Switching signing on is the dangerous step this card gates: enabled
         // before the record is visible, every receiver fails the signature, and
         // under a DMARC quarantine/reject policy that junks or bounces the mail.
         Wpf.Ui.Controls.Button enable = null;
         if (!domain.DkimEnabled)
         {
            enable = new Wpf.Ui.Controls.Button
            {
               Content = "_Enable DKIM signing",
               Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
               IsEnabled = false,
               Margin = new Thickness(0, 8, 0, 0),
               ToolTip = "Enabled once \"Check\" has confirmed the published record carries this key's public key."
            };
            ToolTipService.SetShowOnDisabled(enable, true);
            System.Windows.Automation.AutomationProperties.SetName(enable,
               "Enable DKIM signing for " + domain.Name + "; unlocked once the DNS check has passed");
            System.Windows.Automation.AutomationProperties.SetAutomationId(enable, "dns-records-dkim-enable");
            enable.Click += (s, e) => EnableDkimSigning(domain.Name);
            content.Children.Add(enable);
         }

         Wpf.Ui.Controls.Button enableButton = enable;
         block.Check.Click += async (s, e) => await CheckDkim(block, host, expected, enableButton);

         return card;
      }

      private async Task CheckDkim(RecordBlock block, string host, string expected, Wpf.Ui.Controls.Button enableButton)
      {
         await RunCheck(block, host, () =>
         {
            if (expected == null)
            {
               // Presence-only: the record can be looked up but not compared, and
               // a record that cannot be verified must not unlock the enable step.
               DnsTxtLookup.LookupResult found = DnsTxtLookup.Query(host);
               return (Action)(() =>
               {
                  switch (found.Status)
                  {
                     case DnsTxtLookup.LookupStatus.Found:
                        block.SetResult(StatusLevel.Information, "Cannot tell",
                           "A TXT record exists at " + host + ", but the private key file could not be read from this "
                           + "machine, so whether it carries the RIGHT key cannot be verified from here. Run the check on "
                           + "the server itself, or use the domain's DKIM tab.");
                        break;
                     case DnsTxtLookup.LookupStatus.NoRecord:
                        block.SetResult(StatusLevel.Warning, "Not published", NotFoundAdvice(host));
                        break;
                     default:
                        SetLookupFailed(block, host, found.Error);
                        break;
                  }
               });
            }

            DnsTxtLookup.MatchResult result = DnsTxtLookup.CheckExpected(host, expected);
            return (Action)(() =>
            {
               bool matches = result.Status == DnsTxtLookup.MatchStatus.FoundAndMatches;

               // Any outcome short of a confirmed match locks the enable step
               // (again): a pass from earlier is stale evidence the moment a fresh
               // check stops confirming it. Same rule as the rotation's Promote.
               if (enableButton != null)
                  enableButton.IsEnabled = matches;

               switch (result.Status)
               {
                  case DnsTxtLookup.MatchStatus.FoundAndMatches:
                     block.SetResult(StatusLevel.Good, "Published and correct",
                        "The record at " + host + " carries this key's public key."
                        + (enableButton != null ? " It is safe to enable signing." : ""));
                     break;
                  case DnsTxtLookup.MatchStatus.NoRecord:
                     block.SetResult(StatusLevel.Warning, "Not published", NotFoundAdvice(host));
                     break;
                  case DnsTxtLookup.MatchStatus.FoundButDifferent:
                     block.SetResult(StatusLevel.Critical, "Published, but wrong key",
                        "TXT record(s) exist at " + host + " but none carries this key's public key, so every signature made "
                        + "with this key fails verification. Waiting will not fix this - correct the published value to match "
                        + "the one above." + DescribeFound(result.Records));
                     break;
                  default:
                     SetLookupFailed(block, host, result.Error);
                     break;
               }
            });
         });
      }

      /// <summary>
      /// First-time key generation, reusing <see cref="DkimKeyGenerator"/> - the
      /// same generator and record format as the domain dialog. Deliberately does
      /// NOT switch signing on: that step stays behind the DNS check above, which
      /// is the entire point of this page.
      /// </summary>
      private void GenerateDkim(string domainName, string selector)
      {
         if (selector.Length == 0)
            selector = "dkim";

         var save = new Microsoft.Win32.SaveFileDialog
         {
            Title = "Save the DKIM private key",
            Filter = "PEM key files (*.pem)|*.pem|All files (*.*)|*.*",
            FileName = selector + "._domainkey." + domainName + ".pem"
         };

         if (save.ShowDialog() != true)
            return;

         try
         {
            DkimKeyGenerator.Result generated = DkimKeyGenerator.Generate(selector, domainName);
            File.WriteAllText(save.FileName, generated.PrivateKeyPem);

            dynamic domains = ServerSession.Current.Application.Domains;
            try
            {
               dynamic d = domains.ItemByName[domainName];
               d.DKIMSelector = selector;
               d.DKIMPrivateKeyFile = save.FileName;

               // Explicitly off, even though this branch is only reachable when no
               // key was configured: a domain can be left with signing switched on
               // and no key (the server reports error 5305 and sends unsigned), and
               // storing a fresh key into that state would start signing with a key
               // the world cannot look up yet. The card promises signing stays off
               // until the DNS check has passed; this line is that promise.
               d.DKIMSignEnabled = false;
               d.Save();
               ServerSession.Release(d);
            }
            finally
            {
               ServerSession.Release(domains);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not generate and store the DKIM key: " + ServerSession.DescribeComError(ex), "Control Panel");
            return;
         }

         Reload();
      }

      private void EnableDkimSigning(string domainName)
      {
         try
         {
            dynamic domains = ServerSession.Current.Application.Domains;
            try
            {
               dynamic d = domains.ItemByName[domainName];
               d.DKIMSignEnabled = true;
               d.Save();
               ServerSession.Release(d);
            }
            finally
            {
               ServerSession.Release(domains);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not enable DKIM signing: " + ServerSession.DescribeComError(ex), "Control Panel");
            return;
         }

         Reload();
      }

      /// <summary>
      /// Rebuilds the DNS TXT value from the private key's PEM file - the same
      /// derivation DomainDialog uses to restore a staged rotation. Returns null
      /// when the file cannot be read from this machine.
      /// </summary>
      private static string DeriveDkimRecord(string keyFile)
      {
         try
         {
            string pem = File.ReadAllText(keyFile);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return null;
         }
      }

      // ---- DMARC -----------------------------------------------------------------

      private Border DmarcCard(DomainInfo domain)
      {
         Border card = Card("DMARC — what receivers should do with mail that fails SPF and DKIM", out StackPanel content);

         // rua= is only offered when the domain has a postmaster address to send
         // reports to; inventing a mailbox would produce a record that asks the
         // world to mail reports somewhere that does not exist.
         bool haveRua = domain.Postmaster.Contains("@");
         string suggested = haveRua
            ? "v=DMARC1; p=none; rua=mailto:" + domain.Postmaster
            : "v=DMARC1; p=none";

         content.Children.Add(Paragraph(
            "p=none is the right starting point, not an oversight: it makes receivers check and report but deliver as "
            + "normal. A stricter policy (quarantine or reject) tells every receiver to junk or refuse mail that fails "
            + "alignment - and until reports show your own mail passing SPF or DKIM, the mail that fails will include "
            + "yours. Tighten only after the reports say it is safe.",
            Typography.Caption));

         content.Children.Add(Paragraph(
            haveRua
               ? "rua=mailto:" + domain.Postmaster + " sends you the aggregate reports; the address is the domain's "
                 + "postmaster address as configured on the domain. Reports arrive from strangers, so the mailbox must exist and accept them."
               : "No rua= is included because " + domain.Name + " has no postmaster address configured, and this page will "
                 + "not invent a mailbox. Without rua= the policy still applies, but nobody sends you reports - add "
                 + "\"; rua=mailto:<a mailbox you read>\" when you have one.",
            Typography.Caption));

         RecordBlock block = BuildRecordBlock(content, "dns-records-dmarc", "DMARC",
            host: "_dmarc." + domain.Name,
            hostNote: null,
            value: suggested,
            copyWarning: "Keep p=none until you know your own mail passes. Editing this to p=quarantine or p=reject before "
                         + "then tells every receiver to junk or bounce your own legitimate mail.");

         block.Check.Click += async (s, e) => await CheckDmarc(block, domain.Name);

         return card;
      }

      private async Task CheckDmarc(RecordBlock block, string domainName)
      {
         string host = "_dmarc." + domainName;

         await RunCheck(block, host, () =>
         {
            DnsTxtLookup.LookupResult result = DnsTxtLookup.Query(host);
            return (Action)(() =>
            {
               if (result.Status == DnsTxtLookup.LookupStatus.Failed)
               {
                  SetLookupFailed(block, host, result.Error);
                  return;
               }

               // The same acceptance rule as the server's own evaluator:
               // DMARC::RetrievePolicy_ takes TXT records whose trimmed value
               // begins "v=DMARC1".
               List<string> dmarc = result.Records
                  .Where(r => (r ?? "").Trim().StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase))
                  .ToList();

               if (dmarc.Count == 0)
               {
                  if (result.Records.Count > 0)
                     block.SetResult(StatusLevel.Warning, "Not published",
                        result.Records.Count + " TXT record(s) exist at " + host + ", but none begins \"v=DMARC1\", so "
                        + "receivers - and this server's own DMARC check - see no policy." + DescribeFound(result.Records));
                  else
                     block.SetResult(StatusLevel.Warning, "Not published", NotFoundAdvice(host));
                  return;
               }

               if (dmarc.Count > 1)
               {
                  block.SetResult(StatusLevel.Warning, "Published, but duplicated",
                     dmarc.Count + " records beginning \"v=DMARC1\" exist at " + host + ". Receivers treat multiple DMARC "
                     + "records as no policy at all - remove all but one." + DescribeFound(dmarc));
                  return;
               }

               string record = dmarc[0];
               string policy = ParseTag(record, "p");

               if (policy == null)
               {
                  block.SetResult(StatusLevel.Warning, "Published, but incomplete",
                     "The record at " + host + " has no p= tag, so it declares no policy and receivers ignore it. "
                     + "Published: " + record);
               }
               else if (policy.Equals("none", StringComparison.OrdinalIgnoreCase))
               {
                  block.SetResult(StatusLevel.Good, "Published and correct",
                     "A DMARC record with p=none is live at " + host + "."
                     + (ParseTag(record, "rua") == null
                        ? " It has no rua= tag, so nobody sends you reports - fine, but you learn nothing; add one when you have a mailbox for it."
                        : "")
                     + " Published: " + record);
               }
               else if (policy.Equals("quarantine", StringComparison.OrdinalIgnoreCase)
                     || policy.Equals("reject", StringComparison.OrdinalIgnoreCase))
               {
                  block.SetResult(StatusLevel.Information, "Published, stricter than the starting point",
                     "A DMARC record with p=" + policy.ToLowerInvariant() + " is live at " + host + " - stricter than the "
                     + "p=none starting point. That is right if you already know your mail passes alignment; if you are "
                     + "still setting SPF and DKIM up, mail that fails is being "
                     + (policy.Equals("reject", StringComparison.OrdinalIgnoreCase) ? "rejected" : "quarantined")
                     + " right now. Published: " + record);
               }
               else
               {
                  block.SetResult(StatusLevel.Warning, "Published, but different",
                     "The record at " + host + " carries p=" + policy + ", which is not a policy receivers recognise "
                     + "(none, quarantine, reject). Published: " + record);
               }
            });
         });
      }

      // ---- MTA-STS ---------------------------------------------------------------

      private Border MtaStsCard(DomainInfo domain)
      {
         Border card = Card("MTA-STS — require TLS from servers delivering to " + domain.Name, out StackPanel content);

         content.Children.Add(Paragraph(
            "Two halves: a TXT record that tells senders a policy exists, and the policy itself, fetched from "
            + "https://mta-sts." + domain.Name + "/.well-known/mta-sts.txt. This server serves that policy itself - there "
            + "is no file to upload - when MtaStsHostingEnabled is on and an HTTPS web-services listener is running "
            + "(RFC 8461 section 3.3 allows HTTPS only). The name mta-sts." + domain.Name + " also needs an A or CNAME "
            + "record pointing at this server, and the listener's certificate must cover that name.",
            Typography.Caption));

         // The id's only job is to CHANGE whenever the policy changes, so senders
         // refetch instead of trusting their cache. Any value is as correct as
         // this one; a timestamp just makes "newer" self-evident.
         string suggestedId = DateTime.UtcNow.ToString("yyyyMMddHHmm");
         string suggested = "v=STSv1; id=" + suggestedId;

         RecordBlock block = BuildRecordBlock(content, "dns-records-mtasts", "MTA-STS",
            host: "_mta-sts." + domain.Name,
            hostNote: null,
            value: suggested,
            copyWarning: null);

         content.Children.Add(Paragraph(
            "The id (" + suggestedId + ", generated just now) is yours to choose - any value is correct. Its only job is "
            + "to change whenever the policy changes, so caches refetch.",
            Typography.Caption));

         block.Check.Click += async (s, e) => await CheckMtaStsTxt(block, domain.Name);

         // ---- the policy the server will serve ----
         content.Children.Add(Label("Policy file the server will serve (read-only; served by the server itself):"));

         var policyBox = ReadOnlyBox(70);
         policyBox.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         System.Windows.Automation.AutomationProperties.SetName(policyBox, "MTA-STS policy file content the server will serve");
         System.Windows.Automation.AutomationProperties.SetAutomationId(policyBox, "dns-records-mtasts-policy");
         content.Children.Add(policyBox);

         var policyNote = new TextBlock
         {
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
            Opacity = 0.75
         };
         content.Children.Add(policyNote);

         BuildMtaStsPolicyPreview(domain.Name, policyBox, policyNote);

         // ---- fetch what is actually served ----
         var fetchButton = new Wpf.Ui.Controls.Button { Content = "Check _policy (HTTPS)", Margin = new Thickness(0, 4, 0, 0) };
         System.Windows.Automation.AutomationProperties.SetName(fetchButton,
            "Fetch the MTA-STS policy for " + domain.Name + " over HTTPS and show what is served");
         System.Windows.Automation.AutomationProperties.SetAutomationId(fetchButton, "dns-records-mtasts-fetch");
         content.Children.Add(fetchButton);

         var fetchMark = new Path { Width = 11, Height = 11, Stretch = System.Windows.Media.Stretch.Fill, Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
         var fetchText = new TextBlock { FontSize = Typography.Label, TextWrapping = TextWrapping.Wrap };
         content.Children.Add(ResultRow(fetchMark, fetchText));
         SetResultOn(fetchMark, fetchText, StatusLevel.Normal, "Not checked",
            "Press \"Check policy (HTTPS)\" to fetch https://mta-sts." + domain.Name + "/.well-known/mta-sts.txt from this machine.");

         fetchButton.Click += async (s, e) => await FetchMtaStsPolicy(domain.Name, fetchButton, fetchMark, fetchText);

         return card;
      }

      /// <summary>
      /// Reproduces the policy body HandleMtaStsPolicy_ will serve, from the same
      /// inputs: mode and max_age from hMailServer.INI (clamped the way the server
      /// clamps them), mx lines from the MtaStsPolicyMx override when set,
      /// otherwise from the domain's MX records - which the server looks up live
      /// at request time, so the lookup here is this machine's view of the same
      /// answer - falling back to the server host name, as the server does.
      /// </summary>
      private void BuildMtaStsPolicyPreview(string domainName, TextBox policyBox, TextBlock policyNote)
      {
         bool local = IsLocalServer();
         IniFeatureStore ini = local ? new IniFeatureStore() : null;
         bool iniReadable = ini != null && ini.IsAvailable;

         string mode = "enforce";
         int maxAge = 604800;
         string mxOverride = "";
         bool hosting = true;
         int httpsPort = 0;

         if (iniReadable)
         {
            // Defaults and clamping mirror IniFileSettings.cpp and
            // WebServicesServer::HandleMtaStsPolicy_ exactly.
            hosting = ini.ReadBool("MtaStsHostingEnabled", true);
            httpsPort = int.TryParse(ini.Read("WebServicesHttpsPort", "0").Trim(), out int p) ? p : 0;
            mode = ini.Read("MtaStsPolicyMode", "enforce").Trim().ToLowerInvariant();
            if (mode != "enforce" && mode != "testing" && mode != "none")
               mode = "enforce";
            maxAge = int.TryParse(ini.Read("MtaStsPolicyMaxAge", "604800").Trim(), out int a) ? a : 604800;
            if (maxAge < 86400) maxAge = 86400;
            if (maxAge > 31557600) maxAge = 31557600;
            mxOverride = ini.Read("MtaStsPolicyMx", "").Trim();
         }

         string caveat = iniReadable
            ? "Mode and max_age read from hMailServer.INI on this machine."
            : (local
               ? "hMailServer.INI was not found on this machine, so mode and max_age shown are the shipped defaults."
               : "Connected to a remote server: hMailServer.INI cannot be read from here, so mode and max_age shown are the shipped defaults.");

         if (iniReadable && !hosting)
            caveat += " NOTE: MtaStsHostingEnabled=0, so the server will NOT serve this policy at all.";
         else if (iniReadable && httpsPort <= 0)
            caveat += " NOTE: WebServicesHttpsPort=0, so no HTTPS listener runs and the policy cannot be served "
                      + "(RFC 8461 allows HTTPS only). The TXT record would advertise a policy nothing serves.";

         List<string> mxHosts = null;
         string mxNote;

         if (mxOverride.Length > 0)
         {
            mxHosts = mxOverride.Split(',').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).ToList();
            mxNote = "mx lines from the MtaStsPolicyMx override in hMailServer.INI.";
         }
         else
         {
            mxNote = "mx lines are derived from " + domainName + "'s live MX records at request time; the lookup below is "
                     + "this machine's view of the same answer.";
         }

         void Render(List<string> hosts, string extra)
         {
            var body = new StringBuilder();
            body.Append("version: STSv1\r\n");
            body.Append("mode: ").Append(mode).Append("\r\n");
            foreach (string mx in hosts)
               body.Append("mx: ").Append(mx).Append("\r\n");
            body.Append("max_age: ").Append(maxAge).Append("\r\n");
            policyBox.Text = body.ToString();
            policyNote.Text = caveat + " " + mxNote + (extra.Length > 0 ? " " + extra : "");
         }

         if (mxHosts != null)
         {
            Render(mxHosts, "");
            return;
         }

         Render(new List<string> { "(looking up " + domainName + "'s MX records…)" }, "");

         Task.Run(() => QueryMxRecords(domainName)).ContinueWith(t =>
         {
            List<string> found = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
            if (found != null && found.Count > 0)
            {
               Render(found, "");
            }
            else if (serverHostName_.Length > 0)
            {
               // The server's own fallback when a domain has no MX records:
               // GetPolicyMxHosts_ uses Configuration::GetHostName().
               Render(new List<string> { serverHostName_.ToLowerInvariant() },
                  "No MX records were found from this machine, so the server would fall back to its own host name.");
            }
            else
            {
               Render(new List<string> { "(no MX records found, and the server host name is not set - the server would serve no policy)" }, "");
            }
         }, TaskScheduler.FromCurrentSynchronizationContext());
      }

      private async Task CheckMtaStsTxt(RecordBlock block, string domainName)
      {
         string host = "_mta-sts." + domainName;

         await RunCheck(block, host, () =>
         {
            DnsTxtLookup.LookupResult result = DnsTxtLookup.Query(host);
            return (Action)(() =>
            {
               if (result.Status == DnsTxtLookup.LookupStatus.Failed)
               {
                  SetLookupFailed(block, host, result.Error);
                  return;
               }

               // RFC 8461: a policy-discovery record begins "v=STSv1". The id is
               // the administrator's to choose, so any id counts as correct here.
               List<string> sts = result.Records
                  .Where(r => (r ?? "").Replace(" ", "").StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase))
                  .ToList();

               if (sts.Count == 0)
               {
                  if (result.Records.Count > 0)
                     block.SetResult(StatusLevel.Warning, "Not published",
                        result.Records.Count + " TXT record(s) exist at " + host + ", but none begins \"v=STSv1\", so "
                        + "senders see no policy." + DescribeFound(result.Records));
                  else
                     block.SetResult(StatusLevel.Warning, "Not published", NotFoundAdvice(host));
                  return;
               }

               string id = ParseTag(sts[0], "id");
               if (id == null)
               {
                  block.SetResult(StatusLevel.Warning, "Published, but incomplete",
                     "The record at " + host + " has no id= tag, which RFC 8461 requires - senders may never notice "
                     + "policy updates. Published: " + sts[0]);
               }
               else
               {
                  block.SetResult(StatusLevel.Good, "Published and correct",
                     "A v=STSv1 record is live at " + host + " with id " + id + ". The id being different from the "
                     + "suggestion is fine - it is yours to choose; change it whenever the policy changes.");
               }
            });
         });
      }

      /// <summary>
      /// Fetches the policy exactly the way a sending server does: over HTTPS, no
      /// redirects (RFC 8461 section 3.3). A success proves, from this machine,
      /// that mta-sts.&lt;domain&gt; resolves, the certificate validates for that
      /// name, the listener answers, and what it serves - the internet's view can
      /// still differ if a firewall treats this machine specially, and the result
      /// says so rather than claiming more than one vantage point can know.
      /// </summary>
      private async Task FetchMtaStsPolicy(string domainName, Wpf.Ui.Controls.Button button, Path mark, TextBlock text)
      {
         string url = "https://mta-sts." + domainName + "/.well-known/mta-sts.txt";

         button.IsEnabled = false;
         SetResultOn(mark, text, StatusLevel.Information, "Checking…", "Fetching " + url + "…");

         try
         {
            HttpResponseMessage response = await http_.GetAsync(url);

            int code = (int)response.StatusCode;

            if (code >= 300 && code < 400)
            {
               SetResultOn(mark, text, StatusLevel.Warning, "Not served",
                  url + " answered with a redirect (HTTP " + code + "). Sending servers must not follow redirects "
                  + "(RFC 8461 section 3.3), so this counts as no policy.");
               return;
            }

            if (code != 200)
            {
               SetResultOn(mark, text, StatusLevel.Warning, "Not served",
                  url + " answered HTTP " + code + ". The server serves the policy only when MtaStsHostingEnabled is on, "
                  + "the domain is hosted and active, and the request's Host header is mta-sts." + domainName + ".");
               return;
            }

            string bodyText = await response.Content.ReadAsStringAsync();
            string snippet = bodyText.Length > 400 ? bodyText.Substring(0, 400) + "…" : bodyText;

            if (bodyText.TrimStart().StartsWith("version:", StringComparison.OrdinalIgnoreCase) && bodyText.Contains("STSv1"))
            {
               SetResultOn(mark, text, StatusLevel.Good, "Served",
                  "The policy is being served over HTTPS. From this machine that also proves mta-sts." + domainName
                  + " resolves, the certificate validates for that name, and the listener answers - the internet's view can "
                  + "still differ if a firewall treats this machine specially. Served content: " + snippet);
            }
            else
            {
               SetResultOn(mark, text, StatusLevel.Warning, "Answered, but not with a policy",
                  url + " answered HTTP 200, but the body is not an MTA-STS policy (it does not begin \"version: STSv1\"). "
                  + "Something other than the MTA-STS host is answering that URL. Served: " + snippet);
            }
         }
         catch (TaskCanceledException)
         {
            SetResultOn(mark, text, StatusLevel.Warning, "Not served",
               url + " did not answer within 10 seconds from this machine. Likely causes: no A/CNAME record for mta-sts."
               + domainName + ", the HTTPS listener is not running (WebServicesHttpsPort), or a firewall in between. "
               + "Checked from this machine only.");
         }
         catch (HttpRequestException ex)
         {
            string reason = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
            SetResultOn(mark, text, StatusLevel.Warning, "Not served",
               url + " could not be fetched from this machine: " + reason + " Likely causes: no A/CNAME record for mta-sts."
               + domainName + ", the HTTPS listener is not running (WebServicesHttpsPort=0 is the shipped default), or a "
               + "certificate that does not cover mta-sts." + domainName + ". Checked from this machine only.");
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            SetResultOn(mark, text, StatusLevel.Information, "Lookup failed",
               "The fetch itself failed: " + ex.Message + " This says nothing about the policy.");
         }
         finally
         {
            button.IsEnabled = true;
         }
      }

      // ---- TLS-RPT ---------------------------------------------------------------

      private Border TlsRptCard(DomainInfo domain)
      {
         Border card = Card("TLS-RPT — ask other servers to report TLS failures when delivering to " + domain.Name, out StackPanel content);

         content.Children.Add(Paragraph(
            "Publishing this record asks every server that delivers to " + domain.Name + " to e-mail you a daily report "
            + "when TLS to you fails or is downgraded. The reports go to the rua= mailbox, which must exist and accept "
            + "mail from strangers. This works regardless of any hMailServer setting - the senders do the reporting.",
            Typography.Caption));

         bool havePostmaster = domain.Postmaster.Contains("@");

         var addressRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
         addressRow.Children.Add(new TextBlock
         {
            Text = "Report mailbox:",
            FontSize = Typography.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
         });
         var addressBox = new TextBox
         {
            Text = havePostmaster ? domain.Postmaster : "",
            FontSize = Typography.Body,
            MinWidth = 280,
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center
         };
         System.Windows.Automation.AutomationProperties.SetName(addressBox, "Mailbox that receives TLS reports for " + domain.Name);
         System.Windows.Automation.AutomationProperties.SetAutomationId(addressBox, "dns-records-tlsrpt-address");
         addressRow.Children.Add(addressBox);
         content.Children.Add(addressRow);

         content.Children.Add(Paragraph(
            havePostmaster
               ? "Pre-filled with the domain's postmaster address, as configured on the domain. Change it if reports should go elsewhere."
               : domain.Name + " has no postmaster address configured, and this page will not invent a mailbox - enter the "
                 + "address that should receive the reports. Until one is entered there is no record to copy.",
            Typography.Caption));

         RecordBlock block = BuildRecordBlock(content, "dns-records-tlsrpt", "TLS-RPT",
            host: "_smtp._tls." + domain.Name,
            hostNote: null,
            value: BuildTlsRptValue(addressBox.Text.Trim()),
            copyWarning: null,
            valueCopyable: addressBox.Text.Trim().Contains("@"));

         addressBox.TextChanged += (s, e) =>
         {
            string address = addressBox.Text.Trim();
            bool usable = address.Contains("@");
            block.ValueBox.Text = BuildTlsRptValue(address);
            block.CopyValue.IsEnabled = usable;
            block.CopyValue.ToolTip = usable ? null : "Enter a report mailbox first - a record without a real rua= mailbox asks for nothing.";
         };
         ToolTipService.SetShowOnDisabled(block.CopyValue, true);
         if (!addressBox.Text.Trim().Contains("@"))
            block.CopyValue.ToolTip = "Enter a report mailbox first - a record without a real rua= mailbox asks for nothing.";

         // The other direction, read from the server's own code: TlsRptReporterTask
         // collects statistics hourly but discards each completed day unsent while
         // TlsRptFromAddress is empty in [Settings] of hMailServer.INI.
         string sendingNote;
         if (IsLocalServer())
         {
            var ini = new IniFeatureStore();
            if (ini.IsAvailable)
            {
               string from = ini.Read("TlsRptFromAddress", "").Trim();
               sendingNote = from.Length > 0
                  ? "Separately, this server SENDS such reports to other domains: TlsRptFromAddress is set to " + from + ", so its own reports go out."
                  : "Separately, this server's own SENDING of such reports to other domains is inert: TlsRptFromAddress is "
                    + "empty in hMailServer.INI (the default), so it collects statistics and discards every completed day "
                    + "unsent. Set it on the Transport security page if you want to send reports too.";
            }
            else
            {
               sendingNote = "Separately, this server sends its own reports to other domains only when TlsRptFromAddress is set "
                             + "in hMailServer.INI - which could not be found on this machine to check.";
            }
         }
         else
         {
            sendingNote = "Separately, this server sends its own reports to other domains only when TlsRptFromAddress is set "
                          + "in hMailServer.INI on the server - not readable from this machine.";
         }
         content.Children.Add(Paragraph(sendingNote, Typography.Caption));

         block.Check.Click += async (s, e) => await CheckTlsRpt(block, domain.Name, addressBox);

         return card;
      }

      private static string BuildTlsRptValue(string address)
         => address.Contains("@")
            ? "v=TLSRPTv1; rua=mailto:" + address
            : "v=TLSRPTv1; rua=mailto:<enter a report mailbox above>";

      private async Task CheckTlsRpt(RecordBlock block, string domainName, TextBox addressBox)
      {
         string host = "_smtp._tls." + domainName;
         string suggestedAddress = addressBox.Text.Trim();

         await RunCheck(block, host, () =>
         {
            DnsTxtLookup.LookupResult result = DnsTxtLookup.Query(host);
            return (Action)(() =>
            {
               if (result.Status == DnsTxtLookup.LookupStatus.Failed)
               {
                  SetLookupFailed(block, host, result.Error);
                  return;
               }

               List<string> rpt = result.Records
                  .Where(r => (r ?? "").Trim().StartsWith("v=TLSRPTv1", StringComparison.OrdinalIgnoreCase))
                  .ToList();

               if (rpt.Count == 0)
               {
                  if (result.Records.Count > 0)
                     block.SetResult(StatusLevel.Warning, "Not published",
                        result.Records.Count + " TXT record(s) exist at " + host + ", but none begins \"v=TLSRPTv1\"."
                        + DescribeFound(result.Records));
                  else
                     block.SetResult(StatusLevel.Warning, "Not published", NotFoundAdvice(host));
                  return;
               }

               string record = rpt[0];
               string rua = ParseTag(record, "rua");

               if (rua == null || rua.Length == 0)
               {
                  block.SetResult(StatusLevel.Warning, "Published, but incomplete",
                     "The record at " + host + " has no rua= tag, so it asks for reports and gives senders nowhere to "
                     + "send them. Published: " + record);
               }
               else if (suggestedAddress.Contains("@")
                     && rua.IndexOf("mailto:" + suggestedAddress, StringComparison.OrdinalIgnoreCase) >= 0)
               {
                  block.SetResult(StatusLevel.Good, "Published and correct",
                     "A v=TLSRPTv1 record is live at " + host + " and its reports go to " + suggestedAddress + ".");
               }
               else
               {
                  block.SetResult(StatusLevel.Information, "Published, differs from the suggestion",
                     "A v=TLSRPTv1 record is live at " + host + " with rua=" + rua + " - a different report mailbox than "
                     + "the one above, which is fine as long as it exists and is read. Published: " + record);
               }
            });
         });
      }

      // ---- reverse DNS / PTR and the HELO name ------------------------------------

      /// <summary>
      /// The one entry on this page the administrator cannot publish themselves,
      /// said plainly instead of pretended otherwise. The PTR record for the
      /// server's public IP address lives in the reverse zone owned by whoever
      /// owns the IP - usually the hosting provider or ISP - and is changed by
      /// asking them, not by editing the domain's own zone. The page also does not
      /// know the server's public IP (behind NAT it is not visible from here at
      /// all), so it shows the one half it can know: the HELO name the PTR should
      /// agree with.
      /// </summary>
      private Border PtrCard()
      {
         Border card = Card("Reverse DNS (PTR) and the HELO name — set by whoever owns your IP address", out StackPanel content);

         content.Children.Add(Paragraph(
            "Receivers look up the PTR record of the connecting IP address and compare it with the name the server "
            + "announces in HELO/EHLO; this server judges incoming mail by the same standard when the PTR and HELO checks "
            + "on Anti-spam settings are on. Unlike every other record on this page, the PTR is NOT published in "
            + "your domain's zone: it lives in the reverse zone of the IP address, which belongs to whoever owns the "
            + "address - usually your hosting provider or ISP. Ask them to set it; there is nothing to paste into your "
            + "own DNS console.",
            Typography.Caption));

         string heloName;
         string heloNote;

         if (!serverHostNameKnown_)
         {
            heloName = "(could not be read from the server)";
            heloNote = "The server's host name could not be read, so the name it announces in HELO/EHLO is unknown from here.";
         }
         else if (serverHostName_.Length > 0)
         {
            heloName = serverHostName_;
            heloNote = "This is Settings > Delivery of e-mail > Host name, the name the server announces in HELO/EHLO on "
                       + "every outbound delivery (Utilities::ComputerName). Ask the IP's owner to make the PTR of your "
                       + "public IP resolve to exactly this name, and publish an A record for this name pointing back at "
                       + "the same IP - receivers check both directions.";
         }
         else
         {
            heloName = IsLocalServer() ? Environment.MachineName : "(the server's Windows computer name)";
            heloNote = "The Host name on Delivery of e-mail is EMPTY, so the server falls back to its Windows computer name"
                       + (IsLocalServer() ? " - shown above as this machine's name" : ", which cannot be read from this machine")
                       + ". A bare computer name is not a public DNS name: receivers that compare HELO with DNS will "
                       + "penalise it, and no PTR can agree with it. Set a real host name first.";
         }

         content.Children.Add(Label("Host name this server announces (give this to the IP's owner for the PTR):"));
         TextBox heloBox = ReadOnlyBox(0);
         heloBox.Text = heloName;
         heloBox.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         System.Windows.Automation.AutomationProperties.SetName(heloBox, "Host name this server announces in HELO");
         System.Windows.Automation.AutomationProperties.SetAutomationId(heloBox, "dns-records-ptr-helo");
         content.Children.Add(heloBox);

         var copy = new Wpf.Ui.Controls.Button { Content = "Copy _host name", Margin = new Thickness(0, 0, 0, 6) };
         System.Windows.Automation.AutomationProperties.SetName(copy, "Copy the HELO host name");
         System.Windows.Automation.AutomationProperties.SetAutomationId(copy, "dns-records-ptr-copy");
         bool copyable = serverHostNameKnown_ && serverHostName_.Length > 0;
         copy.IsEnabled = copyable;
         if (!copyable)
         {
            copy.ToolTip = "There is no configured host name to copy yet.";
            ToolTipService.SetShowOnDisabled(copy, true);
         }
         copy.Click += (s, e) => CopyToClipboard(serverHostName_);
         content.Children.Add(copy);

         content.Children.Add(Paragraph(heloNote, Typography.Caption));

         content.Children.Add(Paragraph(
            "Not checked from here, honestly: verifying the PTR needs the server's PUBLIC IP address, which this panel "
            + "cannot determine (behind NAT the machine does not know it either), and this page's checker reads TXT "
            + "records only. Test from outside instead: nslookup <your public IP> shows the PTR; it should name the host "
            + "above.",
            Typography.Caption));

         var links = new StackPanel { Orientation = Orientation.Horizontal };
         links.Children.Add(PageLink("delivery", "Host name setting…", "Open Delivery of e-mail, which owns the Host name"));
         links.Children.Add(PageLink("antispam", "PTR / HELO checks…", "Open Anti-spam settings, which owns the inbound PTR and HELO checks"));
         content.Children.Add(links);

         return card;
      }

      // ---- the record block: type + host + value, each copyable, plus a check ----

      private sealed class RecordBlock
      {
         public TextBox HostBox;
         public TextBox ValueBox;
         public Wpf.Ui.Controls.Button CopyValue;
         public Wpf.Ui.Controls.Button Check;
         public Path Mark;
         public TextBlock Result;

         public void SetResult(StatusLevel level, string word, string message)
            => SetResultOn(Mark, Result, level, word, message);
      }

      /// <summary>
      /// One record, presented the way a DNS provider's console asks for it: the
      /// type stated in words, the host and the value in separate read-only
      /// selectable boxes with their own copy buttons - values that must be copied
      /// exactly never live in labels - and a Check button with a four-way result.
      /// A warning passed in is rendered AT the copy buttons, because a danger
      /// stated in a footnote is a danger stated nowhere.
      /// </summary>
      private RecordBlock BuildRecordBlock(StackPanel parent, string idPrefix, string recordName,
         string host, string hostNote, string value, string copyWarning, bool valueCopyable = true)
      {
         var block = new RecordBlock();

         parent.Children.Add(Label("Record type: TXT.  Host/Name" + (hostNote != null ? " — " + hostNote : "") + ":"));

         block.HostBox = ReadOnlyBox(0);
         block.HostBox.Text = host;
         block.HostBox.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         System.Windows.Automation.AutomationProperties.SetName(block.HostBox, recordName + " record host name");
         System.Windows.Automation.AutomationProperties.SetAutomationId(block.HostBox, idPrefix + "-host");
         parent.Children.Add(block.HostBox);

         parent.Children.Add(Label("TXT value:"));

         block.ValueBox = ReadOnlyBox(44);
         block.ValueBox.Text = value;
         block.ValueBox.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         System.Windows.Automation.AutomationProperties.SetName(block.ValueBox, recordName + " record value");
         System.Windows.Automation.AutomationProperties.SetAutomationId(block.ValueBox, idPrefix + "-value");
         parent.Children.Add(block.ValueBox);

         if (copyWarning != null)
         {
            StatusPresentation warning = StatusSemantics.For(StatusLevel.Warning);
            var row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var mark = new Path { Width = 11, Height = 11, Stretch = System.Windows.Media.Stretch.Fill, Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            ShapeMarkVisuals.ApplyMark(mark, warning.Shape, warning.BrushKey);
            row.Children.Add(mark);

            var text = new TextBlock { FontSize = Typography.Label, TextWrapping = TextWrapping.Wrap };
            var word = new Run("Before you copy — ") { FontWeight = FontWeights.SemiBold };
            word.SetResourceReference(TextElement.ForegroundProperty, warning.BrushKey);
            text.Inlines.Add(word);
            text.Inlines.Add(new Run(copyWarning));
            System.Windows.Automation.AutomationProperties.SetName(text, "Warning before copying. " + copyWarning);
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            parent.Children.Add(row);
         }

         var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };

         var copyHost = new Wpf.Ui.Controls.Button { Content = "Copy host" };   // per record: no access key, the rows are reached with the arrow keys
         System.Windows.Automation.AutomationProperties.SetName(copyHost, "Copy the " + recordName + " record host name");
         System.Windows.Automation.AutomationProperties.SetAutomationId(copyHost, idPrefix + "-copy-host");
         copyHost.Click += (s, e) => CopyToClipboard(block.HostBox.Text);
         copyRow.Children.Add(copyHost);

         block.CopyValue = new Wpf.Ui.Controls.Button { Content = "Copy value", Margin = new Thickness(8, 0, 0, 0), IsEnabled = valueCopyable };   // per record: no access key, the rows are reached with the arrow keys
         System.Windows.Automation.AutomationProperties.SetName(block.CopyValue, "Copy the " + recordName + " record value");
         System.Windows.Automation.AutomationProperties.SetAutomationId(block.CopyValue, idPrefix + "-copy-value");
         if (!valueCopyable)
         {
            block.CopyValue.ToolTip = "The value cannot be produced from this machine, so there is nothing correct to copy.";
            ToolTipService.SetShowOnDisabled(block.CopyValue, true);
         }
         block.CopyValue.Click += (s, e) => { if (block.CopyValue.IsEnabled) CopyToClipboard(block.ValueBox.Text); };
         copyRow.Children.Add(block.CopyValue);

         block.Check = new Wpf.Ui.Controls.Button { Content = "Check", Margin = new Thickness(8, 0, 0, 0) };   // per record: no access key, the rows are reached with the arrow keys
         System.Windows.Automation.AutomationProperties.SetName(block.Check, "Check whether the " + recordName + " record is published in DNS");
         System.Windows.Automation.AutomationProperties.SetAutomationId(block.Check, idPrefix + "-check");
         copyRow.Children.Add(block.Check);

         parent.Children.Add(copyRow);

         block.Mark = new Path { Width = 11, Height = 11, Stretch = System.Windows.Media.Stretch.Fill, Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
         block.Result = new TextBlock { FontSize = Typography.Label, TextWrapping = TextWrapping.Wrap };
         parent.Children.Add(ResultRow(block.Mark, block.Result));

         block.SetResult(StatusLevel.Normal, "Not checked", "Press \"Check\" to look the record up through this machine's resolver.");

         return block;
      }

      private static Grid ResultRow(Path mark, TextBlock text)
      {
         var row = new Grid { Margin = new Thickness(0, 4, 0, 6) };
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         row.Children.Add(mark);
         Grid.SetColumn(text, 1);
         row.Children.Add(text);
         return row;
      }

      /// <summary>
      /// Colour, shape AND word, so none of the three is load-bearing on its own -
      /// the same three channels every status badge in this application uses.
      /// </summary>
      private static void SetResultOn(Path mark, TextBlock text, StatusLevel level, string word, string message)
      {
         StatusPresentation presentation = StatusSemantics.For(level);
         ShapeMarkVisuals.ApplyMark(mark, presentation.Shape, presentation.BrushKey);

         text.Inlines.Clear();
         var wordRun = new Run(word + " — ") { FontWeight = FontWeights.SemiBold };
         wordRun.SetResourceReference(TextElement.ForegroundProperty, presentation.BrushKey);
         text.Inlines.Add(wordRun);
         text.Inlines.Add(new Run(message));

         System.Windows.Automation.AutomationProperties.SetName(text, word + ". " + message);
      }

      /// <summary>
      /// Runs one check: disables the button, shows "checking", runs the blocking
      /// lookup on a worker thread (it blocks for the resolver's full timeout when
      /// the record is missing - the common case right after publishing), then
      /// applies the verdict the worker prepared. DnsTxtLookup never throws; it
      /// reports through its results.
      /// </summary>
      private static async Task RunCheck(RecordBlock block, string host, Func<Action> work)
      {
         if (!block.Check.IsEnabled)
            return;

         block.Check.IsEnabled = false;
         block.SetResult(StatusLevel.Information, "Checking…", "Looking up " + host + "…");

         try
         {
            Action apply = await Task.Run(work);
            apply();
         }
         finally
         {
            block.Check.IsEnabled = true;
         }
      }

      private static void SetLookupFailed(RecordBlock block, string host, string error)
      {
         block.SetResult(StatusLevel.Information, "Lookup failed",
            "The DNS lookup for " + host + " itself failed: " + error + " This says nothing about the record - this "
            + "machine could not get an answer from its DNS server. Check the network and try again.");
      }

      private const string PropagationAdvice =
         "Publish it at your DNS provider - or, if you just did, wait for propagation (usually minutes, sometimes hours) and check again.";

      private static string NotFoundAdvice(string host)
         => "No TXT record was found at " + host + ". " + PropagationAdvice;

      /// <summary>Shows what IS published beside any mismatch verdict - the
      /// administrator cannot fix a difference they cannot see.</summary>
      private static string DescribeFound(List<string> records)
      {
         if (records == null || records.Count == 0)
            return "";

         string first = records[0];
         if (first.Length > 160)
            first = first.Substring(0, 160) + "…";
         return " Found: " + first + (records.Count > 1 ? " (and " + (records.Count - 1) + " more)" : "");
      }

      /// <summary>Reads one tag from a semicolon-separated tag=value record
      /// (DMARC, MTA-STS, TLS-RPT all use the shape). Whitespace-tolerant, like
      /// the comparison in DnsTxtLookup.</summary>
      private static string ParseTag(string record, string tag)
      {
         foreach (string trimmed in (record ?? "").Split(';').Select(part => part.Trim()))
         {
            int equals = trimmed.IndexOf('=');
            if (equals > 0 && trimmed.Substring(0, equals).Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))
               return trimmed.Substring(equals + 1).Trim();
         }
         return null;
      }

      // ---- MX lookup for the MTA-STS policy preview -------------------------------

      /// <summary>
      /// MX lookup through the same Windows resolver DnsTxtLookup uses, with the
      /// same flags for the same reasons (bypass the cache: the administrator is
      /// often here moments after changing DNS; treat as FQDN: never let the
      /// machine's DNS suffix be appended). Lives here rather than in DnsTxtLookup
      /// because that class is deliberately TXT-only and is owned elsewhere.
      /// Returns the exchange host names ordered by preference, or an empty list;
      /// never throws.
      /// </summary>
      private static List<string> QueryMxRecords(string domain)
      {
         var results = new List<KeyValuePair<int, string>>();

         IntPtr records = IntPtr.Zero;
         try
         {
            int status = DnsApi.DnsQuery_W(domain.Trim(), DnsTypeMx,
               DnsQueryBypassCache | DnsQueryTreatAsFqdn, IntPtr.Zero, out records, IntPtr.Zero);

            if (status != 0)
               return new List<string>();

            int headerSize = Marshal.SizeOf<DnsRecordHeader>();

            for (IntPtr cursor = records; cursor != IntPtr.Zero;)
            {
               DnsRecordHeader header = Marshal.PtrToStructure<DnsRecordHeader>(cursor);

               if (header.Type == DnsTypeMx)
               {
                  // DNS_MX_DATAW: PWSTR pNameExchange at the start of the data
                  // area, WORD wPreference at the next pointer-aligned offset -
                  // IntPtr.Size in, on both x86 and x64, the same layout logic
                  // DnsTxtLookup documents for DNS_TXT_DATAW.
                  IntPtr data = cursor + headerSize;
                  IntPtr namePtr = Marshal.ReadIntPtr(data);
                  int preference = Marshal.ReadInt16(data, IntPtr.Size);

                  string exchange = namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr) : null;
                  if (!string.IsNullOrWhiteSpace(exchange))
                  {
                     exchange = exchange.Trim().TrimEnd('.').ToLowerInvariant();
                     if (exchange.Length > 0)
                        results.Add(new KeyValuePair<int, string>(preference, exchange));
                  }
               }

               cursor = header.Next;
            }

            return results.OrderBy(r => r.Key).Select(r => r.Value).Distinct().ToList();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return new List<string>();
         }
         finally
         {
            if (records != IntPtr.Zero)
               DnsApi.DnsRecordListFree(records, DnsFreeRecordList);
         }
      }

      private const ushort DnsTypeMx = 0x000f;              // DNS_TYPE_MX
      private const uint DnsQueryBypassCache = 0x00000008;  // DNS_QUERY_BYPASS_CACHE
      private const uint DnsQueryTreatAsFqdn = 0x00001000;  // DNS_QUERY_TREAT_AS_FQDN
      private const int DnsFreeRecordList = 1;              // DNS_FREE_TYPE::DnsFreeRecordList

      /// <summary>The fixed header of a native DNS_RECORDW; same layout notes as
      /// the copy in DnsTxtLookup, which is TXT-only by design.</summary>
      [StructLayout(LayoutKind.Sequential)]
      private struct DnsRecordHeader
      {
         public IntPtr Next;
         public IntPtr Name;
         public ushort Type;
         public ushort DataLength;
         public uint Flags;
         public uint Ttl;
         public uint Reserved;
      }

      // ---- shared UI helpers -------------------------------------------------------

      private static Border Card(string title, out StackPanel content)
      {
         var border = new Border { Margin = new Thickness(0, 14, 0, 0) };
         border.SetResourceReference(StyleProperty, "Card");

         var inner = new StackPanel();
         inner.Children.Add(new TextBlock
         {
            Text = title,
            FontSize = Typography.SectionHeading,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
         });

         border.Child = inner;
         content = inner;
         return border;
      }

      private static TextBlock Paragraph(string text, double size)
      {
         return new TextBlock
         {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
            Opacity = size <= Typography.Caption ? 0.75 : 1.0
         };
      }

      private static TextBlock Label(string text)
      {
         var label = new TextBlock { Text = text, FontSize = Typography.Label, Margin = new Thickness(0, 6, 0, 4) };
         label.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         return label;
      }

      /// <summary>A selectable, read-only box in the style of the DKIM rotation's
      /// record display: values the user must copy exactly live in these rather
      /// than in labels, so they can be selected.</summary>
      private static TextBox ReadOnlyBox(double minHeight) => new()
      {
         IsReadOnly = true,
         AcceptsReturn = true,
         TextWrapping = TextWrapping.Wrap,
         FontSize = Typography.Caption,
         MinHeight = minHeight,
         Padding = new Thickness(6),
         Margin = new Thickness(0, 0, 0, 4),
         Background = System.Windows.Media.Brushes.Transparent
      };

      private static void CopyToClipboard(string text)
      {
         try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
      }

      /// <summary>A button that opens the page owning a related setting - the same
      /// escape hatch the spam overview and External setup use.</summary>
      private static FrameworkElement PageLink(string page, string caption, string accessibleName)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = caption,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            FontSize = Typography.Caption,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 2, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = accessibleName
         };
         System.Windows.Automation.AutomationProperties.SetName(button, accessibleName);
         System.Windows.Automation.AutomationProperties.SetAutomationId(button, "dns-records-open-" + page);
         button.Click += (s, e) => (Application.Current?.MainWindow as MainWindow)?.NavigateTo(page);
         return button;
      }
   }
}
