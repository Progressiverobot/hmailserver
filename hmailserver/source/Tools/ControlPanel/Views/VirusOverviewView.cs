// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
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

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// One page that answers "will an infected message actually be caught here".
   ///
   /// The anti-virus settings page is a correct editor and cannot answer that,
   /// because the answer is not a setting. It is the interaction of four of them:
   /// which scanners are switched on, whether each has the configuration it needs to
   /// run at all, the size above which messages are quietly skipped, and what the
   /// server then does with a message a scanner has condemned.
   ///
   /// The failure this page exists to make visible is the one the roadmap keeps
   /// finding elsewhere: **a configuration that looks enabled and is inert**. A
   /// scanner switched on with a missing host or a missing executable does not
   /// refuse mail and does not disable itself. `VirusScanner::ScanFile_` reports the
   /// error at Medium and moves on, and when every scanner has errored the verdict
   /// is `NoVirusFound` — indistinguishable, from the message's point of view, from
   /// having been examined and found clean. Nothing in the settings page shows that,
   /// and the only trace is an error log nobody reads until afterwards.
   ///
   /// Read-only on purpose. It is a diagnosis, not a second editor: every row links
   /// to the page that owns the setting, so there is still exactly one place to
   /// change each value — which is also why this page cannot drift out of step.
   ///
   /// The judgement all lives in <see cref="VirusPipeline"/>, which has no WPF and is
   /// tested directly; this file reads the COM settings into a snapshot and draws it.
   /// </summary>
   public class VirusOverviewView : UserControl, IPageLifecycle
   {
      private readonly StackPanel body_ = new();
      private readonly TextBlock status_ = new();

      public VirusOverviewView()
      {
         var page = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };

         var header = new Grid();
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var heading = new StackPanel();
         var title = new TextBlock { Text = "Virus scanning overview" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         heading.Children.Add(title);

         var subtitle = new TextBlock
         {
            Text = "Which scanners can actually run, what they are asked to look at, and what happens to a message "
                   + "one of them condemns. Nothing on this page can be edited — each row opens the page that owns the setting."
         };
         subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
         heading.Children.Add(subtitle);
         header.Children.Add(heading);

         var refresh = new Wpf.Ui.Controls.Button
         {
            Content = "Refresh",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 4, 0, 0)
         };
         System.Windows.Automation.AutomationProperties.SetName(refresh, "Re-read the anti-virus configuration from the server");
         System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "virus-overview-refresh");
         refresh.Click += (s, e) => Reload();
         Grid.SetColumn(refresh, 1);
         header.Children.Add(refresh);

         page.Children.Add(header);
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

      // ---- reading the configuration -----------------------------------------

      private int failedReads_;
      private string firstError_;

      /// <summary>
      /// Reads the anti-virus settings into a snapshot.
      ///
      /// Every property goes through <see cref="Read{T}"/> so that one value the
      /// server cannot give us - an older build, a COM error - costs that value and
      /// its row rather than the whole page. The count of failures is shown at the
      /// bottom instead of being swallowed, because a page that quietly shows a
      /// blank host for a scanner it could not read would be reporting a critical
      /// fault that is not there.
      /// </summary>
      private VirusPipelineConfig ReadConfig()
      {
         failedReads_ = 0;
         firstError_ = null;

         var config = new VirusPipelineConfig();
         dynamic settings = null;
         dynamic antiVirus = null;

         try
         {
            settings = ServerSession.Current.Application.Settings;
            antiVirus = settings.AntiVirus;

            config.ClamAvEnabled = Read(() => (bool) antiVirus.ClamAVEnabled);
            config.ClamAvHost = Read(() => (string) antiVirus.ClamAVHost) ?? "";
            config.ClamAvPort = Read(() => (int) antiVirus.ClamAVPort);

            config.ClamWinEnabled = Read(() => (bool) antiVirus.ClamWinEnabled);
            config.ClamWinExecutable = Read(() => (string) antiVirus.ClamWinExecutable) ?? "";
            config.ClamWinDatabaseFolder = Read(() => (string) antiVirus.ClamWinDBFolder) ?? "";

            config.CustomScannerEnabled = Read(() => (bool) antiVirus.CustomScannerEnabled);
            config.CustomScannerExecutable = Read(() => (string) antiVirus.CustomScannerExecutable) ?? "";
            config.CustomScannerVirusReturnValue = Read(() => (int) antiVirus.CustomScannerReturnValue);

            config.MaxScanKilobytes = Read(() => (int) antiVirus.MaximumMessageSize);

            // eAntivirusAction: 0 = delete the message, 1 = strip the attachments.
            config.Action = Read(() => (int) antiVirus.Action) == 1
               ? VirusAction.StripAttachments
               : VirusAction.DeleteMessage;

            config.NotifySender = Read(() => (bool) antiVirus.NotifySender);
            config.NotifyRecipient = Read(() => (bool) antiVirus.NotifyReceiver);

            config.AttachmentBlockingEnabled = Read(() => (bool) antiVirus.EnableAttachmentBlocking);
            config.BlockedAttachmentPatterns = CountCollection(() => antiVirus.BlockedAttachments);

            CountFetchAccounts(config);
         }
         catch (Exception ex)
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object) antiVirus);
            ServerSession.Release((object) settings);
         }

         return config;
      }

      private T Read<T>(Func<T> read)
      {
         try
         {
            return read();
         }
         catch (Exception ex)
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
            return default;
         }
      }

      private int CountCollection(Func<object> open)
      {
         dynamic collection = null;
         try
         {
            collection = open();
            return (int) collection.Count;
         }
         catch (Exception ex)
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
            return 0;
         }
         finally
         {
            ServerSession.Release((object) collection);
         }
      }

      /// <summary>
      /// Counts external POP3 fetch accounts, and how many of them have their own
      /// anti-virus switch off.
      ///
      /// Worth the walk across every domain and account, because this is the one
      /// way mail reaches a mailbox with none of the settings on this page applied:
      /// SMTPDeliverer only scans a message whose virus-scan flag is set, and for a
      /// fetched message that flag comes from the fetch account rather than from
      /// anything here.
      ///
      /// Bounded, though. Fetch accounts hang off individual accounts and there is no
      /// server-wide collection of them, so this is one COM round trip per account
      /// and a large installation would leave the page unresponsive for as long as it
      /// took. The cap is not silent: past it the config is marked incomplete and the
      /// note says "at least", because a number presented as a total when it is a
      /// floor is worse than no number.
      /// </summary>
      private const int MaxAccountsWalked = 1000;

      private void CountFetchAccounts(VirusPipelineConfig config)
      {
         int accountsWalked = 0;
         dynamic domains = null;

         try
         {
            domains = ServerSession.Current.Application.Domains;
            int domainCount = (int) domains.Count;

            for (int d = 0; d < domainCount; d++)
            {
               dynamic domain = null;
               dynamic accounts = null;

               try
               {
                  domain = domains.Item[d];
                  accounts = domain.Accounts;
                  int accountCount = (int) accounts.Count;

                  for (int a = 0; a < accountCount; a++)
                  {
                     if (accountsWalked >= MaxAccountsWalked)
                     {
                        config.FetchAccountScanIncomplete = true;
                        return;
                     }

                     accountsWalked++;

                     dynamic account = null;
                     dynamic fetchAccounts = null;

                     try
                     {
                        account = accounts.Item[a];
                        fetchAccounts = account.FetchAccounts;
                        int fetchCount = (int) fetchAccounts.Count;

                        for (int f = 0; f < fetchCount; f++)
                        {
                           dynamic fetchAccount = null;

                           try
                           {
                              fetchAccount = fetchAccounts.Item[f];
                              config.FetchAccountsTotal++;

                              if (!(bool) fetchAccount.UseAntiVirus)
                                 config.FetchAccountsWithScanningOff++;
                           }
                           finally
                           {
                              ServerSession.Release((object) fetchAccount);
                           }
                        }
                     }
                     finally
                     {
                        ServerSession.Release((object) fetchAccounts);
                        ServerSession.Release((object) account);
                     }
                  }
               }
               finally
               {
                  ServerSession.Release((object) accounts);
                  ServerSession.Release((object) domain);
               }
            }
         }
         catch (Exception ex)
         {
            // Counted as one failed read rather than several: the page still has
            // everything else, and the fetch-account note simply does not appear.
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object) domains);
         }
      }

      // ---- drawing ------------------------------------------------------------

      private void Reload()
      {
         VirusPipelineConfig config = ReadConfig();

         body_.Children.Clear();
         body_.Children.Add(VerdictCard(config));
         body_.Children.Add(ScannersCard(config));
         body_.Children.Add(WhatIsScannedCard(config));
         body_.Children.Add(InfectedCard(config));
         body_.Children.Add(AttachmentsCard(config));

         IReadOnlyList<VirusPipelineNote> notes = VirusPipeline.Notes(config);
         if (notes.Count > 0)
            body_.Children.Add(NotesCard(notes));

         status_.Text = failedReads_ == 0
            ? "Read from the server. Values are read again every time this page is opened."
            : failedReads_ + " value(s) could not be read — " + firstError_
              + " The rows above may be incomplete, so treat a scanner shown as unusable here as unconfirmed.";
      }

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
            Margin = new Thickness(0, 0, 0, 10)
         });

         border.Child = inner;
         content = inner;
         return border;
      }

      private static Border VerdictCard(VirusPipelineConfig config)
      {
         Border card = Card("What happens to an infected message", out StackPanel content);

         content.Children.Add(Paragraph(VirusPipeline.Verdict(config), Typography.Body));

         content.Children.Add(Paragraph(
            "A scanner that cannot run does not refuse the message. The error is written to the log and the scan "
            + "continues to the next scanner; when none of them can answer, the message is treated exactly as if it "
            + "had been examined and found clean. That is why this page reports whether each scanner can run rather "
            + "than whether it is switched on.",
            Typography.Caption));

         return card;
      }

      private Border ScannersCard(VirusPipelineConfig config)
      {
         Border card = Card("Scanners, in the order the server tries them", out StackPanel content);

         var grid = new Grid();
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // order
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // name + detail
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // state
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // link

         int row = 0;
         foreach (VirusScannerEntry scanner in VirusPipeline.Scanners(config))
         {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddScannerRow(grid, row, scanner);
            row++;
         }

         content.Children.Add(grid);

         content.Children.Add(Paragraph(
            "The first scanner to find something ends the scan, so the order is also the cost: a slow scanner in "
            + "front of a fast one is paid for on every clean message.",
            Typography.Caption));

         return card;
      }

      private void AddScannerRow(Grid grid, int row, VirusScannerEntry scanner)
      {
         var order = new TextBlock
         {
            Text = scanner.Order.ToString(),
            FontSize = Typography.Caption,
            Margin = new Thickness(0, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Top
         };
         order.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
         Grid.SetRow(order, row);
         Grid.SetColumn(order, 0);
         grid.Children.Add(order);

         var text = new StackPanel { Margin = new Thickness(0, 6, 12, 6) };

         var name = new TextBlock
         {
            Text = scanner.Name,
            FontSize = Typography.Body,
            FontWeight = scanner.Usable ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            // Dimmed as well as labelled, because the label is what the reader who
            // cannot see the dimming gets. An enabled-but-broken scanner is NOT
            // dimmed - it is the one that most needs the eye drawn to it.
            Opacity = scanner.Enabled ? 1.0 : 0.6
         };

         // The whole row as the accessible name of the one element in it with an
         // automation peer. A TextBlock has one and a Panel does not, so putting it
         // on the surrounding StackPanel - the obvious place - would set a property
         // nothing ever reads, and the columns would be announced as unrelated
         // fragments for the listener to reassemble.
         System.Windows.Automation.AutomationProperties.SetName(name,
            scanner.Name + ", " + scanner.StateText + ". "
            + (scanner.Problem ?? scanner.Detail));

         text.Children.Add(name);

         var detail = new TextBlock
         {
            Text = scanner.Problem ?? scanner.Detail,
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
         };

         if (scanner.Problem != null)
            detail.SetResourceReference(TextBlock.ForegroundProperty, "AppDangerBrush");

         text.Children.Add(detail);
         Grid.SetRow(text, row);
         Grid.SetColumn(text, 1);
         grid.Children.Add(text);

         // The state as a word with a shape beside it, so neither the colour nor the
         // shape is load-bearing alone. "On, but cannot run" is a third state and is
         // deliberately not collapsed into either "On" or "Off": it is the state the
         // settings page cannot show and the whole reason for this one.
         var statePanel = new StackPanel
         {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 14, 6),
            VerticalAlignment = VerticalAlignment.Top
         };

         StatusLevel level = !scanner.Enabled ? StatusLevel.Normal
            : scanner.Usable ? StatusLevel.Good
            : StatusLevel.Critical;

         StatusPresentation presentation = StatusSemantics.For(level);

         var mark = new Path { Width = 10, Height = 10, Stretch = Stretch.Fill, Margin = new Thickness(0, 4, 6, 0), VerticalAlignment = VerticalAlignment.Top };
         ShapeMarkVisuals.ApplyMark(mark, presentation.Shape, presentation.BrushKey);
         statePanel.Children.Add(mark);

         var state = new TextBlock
         {
            Text = scanner.StateText,
            FontSize = Typography.Label,
            FontWeight = FontWeights.SemiBold,
            MaxWidth = 150,
            TextWrapping = TextWrapping.Wrap
         };
         state.SetResourceReference(TextBlock.ForegroundProperty, presentation.BrushKey);
         statePanel.Children.Add(state);

         Grid.SetRow(statePanel, row);
         Grid.SetColumn(statePanel, 2);
         grid.Children.Add(statePanel);

         FrameworkElement link = PageLink(scanner.Page, "Settings…",
            "Open " + NavigationMap.TitleOf(scanner.Page) + ", which owns the " + scanner.Name + " settings");
         link.Margin = new Thickness(0, 4, 0, 4);
         link.VerticalAlignment = VerticalAlignment.Top;
         Grid.SetRow(link, row);
         Grid.SetColumn(link, 3);
         grid.Children.Add(link);
      }

      private Border WhatIsScannedCard(VirusPipelineConfig config)
      {
         Border card = Card("What gets scanned", out StackPanel content);

         content.Children.Add(Paragraph(
            config.MaxScanKilobytes > 0
               ? "Messages up to " + VirusPipeline.Kilobytes(config.MaxScanKilobytes)
                 + ". A larger message is delivered without being scanned — it is not refused, and nothing is logged."
               : "Every message, whatever its size. The size limit is 0, which here means \"no limit\".",
            Typography.Body));

         content.Children.Add(Paragraph(
            "Each message is scanned twice over: once as the whole file on disk, and then once per attachment, each "
            + "written out to the temporary folder and handed to the scanner on its own. The second pass is what "
            + "catches an attachment inside a structure the scanner does not decode for itself. If the message cannot "
            + "be parsed as MIME the per-attachment pass is skipped and an error is logged.",
            Typography.Caption));

         if (config.FetchAccountsTotal > 0)
         {
            content.Children.Add(Paragraph(
               "Mail collected from external POP3 accounts is scanned only when that fetch account says so — "
               + config.FetchAccountsWithScanningOff + " of " + config.FetchAccountsTotal
               + " currently have anti-virus switched off. That switch is on the account, not on this page."
               + (config.FetchAccountScanIncomplete
                  ? " These are counts of the first " + MaxAccountsWalked
                    + " accounts only; this page stops there rather than making you wait for the rest."
                  : ""),
               Typography.Caption));
         }

         content.Children.Add(PageLink("antivirus", "Size limit and scanners…",
            "Open Anti-virus settings, which owns the maximum message size to scan"));

         return card;
      }

      private static Border InfectedCard(VirusPipelineConfig config)
      {
         Border card = Card("When a scanner finds something", out StackPanel content);

         content.Children.Add(Paragraph(VirusPipeline.ActionSummary(config), Typography.Body));

         return card;
      }

      private Border AttachmentsCard(VirusPipelineConfig config)
      {
         Border card = Card("Attachment blocking, which is separate", out StackPanel content);

         content.Children.Add(Paragraph(
            config.AttachmentBlockingEnabled
               ? config.BlockedAttachmentPatterns + (config.BlockedAttachmentPatterns == 1 ? " pattern is" : " patterns are")
                 + " stripped from every message by file name, whatever the scanners say."
               : "Switched off. " + config.BlockedAttachmentPatterns
                 + (config.BlockedAttachmentPatterns == 1 ? " pattern is" : " patterns are") + " configured and none is applied.",
            Typography.Body));

         content.Children.Add(Paragraph(
            "This runs before virus scanning and independently of it: it applies even with every scanner switched "
            + "off, and it applies to messages the size limit above would skip. A matched attachment is replaced by a "
            + "short text file explaining the removal rather than being deleted outright, so the recipient can tell "
            + "something was taken out.",
            Typography.Caption));

         content.Children.Add(PageLink("blockedattachments", "Patterns…",
            "Open Blocked attachments, which owns the list of file-name patterns"));

         return card;
      }

      private static Border NotesCard(IReadOnlyList<VirusPipelineNote> notes)
      {
         Border card = Card("Worth knowing about this configuration", out StackPanel content);

         foreach (VirusPipelineNote note in notes)
         {
            StatusPresentation presentation = StatusSemantics.For(note.Level);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Colour, shape and word, so that none of the three is load-bearing on
            // its own - the same three channels the dashboard status badges use.
            var mark = new Path { Width = 11, Height = 11, Stretch = Stretch.Fill, Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            ShapeMarkVisuals.ApplyMark(mark, presentation.Shape, presentation.BrushKey);
            row.Children.Add(mark);

            var text = new TextBlock
            {
               FontSize = Typography.Label,
               TextWrapping = TextWrapping.Wrap
            };
            var severity = new Run(presentation.SeverityWord + ": ") { FontWeight = FontWeights.SemiBold };
            text.Inlines.Add(severity);
            text.Inlines.Add(new Run(note.Text));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            content.Children.Add(row);
         }

         return card;
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

      /// <summary>
      /// A button that opens the page owning a setting.
      ///
      /// The whole page is read-only, so these links are the only way out of it, and
      /// they are the reason it can stay read-only: every value shown here is one
      /// click from the single place that edits it.
      /// </summary>
      private static FrameworkElement PageLink(string page, string caption, string accessibleName)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = caption,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            FontSize = Typography.Caption,
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = accessibleName,
            HorizontalAlignment = HorizontalAlignment.Left
         };
         System.Windows.Automation.AutomationProperties.SetName(button, accessibleName);
         System.Windows.Automation.AutomationProperties.SetAutomationId(button, "virus-overview-open-" + page);
         button.Click += (s, e) => (Application.Current?.MainWindow as MainWindow)?.NavigateTo(page);
         return button;
      }
   }
}
