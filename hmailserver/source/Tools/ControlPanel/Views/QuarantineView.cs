using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

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
   public class QuarantineView : UserControl
   {
      private readonly ListBox list_ = new()
      {
         FontSize = Typography.Body,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent,
         MinHeight = 320
      };

      private readonly TextBlock status_ = new()
      {
         FontSize = Typography.Caption,
         Margin = new Thickness(0, 10, 0, 0),
         TextWrapping = TextWrapping.Wrap
      };

      public QuarantineView()
      {
         Build();
         Reload();
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

      private void Reload()
      {
         list_.Items.Clear();

         try
         {
            dynamic quarantine = OpenQuarantine();
            try
            {
               quarantine.Refresh();

               int total = (int) quarantine.Count;
               int listed = 0;

               for (int i = 0; ; i++)
               {
                  dynamic item;

                  try
                  {
                     item = quarantine[i];
                  }
                  catch
                  {
                     // The collection loads a page rather than the whole table, and
                     // indexing past it is how that page ends.
                     break;
                  }

                  try
                  {
                     list_.Items.Add(new ListBoxItem
                     {
                        Content = string.Format("{0}   {1}  ->  {2}   [score {3}]   {4}",
                           (string) item.CreatedTime,
                           (string) item.Sender,
                           (string) item.Recipients,
                           (int) item.Score,
                           string.IsNullOrEmpty((string) item.Subject) ? "(no subject)" : (string) item.Subject),
                        Tag = (int) item.ID,
                        ToolTip = "Held because: " + (string) item.Reason
                     });

                     listed++;
                  }
                  finally
                  {
                     ServerSession.Release(item);
                  }
               }

               if (total == 0)
               {
                  status_.Text = "Nothing is held. Quarantining is off unless QuarantineEnabled is set in "
                               + "hMailServer.ini - until then, spam over the delete threshold is refused during "
                               + "the SMTP conversation rather than stored.";
               }
               else if (listed < total)
               {
                  status_.Text = string.Format(
                     "{0} held, showing the {1} most recent. The rest are still there - work through these, or "
                     + "let the retention sweep age them out.", total, listed);
               }
               else
               {
                  status_.Text = total == 1 ? "1 message held." : total + " messages held.";
               }
            }
            finally
            {
               ServerSession.Release(quarantine);
            }
         }
         catch (Exception ex)
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private int SelectedId()
      {
         if (list_.SelectedItem is ListBoxItem item && item.Tag is int id)
            return id;

         return 0;
      }

      private void ReleaseSelected()
      {
         int id = SelectedId();

         if (id == 0)
         {
            status_.Text = "Select a message first.";
            return;
         }

         try
         {
            dynamic quarantine = OpenQuarantine();
            try
            {
               quarantine.ReleaseByDBID(id);
               status_.Text = "Released. It has been delivered to the recipients it was addressed to.";
            }
            finally
            {
               ServerSession.Release(quarantine);
            }

            Reload();
         }
         catch (Exception ex)
         {
            status_.Text = ServerSession.DescribeComError(ex);
         }
      }

      private void DeleteSelected()
      {
         int id = SelectedId();

         if (id == 0)
         {
            status_.Text = "Select a message first.";
            return;
         }

         // Confirmed, because this is the only copy. The sender was told the message
         // was delivered and will not send it again.
         if (MessageBox.Show(
               "Delete this message permanently?\n\nThe sender was told it was accepted, so nothing will "
               + "retry and there is no other copy.",
               "Delete quarantined message", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
             != MessageBoxResult.OK)
         {
            return;
         }

         try
         {
            dynamic quarantine = OpenQuarantine();
            try
            {
               quarantine.DeleteByDBID(id);
               status_.Text = "Deleted.";
            }
            finally
            {
               ServerSession.Release(quarantine);
            }

            Reload();
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
            dynamic quarantine = OpenQuarantine();
            try
            {
               int removed = (int) quarantine.DeleteExpired();

               status_.Text = removed == 0
                  ? "Nothing was old enough to remove. The window is QuarantineRetentionDays in hMailServer.ini, "
                    + "and 0 means never."
                  : removed + " message(s) past the retention window were removed.";
            }
            finally
            {
               ServerSession.Release(quarantine);
            }

            Reload();
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

         var title = new TextBlock { Text = "Quarantine" };
         title.SetResourceReference(FrameworkElement.StyleProperty, "PageTitle");
         root.Children.Add(title);

         var hint = new TextBlock
         {
            Text = "Messages the server would otherwise have refused, held so somebody can look. "
                 + "The sender was told each of these was accepted, so nothing will arrive again on its own: "
                 + "releasing one delivers it, and deleting one is final.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
         };
         hint.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         root.Children.Add(hint);

         var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
         toolbar.Children.Add(MakeButton("Release", Wpf.Ui.Controls.ControlAppearance.Primary, (_, _) => ReleaseSelected()));
         toolbar.Children.Add(MakeButton("Delete", Wpf.Ui.Controls.ControlAppearance.Danger, (_, _) => DeleteSelected()));
         toolbar.Children.Add(MakeButton("Remove expired", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => SweepExpired()));
         toolbar.Children.Add(MakeButton("Refresh", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => Reload()));
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
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 120
         };
         button.Click += onClick;
         return button;
      }
   }
}
