// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The spam quarantine review queue: what is being held, and the two things that
   /// can be done about each one.
   ///
   /// This page exists because of what quarantining actually does. A refused message
   /// is answered 550 during the SMTP conversation and the sender is told; a
   /// quarantined one is answered 250, so the sender believes it arrived and will
   /// never retry. That makes this list the ONLY place those messages exist - which
   /// is the point when the filter was wrong, and the reason nothing here happens on
   /// a timer. Release delivers; Delete is final.
   ///
   /// Read-heavy on purpose: the columns are the ones somebody needs in order to
   /// decide without opening anything - who it is from, who it was for, the subject,
   /// what the filter objected to, and how badly. If those five are not enough, the
   /// message is a judgement call rather than an obvious one, and it is better to
   /// release it to the person it was addressed to than to guess here.
   /// </summary>
   public class QuarantineView : UserControl, IPageLifecycle
   {
      /// <summary>
      /// Five fields per held message, so it is a table and is drawn as one.
      ///
      /// It used to be a ListBox of strings built as
      /// "{0}   {1}  ->  {2}   [score {3}]   {4}" - date, sender, recipients, score
      /// and subject run together on one line with runs of spaces standing in for
      /// columns. Nothing lined up, because the font is proportional and the fields
      /// vary in length; the subject, which is what anyone scans for, sat at the
      /// far right after everything else had taken what it wanted; and the score,
      /// which is the one field worth sorting by, could not be sorted by.
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
      private readonly EmptyState empty_ = new() { Icon = Wpf.Ui.Controls.SymbolRegular.MailWarning24, Visibility = Visibility.Collapsed };

      public QuarantineView() => Build();

      // Loading on entry rather than in the constructor, because the page cache
      // keeps this instance alive for the rest of the session: a constructor-only
      // load meant leaving and returning showed the list as it stood the FIRST
      // time the page was opened - on the one page whose list is the only copy
      // of the messages it shows.
      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private dynamic OpenQuarantine()
      {
         dynamic settings = ServerSession.Current.Application.Settings;
         dynamic antiSpam = settings.AntiSpam;
         dynamic quarantine = antiSpam.Quarantine;

         ServerSession.Release(antiSpam);
         ServerSession.Release(settings);

         return quarantine;
      }

      /// <summary>What the last action said, at its level.</summary>
      private void Status_(StatusLevel level, string text)
      {
         status_.Level = level;
         status_.Text = text;
         status_.Visibility = Visibility.Visible;
      }

      private void Reload()
      {
         var rows = new System.Collections.Generic.List<HeldRow>();

         try
         {
            dynamic quarantine = OpenQuarantine();
            try
            {
               quarantine.Refresh();

               int total = (int)quarantine.Count;
               int listed = 0;

               for (int i = 0; ; i++)
               {
                  dynamic item;

                  try
                  {
                     item = quarantine[i];
                  }
                  catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
                  {
                     // The collection loads a page rather than the whole table, and
                     // indexing past it is how that page ends.
                     break;
                  }

                  try
                  {
                     rows.Add(new HeldRow
                     {
                        CreatedTime = (string)item.CreatedTime,
                        Sender = (string)item.Sender,
                        Recipients = (string)item.Recipients,
                        Score = (int)item.Score,
                        Subject = string.IsNullOrEmpty((string)item.Subject)
                           ? L("(no subject)") : (string)item.Subject,
                        Reason = (string)item.Reason,
                        Id = (int)item.ID
                     });

                     listed++;
                  }
                  finally
                  {
                     ServerSession.Release(item);
                  }
               }

               list_.ItemsSource = rows;
               StatusText.Show(empty_, null, total, null,
                  L("Nothing is held. Quarantining is switched off - turn it on under Anti-spam settings - and until it is on, spam over the delete threshold is refused during the SMTP conversation rather than stored."));

               if (total == 0)
                  status_.Visibility = Visibility.Collapsed;
               else if (listed < total)
                  Status_(StatusLevel.Information, F("{0} held, showing the {1} most recent. The rest are still there - work through these, or let the retention sweep age them out.", total, listed));
               else
                  Status_(StatusLevel.Information, total == 1 ? L("1 message held.") : F("{0} messages held.", total));
            }
            finally
            {
               ServerSession.Release(quarantine);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            Status_(StatusLevel.Critical, ServerSession.DescribeComError(ex));
         }
      }

      /// <summary>One held message. Values are copied out so the COM object can be
      /// released immediately - the collection is a page of a table, and holding a
      /// live reference per visible row keeps that many server objects alive for as
      /// long as somebody leaves the page open.</summary>
      private sealed class HeldRow
      {
         public string CreatedTime { get; init; }
         public string Sender { get; init; }
         public string Recipients { get; init; }
         public int Score { get; init; }
         public string Subject { get; init; }
         public string Reason { get; init; }
         public int Id { get; init; }

         public string HeldBecause => F("Held because: {0}", Reason);
      }

      /// <summary>
      /// Held, from whom, to whom, how badly it scored, and what it says it is.
      ///
      /// Subject and the two address columns share the width, because those are the
      /// three that vary and the subject is what anyone actually scans. Score sizes
      /// to its content and is right-aligned: it is the field worth sorting by, and
      /// a column of numbers that do not line up is harder to compare than one that
      /// does.
      /// </summary>
      private void BuildColumns()
      {
         list_.Columns.Add(new DataGridTextColumn
         {
            Header = L("Held"),
            Binding = new System.Windows.Data.Binding(nameof(HeldRow.CreatedTime)),
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
            Header = L("Sender"),
            Binding = new System.Windows.Data.Binding(nameof(HeldRow.Sender)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
         });

         list_.Columns.Add(new DataGridTextColumn
         {
            Header = L("Recipients"),
            Binding = new System.Windows.Data.Binding(nameof(HeldRow.Recipients)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star)
         });

         var score = new DataGridTextColumn
         {
            Header = L("Score"),
            Binding = new System.Windows.Data.Binding(nameof(HeldRow.Score)),
            Width = DataGridLength.Auto
         };
         GridStyles.Number(score);
         list_.Columns.Add(score);

         list_.Columns.Add(new DataGridTextColumn
         {
            Header = L("Subject"),
            Binding = new System.Windows.Data.Binding(nameof(HeldRow.Subject)),
            Width = new DataGridLength(2, DataGridLengthUnitType.Star)
         });

         // Why this message was held, which is the question the page exists to
         // answer and has no column of its own because the reasons are sentences.
         // BasedOn the application's implicit style, because assigning RowStyle
         // replaces the implicit one rather than adding to it.
         var rowStyle = new Style(typeof(DataGridRow));

         if (Application.Current?.TryFindResource(typeof(DataGridRow)) is Style appRowStyle)
            rowStyle.BasedOn = appRowStyle;

         rowStyle.Setters.Add(new Setter(ToolTipProperty,
            new System.Windows.Data.Binding(nameof(HeldRow.HeldBecause))));
         list_.RowStyle = rowStyle;
      }

      private int SelectedId()
      {
         if (list_.SelectedItem is HeldRow row)
            return row.Id;

         return 0;
      }

      private void ReleaseSelected()
      {
         int id = SelectedId();

         if (id == 0)
         {
            Status_(StatusLevel.Information, L("Select a message first."));
            return;
         }

         try
         {
            dynamic quarantine = OpenQuarantine();
            try
            {
               quarantine.ReleaseByDBID(id);
            }
            finally
            {
               ServerSession.Release(quarantine);
            }

            Reload();
            Status_(StatusLevel.Good, L("Released. It has been delivered to the recipients it was addressed to."));
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            Status_(StatusLevel.Critical, ServerSession.DescribeComError(ex));
         }
      }

      private void DeleteSelected()
      {
         int id = SelectedId();

         if (id == 0)
         {
            Status_(StatusLevel.Information, L("Select a message first."));
            return;
         }

         // Confirmed, because this is the only copy. The sender was told the message
         // was delivered and will not send it again. YesNo like every other
         // destructive confirmation in the application (revoke key, delete folder,
         // empty mailbox), and No takes Enter: for an action with no undo and no
         // other copy, the keyboard default declines.
         if (MessageBox.Show(
               L("Delete this message permanently?\n\nThe sender was told it was accepted, so nothing will retry and there is no other copy."),
               L("Delete quarantined message"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
               MessageBoxResult.No)
             != MessageBoxResult.Yes)
         {
            return;
         }

         try
         {
            dynamic quarantine = OpenQuarantine();
            try
            {
               quarantine.DeleteByDBID(id);
            }
            finally
            {
               ServerSession.Release(quarantine);
            }

            Reload();
            Status_(StatusLevel.Good, "Deleted.");
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
            dynamic quarantine = OpenQuarantine();
            int removed;
            try
            {
               removed = (int)quarantine.DeleteExpired();
            }
            finally
            {
               ServerSession.Release(quarantine);
            }

            Reload();

            if (removed == 0)
               Status_(StatusLevel.Information, L("Nothing was old enough to remove. The window is QuarantineRetentionDays in hMailServer.ini, and 0 means never."));
            else
               Status_(StatusLevel.Good, F("{0} message(s) past the retention window were removed.", removed));
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
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

         var actions = new StackPanel { Orientation = Orientation.Horizontal };
         var release = MakeButton(L("_Release"), Wpf.Ui.Controls.ControlAppearance.Primary, (_, _) => ReleaseSelected());
         actions.Children.Add(release);
         var delete = MakeButton(L("_Delete"), Wpf.Ui.Controls.ControlAppearance.Danger, (_, _) => DeleteSelected());
         actions.Children.Add(delete);
         hMailServer.ControlPanel.Services.SelectionGate.Bind(list_, release, delete);
         actions.Children.Add(MakeButton(L("Remove _expired"), Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => SweepExpired()));
         actions.Children.Add(MakeButton(L("Re_fresh"), Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => Reload()));

         root.Children.Add(new PageHeader
         {
            Title = L("Quarantine"),
            Subtitle = L("Messages the server would otherwise have refused, held so somebody can look. The sender was told each of these was accepted, so nothing will arrive again on its own: releasing one delivers it, and deleting one is final."),
            Actions = actions
         });

         Grid.SetRow(status_, 1);
         root.Children.Add(status_);

         BuildColumns();
         System.Windows.Automation.AutomationProperties.SetName(list_, L("Quarantine"));

         var host = new Grid();
         host.Children.Add(list_);
         host.Children.Add(empty_);
         var card = new Card { Padding = new Thickness(8), Content = host };
         Grid.SetRow(card, 2);
         root.Children.Add(card);

         Content = root;
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
