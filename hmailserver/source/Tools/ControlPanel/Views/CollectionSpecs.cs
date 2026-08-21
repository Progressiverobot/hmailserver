using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using hMailServer.ControlPanel.Services;
using static hMailServer.ControlPanel.Views.CollectionEditorView;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Factory for the data-driven list panes. Each method returns a fully
   /// configured <see cref="CollectionEditorView"/> bound to a COM collection.
   /// </summary>
   public static class CollectionSpecs
   {
      private static dynamic AntiSpam => ServerSession.Current.Application.Settings.AntiSpam;
      private static dynamic Settings => ServerSession.Current.Application.Settings;

      /// <summary>
      /// hMailServer's eConnectionSecurity, in the order the enum declares it, and
      /// worded the same way the SSL/TLS and Delivery pages word it so the same
      /// choice does not read as two different things in two places. The wording
      /// carries the port each one implies, because choosing the security without
      /// changing the port is the single commonest way an external account is
      /// configured and then never downloads anything.
      /// </summary>
      private static readonly (int Value, string Label)[] ConnectionSecurityOptions =
      {
         (0, "None - no encryption (port 110)"),
         (1, "SSL/TLS - encrypted from the first byte (port 995)"),
         (2, "STARTTLS, optional - upgrade if offered, continue in the clear if not (port 110)"),
         (3, "STARTTLS, required - refuse to download unless the upgrade succeeds (port 110)")
      };

      public static CollectionEditorView SurblServers() => new(new CollectionSpec
      {
         Title = "SURBL servers",
         Subtitle = "Spam URI Realtime Block Lists. Messages whose body links resolve on these hosts gain spam score.",
         ItemNoun = "SURBL server",
         GetCollection = () => AntiSpam.SURBLServers,
         Fields =
         {
            new FieldSpec { Prop = "Active", Label = "Active", Kind = FieldKind.Bool, GridWidth = 70 },
            new FieldSpec { Prop = "DNSHost", Label = "DNS host" },
            new FieldSpec { Prop = "RejectMessage", Label = "Reject message" },
            new FieldSpec { Prop = "Score", Label = "Score", Kind = FieldKind.Number, GridWidth = 80, Default = 5 }
         }
      });

      public static CollectionEditorView DnsBlackLists() => new(new CollectionSpec
      {
         Title = "DNS blacklists (DNSBL)",
         Subtitle = "Real-time blackhole lists checked against the connecting IP address.",
         ItemNoun = "blacklist",
         GetCollection = () => AntiSpam.DNSBlackLists,
         Fields =
         {
            new FieldSpec { Prop = "Active", Label = "Active", Kind = FieldKind.Bool, GridWidth = 70 },
            new FieldSpec { Prop = "DNSHost", Label = "DNS host" },
            new FieldSpec { Prop = "ExpectedResult", Label = "Expected result", GridWidth = 130 },
            new FieldSpec { Prop = "RejectMessage", Label = "Reject message" },
            new FieldSpec { Prop = "Score", Label = "Score", Kind = FieldKind.Number, GridWidth = 80, Default = 5 }
         }
      });

      public static CollectionEditorView SpamWhiteList() => new(new CollectionSpec
      {
         Title = "Anti-spam white list",
         Subtitle = "Senders or IP ranges that bypass spam protection entirely.",
         ItemNoun = "white-list entry",
         GetCollection = () => AntiSpam.WhiteListAddresses,
         Fields =
         {
            new FieldSpec { Prop = "LowerIPAddress", Label = "Lower IP", GridWidth = 150 },
            new FieldSpec { Prop = "UpperIPAddress", Label = "Upper IP", GridWidth = 150 },
            new FieldSpec { Prop = "EmailAddress", Label = "E-mail address" },
            new FieldSpec { Prop = "Description", Label = "Description" }
         }
      });

      public static CollectionEditorView BlockedSenders() => new(new CollectionSpec
      {
         Title = "Blocked senders",
         Subtitle = "Envelope senders refused outright, or scored. An entry with an @ is one exact address; " +
                    "without one it is a whole domain including its subdomains. The default score of 100 " +
                    "crosses the delete threshold and refuses the message during the SMTP conversation. " +
                    "This matches the address the sender CLAIMS, so it stops a correspondent who keeps using " +
                    "one address - it is not an anti-spoofing tool, and stops nothing that rotates addresses.",
         ItemNoun = "blocked sender",
         GetCollection = () => AntiSpam.BlockedSenders,
         Fields =
         {
            new FieldSpec { Prop = "Address", Label = "Address or domain", GridWidth = 260 },
            new FieldSpec { Prop = "Score", Label = "Score", GridWidth = 80, Default = "100" },
            new FieldSpec { Prop = "Description", Label = "Description" }
         }
      });

      public static CollectionEditorView GreyListWhiteList() => new(new CollectionSpec
      {
         Title = "Greylisting white list",
         Subtitle = "IP addresses exempt from greylisting delays.",
         ItemNoun = "address",
         GetCollection = () => AntiSpam.GreyListingWhiteAddresses,
         Fields =
         {
            new FieldSpec { Prop = "IPAddress", Label = "IP address", GridWidth = 200 },
            new FieldSpec { Prop = "Description", Label = "Description" }
         }
      });

      public static CollectionEditorView BlockedAttachments() => new(new CollectionSpec
      {
         Title = "Blocked attachments",
         Subtitle = "File-name wildcards that are stripped from incoming messages (requires attachment blocking on the Anti-virus page).",
         ItemNoun = "rule",
         GetCollection = () => Settings.AntiVirus.BlockedAttachments,
         Fields =
         {
            new FieldSpec { Prop = "Wildcard", Label = "Wildcard", GridWidth = 220, Default = "*.exe" },
            new FieldSpec { Prop = "Description", Label = "Description" }
         }
      });

      /// <summary>
      /// The Groups page. Not a bare <see cref="CollectionEditorView"/> like its
      /// neighbours, because a group is more than its name: it is a member list,
      /// and without a way to edit that list here a group could be created and
      /// granted folder permissions but never actually cover anyone - the feature
      /// only worked over the COM API.
      /// </summary>
      public static UserControl Groups() => new GroupsPageView(new CollectionSpec
      {
         Title = "Groups",
         Subtitle = "Security groups used to grant shared-folder (IMAP ACL) permissions to several accounts at once. " +
                    "A group's permissions cover exactly the accounts on its member list - select a group and choose " +
                    "Members to edit that list.",
         ItemNoun = "group",
         GetCollection = () => Settings.Groups,
         Fields =
         {
            new FieldSpec { Prop = "Name", Label = "Group name" }
         }
      });

      public static CollectionEditorView ServerMessages() => new(new CollectionSpec
      {
         Title = "Server messages",
         Subtitle = "The text templates the server returns to clients (greetings, bounce and error messages). These are a fixed set you can edit.",
         ItemNoun = "message",
         CanAdd = false,
         CanDelete = false,
         GetCollection = () => Settings.ServerMessages,
         Fields =
         {
            new FieldSpec { Prop = "Name", Label = "Name", GridWidth = 260 },
            new FieldSpec { Prop = "Text", Label = "Text", Kind = FieldKind.Multiline }
         }
      });

      // ---- per-object collections (embedded in dialogs) ----------------------

      private static dynamic OpenDomain(string domainName)
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         dynamic domain = domains.ItemByName[domainName];
         ServerSession.Release(domains);
         return domain;
      }

      private static dynamic OpenAccount(string domainName, string address)
      {
         dynamic domain = OpenDomain(domainName);
         dynamic accounts = domain.Accounts;
         dynamic account = accounts.ItemByAddress[address];
         ServerSession.Release(accounts);
         ServerSession.Release(domain);
         return account;
      }

      /// <summary>Domain-name aliases for one domain (embedded in the Domain editor).</summary>
      public static CollectionEditorView DomainAliases(string domainName) => new(new CollectionSpec
      {
         Title = "Domain aliases",
         Subtitle = "Alternative domain names treated as this domain. Mail sent to user@alias is delivered to user@" + domainName + ".",
         ItemNoun = "alias",
         GetCollection = () =>
         {
            dynamic domain = OpenDomain(domainName);
            dynamic aliases = domain.DomainAliases;
            ServerSession.Release(domain);
            return aliases;
         },
         Fields =
         {
            new FieldSpec { Prop = "AliasName", Label = "Alias domain name (e.g. example.net)", Default = "" }
         }
      }, embedded: true);

      /// <summary>External POP3 download (fetch) accounts for one account.</summary>
      public static CollectionEditorView FetchAccounts(string domainName, string address) => new(new CollectionSpec
      {
         Title = "External accounts",
         Subtitle = "POP3 mailboxes hMailServer downloads mail from on behalf of this account. "
                    + "Connection security and port go together: SSL/TLS on 995, STARTTLS on 110.",
         ItemNoun = "external account",
         GetCollection = () =>
         {
            dynamic account = OpenAccount(domainName, address);
            dynamic fetch = account.FetchAccounts;
            ServerSession.Release(account);
            return fetch;
         },
         Fields =
         {
            new FieldSpec { Prop = "Enabled", Label = "Enabled", Kind = FieldKind.Bool, GridWidth = 70, Default = true },
            new FieldSpec { Prop = "Name", Label = "Name", GridWidth = 150, Default = "" },
            new FieldSpec { Prop = "ServerAddress", Label = "POP3 server", Default = "" },
            new FieldSpec { Prop = "Port", Label = "Port", Kind = FieldKind.Number, GridWidth = 70, Default = 110 },
            new FieldSpec { Prop = "Username", Label = "User name", ShowInGrid = false, Default = "" },
            new FieldSpec { Prop = "Password", Label = "Password", Kind = FieldKind.Password, ShowInGrid = false, Default = "" },
            new FieldSpec { Prop = "MinutesBetweenFetch", Label = "Minutes between downloads", Kind = FieldKind.Number, ShowInGrid = false, Default = 15 },
            new FieldSpec { Prop = "DaysToKeepMessages", Label = "Days to keep on server (0 = delete after download)", Kind = FieldKind.Number, ShowInGrid = false, Default = 0 },
            // Was a "Use SSL/TLS" checkbox, which could not express STARTTLS at
            // all. The COM property behind that checkbox is UseSSL, and its
            // getter is ConnectionSecurity == CSSSL while its setter writes
            // CSSSL or CSNone - so an account already set to STARTTLS read back
            // as unticked, and ticking anything on that dialog rewrote it to
            // implicit TLS on the next save. Implicit TLS on port 110 does not
            // connect, which made STARTTLS effectively unreachable from the GUI
            // for the very providers that require it.
            //
            // The server has supported all four values on the fetch path
            // throughout: POP3ClientConnection sends CAPA, issues STLS, and on
            // CSSTARTTLSRequired abandons the download with a log line when the
            // far end does not advertise STLS.
            new FieldSpec
            {
               Prop = "ConnectionSecurity",
               Label = "Connection security",
               Kind = FieldKind.Combo,
               Options = ConnectionSecurityOptions,
               GridWidth = 150,
               Default = 0
            },
            new FieldSpec { Prop = "UseAntiSpam", Label = "Run anti-spam on downloaded mail", Kind = FieldKind.Bool, ShowInGrid = false, Default = true },
            new FieldSpec { Prop = "UseAntiVirus", Label = "Run anti-virus on downloaded mail", Kind = FieldKind.Bool, ShowInGrid = false, Default = true }
         }
      }, embedded: true);

      /// <summary>Account-level rules (enable/disable and delete; criteria/actions are not edited here).</summary>
      public static CollectionEditorView AccountRules(string domainName, string address) => new(new CollectionSpec
      {
         Title = "Rules",
         Subtitle = "Per-account rules. You can add, rename, enable/disable and delete rules here; matching criteria and actions are managed on the server.",
         ItemNoun = "rule",
         GetCollection = () =>
         {
            dynamic account = OpenAccount(domainName, address);
            dynamic rules = account.Rules;
            ServerSession.Release(account);
            return rules;
         },
         Fields =
         {
            new FieldSpec { Prop = "Active", Label = "Active", Kind = FieldKind.Bool, GridWidth = 70, Default = true },
            new FieldSpec { Prop = "Name", Label = "Rule name", Default = "" }
         }
      }, embedded: true);
   }

   /// <summary>
   /// The Groups page: the generic collection editor for the list itself, plus the
   /// one thing the generic editor cannot offer - a way into the selected group's
   /// member list (<see cref="GroupMembersDialog"/>).
   ///
   /// The selection is observed through the DataGrid's bubbling SelectionChanged
   /// event rather than by reaching into the editor's internals, so the editor
   /// stays a black box; the grid's rows are <see cref="CollectionEditorView.Row"/>
   /// instances, which carry the group's database ID and name.
   /// </summary>
   public sealed class GroupsPageView : UserControl, IPageLifecycle
   {
      private const string HintNoSelection =
         "Select a group to edit which accounts are members of it.";

      private readonly CollectionEditorView editor_;
      private readonly Wpf.Ui.Controls.Button membersButton_;
      private readonly TextBlock membersHint_;

      private int selectedGroupId_ = -1;
      private string selectedGroupName_ = "";

      public GroupsPageView(CollectionSpec spec)
      {
         // The page chrome (title and subtitle) is drawn here so the members row
         // can sit inside the same page layout; the editor is hosted in its
         // embedded form, which draws no chrome of its own. The subtitle is taken
         // off the spec so the embedded editor does not repeat it as its hint.
         string subtitle = spec.Subtitle;
         spec.Subtitle = null;

         editor_ = new CollectionEditorView(spec, embedded: true);

         var root = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var head = new StackPanel();
         head.Children.Add(new TextBlock { Text = spec.Title, Style = (Style) FindResource("PageTitle") });
         head.Children.Add(new TextBlock { Text = subtitle, Style = (Style) FindResource("PageSubtitle") });
         root.Children.Add(head);

         Grid.SetRow(editor_, 1);
         root.Children.Add(editor_);

         var membersRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

         membersButton_ = new Wpf.Ui.Controls.Button
         {
            Content = "Members…",
            MinWidth = 110,
            IsEnabled = false
         };
         AutomationProperties.SetName(membersButton_, "Edit the members of the selected group");
         membersButton_.Click += (_, _) => OpenMembers();
         membersRow.Children.Add(membersButton_);

         membersHint_ = new TextBlock
         {
            Text = HintNoSelection,
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
         };
         membersHint_.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         membersRow.Children.Add(membersHint_);

         Grid.SetRow(membersRow, 2);
         root.Children.Add(membersRow);

         Content = root;

         // handledEventsToo, in case a future DataGrid template handles the event
         // on its way up; today it arrives unhandled.
         AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSelectionChanged), true);
      }

      private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
      {
         if (e.OriginalSource is not DataGrid grid)
            return;

         if (grid.SelectedItem is Row row && row.Id > 0)
         {
            selectedGroupId_ = row.Id;
            selectedGroupName_ = row.Display("Name");
            membersButton_.IsEnabled = true;
            membersHint_.Text = "Open the member list of '" + selectedGroupName_ + "'.";
         }
         else
         {
            selectedGroupId_ = -1;
            selectedGroupName_ = "";
            membersButton_.IsEnabled = false;
            membersHint_.Text = HintNoSelection;
         }
      }

      private void OpenMembers()
      {
         if (selectedGroupId_ <= 0)
            return;

         var dialog = new GroupMembersDialog(Window.GetWindow(this), selectedGroupId_, selectedGroupName_);
         dialog.ShowDialog();
      }

      public void OnEnter() => editor_.OnEnter();
      public void OnLeave() => editor_.OnLeave();
   }
}
