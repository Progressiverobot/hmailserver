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
      private readonly Dictionary<string, Func<UserControl>> pageFactories_ = new();
      private bool connected_;

      public MainWindow()
      {
         InitializeComponent();

         ApplySavedTheme();
         RegisterPages();
         BuildNavTree();

         NavTree.IsEnabled = false;
         ContentHost.Content = new ConnectView(OnConnected);

         // Ctrl+K command palette.
         PreviewKeyDown += (s, e) =>
         {
            if (connected_ && e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
            {
               ShowPalette();
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

      private void RegisterPages()
      {
         pageFactories_["welcome"] = () => new WelcomeView();
         pageFactories_["dashboard"] = () => new DashboardView();
         pageFactories_["queue"] = () => new QueueView();
         pageFactories_["logs"] = () => new LogsView();
         pageFactories_["domains"] = () => new DomainsView();
         pageFactories_["rules"] = () => new RulesView();
         pageFactories_["protocols"] = () => new ServerSettingsView(ServerSettingsView.Section.Protocols);
         pageFactories_["delivery"] = () => new ServerSettingsView(ServerSettingsView.Section.Delivery);
         pageFactories_["routes"] = () => new RoutesView();
         pageFactories_["publicfolders"] = () => new PublicFoldersView();
         pageFactories_["antispam"] = () => new ServerSettingsView(ServerSettingsView.Section.AntiSpam);
         pageFactories_["surbl"] = () => CollectionSpecs.SurblServers();
         pageFactories_["dnsbl"] = () => CollectionSpecs.DnsBlackLists();
         pageFactories_["spamwhitelist"] = () => CollectionSpecs.SpamWhiteList();
         pageFactories_["greylistwhitelist"] = () => CollectionSpecs.GreyListWhiteList();
         pageFactories_["antivirus"] = () => new ServerSettingsView(ServerSettingsView.Section.AntiVirus);
         pageFactories_["blockedattachments"] = () => CollectionSpecs.BlockedAttachments();
         pageFactories_["logging"] = () => new ServerSettingsView(ServerSettingsView.Section.Logging);
         pageFactories_["tls"] = () => new ServerSettingsView(ServerSettingsView.Section.Tls);
         pageFactories_["performance"] = () => new ServerSettingsView(ServerSettingsView.Section.Performance);
         pageFactories_["advanced"] = () => new ServerSettingsView(ServerSettingsView.Section.Advanced);
         pageFactories_["groups"] = () => CollectionSpecs.Groups();
         pageFactories_["servermessages"] = () => CollectionSpecs.ServerMessages();
         pageFactories_["scripts"] = () => new ScriptsView();
         pageFactories_["certs"] = () => new SslCertificatesView();
         pageFactories_["ports"] = () => new TcpIpPortsView();
         pageFactories_["ipranges"] = () => new IPRangesView();
         pageFactories_["relays"] = () => new IncomingRelaysView();
         pageFactories_["security"] = () => new FeatureSettingsView(FeatureSettingsView.Section.Security);
         pageFactories_["acme"] = () => new FeatureSettingsView(FeatureSettingsView.Section.Automation);
         pageFactories_["api"] = () => new FeatureSettingsView(FeatureSettingsView.Section.Integration);
         pageFactories_["backup"] = () => new BackupView();
         pageFactories_["mxquery"] = () => new MxQueryView();
         pageFactories_["sendout"] = () => new SendoutView();
         pageFactories_["diagnostics"] = () => new DiagnosticsView();
         pageFactories_["about"] = () => new AboutView();
      }

      private TreeViewItem Item(string title, string key) => new()
      {
         Header = title,
         Tag = key
      };

      private TreeViewItem Group(string title, params TreeViewItem[] children)
      {
         var group = new TreeViewItem
         {
            Header = title,
            IsExpanded = true,
            FontWeight = FontWeights.SemiBold
         };
         foreach (TreeViewItem child in children)
         {
            child.FontWeight = FontWeights.Normal;
            group.Items.Add(child);
         }
         return group;
      }

      /// <summary>Mirrors the classic Administrator tree layout.</summary>
      private void BuildNavTree()
      {
         NavTree.Items.Clear();

         NavTree.Items.Add(Item("Welcome", "welcome"));
         NavTree.Items.Add(Item("Dashboard", "dashboard"));
         NavTree.Items.Add(Group("Status",
            Item("Delivery queue", "queue"),
            Item("Live logs", "logs")));
         NavTree.Items.Add(Item("Domains", "domains"));
         NavTree.Items.Add(Item("Rules", "rules"));

         NavTree.Items.Add(Group("Settings",
            Item("Protocols", "protocols"),
            Item("Delivery of e-mail", "delivery"),
            Item("Routes", "routes"),
            Item("Public folders", "publicfolders"),
            Group("Anti-spam",
               Item("Anti-spam settings", "antispam"),
               Item("SURBL servers", "surbl"),
               Item("DNS blacklists", "dnsbl"),
               Item("White list", "spamwhitelist"),
               Item("Greylisting white list", "greylistwhitelist")),
            Group("Anti-virus",
               Item("Anti-virus settings", "antivirus"),
               Item("Blocked attachments", "blockedattachments")),
            Item("Logging", "logging"),
            Group("Advanced",
               Item("Auto-ban & SSL/TLS", "tls"),
               Item("Performance", "performance"),
               Item("Advanced & scripting", "advanced"),
               Item("Event scripts", "scripts"),
               Item("Server messages", "servermessages"),
               Item("Groups", "groups"),
               Item("SSL certificates", "certs"),
               Item("TCP/IP ports", "ports"),
               Item("IP ranges", "ipranges"),
               Item("Incoming relays", "relays"),
               Item("Transport security", "security"),
               Item("Certificates (ACME)", "acme"),
               Item("API & monitoring", "api"))));

         NavTree.Items.Add(Group("Utilities",
            Item("Backup & restore", "backup"),
            Item("MX query", "mxquery"),
            Item("Server sendout", "sendout"),
            Item("Diagnostics", "diagnostics")));

         NavTree.Items.Add(Item("About", "about"));
      }

      private IEnumerable<TreeViewItem> AllLeaves(ItemCollection items)
      {
         foreach (TreeViewItem item in items)
         {
            if (item.Tag is string)
               yield return item;
            foreach (TreeViewItem child in AllLeaves(item.Items))
               yield return child;
         }
      }

      private void ShowPalette()
      {
         var entries = AllLeaves(NavTree.Items)
            .ToDictionary(item => item.Header.ToString(), item => item);

         var palette = new NavigationPalette(this, entries.Keys);
         palette.ShowDialog();

         if (palette.Selected != null && entries.TryGetValue(palette.Selected, out TreeViewItem target))
         {
            var parent = target.Parent as TreeViewItem;
            while (parent != null)
            {
               parent.IsExpanded = true;
               parent = parent.Parent as TreeViewItem;
            }
            target.IsSelected = true;
            target.BringIntoView();
         }
      }

      private void OnConnected()
      {
         connected_ = true;
         NavTree.IsEnabled = true;

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

         ((TreeViewItem) NavTree.Items[1]).IsSelected = true; // Dashboard
      }

      private void NavTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
      {
         if (!connected_ || ContentHost == null)
            return;

         if (e.NewValue is not TreeViewItem item || item.Tag is not string key)
            return;

         if (!pageCache_.TryGetValue(key, out UserControl page))
         {
            if (!pageFactories_.TryGetValue(key, out Func<UserControl> factory))
               return;
            page = factory();
            pageCache_[key] = page;
         }

         if (ContentHost.Content is IPageLifecycle oldPage)
            oldPage.OnLeave();

         ContentHost.Content = page;

         if (page is IPageLifecycle newPage)
            newPage.OnEnter();
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
   }

   /// <summary>Optional page activation hooks (start/stop timers etc.).</summary>
   public interface IPageLifecycle
   {
      void OnEnter();
      void OnLeave();
   }
}
