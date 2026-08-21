using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// "What happened to the message Jane sent at 14:20."
   ///
   /// That question used to be answered by grepping several log files and hoping the
   /// relevant one had not rotated. The events were never missing - the server has
   /// called its delivery journal from every interesting site for years - they just
   /// went to a text file shaped for a web-statistics tool.
   ///
   /// So this page is a search box, because a search is the shape of the question.
   /// One address in, matched against sender or recipient, newest first; then a
   /// second click on any row to pull together every event for that one message.
   ///
   /// It deliberately does not try to be a dashboard. There is no count of everything
   /// and no time-series, because nobody comes here to browse - they come with an
   /// address and a complaint.
   /// </summary>
   public class MessageTraceView : UserControl
   {
      private readonly TextBox address_ = new()
      {
         Width = 320,
         VerticalContentAlignment = VerticalAlignment.Center
      };

      private readonly ListBox list_ = new()
      {
         FontSize = Typography.Body,
         FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily),
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         MinHeight = 340
      };

      private readonly TextBlock status_ = new()
      {
         FontSize = Typography.Caption,
         Margin = new Thickness(0, 10, 0, 0),
         TextWrapping = TextWrapping.Wrap
      };

      public MessageTraceView()
      {
         Build();
         status_.Text = "Enter an address and search. The trace records nothing at all unless "
                      + "MessageTraceEnabled is set in hMailServer.ini - it is off by default because it "
                      + "stores who corresponds with whom.";
      }

      private dynamic OpenTrace()
      {
         dynamic globalObjects = ServerSession.Current.Application.GlobalObjects;
         dynamic trace = globalObjects.MessageTrace;

         ServerSession.Release(globalObjects);

         return trace;
      }

      private void Fill(dynamic trace, string emptyMessage)
      {
         list_.Items.Clear();

         int count = (int) trace.Count;

         for (int i = 0; i < count; i++)
         {
            dynamic item = trace[i];

            try
            {
               int queueId = (int) item.QueueID;

               list_.Items.Add(new ListBoxItem
               {
                  Content = string.Format("{0}  {1,-11} {2,-34} -> {3,-34} {4}",
                     (string) item.OccurredTime,
                     (string) item.EventName,
                     (string) item.Sender,
                     (string) item.Recipient,
                     (int) item.StatusCode),
                  // 0 means the event happened before a queue entry existed - a refusal
                  // at RCPT - so there is nothing to follow and the row is not clickable
                  // into a story.
                  Tag = queueId,
                  ToolTip = queueId == 0
                     ? "Refused before the message was queued, so there is no message to follow."
                     : "Queue id " + queueId + " - select and choose \"Follow this message\"."
               });
            }
            finally
            {
               ServerSession.Release(item);
            }
         }

         status_.Text = count == 0 ? emptyMessage
            : count == 1 ? "1 event." : count + " events, newest first.";
      }

      private void Search()
      {
         try
         {
            dynamic trace = OpenTrace();
            try
            {
               trace.Search(address_.Text.Trim());
               Fill(trace, "No events for that address. Either nothing has happened to it, or the trace "
                         + "was switched off at the time - it records only while MessageTraceEnabled is set.");
            }
            finally
            {
               ServerSession.Release(trace);
            }
         }
         catch (Exception ex)
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private void FollowSelected()
      {
         if (list_.SelectedItem is not ListBoxItem selected || selected.Tag is not int queueId)
         {
            status_.Text = "Select an event first.";
            return;
         }

         if (queueId == 0)
         {
            status_.Text = "That event happened before the message was queued - a refusal during the SMTP "
                         + "conversation - so there is no message to follow.";
            return;
         }

         try
         {
            dynamic trace = OpenTrace();
            try
            {
               trace.SearchByQueueID(queueId);
               Fill(trace, "No events for that message.");
               status_.Text = "Every event for queue id " + queueId + ", oldest first.";
            }
            finally
            {
               ServerSession.Release(trace);
            }
         }
         catch (Exception ex)
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private void SweepExpired()
      {
         try
         {
            dynamic trace = OpenTrace();
            try
            {
               int removed = (int) trace.DeleteExpired();

               status_.Text = removed == 0
                  ? "Nothing was old enough to remove. The window is MessageTraceRetentionDays, and 0 "
                    + "means never."
                  : removed + " event(s) past the retention window were removed.";
            }
            finally
            {
               ServerSession.Release(trace);
            }
         }
         catch (Exception ex)
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private void Build()
      {
         // The shared page gutter and title style - these two pages were the
         // newest and had drifted to their own 16px gutter and 22px title,
         // which is exactly how a design system erodes: newest pages first.
         var root = new StackPanel { Margin = new Thickness(26, 20, 26, 20) };

         var title = new TextBlock { Text = "Message trace" };
         title.SetResourceReference(FrameworkElement.StyleProperty, "PageTitle");
         root.Children.Add(title);

         var hint = new TextBlock
         {
            Text = "What happened to a particular message. Search by any address - it matches senders and "
                 + "recipients - then follow one row to see every event for that message in order.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
         };
         hint.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         root.Children.Add(hint);

         var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
         toolbar.Children.Add(new TextBlock
         {
            Text = "Address",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
         });
         address_.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Search(); };
         toolbar.Children.Add(address_);
         toolbar.Children.Add(MakeButton("Search", Wpf.Ui.Controls.ControlAppearance.Primary, (_, _) => Search()));
         toolbar.Children.Add(MakeButton("Follow this message", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => FollowSelected()));
         toolbar.Children.Add(MakeButton("Remove expired", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => SweepExpired()));
         root.Children.Add(toolbar);

         var card = new Border { Padding = new Thickness(8) };
         card.SetResourceReference(StyleProperty, "Card");
         card.Child = list_;
         root.Children.Add(card);

         root.Children.Add(status_);

         Content = new ScrollViewer
         {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root
         };
      }

      private static Button MakeButton(string text, Wpf.Ui.Controls.ControlAppearance appearance, RoutedEventHandler onClick)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = text,
            Appearance = appearance,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 110
         };
         button.Click += onClick;
         return button;
      }
   }
}
