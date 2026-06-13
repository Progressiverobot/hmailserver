using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

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
         addPanel.Children.Add(new TextBlock { Text = "Add relay", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });

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
         FontSize = 12.5,
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
         var run = new Wpf.Ui.Controls.Button { Content = "Query", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
         run.Click += async (s, e) => await RunQuery();
         Grid.SetColumn(run, 1);
         inputRow.Children.Add(run);
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
            var psi = new ProcessStartInfo("nslookup", "-type=mx " + domain)
            {
               RedirectStandardOutput = true,
               RedirectStandardError = true,
               UseShellExecute = false,
               CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            string stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            output_.Text = stdout.Trim();
         }
         catch (Exception ex)
         {
            output_.Text = "Query failed: " + ex.Message;
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
         FontSize = 13,
         Padding = new Thickness(6)
      };
      private readonly TextBlock status_ = new() { FontSize = 12, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.7 };

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

      private static TextBlock Label(string text) => new() { Text = text, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 4) };

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
            utilities.EmailAllAccounts(wildcard_.Text, fromAddress_.Text.Trim(), fromName_.Text.Trim(),
               subject_.Text, body_.Text);
            ServerSession.Release(utilities);
            status_.Text = "Sendout queued " + DateTime.Now.ToLongTimeString() + ".";
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
         FontSize = 12.5,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

      public DiagnosticsView()
      {
         var grid = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         var header = new StackPanel();
         var title = new TextBlock { Text = "Diagnostics" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         header.Children.Add(title);
         var sub = new TextBlock { Text = "Runs the server's built-in connectivity and configuration checks (outbound port 25, MX resolution, backup directory, IP configuration)." };
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
         var run = new Wpf.Ui.Controls.Button { Content = "Run diagnostics", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
         run.Click += async (s, e) => await Run();
         Grid.SetColumn(run, 2);
         inputRow.Children.Add(run);
         Grid.SetRow(inputRow, 1);
         grid.Children.Add(inputRow);

         var card = new Border { Padding = new Thickness(12) };
         card.SetResourceReference(StyleProperty, "Card");
         card.Child = output_;
         Grid.SetRow(card, 2);
         grid.Children.Add(card);

         Content = grid;
      }

      public void OnEnter()
      {
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
                  bool success = false;
                  try { name = (string) result.Name; } catch (Exception) { }
                  try { success = (bool) result.Success; } catch (Exception) { }
                  try { details = (string) result.Details; } catch (Exception) { }

                  text.AppendLine((success ? "[ OK ]   " : "[FAIL]   ") + name);
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
   }
}
