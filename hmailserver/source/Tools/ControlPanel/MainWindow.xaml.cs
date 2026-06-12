using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views;

namespace hMailServer.ControlPanel
{
   public partial class MainWindow
   {
      private const string RegistryPath = @"Software\hMailServer\ControlPanel";

      private readonly Dictionary<string, UserControl> pageCache_ = new();
      private bool connected_;

      public MainWindow()
      {
         InitializeComponent();

         ApplySavedTheme();

         SetNavEnabled(false);
         ContentHost.Content = new ConnectView(OnConnected);

         // Ctrl+K command palette.
         PreviewKeyDown += (s, e) =>
         {
            if (connected_ && e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
               new CommandPalette(this, AllNavItems()).ShowDialog();
               e.Handled = true;
            }
         };

         // Optional auto-connect: hMailCP.exe /connect <host> <user> <password>
         string[] args = Environment.GetCommandLineArgs();
         int flag = Array.IndexOf(args, "/connect");
         if (flag >= 0 && args.Length >= flag + 4)
         {
            Loaded += (s, e) =>
            {
               var session = new ServerSession();
               if (session.Connect(args[flag + 1], args[flag + 2], args[flag + 3], out _))
               {
                  ServerSession.SetCurrent(session);
                  OnConnected();
               }
            };
         }
      }

      private IEnumerable<RadioButton> AllNavItems() => new[]
      {
         NavDashboard, NavDomains, NavQueue, NavLogs,
         NavProtocols, NavDelivery, NavAntiSpam, NavAntiVirus, NavTls, NavCerts, NavIpRanges, NavLogging,
         NavSecurity, NavAutomation, NavIntegration
      };

      private void SetNavEnabled(bool enabled)
      {
         foreach (var item in AllNavItems())
            item.IsEnabled = enabled;
      }

      private void ApplySavedTheme()
      {
         string saved = null;
         try
         {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            saved = key?.GetValue("Theme") as string;
         }
         catch (Exception)
         {
         }

         if (saved == "Light")
            ApplicationThemeManager.Apply(ApplicationTheme.Light);
      }

      private void Theme_Click(object sender, RoutedEventArgs e)
      {
         bool toLight = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
         ApplicationThemeManager.Apply(toLight ? ApplicationTheme.Light : ApplicationTheme.Dark);

         try
         {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue("Theme", toLight ? "Light" : "Dark");
         }
         catch (Exception)
         {
         }
      }

      private void OnConnected()
      {
         connected_ = true;
         SetNavEnabled(true);

         ConnBadge.Visibility = Visibility.Visible;
         ConnText.Text = ServerSession.Current.UserName + " @ " + ServerSession.Current.Host;

         try
         {
            VersionText.Text = "hMailServer " + (string) ServerSession.Current.Application.Version;
         }
         catch (Exception)
         {
            VersionText.Text = "";
         }

         NavDashboard.IsChecked = true;
      }

      private void Nav_Checked(object sender, RoutedEventArgs e)
      {
         if (!connected_ || ContentHost == null)
            return;

         string name = ((Control) sender).Name;

         if (!pageCache_.TryGetValue(name, out UserControl page))
         {
            page = name switch
            {
               nameof(NavDashboard) => new DashboardView(),
               nameof(NavDomains) => new DomainsView(),
               nameof(NavQueue) => new QueueView(),
               nameof(NavLogs) => new LogsView(),
               nameof(NavProtocols) => new ServerSettingsView(ServerSettingsView.Section.Protocols),
               nameof(NavDelivery) => new ServerSettingsView(ServerSettingsView.Section.Delivery),
               nameof(NavAntiSpam) => new ServerSettingsView(ServerSettingsView.Section.AntiSpam),
               nameof(NavAntiVirus) => new ServerSettingsView(ServerSettingsView.Section.AntiVirus),
               nameof(NavTls) => new ServerSettingsView(ServerSettingsView.Section.Tls),
               nameof(NavCerts) => new SslCertificatesView(),
               nameof(NavIpRanges) => new IPRangesView(),
               nameof(NavLogging) => new ServerSettingsView(ServerSettingsView.Section.Logging),
               nameof(NavSecurity) => new FeatureSettingsView(FeatureSettingsView.Section.Security),
               nameof(NavAutomation) => new FeatureSettingsView(FeatureSettingsView.Section.Automation),
               nameof(NavIntegration) => new FeatureSettingsView(FeatureSettingsView.Section.Integration),
               _ => null
            };
            if (page != null)
               pageCache_[name] = page;
         }

         if (ContentHost.Content is IPageLifecycle oldPage)
            oldPage.OnLeave();

         ContentHost.Content = page;

         if (page is IPageLifecycle newPage)
            newPage.OnEnter();
      }
   }

   /// <summary>Optional page activation hooks (start/stop timers etc.).</summary>
   public interface IPageLifecycle
   {
      void OnEnter();
      void OnLeave();
   }
}
