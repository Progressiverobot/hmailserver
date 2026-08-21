using System.Windows;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The base class for every Control Panel dialog.
   ///
   /// Every dialog used to derive from plain System.Windows.Window, which
   /// meant a stock Win32 title bar following the OS app mode rather than the
   /// in-app theme - so on a light-mode OS, every Add/Edit dialog opened with
   /// a white title bar bolted onto dark content. Deriving from WPF-UI's
   /// FluentWindow gives the dialogs the same themed chrome as the shell.
   ///
   /// This class exists so the chrome, the application face and the theme
   /// background are decided once rather than re-decided (or forgotten) per
   /// dialog - the same reasoning as the Dialogs message-box class. Layout,
   /// content and footers are untouched: those were already consistent.
   /// </summary>
   public class FluentDialogWindow : Wpf.Ui.Controls.FluentWindow
   {
      public FluentDialogWindow()
      {
         FontFamily = new System.Windows.Media.FontFamily(Services.Typography.UiFontFamily);
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
         ShowInTaskbar = false;
      }
   }
}
