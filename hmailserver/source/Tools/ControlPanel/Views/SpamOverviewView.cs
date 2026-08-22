// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
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

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// One page that answers "what will my spam filtering do to a message".
   ///
   /// Anti-spam was five pages and none of them could answer that, because the
   /// answer is not on any one of them: the checks and their scores are on
   /// Anti-spam settings, two more checks are lists on their own pages, and what a
   /// score then means is two thresholds whose interaction is documented nowhere.
   /// The roadmap put it as "there is no single page that answers what is my spam
   /// configuration".
   ///
   /// Read-only on purpose. It is a diagnosis, not a sixth editor: every row links
   /// to the page that owns the setting, so there is still exactly one place to
   /// change each value - which is also why this page cannot drift out of step with
   /// them.
   ///
   /// The judgement all lives in <see cref="SpamPipeline"/>, which has no WPF and is
   /// tested directly; this file reads the COM settings into a snapshot and draws
   /// it.
   /// </summary>
   public class SpamOverviewView : UserControl, IPageLifecycle
   {
      private readonly StackPanel body_ = new();
      private readonly TextBlock status_ = new();

      public SpamOverviewView()
      {
         var page = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };

         var header = new Grid();
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var heading = new StackPanel();
         var title = new TextBlock { Text = "Spam filtering overview" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         heading.Children.Add(title);

         var subtitle = new TextBlock
         {
            Text = "Every check in the order the server runs them, and what the score they add does to the message. "
                   + "Nothing on this page can be edited - each row opens the page that owns the setting."
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
         System.Windows.Automation.AutomationProperties.SetName(refresh, "Re-read the spam configuration from the server");
         System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "spam-overview-refresh");
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
      /// Reads the anti-spam settings into a snapshot.
      ///
      /// Every property is read through <see cref="Read{T}"/> so that one property
      /// the server does not have - an older build, a COM error - costs that one
      /// value and its row, rather than the whole page. The count of failures is
      /// shown at the bottom instead of being swallowed: a page that quietly shows
      /// zeroes for settings it could not read would be worse than an error, since
      /// zero thresholds are themselves a finding here.
      /// </summary>
      private SpamPipelineConfig ReadConfig()
      {
         failedReads_ = 0;
         firstError_ = null;

         var config = new SpamPipelineConfig();
         dynamic settings = null;
         dynamic antiSpam = null;

         try
         {
            settings = ServerSession.Current.Application.Settings;
            antiSpam = settings.AntiSpam;

            config.MarkThreshold = Read(() => (int) antiSpam.SpamMarkThreshold);
            config.DeleteThreshold = Read(() => (int) antiSpam.SpamDeleteThreshold);
            config.MaxScanKilobytes = Read(() => (int) antiSpam.MaximumMessageSize);
            config.BlockedSenderCount = Read(() => (int) antiSpam.BlockedSenders.Count);

            config.AddSpamHeader = Read(() => (bool) antiSpam.AddHeaderSpam);
            config.AddReasonHeader = Read(() => (bool) antiSpam.AddHeaderReason);
            config.PrependSubject = Read(() => (bool) antiSpam.PrependSubject);

            config.CheckHeloHost = Read(() => (bool) antiSpam.CheckHostInHelo);
            config.HeloHostScore = Read(() => (int) antiSpam.CheckHostInHeloScore);
            config.CheckPtr = Read(() => (bool) antiSpam.CheckPTR);
            config.PtrScore = Read(() => (int) antiSpam.CheckPTRScore);
            config.CheckSenderMx = Read(() => (bool) antiSpam.UseMXChecks);
            config.SenderMxScore = Read(() => (int) antiSpam.UseMXChecksScore);
            config.CheckSpf = Read(() => (bool) antiSpam.UseSPF);
            config.SpfScore = Read(() => (int) antiSpam.UseSPFScore);

            config.VerifyDkim = Read(() => (bool) antiSpam.DKIMVerificationEnabled);
            config.DkimFailureScore = Read(() => (int) antiSpam.DKIMVerificationFailureScore);
            config.EvaluateDmarc = Read(() => (bool) antiSpam.DMARCEnabled);
            config.DmarcFailureScore = Read(() => (int) antiSpam.DMARCFailureScore);

            config.SpamAssassinEnabled = Read(() => (bool) antiSpam.SpamAssassinEnabled);
            config.SpamAssassinMergesScore = Read(() => (bool) antiSpam.SpamAssassinMergeScore);
            config.SpamAssassinScore = Read(() => (int) antiSpam.SpamAssassinScore);
            config.SpamAssassinHost = Read(() => (string) antiSpam.SpamAssassinHost) ?? "";

            config.GreylistingEnabled = Read(() => (bool) antiSpam.GreyListingEnabled);
            config.BypassGreylistingOnSpfPass = Read(() => (bool) antiSpam.BypassGreylistingOnSPFSuccess);
            config.BypassGreylistingOnSenderMx = Read(() => (bool) antiSpam.BypassGreylistingOnMailFromMX);

            CountList(() => antiSpam.DNSBlackLists, true, out int dnsblActive, out int dnsblTotal);
            config.ActiveDnsBlackLists = dnsblActive;
            config.TotalDnsBlackLists = dnsblTotal;

            CountList(() => antiSpam.SURBLServers, true, out int surblActive, out int surblTotal);
            config.ActiveSurblServers = surblActive;
            config.TotalSurblServers = surblTotal;

            // No per-entry "active" flag on either white list, so every entry counts.
            CountList(() => antiSpam.WhiteListAddresses, false, out _, out int whiteList);
            config.WhiteListEntries = whiteList;

            CountList(() => antiSpam.GreyListingWhiteAddresses, false, out _, out int greyList);
            config.GreylistWhiteListEntries = greyList;
         }
         catch (Exception ex)
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object) antiSpam);
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

      /// <summary>
      /// Counts a COM collection, and how many of its entries are active.
      ///
      /// The active count is the part that matters: neither DNSBL nor SURBL has an
      /// enable switch of its own - SpamTestDNSBlackLists::GetIsEnabled returns true
      /// when at least one entry is active - so "3 configured" and "3 active" mean
      /// completely different things and only the second one runs. The two white
      /// lists have no per-entry flag at all, which is what
      /// <paramref name="entriesHaveAnActiveFlag"/> distinguishes.
      /// </summary>
      private void CountList(Func<object> open, bool entriesHaveAnActiveFlag, out int active, out int total)
      {
         active = 0;
         total = 0;

         dynamic collection = null;
         try
         {
            collection = open();
            total = (int) collection.Count;

            if (!entriesHaveAnActiveFlag)
            {
               active = total;
               return;
            }

            for (int i = 0; i < total; i++)
            {
               dynamic item = collection.Item[i];
               try
               {
                  if ((bool) item.Active)
                     active++;
               }
               finally
               {
                  ServerSession.Release((object) item);
               }
            }
         }
         catch (Exception ex)
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object) collection);
         }
      }

      // ---- drawing ------------------------------------------------------------

      private void Reload()
      {
         SpamPipelineConfig config = ReadConfig();

         body_.Children.Clear();
         body_.Children.Add(VerdictCard(config));
         body_.Children.Add(ChecksCard(config));
         body_.Children.Add(GreylistingCard(config));
         body_.Children.Add(ListsCard(config));

         IReadOnlyList<SpamPipelineNote> notes = SpamPipeline.Notes(config);
         if (notes.Count > 0)
            body_.Children.Add(NotesCard(notes));

         status_.Text = failedReads_ == 0
            ? "Read from the server. Values are read again every time this page is opened."
            : failedReads_ + " value(s) could not be read — " + firstError_
              + " The rows below may be incomplete.";
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

      private static Border VerdictCard(SpamPipelineConfig config)
      {
         Border card = Card("What happens to a message", out StackPanel content);

         content.Children.Add(Paragraph(SpamPipeline.Verdict(config), Typography.Body));

         int stop = SpamPipeline.StopScore(config);
         int? ceiling = SpamPipeline.HighestReachableScore(config);

         content.Children.Add(Paragraph(
            "The score is added up across both phases. The server stops testing within a phase as soon as the running "
            + "total reaches " + stop + " - the higher of the two thresholds - so a check late in the order may never run."
            + (ceiling != null
               ? " With the checks currently enabled the highest total reachable is " + ceiling.Value + "."
               : " The total is open-ended, because a list entry or SpamAssassin's own score can be any size."),
            Typography.Caption));

         return card;
      }

      private Border ChecksCard(SpamPipelineConfig config)
      {
         Border card = Card("Checks, in the order the server runs them", out StackPanel content);

         foreach (SpamCheckPhase phase in new[] { SpamCheckPhase.BeforeTheBody, SpamCheckPhase.AfterTheBody })
         {
            content.Children.Add(new TextBlock
            {
               Text = phase == SpamCheckPhase.BeforeTheBody
                  ? "At RCPT TO, before the body is transferred — a refusal here is a 550"
                  : "After the body has been received — a refusal here is a 554",
               FontSize = Typography.Label,
               FontWeight = FontWeights.SemiBold,
               Margin = new Thickness(0, 10, 0, 6),
               TextWrapping = TextWrapping.Wrap
            });

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // order
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // name + detail
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // state
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // score
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // link

            int row = 0;
            foreach (SpamCheck check in SpamPipeline.Checks(config).Where(c => c.Phase == phase))
            {
               grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
               AddChecksRow(grid, row, check);
               row++;
            }

            content.Children.Add(grid);
         }

         return card;
      }

      private void AddChecksRow(Grid grid, int row, SpamCheck check)
      {
         var order = new TextBlock
         {
            Text = check.Order.ToString(),
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
            Text = check.Name,
            FontSize = Typography.Body,
            FontWeight = check.Enabled ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            // A disabled check is dimmed as well as labelled "Off", because the
            // label is what the reader who cannot see the dimming gets.
            Opacity = check.Enabled ? 1.0 : 0.6
         };

         // The whole row as the name of the one element in it that has an
         // automation peer. A TextBlock has one and a Panel does not, so putting it
         // on the surrounding StackPanel - the obvious place - would set a property
         // nothing ever reads. The five columns are otherwise announced as five
         // unrelated fragments and the listener has to reassemble the row.
         System.Windows.Automation.AutomationProperties.SetName(name,
            check.Name + ", " + (check.Enabled ? "on" : "off") + ", score " + check.ScoreText + ". " + check.Detail);

         text.Children.Add(name);

         var detail = new TextBlock
         {
            Text = check.Detail,
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7
         };
         text.Children.Add(detail);
         Grid.SetRow(text, row);
         Grid.SetColumn(text, 1);
         grid.Children.Add(text);

         // "On"/"Off" as words, coloured but never only coloured.
         var state = new TextBlock
         {
            Text = check.Enabled ? "On" : "Off",
            FontSize = Typography.Label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 14, 6),
            VerticalAlignment = VerticalAlignment.Top
         };
         state.SetResourceReference(TextBlock.ForegroundProperty,
            check.Enabled ? "AppSuccessBrush" : "TextFillColorTertiaryBrush");
         Grid.SetRow(state, row);
         Grid.SetColumn(state, 2);
         grid.Children.Add(state);

         var score = new TextBlock
         {
            Text = check.ScoreText,
            FontSize = Typography.Label,
            Margin = new Thickness(0, 6, 14, 6),
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.Right,
            MinWidth = 90
         };
         Grid.SetRow(score, row);
         Grid.SetColumn(score, 3);
         grid.Children.Add(score);

         FrameworkElement link = PageLink(check.Page, "Settings…",
            "Open " + NavigationMap.TitleOf(check.Page) + ", which owns the " + check.Name + " settings");
         link.Margin = new Thickness(0, 4, 0, 4);
         link.VerticalAlignment = VerticalAlignment.Top;
         Grid.SetRow(link, row);
         Grid.SetColumn(link, 4);
         grid.Children.Add(link);
      }

      private Border GreylistingCard(SpamPipelineConfig config)
      {
         Border card = Card("Greylisting", out StackPanel content);

         content.Children.Add(Paragraph(SpamPipeline.GreylistingSummary(config), Typography.Body));
         content.Children.Add(PageLink("antispam", "Greylisting settings…",
            "Open Anti-spam settings, which owns the greylisting settings"));

         return card;
      }

      private Border ListsCard(SpamPipelineConfig config)
      {
         Border card = Card("The lists behind the checks", out StackPanel content);

         content.Children.Add(ListRow("DNS blacklists", config.ActiveDnsBlackLists, config.TotalDnsBlackLists, "dnsbl"));
         content.Children.Add(ListRow("SURBL servers", config.ActiveSurblServers, config.TotalSurblServers, "surbl"));
         content.Children.Add(ListRow("Anti-spam white list", null, config.WhiteListEntries, "spamwhitelist"));
         content.Children.Add(ListRow("Greylisting white list", null, config.GreylistWhiteListEntries, "greylistwhitelist"));

         return card;
      }

      private FrameworkElement ListRow(string name, int? active, int total, string page)
      {
         var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         string count = active != null
            ? active.Value + " active of " + total
            : total + (total == 1 ? " entry" : " entries");

         var text = new TextBlock
         {
            Text = name + " — " + count,
            FontSize = Typography.Body,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
         };
         row.Children.Add(text);

         FrameworkElement link = PageLink(page, "Open…", "Open " + NavigationMap.TitleOf(page));
         Grid.SetColumn(link, 1);
         row.Children.Add(link);

         return row;
      }

      private static Border NotesCard(IReadOnlyList<SpamPipelineNote> notes)
      {
         Border card = Card("Worth knowing about this configuration", out StackPanel content);

         foreach (SpamPipelineNote note in notes)
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
      /// The whole page is read-only, so these links are the only way out of it,
      /// and they are the reason it can stay read-only: every value shown here is
      /// one click from the single place that edits it.
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
            ToolTip = accessibleName
         };
         System.Windows.Automation.AutomationProperties.SetName(button, accessibleName);
         System.Windows.Automation.AutomationProperties.SetAutomationId(button, "spam-overview-open-" + page);
         button.Click += (s, e) => (Application.Current?.MainWindow as MainWindow)?.NavigateTo(page);
         return button;
      }
   }
}
