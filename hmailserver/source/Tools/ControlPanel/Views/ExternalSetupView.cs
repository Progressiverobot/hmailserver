// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shapes;
using hMailServer.ControlPanel.Services;
using Card = hMailServer.ControlPanel.Views.Scaffold.Card;
using InlineNotice = hMailServer.ControlPanel.Views.Scaffold.InlineNotice;
using PageHeader = hMailServer.ControlPanel.Views.Scaffold.PageHeader;
using StatusPill = hMailServer.ControlPanel.Views.Scaffold.StatusPill;

// The Control Panel has its own Typography (the type scale, in Services) and
// System.Windows.Documents declares one too. That import is needed here for Run and
// Inlines, so the reference is aliased to the one meant rather than dropping the import.
using Typography = hMailServer.ControlPanel.Services.Typography;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// One page that answers "what does this server still need done OUTSIDE it".
   ///
   /// Several shipped features are inert until the administrator does something
   /// hMailServer cannot do for them: publish a DNS record, produce a CA bundle,
   /// name the forwarders they trust, register an application with an identity
   /// provider. The server cannot write to anyone's DNS zone, cannot mint
   /// certificates for other people's devices, and cannot know which third
   /// parties the administrator trusts - those calls are the administrator's to
   /// make, on systems the administrator controls. Today nothing collects them
   /// in one place, so a feature can look enabled and quietly do nothing; that
   /// failure shape ("exists but is inert by default") is the one this page
   /// exists to make loud.
   ///
   /// Read-only on purpose, like the spam overview: it is a checklist, not
   /// another editor. Every item links to the page that owns its settings, so
   /// there is still exactly one place to change each value.
   ///
   /// Every item carries one of four states, and the state is always a word in
   /// the text - never only a colour or a shape - so it survives greyscale,
   /// High Contrast and a screen reader alike:
   ///
   ///   Done          - the parts this panel can see are in place.
   ///   Not needed    - the feature is off, so nothing external is required.
   ///   Action needed - something verifiable is missing, and the feature is
   ///                   currently doing less than its settings suggest.
   ///   Cannot tell   - the answer lives somewhere this panel cannot read
   ///                   (the public internet, a remote machine's disk, a
   ///                   non-TXT DNS record, or a database table with no COM
   ///                   property), so the item says exactly what to check and how.
   ///
   /// The checking itself lives in <see cref="ExternalSetupChecks"/> (Services),
   /// shared with the dashboard's "needs attention" summary so the two can never
   /// drift apart and disagree in front of the administrator. This page renders
   /// every item in full; the dashboard card renders the aggregate and the items
   /// that need action, and links here for the rest.
   ///
   /// Configuration is read synchronously from COM and hMailServer.INI, the way
   /// every other page reads it. DNS TXT records (DKIM, MTA-STS discovery) are
   /// then looked up through <see cref="DnsTxtLookup"/> on a background task,
   /// because each lookup can block for the resolver's full timeout, and the
   /// rows update in place when the answers arrive.
   ///
   /// Honesty rule, learned the hard way from fifteen documented overclaims in
   /// this project: nothing on this page asserts a status it did not actually
   /// determine. Where the answer is knowable from COM, the local filesystem or
   /// a TXT lookup it is checked; where it is not, the item says "Cannot tell"
   /// and why, which is itself information the administrator does not have today.
   /// </summary>
   public class ExternalSetupView : UserControl, IPageLifecycle
   {
      private readonly StackPanel body_ = new();

      // The tally, as a notice at the level the tally itself implies: something
      // needing action is a warning, something that cannot be told from here is
      // information, and a clean sheet is good. The counts are still words, so
      // the level is confirmation and never the only carrier.
      private readonly InlineNotice summary_ = new() { Visibility = Visibility.Collapsed };

      // A read that failed is its own notice: it says the items above may be
      // incomplete, which is a different statement from the tally.
      private readonly InlineNotice readFailure_ = new()
      {
         Level = StatusLevel.Warning,
         Visibility = Visibility.Collapsed
      };

      // Where the verdicts came from - a provenance footnote under the list,
      // the quietest text on the page.
      private readonly TextBlock status_ = new();

      public ExternalSetupView()
      {
         var page = new StackPanel { MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };

         var refresh = new Wpf.Ui.Controls.Button { Content = L("_Refresh") };
         System.Windows.Automation.AutomationProperties.SetName(refresh, L("Re-check the external prerequisites"));
         System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "external-setup-refresh");
         refresh.Click += (s, e) => Reload();

         page.Children.Add(new PageHeader
         {
            Title = L("External setup"),
            Subtitle = L("What this server needs done outside it - DNS records, key and CA files, trusted lists. Each item shows a state; where the answer cannot be read from here, the item says what to check instead of guessing."),
            Actions = refresh
         });

         page.Children.Add(summary_);
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

      // ---- running the shared checks ---------------------------------------------

      /// <summary>The current run of the shared checklist. Kept because the DNS
      /// pass updates its items in place, and RenderItems reads them again.</summary>
      private ExternalSetupChecks checks_;

      /// <summary>
      /// Bumped by every Reload. A background DNS pass carries the generation it
      /// was started for and applies nothing if another reload has happened since,
      /// so a slow lookup can never overwrite a newer page with stale verdicts.
      /// </summary>
      private int generation_;

      /// <summary>The COM/INI half of the status line, fixed at build time.</summary>
      private string baseStatus_;

      private void Reload()
      {
         int generation = ++generation_;

         checks_ = ExternalSetupChecks.Run();

         baseStatus_ = L("Checked against the server. Everything is re-checked every time this page is opened.");

         readFailure_.Text = checks_.FailedReads == 0
            ? null
            : F("{0} value(s) could not be read — {1} The items above may be incomplete.", checks_.FailedReads, checks_.FirstError);
         readFailure_.Visibility = checks_.FailedReads == 0 ? Visibility.Collapsed : Visibility.Visible;

         RenderItems(checks_.DnsLookupCount > 0
            ? F(" {0} DNS record(s) are being looked up in the background; the rows marked \"checking\" will update.", checks_.DnsLookupCount)
            : "");

         if (checks_.DnsLookupCount == 0)
            return;

         // The lookups run off the UI thread because each one can block for the
         // resolver's full timeout - and the missing-record case, the slowest,
         // is exactly the case this page exists to surface. The COM objects are
         // never touched from here: everything the probes need was copied into
         // plain strings during the build, so nothing apartment-bound crosses a
         // thread. Results only land if this is still the newest reload.
         ExternalSetupChecks checks = checks_;
         Task.Run(() =>
         {
            checks.ResolveDnsLookups();

            Dispatcher.BeginInvoke(new Action(() =>
            {
               if (generation != generation_)
                  return;

               checks.ApplyDnsResults();
               RenderItems(L(" DNS records checked through the Windows resolver."));
            }));
         });
      }

      // ---- drawing --------------------------------------------------------------

      private void RenderItems(string dnsNote)
      {
         body_.Children.Clear();
         foreach (SetupItem item in checks_.Items)
            body_.Children.Add(ItemCard(item));

         int action = checks_.Items.Count(i => i.State == SetupItemState.ActionNeeded);
         int unknown = checks_.Items.Count(i => i.State == SetupItemState.CannotTell);
         int done = checks_.Items.Count(i => i.State == SetupItemState.Done);
         int unused = checks_.Items.Count(i => i.State == SetupItemState.NotNeeded);

         summary_.Level = action > 0 ? StatusLevel.Warning
            : unknown > 0 ? StatusLevel.Information
            : StatusLevel.Good;
         summary_.Text = F("{0} need action, {1} cannot be told from here, {2} done, {3} not needed.", action, unknown, done, unused);
         summary_.Visibility = Visibility.Visible;

         status_.Text = baseStatus_ + dnsNote;
      }

      private Card ItemCard(SetupItem item)
      {
         var card = new Card();
         card.SetResourceReference(MarginProperty, "AppCardGap");

         var content = new StackPanel();

         // Header: the state as a pill - colour, shape and word together, so no
         // single channel is load-bearing - then the title, then the link to the
         // page that owns the settings.
         var header = new Grid();
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         header.Children.Add(new StatusPill
         {
            Level = ExternalSetupChecks.LevelFor(item.State),
            Text = ExternalSetupChecks.StateWord(item.State),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top
         });

         var title = new TextBlock { Text = item.Title, TextWrapping = TextWrapping.Wrap };
         title.SetResourceReference(StyleProperty, "TextBodyStrong");

         // The whole item, as one utterance, on the one header element that has
         // an automation peer: a listener hears the state, the subject, what it
         // is for and what to do, instead of four unrelated fragments.
         System.Windows.Automation.AutomationProperties.SetName(title,
            ExternalSetupChecks.StateWord(item.State) + ": " + item.Title + ". " + item.Purpose + " " + item.Action);
         Grid.SetColumn(title, 1);
         header.Children.Add(title);

         if (item.Page != null)
         {
            FrameworkElement link = PageLink(item.Page, L("Settings…"),
               F("Open {0}, which owns the settings for: {1}", L(NavigationMap.TitleOf(item.Page)), L(item.Title)));
            link.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(link, 2);
            header.Children.Add(link);
         }

         content.Children.Add(header);

         var purpose = new TextBlock { Text = item.Purpose, Margin = new Thickness(0, 8, 0, 0) };
         purpose.SetResourceReference(StyleProperty, "TextCaption");
         content.Children.Add(purpose);

         foreach (SetupFinding finding in item.Findings)
         {
            StatusPresentation findingPresentation = StatusSemantics.For(ExternalSetupChecks.LevelFor(finding.State));

            var row = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var findingMark = new Path { Width = 9, Height = 9, Stretch = System.Windows.Media.Stretch.Fill, Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
            ShapeMarkVisuals.ApplyMark(findingMark, findingPresentation.Shape, findingPresentation.BrushKey);
            row.Children.Add(findingMark);

            // The state word is IN the text (a bold run), so it reaches
            // greyscale printouts and screen readers without any extra plumbing.
            var text = new TextBlock
            {
               FontSize = Typography.Label,
               TextWrapping = TextWrapping.Wrap
            };
            text.Inlines.Add(new Run(ExternalSetupChecks.StateWord(finding.State) + " — ") { FontWeight = FontWeights.SemiBold });
            text.Inlines.Add(new Run(finding.Text));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            content.Children.Add(row);
         }

         // The action is only worth space when there is (or may be) something to
         // do. "Not needed" earns a quiet card; the instructions would be noise.
         if (item.State != SetupItemState.NotNeeded)
         {
            var action = new TextBlock { Margin = new Thickness(0, 12, 0, 0) };
            action.SetResourceReference(StyleProperty, "TextCaption");
            action.Inlines.Add(new Run(L("What to do: ")) { FontWeight = FontWeights.SemiBold });
            action.Inlines.Add(new Run(item.Action));
            content.Children.Add(action);
         }

         card.Content = content;
         return card;
      }

      /// <summary>
      /// A button that opens the page owning an item's settings. The page is
      /// read-only, so these links are the only way out of it - which is also
      /// what keeps it read-only: every state shown here is one click from the
      /// single place that changes it.
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
         System.Windows.Automation.AutomationProperties.SetAutomationId(button, "external-setup-open-" + page);
         button.Click += (s, e) => (Application.Current?.MainWindow as MainWindow)?.NavigateTo(page);
         return button;
      }
   }
}
