// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Views.Scaffold;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Shared empty/error placeholder behaviour for the Control Panel list and grid
   /// pages. A single <see cref="TextBlock"/> overlaying the list is shown with an
   /// error message when a load failed, an "empty" message when the load succeeded
   /// but returned no rows, and hidden when there is data to display.
   /// </summary>
   public static class StatusText
   {
      public static void Show(TextBlock status, int rowCount, string error,
                              string emptyText, string errorPrefix = "")
      {
         if (status == null)
            return;

         if (!string.IsNullOrEmpty(error))
         {
            status.Text = errorPrefix + error;
            status.Visibility = Visibility.Visible;
         }
         else if (rowCount == 0)
         {
            status.Text = emptyText;
            status.Visibility = Visibility.Visible;
         }
         else
         {
            status.Visibility = Visibility.Collapsed;
         }
      }

      /// <summary>
      /// The same, for a page on the scaffold: the empty message goes on an
      /// <see cref="EmptyState"/> in the grid's place, and a load failure on an
      /// <see cref="InlineNotice"/> at the critical level above the grid - or,
      /// for a page with no notice of its own, on the empty state, so the failure
      /// is never silent. Both are hidden while there are rows.
      /// </summary>
      public static void Show(EmptyState empty, InlineNotice notice, int rowCount, string error,
                              string emptyText, string errorPrefix = "")
      {
         bool failed = !string.IsNullOrEmpty(error);

         if (notice != null)
         {
            notice.Level = StatusLevel.Critical;
            notice.Text = failed ? errorPrefix + error : null;
            notice.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;
         }

         if (empty == null)
            return;

         if (failed)
         {
            empty.Text = errorPrefix + error;
            empty.Visibility = notice == null ? Visibility.Visible : Visibility.Collapsed;
         }
         else if (rowCount == 0)
         {
            empty.Text = emptyText;
            empty.Visibility = Visibility.Visible;
         }
         else
         {
            empty.Visibility = Visibility.Collapsed;
         }
      }
   }
}
