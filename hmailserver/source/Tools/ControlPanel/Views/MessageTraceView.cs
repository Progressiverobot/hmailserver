// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using static hMailServer.ControlPanel.Services.Loc;

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
      /// <summary>
      /// The toolbar's search box is the address: the search runs on Enter or the
      /// Search button, not on every keystroke, because it is a query to the
      /// server rather than a filter over rows already here.
      /// </summary>
      private readonly Toolbar toolbar_ = new() { SearchPlaceholder = L("Address") };

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
         SelectionMode = DataGridSelectionMode.Single
      };

      private readonly InlineNotice status_ = new() { Visibility = Visibility.Collapsed };
      private readonly EmptyState empty_ = new() { Icon = Wpf.Ui.Controls.SymbolRegular.DocumentSearch24, Visibility = Visibility.Collapsed };

      public MessageTraceView()
      {
         Build();
         Status_(StatusLevel.Information, L("Enter an address and search. The trace records nothing at all until message tracing is switched on, on the Logging page - it is off by default because it stores who corresponds with whom."));
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

         int count = (int)trace.Count;

         for (int i = 0; i < count; i++)
         {
            dynamic item = trace[i];

            try
            {
               rows.Add(new TraceRow
               {
                  OccurredTime = (string)item.OccurredTime,
                  EventName = (string)item.EventName,
                  Sender = (string)item.Sender,
                  Recipient = (string)item.Recipient,
                  StatusCode = (int)item.StatusCode,
                  QueueId = (int)item.QueueID
               });
            }
            finally
            {
               ServerSession.Release(item);
            }
         }

         list_.ItemsSource = rows;
         StatusText.Show(empty_, null, count, null, emptyMessage);

         if (count == 0)
            status_.Visibility = Visibility.Collapsed;
         else
            Status_(StatusLevel.Information, count == 1 ? L("1 event.") : F("{0} events, newest first.", count));
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
            ? L("Refused before the message was queued, so there is no message to follow.")
            : F("Queue id {0} - select and choose \"Follow this message\".", QueueId);
      }

      /// <summary>What the last search said, at its level.</summary>
      private void Status_(StatusLevel level, string text)
      {
         status_.Level = level;
         status_.Text = text;
         status_.Visibility = Visibility.Visible;
      }

      private void Search()
      {
         try
         {
            dynamic trace = OpenTrace();
            try
            {
               trace.Search(toolbar_.SearchText.Trim());
               Fill(trace, L("No events for that address. Either nothing has happened to it, or the trace was switched off at the time - it records only while message tracing is on (Logging page)."));
            }
            finally
            {
               ServerSession.Release(trace);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            Status_(StatusLevel.Critical, ServerSession.DescribeComError(ex));
         }
      }

      private void FollowSelected()
      {
         if (list_.SelectedItem is not TraceRow selected)
         {
            Status_(StatusLevel.Information, L("Select an event first."));
            return;
         }

         int queueId = selected.QueueId;

         if (queueId == 0)
         {
            Status_(StatusLevel.Information, L("That event happened before the message was queued - a refusal during the SMTP conversation - so there is no message to follow."));
            return;
         }

         try
         {
            dynamic trace = OpenTrace();
            try
            {
               trace.SearchByQueueID(queueId);
               Fill(trace, L("No events for that message."));
               Status_(StatusLevel.Information, F("Every event for queue id {0}, oldest first.", queueId));
            }
            finally
            {
               ServerSession.Release(trace);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            Status_(StatusLevel.Critical, ServerSession.DescribeComError(ex));
         }
      }

      private void SweepExpired()
      {
         try
         {
            dynamic trace = OpenTrace();
            try
            {
               int removed = (int)trace.DeleteExpired();

               if (removed == 0)
                  Status_(StatusLevel.Information, L("Nothing was old enough to remove. The window is MessageTraceRetentionDays, and 0 means never."));
               else
                  Status_(StatusLevel.Good, F("{0} event(s) past the retention window were removed.", removed));
            }
            finally
            {
               ServerSession.Release(trace);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            Status_(StatusLevel.Critical, ServerSession.DescribeComError(ex));
         }
      }

      private void Build()
      {
         var root = new Grid();
         root.SetResourceReference(MarginProperty, "AppPagePadding");
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         root.Children.Add(new PageHeader
         {
            Title = L("Message trace"),
            Subtitle = L("What happened to a particular message. Search by any address - it matches senders and recipients - then follow one row to see every event for that message in order.")
         });

         // Enter in the address box searches, as it always did.
         toolbar_.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Search(); };

         var filters = new StackPanel { Orientation = Orientation.Horizontal };
         filters.Children.Add(MakeButton(L("_Search"), Wpf.Ui.Controls.ControlAppearance.Primary, (_, _) => Search()));
         toolbar_.Filters = filters;

         var actions = new StackPanel { Orientation = Orientation.Horizontal };
         var follow = MakeButton(L("_Follow this message"), Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => FollowSelected());
         actions.Children.Add(follow);
         hMailServer.ControlPanel.Services.SelectionGate.Bind(list_, follow);
         actions.Children.Add(MakeButton(L("_Remove expired"), Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => SweepExpired()));
         toolbar_.Actions = actions;
         Grid.SetRow(toolbar_, 1);
         root.Children.Add(toolbar_);

         Grid.SetRow(status_, 2);
         root.Children.Add(status_);

         BuildColumns();
         System.Windows.Automation.AutomationProperties.SetName(list_, L("Message trace"));

         var host = new Grid();
         host.Children.Add(list_);
         host.Children.Add(empty_);
         var card = new Card { Padding = new Thickness(8), Content = host };
         Grid.SetRow(card, 3);
         root.Children.Add(card);

         Content = root;
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
            Header = L("Time"),
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
            Header = L("Event"),
            Binding = new System.Windows.Data.Binding(nameof(TraceRow.EventName)),
            Width = DataGridLength.Auto
         });

         list_.Columns.Add(new DataGridTextColumn
         {
            Header = L("Sender"),
            Binding = new System.Windows.Data.Binding(nameof(TraceRow.Sender)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
         });

         list_.Columns.Add(new DataGridTextColumn
         {
            Header = L("Recipient"),
            Binding = new System.Windows.Data.Binding(nameof(TraceRow.Recipient)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
         });

         var status = new DataGridTextColumn
         {
            Header = L("Status"),
            Binding = new System.Windows.Data.Binding(nameof(TraceRow.StatusCode)),
            Width = DataGridLength.Auto
         };
         GridStyles.Number(status);
         list_.Columns.Add(status);

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
