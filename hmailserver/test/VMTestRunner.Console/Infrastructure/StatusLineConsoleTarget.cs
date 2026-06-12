using System;
using System.Threading;
using NLog;
using NLog.Targets;

namespace VMTestRunner.Console
{
   [Target("StatusLineConsole")]
   public sealed class StatusLineConsoleTarget : TargetWithLayout
   {
      private static string _lastStatus;
      private static DateTime _statusStarted;
      private static readonly object _lock = new object();
      private static readonly Timer _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);

      protected override void Write(LogEventInfo logEvent)
      {
         lock (_lock)
         {
            if (logEvent.Level == LogLevel.Debug)
            {
               _lastStatus = logEvent.FormattedMessage;
               _statusStarted = DateTime.UtcNow;
               DrawStatus();
               _timer.Change(1000, 1000);
            }
            else
            {
               _timer.Change(Timeout.Infinite, Timeout.Infinite);
               _lastStatus = null;
               ClearStatusLine();
               var isSuccess = logEvent.Properties.ContainsKey("success");
               if (isSuccess) System.Console.ForegroundColor = ConsoleColor.Green;
               System.Console.WriteLine(RenderLogEvent(Layout, logEvent));
               if (isSuccess) System.Console.ResetColor();
            }
         }
      }

      private static void Tick()
      {
         lock (_lock)
         {
            if (_lastStatus != null)
               DrawStatus();
         }
      }

      private static void DrawStatus()
      {
         var elapsed = DateTime.UtcNow - _statusStarted;
         var timestamp = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
         WriteConsoleLine($"   {_lastStatus} [{timestamp}]");
      }

      private static void ClearStatusLine()
      {
         WriteConsoleLine(string.Empty);
      }

      private static void WriteConsoleLine(string text)
      {
         try
         {
            var width = System.Console.WindowWidth - 1;
            var display = text.Length > width ? text.Substring(0, width) : text;
            System.Console.Write("\r" + display.PadRight(width) + "\r");
         }
         catch (Exception)
         {
            // Console may be redirected; silently ignore.
         }
      }
   }
}
