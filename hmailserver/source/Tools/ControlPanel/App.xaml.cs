using System.Windows;

namespace hMailServer.ControlPanel
{
   public partial class App : Application
   {
      protected override void OnStartup(StartupEventArgs e)
      {
         base.OnStartup(e);

         DispatcherUnhandledException += (s, args) =>
         {
            MessageBox.Show(args.Exception.Message, "hMailServer Control Panel",
               MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
         };
      }
   }
}
