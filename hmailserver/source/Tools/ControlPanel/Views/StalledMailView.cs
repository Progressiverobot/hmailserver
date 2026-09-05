// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The stall-diagnosis path from hmailserver/docs/DiagnosingStalledMail.md,
   /// as a page rather than a document: the same two-halves question, the same
   /// log lines to look for, and a button beside each step that goes where the
   /// step says to go. The guide exists because mail that stops moving on a
   /// server that looks healthy took three releases to track down; this page
   /// exists because until now nothing in the product pointed at the guide.
   ///
   /// Nothing here is a setting. The one thing it changes on the server is
   /// debug logging, and only on request, because that is the first step of the
   /// guide and the one people skip.
   /// </summary>
   public class StalledMailView : UserControl, IPageLifecycle
   {
      private const string GuideUrl =
         "https://github.com/Progressiverobot/hmailserver/blob/master/hmailserver/docs/DiagnosingStalledMail.md";

      private readonly TextBlock loggingStatus_ = new()
      {
         FontSize = Typography.Caption,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(0, 8, 0, 0)
      };

      public StalledMailView()
      {
         var panel = new StackPanel { Margin = new Thickness(26, 20, 26, 20), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };

         var title = new TextBlock { Text = "Diagnosing stalled mail" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         panel.Children.Add(title);

         var subtitle = new TextBlock
         {
            Text = "Mail is not moving, the service is running, nothing has crashed and the log seems to stop mid-transaction. " +
                   "The server is not silent about this any more: the answer is usually in one line of the log, and this page says which."
         };
         subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
         panel.Children.Add(subtitle);

         panel.Children.Add(WhichHalf());
         panel.Children.Add(DebugLogging());
         panel.Children.Add(Accepting());
         panel.Children.Add(EveryMessage());
         panel.Children.Add(Delivering());
         panel.Children.Add(Bounds());
         panel.Children.Add(Report());

         Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
      }

      public void OnEnter()
      {
         RefreshLoggingStatus();
      }

      public void OnLeave()
      {
      }

      // ---- the steps ---------------------------------------------------------

      private Border WhichHalf()
      {
         var card = Card("First: which half is stuck?",
            "The two halves fail differently and have different causes. Work out which one you have before anything else.");
         var columns = new UniformGrid { Columns = 2 };

         var accepting = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
         accepting.Children.Add(Heading("Accepting"));
         accepting.Children.Add(Body("The sending server connects, sends the message, and then waits. From its side you see a timeout " +
            "after the message body was transmitted - Postfix reports \"timed out while sending end of data\". " +
            "The message never appears in your queue."));
         accepting.Children.Add(Link("Open the live logs", "logs"));
         columns.Children.Add(accepting);

         var delivering = new StackPanel();
         delivering.Children.Add(Heading("Delivering"));
         delivering.Children.Add(Body("The message is accepted - the sender got a 250 - is visible in the delivery queue, and never leaves."));
         delivering.Children.Add(Link("Open the delivery queue", "queue"));
         columns.Children.Add(delivering);

         ((StackPanel)card.Child).Children.Add(columns);
         return card;
      }

      private Border DebugLogging()
      {
         var card = Card("Turn on debug logging first",
            "The lines this page refers to are written at debug level; the slow ones are also written at application level, " +
            "so with application logging alone you still see the important ones. Reproduce the problem once, then read the log. " +
            "Remember to turn debug logging off afterwards on a busy server.");
         var content = (StackPanel)card.Child;

         var row = new StackPanel { Orientation = Orientation.Horizontal };
         var enable = new Wpf.Ui.Controls.Button
         {
            Content = "Turn on debug logging now",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            Margin = new Thickness(0, 4, 8, 0)
         };
         AutomationProperties.SetName(enable, "Turn on debug logging now");
         enable.Click += (s, e) => SetDebugLogging(true);
         row.Children.Add(enable);

         var disable = new Wpf.Ui.Controls.Button { Content = "Turn it off again", Margin = new Thickness(0, 4, 8, 0) };
         AutomationProperties.SetName(disable, "Turn debug logging off again");
         disable.Click += (s, e) => SetDebugLogging(false);
         row.Children.Add(disable);

         row.Children.Add(Link("Open Logging settings", "logging"));
         content.Children.Add(row);
         content.Children.Add(loggingStatus_);
         return card;
      }

      private Border Accepting()
      {
         var card = Card("Accepting: the sender times out after sending the message",
            "Between the 354 and the 250 the server runs the accept pipeline - spam tests, message modifications, archiving, " +
            "the OnAcceptMessage script and the database save - on a bounded pool of threads, and replies only when it finishes. " +
            "The log times each stage. Read it as a sequence and look for the last line written: the stage that is stuck is the one " +
            "after it. Two stages announce themselves with a start line (spam-protection and script/save); message modifications " +
            "do not, so a stall there shows as \"done spam-protection\" followed by silence. A stage taking ten seconds or more is " +
            "logged at application level even without debug logging.");
         var content = (StackPanel)card.Child;

         content.Children.Add(Table(new[]
         {
            ("SpamTestSpamAssassin with a large Time:", "spamd is unreachable, overloaded, or accepting connections without answering."),
            ("SpamTestDNSBlackLists, SpamTestSURBL or SpamTestSPF slow", "The resolver is not answering: look in the TCP/IP log for \"DNS - Query timed out\". Do not reach for DNSServer as the fix - leave it empty unless you need a specific resolver."),
            ("\"done spam-protection\", then silence", "Message modifications: the spam headers, the signature, the List-* headers, or the write of the modified message back to disk."),
            ("script/save slow", "An OnAcceptMessage event script, or the database."),
            ("Nothing between 354 and silence", "A version older than 6.2.17; upgrade, because that is the version that added these lines."),
            ("451 4.3.1 replies", "Acceptance exceeded FinalizationTimeout (240 s by default): the sender retries rather than waiting for ever, the error entry names the deadline, and the stage timings above it name what consumed the time."),
         }));

         var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
         links.Children.Add(Link("Open the live logs", "logs"));
         links.Children.Add(Link("Anti-spam settings (SpamAssassin)", "antispam"));
         links.Children.Add(Link("Test SpamAssassin and ClamAV", "diagnostics"));
         content.Children.Add(links);
         return card;
      }

      private Border EveryMessage()
      {
         var card = Card("If every message stalls, not just one",
            "Look for \"Task SMTP-accept session=42 ip=... waited 8 seconds for a thread in work queue Asynchronous task queue\" and, when it " +
            "is severe, \"All 15 threads in work queue Asynchronous task queue have been busy for at least 120 seconds, so no further task " +
            "on this queue can start\". That means every worker is occupied and messages are queuing behind them: one slow dependency does " +
            "this to the whole server, which is why a single wedged scanner used to look like the server had stopped responding. The task " +
            "names say which sessions are stuck; the stage timings say what they are stuck on.");
         var content = (StackPanel)card.Child;
         var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
         links.Children.Add(Link("Server status (who is connected)", "status"));
         links.Children.Add(Link("Threads and the stall threshold", "hardening"));
         content.Children.Add(links);
         return card;
      }

      private Border Delivering()
      {
         var card = Card("Delivering: the message is accepted but never leaves",
            "Delivery runs on a separate, smaller pool. The usual causes, in the order they occur:");
         var content = (StackPanel)card.Child;

         content.Children.Add(Bullet("A virus scanner that stops responding. ClamAV is contacted after the message is accepted, so a wedged " +
            "clamd shows up as accepted-but-never-delivered. Each socket operation is bounded by ClamMinTimeout / ClamMaxTimeout - a deadline " +
            "on one read or write, armed afresh each time, so a large message streamed in chunks can take longer than ClamMaxTimeout in total."));
         content.Children.Add(Bullet("A remote server that answers extremely slowly. The idle timeout is re-armed on every byte, so a host " +
            "that sends one byte occasionally used to hold a delivery thread indefinitely; ClientSessionCeiling (30 minutes by default) is the " +
            "absolute ceiling, armed once and never re-armed."));
         content.Children.Add(Bullet("A custom virus scanner or external tool that hangs - bounded by ExternalProcessTimeout."));
         content.Children.Add(Bullet("The database: check for errors mentioning the connection pool. When the pool deadline expires a recipient " +
            "lookup answers 451 4.3.2 rather than 550, so a database locked by a backup defers mail instead of bouncing it."));

         var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
         links.Children.Add(Link("Open the delivery queue", "queue"));
         links.Children.Add(Link("Trace one message", "messagetrace"));
         links.Children.Add(Link("Anti-virus settings (ClamAV timeouts)", "antivirus"));
         content.Children.Add(links);
         return card;
      }

      private Border Bounds()
      {
         var card = Card("The settings that bound each stage",
            "All in hMailServer.ini under [Settings], all in seconds, with defaults chosen to sit well inside a typical sending server's " +
            "timeout. Two exceptions matter: SAMaxTimeout and ClamMaxTimeout go through TimeoutCalculator, which returns the minimum whenever " +
            "the maximum is lower than it - so 0 gives a shorter bound, not an absent one. To lengthen either a long way, raise the matching " +
            "...MinTimeout too.");
         var content = (StackPanel)card.Child;

         content.Children.Add(Table(new[]
         {
            ("FinalizationTimeout (240)", "The whole accept pipeline; then the sender gets a 451. 0 = no bound."),
            ("SAMaxTimeout (90)", "SpamAssassin: idle timeout, and a session ceiling of this plus 30 s. 0 is NOT no bound."),
            ("ClamMaxTimeout (90)", "ClamAV: idle timeout per socket operation. 0 is NOT no bound."),
            ("DNSQueryTimeout (10)", "A single DNS query. 0 = no bound."),
            ("ScriptTimeout (60)", "One event script invocation. 0 = no bound."),
            ("ExternalProcessTimeout (300)", "An external scanner process. 0 = no bound."),
            ("ClientSessionCeiling (1800)", "An entire outbound delivery session. 0 = no bound."),
            ("AsyncQueueStallThreshold (120)", "How long every worker may be busy before the saturation report. 0 = reporting off."),
            ("DBConnectionAcquireTimeout (60)", "Waiting for a pooled database connection. 0 = no bound."),
         }));

         var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
         links.Children.Add(Link("Server limits & expert settings (the timeouts)", "hardening"));
         links.Children.Add(Link("Anti-spam (SAMaxTimeout)", "antispam"));
         links.Children.Add(Link("Anti-virus (ClamMaxTimeout)", "antivirus"));
         content.Children.Add(links);
         return card;
      }

      private Border Report()
      {
         var card = Card("If none of that identifies it",
            "Open an issue with: the log from the 354 (or from the delivery attempt) onwards, including the stage timing lines; the " +
            "ERROR_hmailserver_<date>.log for the same window; which scanners and event scripts are enabled; and whether the message " +
            "eventually arrives, arrives twice, or never arrives.");
         var content = (StackPanel)card.Child;

         var open = new Wpf.Ui.Controls.Button { Content = "Open the full guide (DiagnosingStalledMail.md)", Margin = new Thickness(0, 6, 8, 0) };
         AutomationProperties.SetName(open, "Open the full guide on GitHub");
         open.Click += (s, e) => OpenGuide();
         content.Children.Add(open);
         return card;
      }

      // ---- actions -------------------------------------------------------------

      private void SetDebugLogging(bool on)
      {
         try
         {
            dynamic logging = ServerSession.Current.Application.Settings.Logging;
            if (on)
               logging.Enabled = true;
            logging.LogDebug = on;
            RefreshLoggingStatus();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            loggingStatus_.Text = "Could not change logging: " + ex.Message;
         }
      }

      private void RefreshLoggingStatus()
      {
         try
         {
            dynamic logging = ServerSession.Current.Application.Settings.Logging;
            bool enabled = (bool)logging.Enabled;
            bool debug = (bool)logging.LogDebug;
            loggingStatus_.Text = !enabled
               ? "Logging is off altogether, so nothing below will be written until it is on."
               : debug
                  ? "Debug logging is ON. The stage timings and the DNS lines are being written; turn it off again when you have what you need."
                  : "Debug logging is off. Application logging still records any stage that takes ten seconds or more.";
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            loggingStatus_.Text = "Could not read the logging state: " + ex.Message;
         }
      }

      private static void OpenGuide()
      {
         try
         {
            Process.Start(new ProcessStartInfo(GuideUrl) { UseShellExecute = true });
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            Dialogs.Show("Could not open a browser: " + ex.Message + "\n\n" + GuideUrl, "Diagnosing stalled mail");
         }
      }

      // ---- building blocks -------------------------------------------------------

      private static Border Card(string heading, string intro)
      {
         var border = new Border { Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(16, 12, 16, 14) };
         border.SetResourceReference(StyleProperty, "Card");

         var stack = new StackPanel();
         stack.Children.Add(new TextBlock
         {
            Text = heading,
            FontSize = Typography.SectionHeading,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
         });
         stack.Children.Add(Body(intro));
         border.Child = stack;
         return border;
      }

      private static TextBlock Heading(string text)
      {
         return new TextBlock { Text = text, FontSize = Typography.Body, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) };
      }

      private static TextBlock Body(string text)
      {
         var block = new TextBlock { Text = text, FontSize = Typography.Body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
         block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         return block;
      }

      private static TextBlock Bullet(string text)
      {
         var block = new TextBlock { Text = "•  " + text, FontSize = Typography.Body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 6) };
         block.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         return block;
      }

      private static Grid Table((string what, string cause)[] rows)
      {
         var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
         grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

         for (int i = 0; i < rows.Length; i++)
         {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var what = new TextBlock
            {
               Text = rows[i].what,
               FontSize = Typography.Body,
               FontWeight = FontWeights.SemiBold,
               TextWrapping = TextWrapping.Wrap,
               Margin = new Thickness(0, 2, 12, 6)
            };
            Grid.SetRow(what, i);
            Grid.SetColumn(what, 0);
            grid.Children.Add(what);

            var cause = new TextBlock { Text = rows[i].cause, FontSize = Typography.Body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
            cause.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetRow(cause, i);
            Grid.SetColumn(cause, 1);
            grid.Children.Add(cause);
         }

         return grid;
      }

      private static Wpf.Ui.Controls.Button Link(string text, string navKey)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = text,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Margin = new Thickness(0, 4, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand
         };
         AutomationProperties.SetName(button, text);
         button.Click += (s, e) => (Application.Current.MainWindow as MainWindow)?.NavigateTo(navKey);
         return button;
      }
   }
}
