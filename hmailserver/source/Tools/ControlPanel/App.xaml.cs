using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel
{
   public partial class App : Application
   {
      protected override void OnStartup(StartupEventArgs e)
      {
         base.OnStartup(e);

         DispatcherUnhandledException += OnDispatcherUnhandledException;
         AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

         // Register the semantic colour tokens before any window loads so that
         // every page resolves on-brand, theme-aware state colours.
         Services.ThemeTokens.Initialize();
      }

      private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
      {
         string logPath = LogException(args.Exception);

         string message =
            "An unexpected error occurred:\n\n" + args.Exception.Message +
            "\n\nThe full details were written to:\n" + logPath +
            "\n\nRestart the Control Panel now? Choose No to try to keep working.";

         // Keep the app alive so the user decides; never silently swallow.
         args.Handled = true;

         if (MessageBox.Show(message, "hMailServer Control Panel",
                MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.Yes)
         {
            Restart();
         }
      }

      private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
      {
         // Non-UI-thread failures can't be recovered from; at least record them.
         if (args.ExceptionObject is Exception ex)
            LogException(ex);
      }

      /// <summary>
      ///    Writes an exception to the Control Panel's own log and returns the path.
      ///
      ///    Internal rather than private because ThemeTokens needs it: it invokes theme
      ///    subscribers one at a time so that a chart card which throws cannot cost the
      ///    others their restyle, and a swallowed exception with nowhere to go is how a
      ///    card silently keeps the wrong palette. This is the only log the Control
      ///    Panel has, so widening it by one assembly is better than a second one.
      /// </summary>
      internal static string LogException(Exception ex)
      {
         try
         {
            string dir = Path.Combine(
               Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
               "hMailServer", "ControlPanel");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "control-panel-errors.log");
            File.AppendAllText(path,
               string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}{2}{2}", DateTime.Now, ex, Environment.NewLine));
            return path;
         }
         catch
         {
            return "(the log file could not be written)";
         }
      }

      private void Restart()
      {
         try
         {
            string exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
               // Preserve the original command-line arguments (e.g. /connect ...).
               var psi = new ProcessStartInfo(exe);
               string[] args = Environment.GetCommandLineArgs();
               for (int i = 1; i < args.Length; i++)
                  psi.ArgumentList.Add(args[i]);
               Process.Start(psi);
            }
         }
         catch
         {
         }

         Shutdown();
      }
   }
}
