using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Incoming relays: trusted upstream servers whose IPs are skipped in spam host checks.</summary>
   public class IncomingRelaysView : UserControl, IPageLifecycle
   {
      private readonly ListView list_ = new() { BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
      private readonly Wpf.Ui.Controls.TextBox name_ = new() { PlaceholderText = "Name", Margin = new Thickness(0, 0, 8, 0) };
      private readonly Wpf.Ui.Controls.TextBox lower_ = new() { PlaceholderText = "Lower IP", Margin = new Thickness(0, 0, 8, 0) };
      private readonly Wpf.Ui.Controls.TextBox upper_ = new() { PlaceholderText = "Upper IP", Margin = new Thickness(0, 0, 8, 0) };

      public IncomingRelaysView()
      {
         var grid = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var header = new StackPanel();
         var title = new TextBlock { Text = "Incoming relays" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         header.Children.Add(title);
         var sub = new TextBlock { Text = "Upstream gateways (spam filters, load balancers) whose IP addresses should not count as the connecting client in anti-spam host checks." };
         sub.SetResourceReference(StyleProperty, "PageSubtitle");
         header.Children.Add(sub);
         grid.Children.Add(header);

         var listCard = new Border { Padding = new Thickness(10) };
         listCard.SetResourceReference(StyleProperty, "Card");
         listCard.Child = list_;
         Grid.SetRow(listCard, 1);
         grid.Children.Add(listCard);

         var addCard = new Border { Margin = new Thickness(0, 12, 0, 0) };
         addCard.SetResourceReference(StyleProperty, "Card");
         var addPanel = new StackPanel();
         addPanel.Children.Add(new TextBlock { Text = "Add relay", FontSize = Typography.SectionHeading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

         var row = new Grid();
         for (int i = 0; i < 3; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         row.Children.Add(name_);
         Grid.SetColumn(lower_, 1);
         row.Children.Add(lower_);
         Grid.SetColumn(upper_, 2);
         row.Children.Add(upper_);

         var add = new Wpf.Ui.Controls.Button { Content = "Add", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
         add.Click += (s, e) => Add();
         Grid.SetColumn(add, 3);
         row.Children.Add(add);

         var del = new Wpf.Ui.Controls.Button { Content = "Delete selected", Appearance = Wpf.Ui.Controls.ControlAppearance.Danger };
         del.Click += (s, e) => DeleteSelected();
         Grid.SetColumn(del, 4);
         row.Children.Add(del);

         addPanel.Children.Add(row);
         addCard.Child = addPanel;
         Grid.SetRow(addCard, 2);
         grid.Children.Add(addCard);

         Content = grid;
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private void Reload()
      {
         var rows = new List<string>();
         dynamic relays = ServerSession.Current.Application.Settings.IncomingRelays;
         try
         {
            int count = (int) relays.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic relay = relays.Item[i];
               rows.Add((string) relay.Name + "   (" + (string) relay.LowerIP + " - " + (string) relay.UpperIP + ")");
               ServerSession.Release(relay);
            }
         }
         finally
         {
            ServerSession.Release(relays);
         }

         list_.ItemsSource = rows;
      }

      private void Add()
      {
         if (name_.Text.Trim().Length == 0 || lower_.Text.Trim().Length == 0 || upper_.Text.Trim().Length == 0)
         {
            MessageBox.Show("Name, lower IP and upper IP are required.", "Control Panel");
            return;
         }

         dynamic relays = ServerSession.Current.Application.Settings.IncomingRelays;
         try
         {
            dynamic relay = relays.Add();
            relay.Name = name_.Text.Trim();
            relay.LowerIP = lower_.Text.Trim();
            relay.UpperIP = upper_.Text.Trim();
            relay.Save();
            ServerSession.Release(relay);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not add the relay: " + ex.Message, "Control Panel");
            return;
         }
         finally
         {
            ServerSession.Release(relays);
         }

         name_.Text = lower_.Text = upper_.Text = "";
         Reload();
      }

      private void DeleteSelected()
      {
         string selected = list_.SelectedItem as string;
         if (selected == null)
            return;

         string relayName = selected.Split("   (")[0];

         if (MessageBox.Show("Delete the incoming relay " + relayName + "?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic relays = ServerSession.Current.Application.Settings.IncomingRelays;
         try
         {
            int count = (int) relays.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic relay = relays.Item[i];
               if ((string) relay.Name == relayName)
               {
                  relay.Delete();
                  ServerSession.Release(relay);
                  break;
               }
               ServerSession.Release(relay);
            }
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the relay: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(relays);
         }

         Reload();
      }
   }

   /// <summary>MX query utility (same as the classic Utilities > MX-query).</summary>
   public class MxQueryView : UserControl, IPageLifecycle
   {
      private readonly Wpf.Ui.Controls.TextBox domain_ = new() { PlaceholderText = "Domain (e.g. gmail.com)", Margin = new Thickness(0, 0, 8, 0) };
      private readonly TextBox output_ = new()
      {
         IsReadOnly = true,
         AcceptsReturn = true,
         FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
         FontSize = Typography.Label,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

      public MxQueryView()
      {
         var grid = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         var header = new StackPanel();
         var title = new TextBlock { Text = "MX query" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         header.Children.Add(title);
         var sub = new TextBlock { Text = "Look up the mail exchanger records for a domain - where e-mail to that domain is delivered." };
         sub.SetResourceReference(StyleProperty, "PageSubtitle");
         header.Children.Add(sub);
         grid.Children.Add(header);

         var inputRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         inputRow.Children.Add(domain_);
         var actions = new StackPanel { Orientation = Orientation.Horizontal };
         var run = new Wpf.Ui.Controls.Button { Content = "Query", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
         run.Click += async (s, e) => await RunQuery();
         actions.Children.Add(run);
         var copy = new Wpf.Ui.Controls.Button { Content = "Copy", Margin = new Thickness(8, 0, 0, 0) };
         copy.Click += (s, e) => { try { if (output_.Text.Length > 0) Clipboard.SetText(output_.Text); } catch (Exception) { } };
         actions.Children.Add(copy);
         Grid.SetColumn(actions, 1);
         inputRow.Children.Add(actions);
         Grid.SetRow(inputRow, 1);
         grid.Children.Add(inputRow);

         var card = new Border { Padding = new Thickness(12) };
         card.SetResourceReference(StyleProperty, "Card");
         card.Child = output_;
         Grid.SetRow(card, 2);
         grid.Children.Add(card);

         Content = grid;
      }

      private async Task RunQuery()
      {
         string domain = domain_.Text.Trim();
         if (domain.Length == 0)
            return;

         output_.Text = "Querying MX records for " + domain + "...";

         try
         {
            // Through the server, deliberately - issue #29. This tool used to shell
            // out to nslookup, which asks the OPERATING SYSTEM's resolver; the server
            // resolves through its own, honouring the DNSServer setting in
            // hMailServer.ini. The two disagree exactly when it matters - a custom
            // internal DNS server - and it is the server's answer that decides where
            // mail actually goes. Utilities.ResolveMXRecords runs the same resolver
            // path SMTP delivery uses.
            string result = await Task.Run(() =>
            {
               dynamic utilities = null;

               try
               {
                  utilities = ServerSession.Current.Application.Utilities;
                  return (string) utilities.ResolveMXRecords(domain);
               }
               finally
               {
                  ServerSession.Release((object) utilities);
               }
            });

            if (string.IsNullOrEmpty(result))
            {
               output_.Text = "The server found no mail servers for " + domain + ".\r\n\r\n"
                  + "This is the server's own resolver answering - the same one SMTP delivery uses, including any "
                  + "custom DNSServer configured in hMailServer.INI - so mail sent to this domain from this server "
                  + "would not be deliverable right now.";
               return;
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine("Mail servers for " + domain + ", in the order this server would try them:");
            report.AppendLine();

            foreach (string line in result.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
               int tab = line.IndexOf('\t');
               if (tab > 0)
                  report.AppendLine("  " + line.Substring(0, tab) + "  (" + line.Substring(tab + 1) + ")");
               else
                  report.AppendLine("  " + line);
            }

            report.AppendLine();
            report.Append("Resolved by the server itself, so a custom DNSServer in hMailServer.INI is honoured - "
               + "this is where mail actually goes, which an nslookup from this workstation cannot promise.");

            output_.Text = report.ToString();
         }
         catch (Exception ex)
         {
            output_.Text = "Query failed: " + ServerSession.DescribeComError(ex);
         }
      }

      public void OnEnter()
      {
      }

      public void OnLeave()
      {
      }
   }

   /// <summary>Server sendout: e-mail every account on the server (classic Utilities > Server sendout).</summary>
   public class SendoutView : UserControl, IPageLifecycle
   {
      private readonly Wpf.Ui.Controls.TextBox wildcard_ = new() { Text = "*" };
      private readonly Wpf.Ui.Controls.TextBox fromAddress_ = new() { PlaceholderText = "postmaster@yourdomain.com" };
      private readonly Wpf.Ui.Controls.TextBox fromName_ = new() { PlaceholderText = "Administrator" };
      private readonly Wpf.Ui.Controls.TextBox subject_ = new() { PlaceholderText = "Subject" };
      private readonly TextBox body_ = new()
      {
         AcceptsReturn = true,
         Height = 140,
         TextWrapping = TextWrapping.Wrap,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
         FontSize = Typography.Body,
         Padding = new Thickness(6)
      };
      private readonly TextBlock status_ = new() { FontSize = Typography.Caption, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.7 };

      public SendoutView()
      {
         var panel = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };

         var title = new TextBlock { Text = "Server sendout" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         panel.Children.Add(title);
         var sub = new TextBlock { Text = "Send a message to every account on the server (or those matching a wildcard) - for maintenance announcements." };
         sub.SetResourceReference(StyleProperty, "PageSubtitle");
         panel.Children.Add(sub);

         var card = new Border();
         card.SetResourceReference(StyleProperty, "Card");
         var form = new StackPanel();

         form.Children.Add(Label("Recipient wildcard (* = everyone)"));
         form.Children.Add(Spaced(wildcard_));
         form.Children.Add(Label("From address"));
         form.Children.Add(Spaced(fromAddress_));
         form.Children.Add(Label("From name"));
         form.Children.Add(Spaced(fromName_));
         form.Children.Add(Label("Subject"));
         form.Children.Add(Spaced(subject_));
         form.Children.Add(Label("Message"));
         body_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         body_.Background = System.Windows.Media.Brushes.Transparent;
         form.Children.Add(body_);

         var send = new Wpf.Ui.Controls.Button
         {
            Content = "Send to all matching accounts",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 14, 0, 0)
         };
         send.Click += (s, e) => Send();
         form.Children.Add(send);
         form.Children.Add(status_);

         card.Child = form;
         panel.Children.Add(card);
         Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
      }

      private static TextBlock Label(string text) => new() { Text = text, FontSize = Typography.Label, Margin = new Thickness(0, 6, 0, 4) };

      private static FrameworkElement Spaced(FrameworkElement element)
      {
         element.Margin = new Thickness(0, 0, 0, 6);
         return element;
      }

      private void Send()
      {
         if (fromAddress_.Text.Trim().Length == 0 || subject_.Text.Trim().Length == 0)
         {
            MessageBox.Show("From address and subject are required.", "Control Panel");
            return;
         }

         if (MessageBox.Show("Send this message to all accounts matching '" + wildcard_.Text + "'?",
             "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

         try
         {
            dynamic utilities = ServerSession.Current.Application.Utilities;
            bool queued = (bool) utilities.EmailAllAccounts(wildcard_.Text, fromAddress_.Text.Trim(), fromName_.Text.Trim(),
               subject_.Text, body_.Text);
            ServerSession.Release(utilities);
            // The server reports failure through the return value rather than an
            // error, so don't claim success when it declined the sendout.
            status_.Text = queued
               ? "Sendout queued " + DateTime.Now.ToLongTimeString() + "."
               : "The server did not queue the sendout. Check the address wildcard and the hMailServer error log.";
         }
         catch (Exception ex)
         {
            MessageBox.Show("Sendout failed: " + ex.Message, "Control Panel");
         }
      }

      public void OnEnter()
      {
      }

      public void OnLeave()
      {
      }
   }

   /// <summary>Server diagnostics (classic Utilities > Diagnostics).</summary>
   public class DiagnosticsView : UserControl, IPageLifecycle
   {
      private readonly Wpf.Ui.Controls.TextBox localDomain_ = new() { PlaceholderText = "A domain hosted on this server" };
      private readonly Wpf.Ui.Controls.TextBox testDomain_ = new() { Text = "gmail.com" };
      private readonly TextBox output_ = new()
      {
         IsReadOnly = true,
         AcceptsReturn = true,
         FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
         FontSize = Typography.Label,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

      // Message-store consistency. The server has no COM surface for this scan -
      // it is a background task gated on hMailServer.INI MessageStoreConsistencyCheck
      // that rewrites a recovery report in the log folder on every run - so the
      // Control Panel shows the result by reading that report.
      private readonly TextBlock consistencyStatus_ = new()
      {
         FontSize = Typography.Label,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(0, 0, 0, 10)
      };
      private readonly ListView consistencyList_ = new()
      {
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         // A badly damaged store can list thousands of messages; cap the section
         // so it scrolls internally instead of pushing the page around.
         MaxHeight = 260,
         Visibility = Visibility.Collapsed
      };
      private readonly Wpf.Ui.Controls.Button consistencyRefresh_ = new() { Content = "Refresh" };
      private readonly Wpf.Ui.Controls.Button consistencyOpen_ = new()
      {
         Content = "Open report",
         Margin = new Thickness(8, 0, 0, 0),
         IsEnabled = false
      };
      private string reportPath_;
      private bool loadingReport_;

      public DiagnosticsView()
      {
         var grid = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         // Auto so the consistency card is only as tall as what it has to say -
         // a clean scan is one line, and the connectivity output keeps the rest.
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var header = new StackPanel();
         var title = new TextBlock { Text = "Diagnostics" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         header.Children.Add(title);
         var sub = new TextBlock { Text = "Runs the server's built-in connectivity and configuration checks (outbound port 25, MX resolution, backup directory, IP configuration). " +
            "The message-store consistency scan below is a separate read-only background task - the server runs it at start-up and hourly and records what it found in a recovery report." };
         sub.SetResourceReference(StyleProperty, "PageSubtitle");
         header.Children.Add(sub);
         grid.Children.Add(header);

         var inputRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         localDomain_.Margin = new Thickness(0, 0, 8, 0);
         inputRow.Children.Add(localDomain_);
         testDomain_.Margin = new Thickness(0, 0, 8, 0);
         Grid.SetColumn(testDomain_, 1);
         inputRow.Children.Add(testDomain_);
         var actions = new StackPanel { Orientation = Orientation.Horizontal };
         var run = new Wpf.Ui.Controls.Button { Content = "Run diagnostics", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
         run.Click += async (s, e) => await Run();
         actions.Children.Add(run);
         var copy = new Wpf.Ui.Controls.Button { Content = "Copy", Margin = new Thickness(8, 0, 0, 0) };
         copy.Click += (s, e) => { try { if (output_.Text.Length > 0) Clipboard.SetText(output_.Text); } catch (Exception) { } };
         actions.Children.Add(copy);
         Grid.SetColumn(actions, 2);
         inputRow.Children.Add(actions);
         Grid.SetRow(inputRow, 1);
         grid.Children.Add(inputRow);

         var card = new Border { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 12) };
         card.SetResourceReference(StyleProperty, "Card");
         card.Child = output_;
         Grid.SetRow(card, 2);
         grid.Children.Add(card);

         var consistencyCard = new Border();
         consistencyCard.SetResourceReference(StyleProperty, "Card");
         consistencyCard.Child = BuildConsistencySection();
         Grid.SetRow(consistencyCard, 3);
         grid.Children.Add(consistencyCard);

         Content = grid;
      }

      private FrameworkElement BuildConsistencySection()
      {
         var section = new Grid();
         section.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         section.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         section.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         var titleRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
         titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         titleRow.Children.Add(new TextBlock
         {
            Text = "Message-store consistency",
            FontSize = Typography.SectionHeading,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
         });

         var consistencyActions = new StackPanel { Orientation = Orientation.Horizontal };
         consistencyRefresh_.Click += async (s, e) => await LoadConsistencyReport();
         consistencyActions.Children.Add(consistencyRefresh_);
         consistencyOpen_.Click += (s, e) => OpenReport();
         consistencyActions.Children.Add(consistencyOpen_);
         Grid.SetColumn(consistencyActions, 1);
         titleRow.Children.Add(consistencyActions);
         section.Children.Add(titleRow);

         Grid.SetRow(consistencyStatus_, 1);
         section.Children.Add(consistencyStatus_);

         var columns = new GridView();
         columns.Columns.Add(new GridViewColumn
         {
            Header = "Message ID",
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Services.MessageStoreConsistencyEntry.MessageId)),
            Width = 110
         });
         columns.Columns.Add(new GridViewColumn
         {
            Header = "Account",
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Services.MessageStoreConsistencyEntry.Account)),
            Width = 220
         });
         columns.Columns.Add(new GridViewColumn
         {
            Header = "Expected file",
            DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Services.MessageStoreConsistencyEntry.ExpectedPath)),
            Width = 480
         });
         consistencyList_.View = columns;
         Grid.SetRow(consistencyList_, 2);
         section.Children.Add(consistencyList_);

         return section;
      }

      public void OnEnter()
      {
         // The report is a local file, so read it off the UI thread the same way
         // the diagnostics run does; nothing else on the page depends on it.
         _ = LoadConsistencyReport();

         // Suggest the first hosted domain as the local domain.
         if (localDomain_.Text.Length > 0)
            return;
         try
         {
            dynamic domains = ServerSession.Current.Application.Domains;
            if ((int) domains.Count > 0)
            {
               dynamic first = domains.Item[0];
               localDomain_.Text = (string) first.Name;
               ServerSession.Release(first);
            }
            ServerSession.Release(domains);
         }
         catch (Exception)
         {
         }
      }

      public void OnLeave()
      {
      }

      private async Task Run()
      {
         output_.Text = "Running diagnostics...";

         string report = await Task.Run(() =>
         {
            try
            {
               dynamic diagnostics = ServerSession.Current.Application.Diagnostics;
               diagnostics.LocalDomainName = localDomain_.Dispatcher.Invoke(() => localDomain_.Text.Trim());
               diagnostics.TestDomainName = testDomain_.Dispatcher.Invoke(() => testDomain_.Text.Trim());

               dynamic results = diagnostics.PerformTests();

               var text = new System.Text.StringBuilder();
               int count = (int) results.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic result = results.Item[i];
                  string name = "", details = "";
                  bool? success = null;
                  try { name = (string) result.Name; } catch (Exception) { }
                  try { success = (bool) result.Result; } catch (Exception) { }
                  try { details = (string) result.ExecutionDetails; } catch (Exception) { }

                  string state = success == null ? "[ ?? ]   " : success.Value ? "[ OK ]   " : "[FAIL]   ";
                  text.AppendLine(state + name);
                  if (!string.IsNullOrWhiteSpace(details))
                     text.AppendLine("         " + details.Replace("\r\n", "\r\n         "));
                  text.AppendLine();
                  ServerSession.Release(result);
               }

               ServerSession.Release(results);
               ServerSession.Release(diagnostics);
               return text.Length > 0 ? text.ToString() : "No diagnostic results returned.";
            }
            catch (Exception ex)
            {
               return "Diagnostics failed: " + ex.Message;
            }
         });

         output_.Text = report;
      }

      /// <summary>
      /// What the last consistency scan found, ready for the UI thread. The
      /// server never returns this over COM, so everything here comes from
      /// hMailServer.INI and the recovery report the scan writes.
      /// </summary>
      private sealed class ConsistencyResult
      {
         public string Message;
         public Severity Level;
         public string ReportPath;
         public IReadOnlyList<Services.MessageStoreConsistencyEntry> Entries;
      }

      private enum Severity
      {
         Neutral,
         Good,
         Bad
      }

      private async Task LoadConsistencyReport()
      {
         // OnEnter starts a load without awaiting it, so guard against a second
         // one overlapping when the page is re-entered while the first is
         // running.
         if (loadingReport_)
            return;
         loadingReport_ = true;

         consistencyRefresh_.IsEnabled = false;
         consistencyOpen_.IsEnabled = false;
         consistencyList_.ItemsSource = null;
         consistencyList_.Visibility = Visibility.Collapsed;
         consistencyStatus_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         consistencyStatus_.Text = "Reading the recovery report...";
         reportPath_ = null;

         try
         {
            ConsistencyResult result = await Task.Run(ReadConsistencyReport);

            reportPath_ = result.ReportPath;
            consistencyStatus_.Text = result.Message;
            switch (result.Level)
            {
               case Severity.Good:
                  consistencyStatus_.Foreground = Services.ThemeTokens.Success;
                  break;
               case Severity.Bad:
                  consistencyStatus_.Foreground = Services.ThemeTokens.Danger;
                  break;
               default:
                  consistencyStatus_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                  break;
            }

            if (result.Entries != null && result.Entries.Count > 0)
            {
               consistencyList_.ItemsSource = result.Entries;
               consistencyList_.Visibility = Visibility.Visible;
            }
         }
         catch (Exception ex)
         {
            // Nothing awaits the load started from OnEnter, so report the failure
            // on the page rather than losing it in an unobserved task.
            consistencyStatus_.Text = "Could not read the consistency report: " + ex.Message;
            consistencyStatus_.Foreground = Services.ThemeTokens.Danger;
         }
         finally
         {
            consistencyOpen_.IsEnabled = reportPath_ != null;
            consistencyRefresh_.IsEnabled = true;
            loadingReport_ = false;
         }
      }

      private static ConsistencyResult ReadConsistencyReport()
      {
         var store = new IniFeatureStore();
         if (!store.IsAvailable)
         {
            return new ConsistencyResult
            {
               Level = Severity.Neutral,
               Message = "The scan result is only readable on the server machine - hMailServer.INI was not found here. " +
                         "The server publishes the same number as the hmailserver_messagestore_missing_files metric."
            };
         }

         bool enabled = store.ReadBool("MessageStoreConsistencyCheck", false);
         string logFolder = store.GetLogFolder();

         if (string.IsNullOrWhiteSpace(logFolder))
         {
            return new ConsistencyResult
            {
               Level = Severity.Neutral,
               Message = "No log folder is configured in hMailServer.INI, so the server has nowhere to write the recovery report."
            };
         }

         string path = System.IO.Path.Combine(logFolder, Services.MessageStoreConsistencyReport.FileName);
         string enabledNote = enabled
            ? ""
            // Asked for by nav key rather than spelled out, so that renaming the
            // page cannot leave this pointing at a page title that no longer exists.
            : " The periodic check is currently switched off (MessageStoreConsistencyCheck on the "
              + Services.NavigationMap.TitleOf("hardening") + " page), so this will not be refreshed.";

         string text;
         try
         {
            if (!System.IO.File.Exists(path))
            {
               return new ConsistencyResult
               {
                  Level = Severity.Neutral,
                  Message = enabled
                     ? "The consistency check is enabled but has not written a report yet. The server scans at start-up and then hourly, and writes " + path + "."
                     : "The consistency check is switched off, so no scan has run. Enable MessageStoreConsistencyCheck on the "
                       + Services.NavigationMap.TitleOf("hardening") + " page; " +
                       "the server then scans at start-up and hourly and writes " + path + "."
               };
            }

            using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
               System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            using var reader = new System.IO.StreamReader(stream);
            text = reader.ReadToEnd();
         }
         catch (Exception ex)
         {
            return new ConsistencyResult
            {
               Level = Severity.Neutral,
               ReportPath = path,
               Message = "Could not read " + path + ": " + ex.Message
            };
         }

         Services.MessageStoreConsistencyReport report = Services.MessageStoreConsistencyReport.Parse(text);
         string when = string.IsNullOrEmpty(report.Generated) ? "" : " Last scan: " + report.Generated + ".";
         string truncated = report.IsTruncated
            ? " The report header says " + report.ReportedMissingCount.Value + " but lists " + report.Entries.Count +
              " - it was probably read while the server was rewriting it, so refresh."
            : "";

         if (report.MissingCount == 0)
         {
            return new ConsistencyResult
            {
               Level = Severity.Good,
               ReportPath = path,
               Message = "No problems found - every message row has its file on disk." + when + truncated + enabledNote
            };
         }

         return new ConsistencyResult
         {
            Level = Severity.Bad,
            ReportPath = path,
            Entries = report.Entries,
            Message = report.MissingCount + (report.MissingCount == 1 ? " message references a file" : " messages reference a file") +
                      " that is missing on disk." + when + truncated + enabledNote +
                      " The check is read-only: the server does not delete or repair anything."
         };
      }

      private void OpenReport()
      {
         if (reportPath_ == null)
            return;

         try
         {
            // .report has no shell association on a stock Windows install, so
            // fall back to revealing the file in Explorer instead of leaving the
            // administrator with an "Open with" dialog and no report.
            Process.Start(new ProcessStartInfo(reportPath_) { UseShellExecute = true });
         }
         catch (Exception)
         {
            try
            {
               Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + reportPath_ + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
               MessageBox.Show("Could not open " + reportPath_ + ": " + ex.Message, "Control Panel");
            }
         }
      }
   }
}
