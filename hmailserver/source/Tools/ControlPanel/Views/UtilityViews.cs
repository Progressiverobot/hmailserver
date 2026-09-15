// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Incoming relays: trusted upstream servers whose IPs are skipped in spam host checks.</summary>
   public class IncomingRelaysView : UserControl, IPageLifecycle
   {
      private readonly ListView list_ = new()
      {
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         HorizontalContentAlignment = HorizontalAlignment.Stretch
      };
      // The three boxes are captioned by their FieldRows now, so the caption is
      // the accessible name and the placeholder would only repeat it.
      private readonly Wpf.Ui.Controls.TextBox name_ = new();
      private readonly Wpf.Ui.Controls.TextBox lower_ = new();
      private readonly Wpf.Ui.Controls.TextBox upper_ = new();
      private readonly EmptyState empty_ = new() { Icon = Wpf.Ui.Controls.SymbolRegular.ArrowRouting24, Visibility = Visibility.Collapsed };

      public IncomingRelaysView()
      {
         var grid = new Grid();
         grid.SetResourceReference(MarginProperty, "AppPagePadding");
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var del = new Wpf.Ui.Controls.Button { Content = L("_Delete selected"), Appearance = Wpf.Ui.Controls.ControlAppearance.Danger };
         del.Click += (s, e) => DeleteSelected();

         grid.Children.Add(new PageHeader
         {
            Title = L("Incoming relays"),
            Subtitle = L("Upstream gateways (spam filters, load balancers) whose IP addresses should not count as the connecting client in anti-spam host checks."),
            Actions = del
         });

         System.Windows.Automation.AutomationProperties.SetName(list_, L("Incoming relays"));
         var listHost = new Grid();
         listHost.Children.Add(list_);
         listHost.Children.Add(empty_);
         var listCard = new Card { Padding = new Thickness(10), Content = listHost };
         listCard.SetResourceReference(MarginProperty, "AppCardGap");
         Grid.SetRow(listCard, 1);
         grid.Children.Add(listCard);

         var row = new Grid();
         for (int i = 0; i < 3; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var nameField = new FieldRow { Label = L("Name"), Content = name_, Margin = new Thickness(0, 0, 8, 0) };
         row.Children.Add(nameField);
         var lowerField = new FieldRow { Label = L("Lower IP"), Content = lower_, Margin = new Thickness(0, 0, 8, 0) };
         Grid.SetColumn(lowerField, 1);
         row.Children.Add(lowerField);
         var upperField = new FieldRow { Label = L("Upper IP"), Content = upper_, Margin = new Thickness(0, 0, 8, 0) };
         Grid.SetColumn(upperField, 2);
         row.Children.Add(upperField);

         var add = new Wpf.Ui.Controls.Button
         {
            Content = L("_Add"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 12)
         };
         add.Click += (s, e) => Add();
         Grid.SetColumn(add, 3);
         row.Children.Add(add);

         var addCard = new Card { Title = L("Add relay"), Content = row };
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
            int count = (int)relays.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic relay = relays.Item[i];
               rows.Add((string)relay.Name + "   (" + (string)relay.LowerIP + " - " + (string)relay.UpperIP + ")");
               ServerSession.Release(relay);
            }
         }
         finally
         {
            ServerSession.Release(relays);
         }

         list_.ItemsSource = rows;
         StatusText.Show(empty_, null, rows.Count, null,
            L("No incoming relays. Add one only for a gateway in front of this server whose address you trust."));
      }

      private void Add()
      {
         if (name_.Text.Trim().Length == 0 || lower_.Text.Trim().Length == 0 || upper_.Text.Trim().Length == 0)
         {
            MessageBox.Show(L("Name, lower IP and upper IP are required."), L("Control Panel"));
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
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not add the relay: {0}", ex.Message), L("Control Panel"));
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

         if (MessageBox.Show(F("Delete the incoming relay {0}?", relayName), L("Control Panel"),
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic relays = ServerSession.Current.Application.Settings.IncomingRelays;
         try
         {
            int count = (int)relays.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic relay = relays.Item[i];
               if ((string)relay.Name == relayName)
               {
                  relay.Delete();
                  ServerSession.Release(relay);
                  break;
               }
               ServerSession.Release(relay);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not delete the relay: {0}", ex.Message), L("Control Panel"));
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
      private readonly Wpf.Ui.Controls.TextBox domain_ = new();
      private readonly TextBox output_ = new()
      {
         IsReadOnly = true,
         AcceptsReturn = true,
         FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily),
         FontSize = Typography.Label,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

      public MxQueryView()
      {
         var grid = new Grid();
         grid.SetResourceReference(MarginProperty, "AppPagePadding");
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         grid.Children.Add(new PageHeader
         {
            Title = L("MX query"),
            Subtitle = L("Look up the mail exchanger records for a domain - where e-mail to that domain is delivered.")
         });

         var inputRow = new Grid();
         inputRow.SetResourceReference(MarginProperty, "AppCardGap");
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         inputRow.Children.Add(new FieldRow
         {
            Label = L("Domain (e.g. gmail.com)"),
            Content = domain_,
            Margin = new Thickness(0, 0, 8, 0)
         });
         var actions = new StackPanel
         {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 12)
         };
         var run = new Wpf.Ui.Controls.Button { Content = L("_Query"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
         run.Click += async (s, e) => await RunQuery();
         actions.Children.Add(run);
         var copy = new Wpf.Ui.Controls.Button { Content = L("_Copy"), Margin = new Thickness(8, 0, 0, 0) };
         copy.Click += (s, e) => { try { if (output_.Text.Length > 0) Clipboard.SetText(output_.Text); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ } };
         actions.Children.Add(copy);
         Grid.SetColumn(actions, 1);
         inputRow.Children.Add(actions);
         Grid.SetRow(inputRow, 1);
         grid.Children.Add(inputRow);

         System.Windows.Automation.AutomationProperties.SetName(output_, L("MX query"));
         var card = new Card { Padding = new Thickness(12), Content = output_ };
         Grid.SetRow(card, 2);
         grid.Children.Add(card);

         Content = grid;
      }

      private async Task RunQuery()
      {
         string domain = domain_.Text.Trim();
         if (domain.Length == 0)
            return;

         output_.Text = F("Querying MX records for {0}...", domain);

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
                  return (string)utilities.ResolveMXRecords(domain);
               }
               finally
               {
                  ServerSession.Release((object)utilities);
               }
            });

            if (string.IsNullOrEmpty(result))
            {
               output_.Text = F("The server found no mail servers for {0}.\r\n\r\nThis is the server's own resolver answering - the same one SMTP delivery uses, including any custom DNSServer configured in hMailServer.INI - so mail sent to this domain from this server would not be deliverable right now.", domain);
               return;
            }

            var report = new System.Text.StringBuilder();
            report.AppendLine(F("Mail servers for {0}, in the order this server would try them:", domain));
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
            report.Append(L("Resolved by the server itself, so a custom DNSServer in hMailServer.INI is honoured - this is where mail actually goes, which an nslookup from this workstation cannot promise."));

            output_.Text = report.ToString();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            output_.Text = F("Query failed: {0}", ServerSession.DescribeComError(ex));
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
      // The display name recipients see, not an account name - the hint used to
      // be "Administrator", which read as though the account name were a word
      // that translates. It is not: see the comment in ConnectView.xaml.
      private readonly Wpf.Ui.Controls.TextBox fromName_ = new() { PlaceholderText = L("Sender name, as recipients see it") };
      // No placeholder: the field's caption is the same word, and a placeholder
      // that repeats the caption is a second copy of it inside the editor.
      private readonly Wpf.Ui.Controls.TextBox subject_ = new();
      private readonly TextBox body_ = new()
      {
         AcceptsReturn = true,
         Height = 140,
         TextWrapping = TextWrapping.Wrap,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
         FontSize = Typography.Body,
         Padding = new Thickness(6)
      };
      private readonly InlineNotice status_ = new() { Visibility = Visibility.Collapsed };

      public SendoutView()
      {
         var panel = new StackPanel { MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };

         panel.Children.Add(new PageHeader
         {
            Title = L("Server sendout"),
            Subtitle = L("Send a message to every account on the server (or those matching a wildcard) - for maintenance announcements.")
         });

         var form = new StackPanel();

         form.Children.Add(new FieldRow { Label = L("Recipient wildcard (* = everyone)"), Content = wildcard_ });
         form.Children.Add(new FieldRow { Label = L("From address"), Content = fromAddress_ });
         form.Children.Add(new FieldRow { Label = L("From name"), Content = fromName_ });
         form.Children.Add(new FieldRow { Label = L("Subject"), Content = subject_ });
         body_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         body_.Background = System.Windows.Media.Brushes.Transparent;
         form.Children.Add(new FieldRow { Label = L("Message"), Content = body_ });

         var send = new Wpf.Ui.Controls.Button
         {
            Content = L("_Send to all matching accounts"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 4, 0, 0)
         };
         send.Click += (s, e) => Send();
         form.Children.Add(send);
         status_.Margin = new Thickness(0, 12, 0, 0);
         form.Children.Add(status_);

         panel.Children.Add(new Card { Content = form });

         var scroller = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
         scroller.SetResourceReference(PaddingProperty, "AppPagePadding");
         Content = scroller;
      }

      private void Send()
      {
         if (fromAddress_.Text.Trim().Length == 0 || subject_.Text.Trim().Length == 0)
         {
            MessageBox.Show(L("From address and subject are required."), L("Control Panel"));
            return;
         }

         if (MessageBox.Show(F("Send this message to all accounts matching '{0}'?", wildcard_.Text),
             L("Control Panel"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

         try
         {
            dynamic utilities = ServerSession.Current.Application.Utilities;
            bool queued = (bool)utilities.EmailAllAccounts(wildcard_.Text, fromAddress_.Text.Trim(), fromName_.Text.Trim(),
               subject_.Text, body_.Text);
            ServerSession.Release(utilities);
            // The server reports failure through the return value rather than an
            // error, so don't claim success when it declined the sendout.
            status_.Level = queued ? StatusLevel.Good : StatusLevel.Critical;
            status_.Text = queued
               ? F("Sendout queued {0}.", DateTime.Now.ToLongTimeString())
               : L("The server did not queue the sendout. Check the address wildcard and the hMailServer error log.");
            status_.Visibility = Visibility.Visible;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Sendout failed: {0}", ex.Message), L("Control Panel"));
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
      private readonly Wpf.Ui.Controls.TextBox localDomain_ = new() { PlaceholderText = L("A domain hosted on this server") };
      private readonly Wpf.Ui.Controls.TextBox testDomain_ = new() { Text = "gmail.com" };
      private readonly TextBox output_ = new()
      {
         IsReadOnly = true,
         AcceptsReturn = true,
         FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily),
         FontSize = Typography.Label,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

      // Message-store consistency. The server has no COM surface for this scan -
      // it is a background task gated on hMailServer.INI MessageStoreConsistencyCheck
      // that rewrites a recovery report in the log folder on every run - so the
      // Control Panel shows the result by reading that report.
      private readonly InlineNotice consistencyStatus_ = new();
      // A DataGrid rather than a ListView with a GridView: the application's
      // implicit DataGrid styles theme it, and nothing themes a GridView, whose
      // white header and system-hyperlink text were unreadable on the dark theme.
      private readonly DataGrid consistencyList_ = new()
      {
         AutoGenerateColumns = false,
         IsReadOnly = true,
         SelectionMode = DataGridSelectionMode.Single,
         // A badly damaged store can list thousands of messages; cap the section
         // so it scrolls internally instead of pushing the page around.
         MaxHeight = 260,
         Visibility = Visibility.Collapsed
      };
      private readonly Wpf.Ui.Controls.Button consistencyRefresh_ = new() { Content = L("Re_fresh") };
      private readonly Wpf.Ui.Controls.Button consistencyOpen_ = new()
      {
         Content = L("_Open report"),
         Margin = new Thickness(8, 0, 0, 0),
         IsEnabled = false
      };
      private string reportPath_;
      private bool loadingReport_;

      public DiagnosticsView()
      {
         var grid = new Grid();
         grid.SetResourceReference(MarginProperty, "AppPagePadding");
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         // Auto so the consistency card is only as tall as what it has to say -
         // a clean scan is one line, and the connectivity output keeps the rest.
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         grid.Children.Add(new PageHeader
         {
            Title = L("Diagnostics"),
            Subtitle = L("Runs the server's built-in connectivity and configuration checks (outbound port 25, MX resolution, backup directory, IP configuration). The message-store consistency scan below is a separate read-only background task - the server runs it at start-up and hourly and records what it found in a recovery report.")
         });

         var inputRow = new Grid();
         inputRow.SetResourceReference(MarginProperty, "AppCardGap");
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         localDomain_.Margin = new Thickness(0, 0, 8, 0);
         // Two unlabelled boxes read as one question; a caption over each says
         // which domain is which. The captions double as the accessible names.
         System.Windows.Automation.AutomationProperties.SetName(localDomain_, L("Hosted domain"));
         System.Windows.Automation.AutomationProperties.SetName(testDomain_, L("Remote domain"));
         inputRow.Children.Add(Captioned(L("Hosted domain"), localDomain_));
         testDomain_.Margin = new Thickness(0, 0, 8, 0);
         Grid.SetColumn(testDomain_, 1);
         inputRow.Children.Add(Captioned(L("Remote domain"), testDomain_));
         // Bottom-aligned, level with the two captioned inputs' boxes.
         var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
         var run = new Wpf.Ui.Controls.Button { Content = L("_Run diagnostics"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
         run.Click += async (s, e) => await Run();
         actions.Children.Add(run);
         var copy = new Wpf.Ui.Controls.Button { Content = L("_Copy"), Margin = new Thickness(8, 0, 0, 0) };
         copy.Click += (s, e) => { try { if (output_.Text.Length > 0) Clipboard.SetText(output_.Text); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ } };
         actions.Children.Add(copy);
         Grid.SetColumn(actions, 2);
         inputRow.Children.Add(actions);
         Grid.SetRow(inputRow, 1);
         grid.Children.Add(inputRow);

         System.Windows.Automation.AutomationProperties.SetName(output_, L("Diagnostics"));
         var card = new Card { Padding = new Thickness(12), Content = output_ };
         card.SetResourceReference(MarginProperty, "AppCardGap");
         Grid.SetRow(card, 2);
         grid.Children.Add(card);

         Card consistencyCard = BuildConsistencySection();
         Grid.SetRow(consistencyCard, 3);
         grid.Children.Add(consistencyCard);

         Content = grid;
      }

      private Card BuildConsistencySection()
      {
         var section = new Grid();
         section.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         section.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         consistencyStatus_.Margin = new Thickness(0);
         section.Children.Add(consistencyStatus_);

         var consistencyActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
         consistencyRefresh_.Click += async (s, e) => await LoadConsistencyReport();
         consistencyActions.Children.Add(consistencyRefresh_);
         consistencyOpen_.Click += (s, e) => OpenReport();
         consistencyActions.Children.Add(consistencyOpen_);

         consistencyList_.Columns.Add(new DataGridTextColumn
         {
            Header = L("Message ID"),
            Binding = new System.Windows.Data.Binding(nameof(Services.MessageStoreConsistencyEntry.MessageId)),
            Width = 110
         });
         consistencyList_.Columns.Add(new DataGridTextColumn
         {
            Header = L("Account"),
            Binding = new System.Windows.Data.Binding(nameof(Services.MessageStoreConsistencyEntry.Account)),
            Width = 220
         });
         consistencyList_.Columns.Add(new DataGridTextColumn
         {
            Header = L("Expected file"),
            Binding = new System.Windows.Data.Binding(nameof(Services.MessageStoreConsistencyEntry.ExpectedPath)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
         });
         Grid.SetRow(consistencyList_, 1);
         System.Windows.Automation.AutomationProperties.SetName(consistencyList_, L("Message-store consistency"));
         section.Children.Add(consistencyList_);

         return new Card
         {
            Title = L("Message-store consistency"),
            Content = section,
            Footer = consistencyActions
         };
      }

      /// <summary>One captioned input, as the form row the design system draws.</summary>
      private static FieldRow Captioned(string caption, FrameworkElement input)
      {
         // The row takes the input's place in its grid: its column, and its margin.
         var field = new FieldRow { Label = caption, Content = input, Margin = input.Margin };
         Grid.SetColumn(field, Grid.GetColumn(input));
         Grid.SetRow(field, Grid.GetRow(input));
         input.Margin = new Thickness(0);
         return field;
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
            if ((int)domains.Count > 0)
            {
               dynamic first = domains.Item[0];
               localDomain_.Text = (string)first.Name;
               ServerSession.Release(first);
            }
            ServerSession.Release(domains);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }
      }

      public void OnLeave()
      {
      }

      private async Task Run()
      {
         output_.Text = L("Running diagnostics...");

         string report = await Task.Run(() =>
         {
            try
            {
               dynamic diagnostics = ServerSession.Current.Application.Diagnostics;
               diagnostics.LocalDomainName = localDomain_.Dispatcher.Invoke(() => localDomain_.Text.Trim());
               diagnostics.TestDomainName = testDomain_.Dispatcher.Invoke(() => testDomain_.Text.Trim());

               dynamic results = diagnostics.PerformTests();

               var text = new System.Text.StringBuilder();
               int count = (int)results.Count;
               for (int i = 0; i < count; i++)
               {
                  dynamic result = results.Item[i];
                  string name = "", details = "";
                  bool? success = null;
                  try { name = (string)result.Name; } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
                  try { success = (bool)result.Result; } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
                  try { details = (string)result.ExecutionDetails; } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }

                  string state = success == null ? "[ ?? ]   " : success.Value ? "[ OK ]   " : "[FAIL]   "; // no-loc
                  text.AppendLine(state + name);
                  if (!string.IsNullOrWhiteSpace(details))
                     text.AppendLine("         " + details.Replace("\r\n", "\r\n         "));
                  text.AppendLine();
                  ServerSession.Release(result);
               }

               ServerSession.Release(results);
               ServerSession.Release(diagnostics);
               return text.Length > 0 ? text.ToString() : L("No diagnostic results returned.");
            }
            catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
            {
               return F("Diagnostics failed: {0}", ex.Message);
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
         consistencyStatus_.Level = StatusLevel.Information;
         consistencyStatus_.Text = L("Reading the recovery report...");
         reportPath_ = null;

         try
         {
            ConsistencyResult result = await Task.Run(ReadConsistencyReport);

            reportPath_ = result.ReportPath;
            consistencyStatus_.Text = result.Message;
            consistencyStatus_.Level = result.Level switch
            {
               Severity.Good => StatusLevel.Good,
               Severity.Bad => StatusLevel.Critical,
               _ => StatusLevel.Information
            };

            if (result.Entries != null && result.Entries.Count > 0)
            {
               consistencyList_.ItemsSource = result.Entries;
               consistencyList_.Visibility = Visibility.Visible;
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            // Nothing awaits the load started from OnEnter, so report the failure
            // on the page rather than losing it in an unobserved task.
            consistencyStatus_.Level = StatusLevel.Critical;
            consistencyStatus_.Text = F("Could not read the consistency report: {0}", ex.Message);
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
               Message = L("The scan result is only readable on the server machine - hMailServer.INI was not found here. The server publishes the same number as the hmailserver_messagestore_missing_files metric.")
            };
         }

         bool enabled = store.ReadBool("MessageStoreConsistencyCheck", false);
         string logFolder = store.GetLogFolder();

         if (string.IsNullOrWhiteSpace(logFolder))
         {
            return new ConsistencyResult
            {
               Level = Severity.Neutral,
               Message = L("No log folder is configured in hMailServer.INI, so the server has nowhere to write the recovery report.")
            };
         }

         string path = System.IO.Path.Join(logFolder, Services.MessageStoreConsistencyReport.FileName);
         string enabledNote = enabled
            ? ""
            // Asked for by nav key rather than spelled out, so that renaming the
            // page cannot leave this pointing at a page title that no longer exists.
            : F(" The periodic check is currently switched off (MessageStoreConsistencyCheck on the {0} page), so this will not be refreshed.", L(Services.NavigationMap.TitleOf("hardening")));

         string text;
         try
         {
            if (!System.IO.File.Exists(path))
            {
               return new ConsistencyResult
               {
                  Level = Severity.Neutral,
                  Message = enabled
                     ? F("The consistency check is enabled but has not written a report yet. The server scans at start-up and then hourly, and writes {0}.", path)
                     : F("The consistency check is switched off, so no scan has run. Enable MessageStoreConsistencyCheck on the {0} page; the server then scans at start-up and hourly and writes {1}.", L(Services.NavigationMap.TitleOf("hardening")), path)
               };
            }

            using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
               System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            using var reader = new System.IO.StreamReader(stream);
            text = reader.ReadToEnd();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            return new ConsistencyResult
            {
               Level = Severity.Neutral,
               ReportPath = path,
               Message = F("Could not read {0}: {1}", path, ex.Message)
            };
         }

         Services.MessageStoreConsistencyReport report = Services.MessageStoreConsistencyReport.Parse(text);
         string when = string.IsNullOrEmpty(report.Generated) ? "" : F(" Last scan: {0}.", report.Generated);
         int? reportedMissing = report.ReportedMissingCount;
         string truncated = report.IsTruncated && reportedMissing.HasValue
            ? F(" The report header says {0} but lists {1} - it was probably read while the server was rewriting it, so refresh.", reportedMissing.Value, report.Entries.Count)
            : "";

         if (report.MissingCount == 0)
         {
            return new ConsistencyResult
            {
               Level = Severity.Good,
               ReportPath = path,
               Message = L("No problems found - every message row has its file on disk.") + when + truncated + enabledNote
            };
         }

         return new ConsistencyResult
         {
            Level = Severity.Bad,
            ReportPath = path,
            Entries = report.Entries,
            Message = (report.MissingCount == 1
                          ? F("{0} message references a file that is missing on disk.", report.MissingCount)
                          : F("{0} messages reference a file that is missing on disk.", report.MissingCount))
                      + when + truncated + enabledNote
                      + L(" The check is read-only: the server does not delete or repair anything.")
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
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            try
            {
               Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + reportPath_ + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
            {
               MessageBox.Show(F("Could not open {0}: {1}", reportPath_, ex.Message), L("Control Panel"));
            }
         }
      }
   }
}
