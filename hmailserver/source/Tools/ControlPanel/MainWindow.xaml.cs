// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

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
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel
{
   public partial class MainWindow
   {
      private const string RegistryPath = @"Software\hMailServer\ControlPanel";

      /// <summary>
      /// Where the palette's recently-visited and most-used history is kept.
      /// Beside the window bounds, under the same key, because it is the same
      /// kind of thing: a per-user convenience that is worth nothing on another
      /// machine and must never be required for the application to work.
      /// </summary>
      private const string PaletteUsageValue = "PaletteUsage";

      private readonly Dictionary<string, UserControl> pageCache_ = new();
      private readonly Dictionary<string, Func<UserControl>> pageFactories_ = new();
      private PaletteUsage paletteUsage_ = new();
      private string currentPage_;
      private bool arrivedFromSearch_;
      private bool connected_;

      // Set while the application navigates on the user's behalf rather than at their
      // request, so that "Most used" counts choices and not startup.
      private bool automaticNavigation_;

      public MainWindow()
      {
         InitializeComponent();

         // The default text colour for the whole window, delivered by property
         // inheritance rather than by an implicit TextBlock style. App.xaml
         // carried such a style as a "global contrast guarantee" until it was
         // found to be the cause of the one look complaint the forum ever
         // recorded: an app-level implicit style also reaches the TextBlocks
         // that ContentPresenter generates inside control templates, and a
         // style setter outranks an inherited value - so it overwrote WPF-UI's
         // on-accent button text with the ordinary body colour, which in the
         // light theme is near-black on accent blue. Inheritance gives bare
         // TextBlocks the same default while letting templates and Appearance
         // setters override it, which is the entire difference.
         SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

         Services.Toast.Init(RootSnackbar);
         ApplySavedTheme();
         RestoreWindowBounds();
         LoadPaletteUsage();
         RegisterPages();
         BuildNavTree();

         NavTree.IsEnabled = false;
         SearchButton.IsEnabled = false;
         ContentHost.Content = new ConnectView(OnConnected);

         Closing += (s, e) => SaveWindowBounds();
         Closing += (s, e) => SavePaletteUsage();

         ServerSession.Reconnected += OnSessionReconnected;
         Closing += (s, e) => ServerSession.Reconnected -= OnSessionReconnected;

         // Covers every way the theme can change after ApplySavedTheme's own
         // explicit call: the toggle button, and the OS switching while the
         // application is still following the system theme.
         ApplicationThemeManager.Changed += OnThemeChanged;
         Closing += (s, e) => ApplicationThemeManager.Changed -= OnThemeChanged;

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
         pageFactories_["status"] = () => new StatusView();
         pageFactories_["queue"] = () => new QueueView();
         pageFactories_["stalledmail"] = () => new StalledMailView();
         pageFactories_["messagetrace"] = () => new MessageTraceView();
         pageFactories_["logs"] = () => new LogsView();
         pageFactories_["domains"] = () => new DomainsView();
         pageFactories_["rules"] = () => new RulesView();
         pageFactories_["protocols"] = () => new ServerSettingsView(ServerSettingsView.Section.Protocols);
         pageFactories_["delivery"] = () => new ServerSettingsView(ServerSettingsView.Section.Delivery);
         pageFactories_["routes"] = () => new RoutesView();
         pageFactories_["publicfolders"] = () => new PublicFoldersView();
         pageFactories_["spamoverview"] = () => new SpamOverviewView();
         pageFactories_["quarantine"] = () => new QuarantineView();
         pageFactories_["antispam"] = () => new ServerSettingsView(ServerSettingsView.Section.AntiSpam);
         pageFactories_["surbl"] = () => CollectionSpecs.SurblServers();
         pageFactories_["dnsbl"] = () => CollectionSpecs.DnsBlackLists();
         pageFactories_["spamwhitelist"] = () => CollectionSpecs.SpamWhiteList();
         pageFactories_["blockedsenders"] = () => CollectionSpecs.BlockedSenders();
         pageFactories_["greylistwhitelist"] = () => CollectionSpecs.GreyListWhiteList();
         pageFactories_["virusoverview"] = () => new VirusOverviewView();
         pageFactories_["antivirus"] = () => new ServerSettingsView(ServerSettingsView.Section.AntiVirus);
         pageFactories_["blockedattachments"] = () => CollectionSpecs.BlockedAttachments();
         pageFactories_["logging"] = () => new ServerSettingsView(ServerSettingsView.Section.Logging);
         pageFactories_["tlsoverview"] = () => new TlsOverviewView();
         pageFactories_["tls"] = () => new ServerSettingsView(ServerSettingsView.Section.Tls);
         pageFactories_["autoban"] = () => new ServerSettingsView(ServerSettingsView.Section.AutoBan);
         pageFactories_["performance"] = () => new ServerSettingsView(ServerSettingsView.Section.Performance);
         pageFactories_["advanced"] = () => new ServerSettingsView(ServerSettingsView.Section.Advanced);
         pageFactories_["adminaccess"] = () => new ServerSettingsView(ServerSettingsView.Section.AdminAccess);
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
         pageFactories_["apikeys"] = () => new ApiKeysView();
         pageFactories_["hardening"] = () => new FeatureSettingsView(FeatureSettingsView.Section.Hardening);
         pageFactories_["authentication"] = () => new FeatureSettingsView(FeatureSettingsView.Section.Authentication);
         pageFactories_["dns"] = () => new FeatureSettingsView(FeatureSettingsView.Section.Dns);
         pageFactories_["webservices"] = () => new FeatureSettingsView(FeatureSettingsView.Section.WebServices);
         pageFactories_["backup"] = () => new BackupView();
         pageFactories_["mxquery"] = () => new MxQueryView();
         pageFactories_["sendout"] = () => new SendoutView();
         pageFactories_["diagnostics"] = () => new DiagnosticsView();
         pageFactories_["ldap"] = () => new LdapSettingsView();
         pageFactories_["directorysync"] = () => new DirectorySyncView();
         pageFactories_["externalsetup"] = () => new ExternalSetupView();
         pageFactories_["dnsrecords"] = () => new DnsRecordsView();
         pageFactories_["about"] = () => new AboutView();
      }

      /// <summary>
      /// Builds the sidebar from <see cref="NavigationMap"/>.
      ///
      /// The tree used to be written out here as nested Item/Group calls, and its
      /// comment said as much: it reproduced the classic Administrator layout,
      /// organised around where the product keeps things rather than around what
      /// an administrator is trying to do, with thirty of the forty-two pages
      /// buried under a single catch-all group three levels deep. The structure
      /// now lives in a WPF-free data file for two reasons: it can be tested
      /// (every registered page must appear exactly once, no page title may
      /// change), and the palette can search the same structure the sidebar
      /// draws instead of reverse-engineering it from TreeViewItem headers.
      ///
      /// This method deliberately contains no page names. If a name appears here
      /// again, the tree and the palette have two sources of truth.
      /// </summary>
      private void BuildNavTree()
      {
         NavTree.Items.Clear();

         foreach (NavNode node in NavigationMap.Roots)
            NavTree.Items.Add(BuildNavItem(node));
      }

      /// <summary>
      /// A group header is a glyph and its title; a page header stays plain
      /// text. The glyph is resolved from the map's string name so that
      /// NavigationMap stays WPF-free, and a name that does not resolve
      /// renders no icon rather than failing - a missing glyph is a cosmetic
      /// bug, not a broken sidebar.
      /// </summary>
      private static object BuildNavHeader_(NavNode node)
      {
         if (string.IsNullOrEmpty(node.Icon))
            return node.Title;

         if (!System.Enum.TryParse(node.Icon, out Wpf.Ui.Controls.SymbolRegular symbol))
         {
            // Right for the user, wrong to be quiet about: Enum.TryParse failing
            // silently is how a mistyped name once shipped one group bare among
            // seven with glyphs, and no test can catch it because the tests are
            // deliberately WPF-free and never see this enum. Loud in DEBUG,
            // still just a missing icon in Release.
            System.Diagnostics.Debug.Fail(
               "Navigation icon \"" + node.Icon + "\" on \"" + node.Title +
               "\" is not a member of Wpf.Ui.Controls.SymbolRegular.");
            return node.Title;
         }

         // A Grid, not a horizontal StackPanel. A StackPanel measures its children
         // with infinite width along the stacking direction, so the label below
         // always reported its full desired width and was CLIPPED by the sidebar
         // rather than trimmed - "Monitoring & troubleshooting" ended mid-word with
         // no ellipsis to say so. A star column hands the label the width that is
         // actually left, which is what TextTrimming needs in order to do anything.
         var header = new Grid();
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         // No explicit Foreground: SymbolIcon's Foreground inherits from the
         // TreeViewItem exactly as the TextBlock's does, so the glyph dims at
         // rest and brightens on hover and selection together with its label.
         // It was pinned to the secondary brush here once, which left it the
         // only part of the row that never responded to state.
         var icon = new Wpf.Ui.Controls.SymbolIcon(symbol)
         {
            FontSize = 16,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
         };
         header.Children.Add(icon);

         var label = new TextBlock
         {
            Text = node.Title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
         };

         Grid.SetColumn(label, 1);
         header.Children.Add(label);

         return header;
      }

      private TreeViewItem BuildNavItem(NavNode node)
      {
         var item = new TreeViewItem { Header = BuildNavHeader_(node) };

         // The purpose statement as the tool tip and as the accessible help text.
         // A page whose title needs the manual has failed; this is the cheapest
         // possible fix, and it reaches a screen reader as well as a mouse.
         if (!string.IsNullOrEmpty(node.Purpose))
         {
            // The title leads, then the purpose. The title is in the tip because the
            // label can be ellipsized when the pane is narrow, and a tool tip that
            // explains a name the reader cannot finish reading is answering the
            // wrong question.
            item.ToolTip = node.Title + " - " + node.Purpose;
            System.Windows.Automation.AutomationProperties.SetHelpText(item, node.Purpose);
         }
         else
         {
            item.ToolTip = node.Title;
         }

         System.Windows.Automation.AutomationProperties.SetName(item, node.Title);

         if (node.IsPage)
         {
            item.Tag = node.Key;
            item.FontWeight = FontWeights.Normal;
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "nav-" + node.Key);
            return item;
         }

         // Groups keep the automation id they had, derived from the title, so
         // that anything driving this interface by id keeps working for the
         // groups that kept their names.
         //
         // The Tag contract for the whole tree: a page's Tag is its key (a
         // string), a group's Tag is its NavNode. AllLeaves, AllGroups and
         // NavTree_SelectedItemChanged all discriminate on "Tag is string", so
         // a group's Tag must never be one. The group's node is here so that
         // RevealGroup can find the group without inspecting its Header -
         // matching on "Header as string" broke the moment headers became
         // icon-plus-text panels.
         item.Tag = node;
         item.IsExpanded = true;
         item.FontWeight = FontWeights.SemiBold;
         System.Windows.Automation.AutomationProperties.SetAutomationId(item, "navgroup-" + NavigationMap.Slug(node.Title));

         foreach (NavNode child in node.Children)
            item.Items.Add(BuildNavItem(child));

         return item;
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

      /// <summary>Selects the navigation leaf with the given key, expanding its
      /// parent groups. Used by the Welcome page quick-action tiles, the palette
      /// and the breadcrumb's related-page links.</summary>
      public void NavigateTo(string key) => NavigateTo(key, false);

      /// <summary>
      /// <paramref name="fromSearch"/> marks the arrival as a palette jump, which
      /// the breadcrumb says out loud once. Landing on a page with no idea how
      /// you got there is the specific way a command palette stops teaching the
      /// structure and starts replacing it.
      /// </summary>
      private void NavigateTo(string key, bool fromSearch)
      {
         foreach (TreeViewItem leaf in AllLeaves(NavTree.Items)
            .Where(l => string.Equals(l.Tag as string, key, StringComparison.OrdinalIgnoreCase)))
         {
            arrivedFromSearch_ = fromSearch;

            for (var parent = leaf.Parent as TreeViewItem; parent != null; parent = parent.Parent as TreeViewItem)
               parent.IsExpanded = true;

            // Re-selecting the page that is already selected raises no
            // SelectedItemChanged, so the breadcrumb would keep saying whatever
            // it said before - including a stale "from search" marker.
            if (leaf.IsSelected)
               UpdateBreadcrumb(key);
            else
               leaf.IsSelected = true;

            leaf.BringIntoView();
            return;
         }
      }

      private void Search_Click(object sender, RoutedEventArgs e) => ShowPalette();

      private void ShowPalette()
      {
         if (!connected_)
            return;

         var palette = new NavigationPalette(this, paletteUsage_, currentPage_);
         palette.ShowDialog();

         if (palette.SelectedPage != null)
            NavigateTo(palette.SelectedPage, true);
      }

      /// <summary>
      /// Rebuilds the breadcrumb bar for the page just opened: an optional
      /// "found by search" marker, the trail down to the page, and links to the
      /// neighbouring subjects.
      /// </summary>
      private void UpdateBreadcrumb(string key)
      {
         if (BreadcrumbHost == null)
            return;

         BreadcrumbHost.Children.Clear();
         RelatedHost.Children.Clear();

         IReadOnlyList<NavNode> trail = NavigationMap.PathTo(key);
         if (trail.Count == 0)
         {
            // An unknown key means the page is reachable but not in the map,
            // which NavigationMapTests fails on. Hide the bar rather than draw an
            // empty one.
            BreadcrumbBar.Visibility = Visibility.Collapsed;
            return;
         }

         BreadcrumbBar.Visibility = Visibility.Visible;

         if (arrivedFromSearch_)
         {
            BreadcrumbHost.Children.Add(SearchMarker());
            arrivedFromSearch_ = false;
         }

         for (int index = 0; index < trail.Count; index++)
         {
            if (index > 0)
               BreadcrumbHost.Children.Add(BreadcrumbSeparator());

            NavNode node = trail[index];

            if (index == trail.Count - 1)
            {
               var here = new TextBlock
               {
                  Text = node.Title,
                  FontSize = Typography.Label,
                  FontWeight = FontWeights.SemiBold,
                  VerticalAlignment = VerticalAlignment.Center
               };
               here.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
               System.Windows.Automation.AutomationProperties.SetAutomationId(here, "breadcrumb-page");
               BreadcrumbHost.Children.Add(here);
            }
            else
            {
               BreadcrumbHost.Children.Add(GroupSegment(node));
            }
         }

         // Three is the most that fits beside a long page title on a narrow
         // window without the bar wrapping into two lines; the map may list more.
         foreach (string related in NavigationMap.Find(key).SeeAlso)
         {
            if (RelatedHost.Children.Count >= 3)
               break;

            NavNode target = NavigationMap.Find(related);
            if (target != null)
               RelatedHost.Children.Add(RelatedLink(target));
         }

         if (RelatedHost.Children.Count > 0)
         {
            var caption = new TextBlock
            {
               Text = "Related:",
               FontSize = Typography.Caption,
               Margin = new Thickness(0, 0, 2, 0),
               VerticalAlignment = VerticalAlignment.Center
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            RelatedHost.Children.Insert(0, caption);
         }
      }

      /// <summary>
      /// A group in the trail. A button rather than a label because a group has
      /// no page of its own: activating it reveals the group in the sidebar, so
      /// the user can see what else is in the same place. Keyboard reachable like
      /// any other button, which a plain text breadcrumb would not be.
      /// </summary>
      private FrameworkElement GroupSegment(NavNode group)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = group.Title,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            FontSize = Typography.Label,
            Padding = new Thickness(4, 2, 4, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = string.IsNullOrEmpty(group.Purpose)
               ? "Show " + group.Title + " in the navigation"
               : group.Purpose
         };
         System.Windows.Automation.AutomationProperties.SetAutomationId(button, "breadcrumb-group-" + NavigationMap.Slug(group.Title));
         System.Windows.Automation.AutomationProperties.SetName(button, "Show the group " + group.Title + " in the navigation");
         button.Click += (s, e) => RevealGroup(group.Title);
         return button;
      }

      private FrameworkElement RelatedLink(NavNode page)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = page.Title,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            FontSize = Typography.Caption,
            Padding = new Thickness(6, 2, 6, 2),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = page.Purpose
         };
         System.Windows.Automation.AutomationProperties.SetAutomationId(button, "breadcrumb-related-" + page.Key);
         System.Windows.Automation.AutomationProperties.SetName(button, "Go to the related page " + page.Title);
         button.Click += (s, e) => NavigateTo(page.Key);
         return button;
      }

      private static FrameworkElement BreadcrumbSeparator()
      {
         var separator = new TextBlock
         {
            Text = ">",
            FontSize = Typography.Caption,
            Margin = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center
         };
         separator.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
         return separator;
      }

      private static FrameworkElement SearchMarker()
      {
         var marker = new Border
         {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
         };
         marker.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");

         var text = new TextBlock { Text = "Found by search", FontSize = Typography.Caption };
         text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         marker.Child = text;

         System.Windows.Automation.AutomationProperties.SetAutomationId(marker, "breadcrumb-from-search");
         System.Windows.Automation.AutomationProperties.SetName(marker, "You arrived here from the search palette");
         return marker;
      }

      /// <summary>
      ///    Expands a group in the sidebar and scrolls it into view, without disturbing
      ///    which page is selected.
      ///
      ///    Deliberately does NOT call group.Focus(). A TreeView's selection follows
      ///    keyboard focus, so focusing the group heading selected it and deselected the
      ///    page the user is actually looking at: the content stayed put - because
      ///    NavTree_SelectedItemChanged early-returns for a heading, whose Tag is its
      ///    NavNode rather than a page-key string -
      ///    while the sidebar highlight and the brand pill jumped to the heading. The
      ///    sidebar then disagreed with the content, which is precisely the "where am I"
      ///    problem this navigation work exists to remove.
      ///
      ///    Expanding and bringing into view is the whole job here. Whoever wants the
      ///    keyboard in the sidebar can Tab to it, and arrowing from the selected page
      ///    still works because the selection was never moved.
      /// </summary>
      private void RevealGroup(string title)
      {
         // By the NavNode in the Tag, never by the Header: a group's header is
         // an icon-plus-text StackPanel whenever its icon name resolves, so
         // "Header as string" matched only the groups whose icon was broken.
         foreach (TreeViewItem group in AllGroups(NavTree.Items)
            .Where(g => g.Tag is NavNode node && string.Equals(node.Title, title, StringComparison.Ordinal)))
         {
            for (var parent = group.Parent as TreeViewItem; parent != null; parent = parent.Parent as TreeViewItem)
               parent.IsExpanded = true;

            group.IsExpanded = true;
            group.BringIntoView();
            return;
         }
      }

      private IEnumerable<TreeViewItem> AllGroups(ItemCollection items)
      {
         foreach (TreeViewItem item in items)
         {
            if (item.Tag is not string)
               yield return item;
            foreach (TreeViewItem child in AllGroups(item.Items))
               yield return child;
         }
      }

      /// <summary>
      /// Reads the palette history. Anything at all can be in a registry value,
      /// so a damaged one costs the history and nothing else - an error dialog on
      /// start-up because the list of recently visited pages was corrupt would be
      /// absurd.
      /// </summary>
      private void LoadPaletteUsage()
      {
         try
         {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            paletteUsage_ = PaletteUsage.Deserialize(key?.GetValue(PaletteUsageValue) as string);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            paletteUsage_ = new PaletteUsage();
         }
      }

      /// <summary>
      /// Written after every visit rather than once on close. The window bounds
      /// can afford to be saved on close because losing them costs one resize;
      /// losing a session's worth of history makes the feature look like it does
      /// not work, and the process does not always get to close cleanly. One
      /// short SetValue at user-navigation rate is not worth optimising.
      /// </summary>
      private void SavePaletteUsage()
      {
         try
         {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue(PaletteUsageValue, paletteUsage_.Serialize());
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }
      }

      private void OnConnected()
      {
         if (!VerifyTwoFactor())
         {
            // Two-factor verification failed or was cancelled: drop the session
            // and return to the connect screen instead of revealing the UI.
            connected_ = false;
            NavTree.IsEnabled = false;
            SearchButton.IsEnabled = false;
            BreadcrumbBar.Visibility = Visibility.Collapsed;
            ConnBadge.Visibility = Visibility.Collapsed;
            currentPage_ = null;
            ContentHost.Content = new ConnectView(OnConnected);
            return;
         }

         connected_ = true;
         NavTree.IsEnabled = true;
         SearchButton.IsEnabled = true;

         ConnBadge.Visibility = Visibility.Visible;
         ConnText.Text = ServerSession.Current.UserName + " @ " + ServerSession.Current.Host;

         // "admin @ mail.example.test" is a fact, not a statement: nothing in it
         // says that this is a live connection. That was carried entirely by the
         // green dot beside it, which is information conveyed by colour alone and
         // reaches a screen reader not at all - an Ellipse has no automation peer.
         System.Windows.Automation.AutomationProperties.SetName(ConnText,
            "Connected to " + ServerSession.Current.Host + " as " + ServerSession.Current.UserName);

         try
         {
            VersionText.Text = "hMailServer " + (string)ServerSession.Current.Application.Version;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            VersionText.Text = "";
         }

         // Drop any selection left over from an earlier session on this window.
         // Selecting the item that is already selected raises no
         // SelectedItemChanged, so without this a reconnect after a cancelled
         // two-factor prompt left the connect screen on display with the
         // navigation enabled beside it and nothing to click that would replace
         // it. Deselecting raises the event with a null item, which the handler
         // ignores.
         foreach (TreeViewItem selected in AllLeaves(NavTree.Items).Where(l => l.IsSelected).ToList())
            selected.IsSelected = false;

         // By key rather than by position in the tree: the first release that
         // reordered the sidebar would otherwise have opened whatever page
         // happened to land at index 1.
         //
         // Flagged as automatic so it is not counted in "Most used" - see the guard in
         // NavigateTo. try/finally because a page's OnEnter can throw, and leaving the
         // flag set would then stop counting every genuine visit for the rest of the
         // session.
         automaticNavigation_ = true;

         try
         {
            NavigateTo("dashboard");
         }
         finally
         {
            automaticNavigation_ = false;
         }
      }

      /// <summary>
      /// The session healed itself after the service went away (a restart, most
      /// often one the Control Panel triggered itself). Say so, and put fresh
      /// data on the page the user is looking at. Deferred to the message loop
      /// because the reconnect happens in the middle of somebody else's COM
      /// call, possibly on a background thread.
      /// </summary>
      private void OnSessionReconnected(ServerSession session)
      {
         Dispatcher.BeginInvoke(new Action(() =>
         {
            if (!connected_)
               return;

            Services.Toast.Info("Reconnected to " + session.Host + " after the service restarted.", "Connection restored");
            EnterPage(ContentHost.Content);
         }));
      }

      private bool VerifyTwoFactor()
      {
         if (!TotpManager.IsConfigured())
            return true;

         for (int attempt = 0; attempt < 3; attempt++)
         {
            var prompt = new Views.TotpPromptDialog(this);
            if (prompt.ShowDialog() != true)
               return false;

            if (Totp.VerifyCode(TotpManager.ReadSecret(), prompt.Code))
               return true;

            MessageBox.Show("The verification code is incorrect.", "Two-factor authentication");
         }

         return false;
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
            {
               // Nothing was navigated to, so the "from search" marker must not
               // be left armed for whatever page is opened next.
               arrivedFromSearch_ = false;
               return;
            }

            page = factory();
            pageCache_[key] = page;
         }

         if (ContentHost.Content is IPageLifecycle oldPage)
         {
            try
            {
               oldPage.OnLeave();
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // Leaving a page must never block navigating away from it.
            }
         }

         ContentHost.Content = page;
         currentPage_ = key;

         // Before the page is entered, so that a page whose OnEnter throws still
         // leaves the user able to see where they are and get back out.
         UpdateBreadcrumb(key);

         // Only pages the user chose. OnConnected navigates to the dashboard on every
         // launch, so counting that visit made "Most used" report the dashboard at the
         // top for ever - one count per launch, against single digits for the pages the
         // installation actually lives in. A section whose entire purpose is to surface
         // what this operator uses must not be dominated by what the application does to
         // itself before the operator has touched anything.
         if (!automaticNavigation_)
         {
            paletteUsage_.RecordVisit(key);
            SavePaletteUsage();
         }

         EnterPage(page);
      }

      /// <summary>
      /// Activates a page, reporting a server that has gone away as a message
      /// instead of an unhandled exception. Several pages load their data
      /// straight from the COM API in OnEnter, so before this the first click
      /// after the hMailServer service stopped threw the application-level
      /// error dialog.
      /// </summary>
      private void EnterPage(object page)
      {
         if (page is not IPageLifecycle lifecycle)
            return;

         try
         {
            lifecycle.OnEnter();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            Services.Toast.Info("Could not load this page: " + ServerSession.DescribeComError(ex),
               "Server unavailable");
         }
      }

      private void RestoreWindowBounds()
      {
         try
         {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key == null)
               return;

            double w = ToDouble(key.GetValue("WindowWidth"));
            double h = ToDouble(key.GetValue("WindowHeight"));
            double left = ToDouble(key.GetValue("WindowLeft"), double.NaN);
            double top = ToDouble(key.GetValue("WindowTop"), double.NaN);

            if (w >= MinWidth && h >= MinHeight)
            {
               Width = w;
               Height = h;
            }

            // Only restore the position if it lands on a currently-visible screen,
            // so the window can't open off-screen after a monitor change.
            if (!double.IsNaN(left) && !double.IsNaN(top) &&
                left + w > SystemParameters.VirtualScreenLeft + 40 &&
                top + h > SystemParameters.VirtualScreenTop + 40 &&
                left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
                top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
            {
               WindowStartupLocation = WindowStartupLocation.Manual;
               Left = left;
               Top = top;
            }

            if (string.Equals(key.GetValue("WindowMaximized") as string, "1", StringComparison.Ordinal))
               WindowState = WindowState.Maximized;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }
      }

      private void SaveWindowBounds()
      {
         try
         {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            if (key == null)
               return;

            bool maximized = WindowState == WindowState.Maximized;
            // Persist the normal (restored) bounds so un-maximizing next time works.
            Rect bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);

            key.SetValue("WindowWidth", (int)bounds.Width);
            key.SetValue("WindowHeight", (int)bounds.Height);
            key.SetValue("WindowLeft", (int)bounds.Left);
            key.SetValue("WindowTop", (int)bounds.Top);
            key.SetValue("WindowMaximized", maximized ? "1" : "0");
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }
      }

      private static double ToDouble(object value, double fallback = 0)
      {
         if (value == null)
            return fallback;
         return double.TryParse(value.ToString(), out double d) ? d : fallback;
      }

      private void ApplySavedTheme()
      {
         string saved = null;
         try
         {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            saved = key?.GetValue("Theme") as string;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }

         if (saved == "Light")
         {
            ApplicationThemeManager.Apply(ApplicationTheme.Light);
         }
         else if (saved == "Dark")
         {
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
         }
         else
         {
            // No saved preference: follow the OS theme and keep tracking it until
            // the user makes an explicit choice with the theme toggle.
            try
            {
               ApplicationThemeManager.ApplySystemTheme();
               SystemThemeWatcher.Watch(this);
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
            }
         }

         // Make sure the semantic tokens match whatever theme we just applied.
         Services.ThemeTokens.Refresh();

         UpdateThemeToggle_();
      }

      /// <summary>
      /// Points the theme toggle at the state a click will produce: a sun while
      /// the application is dark, a moon while it is light. It used to show
      /// DarkTheme24 in both states - an icon disagreeing with its verb, the
      /// same defect the Pause button had. The tool tip and accessible name
      /// follow, so "what will this do" reads the same by eye and by ear.
      /// </summary>
      private void UpdateThemeToggle_()
      {
         bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

         ThemeButton.Icon = new Wpf.Ui.Controls.SymbolIcon(
            dark ? Wpf.Ui.Controls.SymbolRegular.WeatherSunny24
                 : Wpf.Ui.Controls.SymbolRegular.WeatherMoon24);

         string action = dark ? "Switch to the light theme" : "Switch to the dark theme";
         ThemeButton.ToolTip = action;
         System.Windows.Automation.AutomationProperties.SetName(ThemeButton, action);
      }

      private void OnThemeChanged(ApplicationTheme theme, System.Windows.Media.Color accent)
         => UpdateThemeToggle_();

      private void Theme_Click(object sender, RoutedEventArgs e)
      {
         // An explicit toggle takes over from the OS, so stop following it.
         try
         {
            SystemThemeWatcher.UnWatch(this);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }

         bool toLight = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
         ApplicationThemeManager.Apply(toLight ? ApplicationTheme.Light : ApplicationTheme.Dark);

         // Recompute semantic tokens for the newly applied theme.
         Services.ThemeTokens.Refresh();

         // OnThemeChanged has already run via the Changed event; calling again
         // costs nothing and keeps the button honest even if a future WPF-UI
         // stops raising Changed from Apply.
         UpdateThemeToggle_();

         try
         {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue("Theme", toLight ? "Light" : "Dark");
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
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
