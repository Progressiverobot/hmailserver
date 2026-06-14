using System.Windows;
using System.Windows.Controls;

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
   }
}
