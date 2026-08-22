// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

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

      /// <summary>
      /// A real grid, because the rows are a table and were being drawn as one.
      ///
      /// This was a ListBox of monospaced strings padded to fixed widths - the
      /// addresses to 34 characters each. Every address longer than its column
      /// pushed the rest of that row out of line, and mail addresses longer than 34
      /// characters are not an edge case. It also had no headers, so the reader had
      /// to work out what the columns were; nothing could be sorted; a single field
      /// could not be copied without the padding around it; and a screen reader read
      /// each row as one run-on line with the alignment spaces in it.
      ///
      /// The column styling is the application's, from the implicit DataGrid styles
      /// in App.xaml, so this page did not need any appearance code of its own.
      /// </summary>
      private readonly DataGrid list_ = new()
      {
         AutoGenerateColumns = false,
         IsReadOnly = true,
         CanUserAddRows = false,
         CanUserDeleteRows = false,
         CanUserResizeRows = false,
         SelectionMode = DataGridSelectionMode.Single,
         SelectionUnit = DataGridSelectionUnit.FullRow,
         HeadersVisibility = DataGridHeadersVisibility.Column,
         GridLinesVisibility = DataGridGridLinesVisibility.None,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         RowBackground = System.Windows.Media.Brushes.Transparent,
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
         var rows = new System.Collections.Generic.List<TraceRow>();

         int count = (int) trace.Count;

         for (int i = 0; i < count; i++)
         {
            dynamic item = trace[i];

            try
            {
               rows.Add(new TraceRow
               {
                  OccurredTime = (string) item.OccurredTime,
                  EventName = (string) item.EventName,
                  Sender = (string) item.Sender,
                  Recipient = (string) item.Recipient,
                  StatusCode = (int) item.StatusCode,
                  QueueId = (int) item.QueueID
               });
            }
            finally
            {
               ServerSession.Release(item);
            }
         }

         list_.ItemsSource = rows;

         status_.Text = count == 0 ? emptyMessage
            : count == 1 ? "1 event." : count + " events, newest first.";
      }

      /// <summary>
      /// One row of the grid.
      ///
      /// The COM objects behind these are released as soon as they are read - the
      /// trace collection is a snapshot and holding a few hundred live references
      /// across a user's browsing is how a page keeps a server object alive for an
      /// afternoon - so the values are copied out rather than bound through.
      /// </summary>
      private sealed class TraceRow
      {
         public string OccurredTime { get; init; }
         public string EventName { get; init; }
         public string Sender { get; init; }
         public string Recipient { get; init; }
         public int StatusCode { get; init; }

         /// <summary>
         /// 0 means the event happened before a queue entry existed - a refusal at
         /// RCPT - so there is nothing to follow and no story to open.
         /// </summary>
         public int QueueId { get; init; }

         public string FollowHint => QueueId == 0
            ? "Refused before the message was queued, so there is no message to follow."
            : "Queue id " + QueueId + " - select and choose \"Follow this message\".";
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
         if (list_.SelectedItem is not TraceRow selected)
         {
            status_.Text = "Select an event first.";
            return;
         }

         int queueId = selected.QueueId;

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

         BuildColumns();

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

      /// <summary>
      /// The columns, in the order the question is asked: when, what happened, who
      /// to whom, and what the server said.
      ///
      /// The two address columns are the ones that used to be padded to a fixed 34
      /// characters and are now star-sized, so a long address takes the room it
      /// needs and the reader can drag the divider if it needs more. Time, event and
      /// status size to their contents, because none of them varies much and giving
      /// them a share of the width takes it from the addresses, which is where the
      /// variation actually is.
      /// </summary>
      private void BuildColumns()
      {
         list_.Columns.Add(new DataGridTextColumn
         {
            Header = "Time",
            Binding = new System.Windows.Data.Binding(nameof(TraceRow.OccurredTime)),
            // Auto, not SizeToCells. SizeToCells measures the CELLS only, so on an
            // empty grid the column collapses to zero and its header disappears
            // with it - which is how Quarantine shipped a table showing Sender,
            // Recipients and Subject while Held and Score were simply absent until
            // the first row arrived. Auto is max(header, cells), so the column is
            // never narrower than the word naming it.
            Width = DataGridLength.Auto
         });

         list_.Columns.Add(new DataGridTextColumn
         {
            Header = "Event",
            Binding = new System.Windows.Data.Binding(nameof(TraceRow.EventName)),
            Width = DataGridLength.Auto
         });

         list_.Columns.Add(new DataGridTextColumn
         {
            Header = "Sender",
            Binding = new System.Windows.Data.Binding(nameof(TraceRow.Sender)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
         });

         list_.Columns.Add(new DataGridTextColumn
         {
            Header = "Recipient",
            Binding = new System.Windows.Data.Binding(nameof(TraceRow.Recipient)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
         });

         list_.Columns.Add(new DataGridTextColumn
         {
            Header = "Status",
            Binding = new System.Windows.Data.Binding(nameof(TraceRow.StatusCode)),
            Width = DataGridLength.Auto
         });

         // The tip that used to live on each ListBoxItem. It says whether a row can
         // be followed at all, which is not otherwise visible - a refusal at RCPT
         // looks exactly like any other event until you try to follow it.
         // BasedOn the application's implicit DataGridRow style, not instead of it.
         // Assigning RowStyle REPLACES the implicit style rather than adding to it,
         // so building this from scratch would have quietly cost this grid alone the
         // hover and selection fills every other grid in the app has.
         var rowStyle = new Style(typeof(DataGridRow));

         if (Application.Current?.TryFindResource(typeof(DataGridRow)) is Style appRowStyle)
            rowStyle.BasedOn = appRowStyle;

         rowStyle.Setters.Add(new Setter(ToolTipProperty,
            new System.Windows.Data.Binding(nameof(TraceRow.FollowHint))));
         list_.RowStyle = rowStyle;
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
