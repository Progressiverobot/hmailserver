using System;
using Wpf.Ui;
using Wpf.Ui.Controls;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Thin wrapper over the WPF-UI snackbar so any view can show a transient
   /// "saved" confirmation without holding a reference to the main window.
   /// </summary>
   public static class Toast
   {
      private static readonly SnackbarService Service = new();
      private static bool ready_;

      /// <summary>Wires the singleton to the main window's snackbar presenter.</summary>
      public static void Init(SnackbarPresenter presenter)
      {
         if (presenter == null)
            return;
         Service.SetSnackbarPresenter(presenter);
         ready_ = true;
      }

      public static void Success(string message, string title = "Saved")
         => Show(title, message, ControlAppearance.Success);

      public static void Info(string message, string title = "")
         => Show(title, message, ControlAppearance.Info);

      private static void Show(string title, string message, ControlAppearance appearance)
      {
         if (!ready_)
            return;
         try
         {
            Service.Show(title, message, appearance, null, TimeSpan.FromSeconds(3));
         }
         catch (Exception)
         {
            // Never let a UI confirmation crash a successful save.
         }
      }
   }
}
