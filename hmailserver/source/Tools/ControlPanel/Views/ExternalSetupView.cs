// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Shapes;
using hMailServer.ControlPanel.Services;

// The Control Panel has its own Typography (the type scale, in Services) and
// System.Windows.Documents declares one too. That import is needed here for Run and
// Inlines, so the reference is aliased to the one meant rather than dropping the import.
using Typography = hMailServer.ControlPanel.Services.Typography;

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
      private readonly TextBlock summary_ = new();
      private readonly TextBlock status_ = new();

      public ExternalSetupView()
      {
         var page = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Left };

         var header = new Grid();
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var heading = new StackPanel();
         var title = new TextBlock { Text = "External setup" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         heading.Children.Add(title);

         var subtitle = new TextBlock
         {
            Text = "What this server needs done outside it - DNS records, key and CA files, trusted lists. "
                   + "Each item shows a state; where the answer cannot be read from here, the item says what to check instead of guessing."
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
         System.Windows.Automation.AutomationProperties.SetName(refresh, "Re-check the external prerequisites");
         System.Windows.Automation.AutomationProperties.SetAutomationId(refresh, "external-setup-refresh");
         refresh.Click += (s, e) => Reload();
         Grid.SetColumn(refresh, 1);
         header.Children.Add(refresh);

         page.Children.Add(header);

         // A one-line tally so the page can be judged without reading it all.
         // Plain text, because a count that only exists as coloured badges does
         // not exist for a screen-reader user or a greyscale printout.
         summary_.FontSize = Typography.Body;
         summary_.FontWeight = FontWeights.SemiBold;
         summary_.Margin = new Thickness(0, 12, 0, 0);
         summary_.TextWrapping = TextWrapping.Wrap;
         page.Children.Add(summary_);

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

         baseStatus_ = checks_.FailedReads == 0
            ? "Checked against the server. Everything is re-checked every time this page is opened."
            : checks_.FailedReads + " value(s) could not be read — " + checks_.FirstError + " The items above may be incomplete.";

         RenderItems(checks_.DnsLookupCount > 0
            ? " " + checks_.DnsLookupCount + " DNS record(s) are being looked up in the background; the rows marked \"checking\" will update."
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
               RenderItems(" DNS records checked through the Windows resolver.");
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

         summary_.Text = action + " need action, " + unknown + " cannot be told from here, "
                         + done + " done, " + unused + " not needed.";

         status_.Text = baseStatus_ + dnsNote;
      }

      private Border ItemCard(SetupItem item)
      {
         var border = new Border { Margin = new Thickness(0, 14, 0, 0) };
         border.SetResourceReference(StyleProperty, "Card");

         var content = new StackPanel();

         // Header: shape + state word + title. Colour, shape and word together,
         // so no single channel is load-bearing - the same three channels the
         // status badges use everywhere else in this application.
         var header = new Grid();
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         StatusPresentation presentation = StatusSemantics.For(ExternalSetupChecks.LevelFor(item.State));

         var mark = new Path { Width = 11, Height = 11, Stretch = System.Windows.Media.Stretch.Fill, Margin = new Thickness(0, 4, 8, 0), VerticalAlignment = VerticalAlignment.Top };
         ShapeMarkVisuals.ApplyMark(mark, presentation.Shape, presentation.BrushKey);
         header.Children.Add(mark);

         var stateWord = new TextBlock
         {
            Text = ExternalSetupChecks.StateWord(item.State),
            FontSize = Typography.Label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 92
         };
         stateWord.SetResourceReference(TextBlock.ForegroundProperty, presentation.BrushKey);
         Grid.SetColumn(stateWord, 1);
         header.Children.Add(stateWord);

         var title = new TextBlock
         {
            Text = item.Title,
            FontSize = Typography.SectionHeading,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
         };

         // The whole item, as one utterance, on the one header element that has
         // an automation peer: a listener hears the state, the subject, what it
         // is for and what to do, instead of four unrelated fragments.
         System.Windows.Automation.AutomationProperties.SetName(title,
            ExternalSetupChecks.StateWord(item.State) + ": " + item.Title + ". " + item.Purpose + " " + item.Action);
         Grid.SetColumn(title, 2);
         header.Children.Add(title);

         if (item.Page != null)
         {
            FrameworkElement link = PageLink(item.Page, "Settings…",
               "Open " + NavigationMap.TitleOf(item.Page) + ", which owns the settings for: " + item.Title);
            link.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(link, 3);
            header.Children.Add(link);
         }

         content.Children.Add(header);

         content.Children.Add(new TextBlock
         {
            Text = item.Purpose,
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(19, 4, 0, 0),
            Opacity = 0.75
         });

         foreach (SetupFinding finding in item.Findings)
         {
            StatusPresentation findingPresentation = StatusSemantics.For(ExternalSetupChecks.LevelFor(finding.State));

            var row = new Grid { Margin = new Thickness(19, 8, 0, 0) };
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
            var action = new TextBlock
            {
               FontSize = Typography.Label,
               TextWrapping = TextWrapping.Wrap,
               Margin = new Thickness(19, 10, 0, 0)
            };
            action.Inlines.Add(new Run("What to do: ") { FontWeight = FontWeights.SemiBold });
            action.Inlines.Add(new Run(item.Action));
            content.Children.Add(action);
         }

         border.Child = content;
         return border;
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
