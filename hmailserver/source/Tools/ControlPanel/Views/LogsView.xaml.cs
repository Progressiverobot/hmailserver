using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   public partial class LogsView : UserControl, IPageLifecycle
   {
      private const int MaxLines = 2000;

      public class LogLine
      {
         public string Text { get; set; }
         public Brush Brush { get; set; }
      }

      private readonly ObservableCollection<LogLine> lines_ = new();
      private readonly DispatcherTimer timer_;
      private string logFile_;
      private long position_;
      private bool paused_;

      private static readonly Brush DefaultBrush = new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xD9));
      private static readonly Brush SmtpBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
      private static readonly Brush ImapBrush = new SolidColorBrush(Color.FromRgb(0xA3, 0x71, 0xF7));
      private static readonly Brush Pop3Brush = new SolidColorBrush(Color.FromRgb(0xD2, 0x99, 0x22));
      private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
      private static readonly Brush AppBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x81, 0xF7));

      public LogsView()
      {
         InitializeComponent();
         LogList.ItemsSource = lines_;

         timer_ = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
         timer_.Tick += (s, e) => Poll();
      }

      public void OnEnter()
      {
         var store = new IniFeatureStore();
         string folder = store.GetLogFolder();

         if (folder == null || !Directory.Exists(folder))
         {
            SubtitleText.Text = "Log folder not found on this machine (live logs need a local server).";
            return;
         }

         var newest = new DirectoryInfo(folder).GetFiles("hmailserver_*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

         if (newest == null)
         {
            SubtitleText.Text = "No log files in " + folder + " yet. Enable logging in the server settings.";
            return;
         }

         logFile_ = newest.FullName;
         position_ = Math.Max(0, newest.Length - 64 * 1024); // start with the last 64 KB
         SubtitleText.Text = "Streaming " + newest.Name;

         Poll();
         timer_.Start();
      }

      public void OnLeave() => timer_.Stop();

      private void Poll()
      {
         if (paused_ || logFile_ == null)
            return;

         try
         {
            using var stream = new FileStream(logFile_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < position_)
               position_ = 0; // rotated

            if (stream.Length == position_)
               return;

            stream.Seek(position_, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string chunk = reader.ReadToEnd();
            position_ = stream.Length;

            bool added = false;
            foreach (string raw in chunk.Split('\n'))
            {
               string line = raw.TrimEnd('\r');
               if (line.Length == 0)
                  continue;

               lines_.Add(new LogLine { Text = line, Brush = Classify(line) });
               added = true;
            }

            while (lines_.Count > MaxLines)
               lines_.RemoveAt(0);

            if (added && LogList.Items.Count > 0)
               LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
         }
         catch (IOException)
         {
            // File momentarily locked; retry next tick.
         }
      }

      private static Brush Classify(string line)
      {
         if (line.Contains("\"ERROR\"") || line.Contains("Severity: 1") || line.Contains("Severity: 2"))
            return ErrorBrush;
         if (line.StartsWith("\"SMTPD\"") || line.Contains("\"SMTPD\""))
            return SmtpBrush;
         if (line.Contains("\"IMAPD\""))
            return ImapBrush;
         if (line.Contains("\"POP3D\""))
            return Pop3Brush;
         if (line.Contains("\"APPLICATION\""))
            return AppBrush;
         return DefaultBrush;
      }

      private void Pause_Click(object sender, RoutedEventArgs e)
      {
         paused_ = !paused_;
         PauseButton.Content = paused_ ? "Resume" : "Pause";
      }

      private void Clear_Click(object sender, RoutedEventArgs e) => lines_.Clear();
   }
}
