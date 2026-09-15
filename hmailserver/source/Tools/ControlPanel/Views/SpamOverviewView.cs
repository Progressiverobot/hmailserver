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

      // A read that failed is a warning in the flow of the page, not a grey line
      // at the bottom: it says the rows below may be incomplete, and on a page
      // whose whole purpose is to be believed that has to be loud.
      private readonly InlineNotice readFailure_ = new()
      {
         Level = StatusLevel.Warning,
         Visibility = Visibility.Collapsed
      };

      // Where the values came from - a provenance footnote, the quietest text.
      private readonly TextBlock status_ = new();

      public SpamOverviewView()
      {
         var page = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };

         var refresh = new Wpf.Ui.Controls.Button { Content = L("_Refresh") };
         System.Windows.Automation.AutomationProperties.SetName(refresh, L("Re-read the spam configuration from the server"));
         System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "spam-overview-refresh");
         refresh.Click += (s, e) => Reload();

         page.Children.Add(new PageHeader
         {
            Title = L("Spam filtering overview"),
            Subtitle = L("Every check in the order the server runs them, and what the score they add does to the message. Nothing on this page can be edited - each row opens the page that owns the setting."),
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

            config.MarkThreshold = Read(() => (int)antiSpam.SpamMarkThreshold);
            config.DeleteThreshold = Read(() => (int)antiSpam.SpamDeleteThreshold);
            config.MaxScanKilobytes = Read(() => (int)antiSpam.MaximumMessageSize);
            config.BlockedSenderCount = Read(() => (int)antiSpam.BlockedSenders.Count);

            config.AddSpamHeader = Read(() => (bool)antiSpam.AddHeaderSpam);
            config.AddReasonHeader = Read(() => (bool)antiSpam.AddHeaderReason);
            config.PrependSubject = Read(() => (bool)antiSpam.PrependSubject);

            config.CheckHeloHost = Read(() => (bool)antiSpam.CheckHostInHelo);
            config.HeloHostScore = Read(() => (int)antiSpam.CheckHostInHeloScore);
            config.CheckPtr = Read(() => (bool)antiSpam.CheckPTR);
            config.PtrScore = Read(() => (int)antiSpam.CheckPTRScore);
            config.CheckSenderMx = Read(() => (bool)antiSpam.UseMXChecks);
            config.SenderMxScore = Read(() => (int)antiSpam.UseMXChecksScore);
            config.CheckSpf = Read(() => (bool)antiSpam.UseSPF);
            config.SpfScore = Read(() => (int)antiSpam.UseSPFScore);

            config.VerifyDkim = Read(() => (bool)antiSpam.DKIMVerificationEnabled);
            config.DkimFailureScore = Read(() => (int)antiSpam.DKIMVerificationFailureScore);
            config.EvaluateDmarc = Read(() => (bool)antiSpam.DMARCEnabled);
            config.DmarcFailureScore = Read(() => (int)antiSpam.DMARCFailureScore);

            config.SpamAssassinEnabled = Read(() => (bool)antiSpam.SpamAssassinEnabled);
            config.SpamAssassinMergesScore = Read(() => (bool)antiSpam.SpamAssassinMergeScore);
            config.SpamAssassinScore = Read(() => (int)antiSpam.SpamAssassinScore);
            config.SpamAssassinHost = Read(() => (string)antiSpam.SpamAssassinHost) ?? "";

            config.GreylistingEnabled = Read(() => (bool)antiSpam.GreyListingEnabled);
            config.BypassGreylistingOnSpfPass = Read(() => (bool)antiSpam.BypassGreylistingOnSPFSuccess);
            config.BypassGreylistingOnSenderMx = Read(() => (bool)antiSpam.BypassGreylistingOnMailFromMX);

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
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object)antiSpam);
            ServerSession.Release((object)settings);
         }

         return config;
      }

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
            total = (int)collection.Count;

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
                  if ((bool)item.Active)
                     active++;
               }
               finally
               {
                  ServerSession.Release((object)item);
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
            ServerSession.Release((object)collection);
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

         status_.Text = L("Read from the server. Values are read again every time this page is opened.");

         readFailure_.Text = failedReads_ == 0
            ? null
            : F("{0} value(s) could not be read — {1} The rows below may be incomplete.", failedReads_, firstError_);
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

      private static Card VerdictCard(SpamPipelineConfig config)
      {
         Card card = Section(L("What happens to a message"), out StackPanel content);

         content.Children.Add(Paragraph(SpamPipeline.Verdict(config), Typography.Body));

         int stop = SpamPipeline.StopScore(config);
         int? ceiling = SpamPipeline.HighestReachableScore(config);

         content.Children.Add(Paragraph(F("The score is added up across both phases. The server stops testing within a phase as soon as the running total reaches {0} - the higher of the two thresholds - so a check late in the order may never run.", stop)
            + (ceiling != null
               ? F(" With the checks currently enabled the highest total reachable is {0}.", ceiling.Value)
               : L(" The total is open-ended, because a list entry or SpamAssassin's own score can be any size.")),
            Typography.Caption));

         return card;
      }

      private Card ChecksCard(SpamPipelineConfig config)
      {
         Card card = Section(L("Checks, in the order the server runs them"), out StackPanel content);

         foreach (SpamCheckPhase phase in new[] { SpamCheckPhase.BeforeTheBody, SpamCheckPhase.AfterTheBody })
         {
            var phaseHeading = new TextBlock
            {
               Text = phase == SpamCheckPhase.BeforeTheBody
                  ? L("At RCPT TO, before the body is transferred — a refusal here is a 550")
                  : L("After the body has been received — a refusal here is a 554"),
               Margin = new Thickness(0, 12, 0, 8)
            };
            phaseHeading.SetResourceReference(StyleProperty, "TextBodyStrong");
            System.Windows.Automation.AutomationProperties.SetHeadingLevel(phaseHeading, System.Windows.Automation.AutomationHeadingLevel.Level2);
            content.Children.Add(phaseHeading);

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
            Margin = new Thickness(0, 6, 10, 6),
            VerticalAlignment = VerticalAlignment.Top
         };
         order.SetResourceReference(StyleProperty, "TextCaptionTertiary");
         Grid.SetRow(order, row);
         Grid.SetColumn(order, 0);
         grid.Children.Add(order);

         var text = new StackPanel { Margin = new Thickness(0, 6, 12, 6) };

         var name = new TextBlock { Text = check.Name, TextWrapping = TextWrapping.Wrap };
         // A disabled check is dimmed as well as labelled "Off", because the
         // label is what the reader who cannot see the dimming gets.
         name.SetResourceReference(StyleProperty, check.Enabled ? "TextBodyStrong" : "TextSecondary");

         // The whole row as the name of the one element in it that has an
         // automation peer. A TextBlock has one and a Panel does not, so putting it
         // on the surrounding StackPanel - the obvious place - would set a property
         // nothing ever reads. The five columns are otherwise announced as five
         // unrelated fragments and the listener has to reassemble the row.
         System.Windows.Automation.AutomationProperties.SetName(name,
            F("{0}, {1}, score {2}. {3}", check.Name, check.Enabled ? L("On") : L("Off"), check.ScoreText, check.Detail));

         text.Children.Add(name);

         var detail = new TextBlock { Text = check.Detail, TextWrapping = TextWrapping.Wrap };
         detail.SetResourceReference(StyleProperty, "TextCaption");
         text.Children.Add(detail);
         Grid.SetRow(text, row);
         Grid.SetColumn(text, 1);
         grid.Children.Add(text);

         // "On"/"Off" as a pill: the word, the level's shape and the level's
         // colour together, with Off drawn in the neutral grey that says nothing.
         var state = new StatusPill
         {
            Level = check.Enabled ? StatusLevel.Good : StatusLevel.Normal,
            Text = check.Enabled ? L("On") : L("Off"),
            Margin = new Thickness(0, 6, 14, 6),
            VerticalAlignment = VerticalAlignment.Top
         };
         Grid.SetRow(state, row);
         Grid.SetColumn(state, 2);
         grid.Children.Add(state);

         var score = new TextBlock
         {
            Text = check.ScoreText,
            Margin = new Thickness(0, 6, 14, 6),
            VerticalAlignment = VerticalAlignment.Top,
            TextAlignment = TextAlignment.Right,
            MinWidth = 90
         };
         score.SetResourceReference(StyleProperty, "TextCaption");
         Grid.SetRow(score, row);
         Grid.SetColumn(score, 3);
         grid.Children.Add(score);

         FrameworkElement link = PageLink(check.Page, L("Settings…"),
            F("Open {0}, which owns the {1} settings", L(NavigationMap.TitleOf(check.Page)), check.Name));
         link.Margin = new Thickness(0, 4, 0, 4);
         link.VerticalAlignment = VerticalAlignment.Top;
         Grid.SetRow(link, row);
         Grid.SetColumn(link, 4);
         grid.Children.Add(link);
      }

      private Card GreylistingCard(SpamPipelineConfig config)
      {
         Card card = Section(L("Greylisting"), out StackPanel content);

         content.Children.Add(Paragraph(SpamPipeline.GreylistingSummary(config), Typography.Body));
         content.Children.Add(PageLink("antispam", L("Greylisting settings…"),
            L("Open Anti-spam settings, which owns the greylisting settings")));

         return card;
      }

      private Card ListsCard(SpamPipelineConfig config)
      {
         Card card = Section(L("The lists behind the checks"), out StackPanel content);

         content.Children.Add(ListRow(L("DNS blacklists"), config.ActiveDnsBlackLists, config.TotalDnsBlackLists, "dnsbl"));
         content.Children.Add(ListRow(L("SURBL servers"), config.ActiveSurblServers, config.TotalSurblServers, "surbl"));
         content.Children.Add(ListRow(L("Anti-spam white list"), null, config.WhiteListEntries, "spamwhitelist"));
         content.Children.Add(ListRow(L("Greylisting white list"), null, config.GreylistWhiteListEntries, "greylistwhitelist"));

         return card;
      }

      private FrameworkElement ListRow(string name, int? active, int total, string page)
      {
         var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         string count = active != null
            ? F("{0} active of {1}", active.Value, total)
            : total == 1 ? F("{0} entry", total) : F("{0} entries", total);

         var text = new TextBlock { Text = name + " — " + count, VerticalAlignment = VerticalAlignment.Center };
         text.SetResourceReference(StyleProperty, "TextBody");
         row.Children.Add(text);

         FrameworkElement link = PageLink(page, L("Open…"), F("Open {0}", L(NavigationMap.TitleOf(page))));
         Grid.SetColumn(link, 1);
         row.Children.Add(link);

         return row;
      }

      private static Card NotesCard(IReadOnlyList<SpamPipelineNote> notes)
      {
         Card card = Section(L("Worth knowing about this configuration"), out StackPanel content);

         // One notice per note, at the note's own level: the component draws the
         // colour, the shape and the severity word, and puts the word first in
         // its accessible name, which is what this card used to build by hand.
         foreach (SpamPipelineNote note in notes)
            content.Children.Add(new InlineNotice { Level = note.Level, Text = note.Text });

         return card;
      }

      private static TextBlock Paragraph(string text, double size)
      {
         var block = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6) };
         block.SetResourceReference(StyleProperty, size <= Typography.Caption ? "TextCaption" : "TextBody");
         return block;
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
