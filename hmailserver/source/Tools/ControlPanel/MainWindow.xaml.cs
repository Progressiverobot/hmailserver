using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views;

namespace hMailServer.ControlPanel
{
   public partial class MainWindow
   {
      private readonly Dictionary<string, UserControl> pageCache_ = new();
      private bool connected_;

      public MainWindow()
      {
         InitializeComponent();

         SetNavEnabled(false);
         ContentHost.Content = new ConnectView(OnConnected);

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

      private void SetNavEnabled(bool enabled)
      {
         foreach (var item in new Control[] { NavDashboard, NavDomains, NavQueue, NavLogs,
                  NavProtocols, NavDelivery, NavAntiSpam, NavAntiVirus, NavTls, NavLogging,
                  NavSecurity, NavAutomation, NavIntegration })
            item.IsEnabled = enabled;
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
