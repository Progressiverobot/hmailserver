using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using hMailServer.ControlPanel.Services;

// The Control Panel has its own Typography (the type scale, in Services) and
// System.Windows.Documents declares one too; ImplicitUsings also puts System.IO.Path
// in scope while the status marks need the WPF shape. Both are aliased to the one
// meant rather than dropping the imports.
using Typography = hMailServer.ControlPanel.Services.Typography;
using Path = System.Windows.Shapes.Path;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The page for the whole [LDAP] section of hMailServer.INI - directory
   /// authentication, which lets an account marked as directory-linked log on with
   /// its domain password by an LDAP bind instead of LogonUser. Until this page
   /// existed the feature was a README headline with no GUI and no COM: an
   /// administrator could only enable it by hand-editing an INI section they had to
   /// know existed.
   ///
   /// Everything shown here - every key, every default, every rule about what the
   /// server does with a value nobody meant - is taken from
   /// Server/Common/LDAP/LdapSettings.cpp and LdapDirectoryAuthenticator.cpp, not
   /// from documentation. Where the page states a behaviour ("an unrecognised
   /// Security value is read as LDAPS"), that statement has a line of server code
   /// behind it.
   ///
   /// Two things about this page differ from the other INI-backed settings pages:
   ///
   ///  - It writes the [LDAP] section, which IniFeatureStore cannot address (its
   ///    section name is a private constant, "Settings"). The store is still used to
   ///    LOCATE the INI; the reads and writes go through this page's own
   ///    GetPrivateProfileString/WritePrivateProfileString calls with the right
   ///    section, rather than silently landing every key in [Settings] where the
   ///    server would never see them.
   ///
   ///  - Saving needs no service restart. LdapSettings re-reads the section within
   ///    two seconds of the file's timestamp changing - deliberately, because the
   ///    thing being corrected is often the reason nobody can log in - so this page
   ///    says "live within two seconds" where the other pages say "restart the
   ///    service".
   /// </summary>
   public class LdapSettingsView : UserControl, IPageLifecycle
   {
      // The shipped filter, character for character from LdapSettings.cpp. Shown as
      // the placeholder and used by the connection test when the box is empty,
      // because that is exactly what the server does with an empty value.
      private const string DefaultUserSearchFilter =
         "(&(objectCategory=person)(objectClass=user)(sAMAccountName=%u))";

      private const string IniSection = "LDAP";

      private readonly IniFeatureStore store_ = new();

      // ---- editors --------------------------------------------------------------

      private readonly CheckBox enabled_ = new();
      private readonly Wpf.Ui.Controls.TextBox server_ = new();
      private readonly Wpf.Ui.Controls.TextBox port_ = new();
      private readonly ComboBox security_ = new();
      private readonly CheckBox verifyCertificate_ = new();
      private readonly Wpf.Ui.Controls.TextBox timeout_ = new();
      private readonly ComboBox bindMethod_ = new();
      private readonly CheckBox allowUnprotected_ = new();
      private readonly Wpf.Ui.Controls.TextBox searchBase_ = new();
      private readonly Wpf.Ui.Controls.TextBox userSearchFilter_ = new();
      private readonly Wpf.Ui.Controls.TextBox userDnTemplate_ = new();
      private readonly Wpf.Ui.Controls.TextBox serviceUsername_ = new();
      private readonly Wpf.Ui.Controls.PasswordBox servicePassword_ = new();
      private readonly CheckBox clearServicePassword_ = new();
      private readonly TextBlock serviceDomainNote_ = new();
      private readonly CheckBox fallback_ = new();

      // ---- state card -----------------------------------------------------------

      private readonly Path stateMark_ = new();
      private readonly TextBlock stateText_ = new();
      private readonly TextBlock stateDetail_ = new();

      // ---- connection test ------------------------------------------------------

      private readonly Wpf.Ui.Controls.TextBox testUsername_ = new();
      private readonly Wpf.Ui.Controls.TextBox testDomain_ = new();
      private readonly Wpf.Ui.Controls.PasswordBox testPassword_ = new();
      private Wpf.Ui.Controls.Button testButton_;
      private readonly Path testMark_ = new();
      private readonly TextBlock testText_ = new();

      // ---- footer ---------------------------------------------------------------

      private readonly TextBlock footerStatus_ = new();
      private Wpf.Ui.Controls.Button saveButton_;

      /// <summary>Whether the INI already holds a ServicePassword. Only the fact is
      /// kept, never the value: the password is write-only on this page.</summary>
      private bool storedServicePassword_;

      /// <summary>Suppresses the live state recomputation while Load() is filling
      /// the editors, so half-loaded values are never evaluated.</summary>
      private bool loading_;

      public LdapSettingsView()
      {
         var root = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var heading = new StackPanel();
         var title = new TextBlock { Text = "Directory authentication (LDAP)" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         heading.Children.Add(title);

         var subtitle = new TextBlock
         {
            Text = "Accounts marked as directory-linked log on with their domain password through an LDAP bind - "
                   + "unlike Windows logon, this works from a host that is not joined to the domain, which is the "
                   + "usual situation for a mail server in a DMZ. Edits the [LDAP] section of hMailServer.INI; the "
                   + "server re-reads that section within two seconds of a save, so no service restart is needed."
         };
         subtitle.SetResourceReference(StyleProperty, "PageSubtitle");
         heading.Children.Add(subtitle);
         root.Children.Add(heading);

         var cards = new StackPanel { Margin = new Thickness(0, 0, 12, 0), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
         cards.Children.Add(BuildStateCard_());
         cards.Children.Add(BuildConnectionCard_());
         cards.Children.Add(BuildBindMethodCard_());
         cards.Children.Add(BuildLookupCard_());
         cards.Children.Add(BuildServiceCredentialCard_());
         cards.Children.Add(BuildFallbackCard_());
         cards.Children.Add(BuildPrerequisitesCard_());
         cards.Children.Add(BuildTestCard_());

         var scroll = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
         Grid.SetRow(scroll, 1);
         root.Children.Add(scroll);

         FrameworkElement footer = BuildFooter_();
         Grid.SetRow(footer, 2);
         root.Children.Add(footer);

         Content = root;

         WireLiveState_();
      }

      public void OnEnter()
      {
         // The page instance is cached by MainWindow, so the INI is re-read on every
         // navigation - the same reasoning as FeatureSettingsView, and doubly so
         // here, where the server itself picks up hand edits within two seconds and
         // this page must not show staler data than the server is running on.
         Load_();
      }

      public void OnLeave()
      {
      }

      // =========================================================================
      // Layout
      // =========================================================================

      private FrameworkElement BuildFooter_()
      {
         var footer = new Grid { Margin = new Thickness(0, 14, 12, 0) };

         footerStatus_.VerticalAlignment = VerticalAlignment.Center;
         footerStatus_.FontSize = Typography.Caption;
         footerStatus_.TextWrapping = TextWrapping.Wrap;
         footerStatus_.MaxWidth = 480;
         footerStatus_.HorizontalAlignment = HorizontalAlignment.Left;
         footerStatus_.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
         footer.Children.Add(footerStatus_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

         var reload = new Wpf.Ui.Controls.Button { Content = "Reload", Margin = new Thickness(0, 0, 8, 0) };
         System.Windows.Automation.AutomationProperties.SetName(reload, "Re-read the LDAP settings from hMailServer.INI");
         System.Windows.Automation.AutomationProperties.SetAutomationId(reload, "ldap-reload");
         reload.Click += (s, e) => Load_();
         buttons.Children.Add(reload);

         saveButton_ = new Wpf.Ui.Controls.Button { Content = "Save changes", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary };
         System.Windows.Automation.AutomationProperties.SetName(saveButton_, "Save the LDAP settings to hMailServer.INI");
         System.Windows.Automation.AutomationProperties.SetAutomationId(saveButton_, "ldap-save");
         saveButton_.Click += (s, e) => Save_();
         buttons.Children.Add(saveButton_);

         footer.Children.Add(buttons);
         return footer;
      }

      private static Border Card_(string cardTitle, string blurb, out StackPanel content)
      {
         var border = new Border { Margin = new Thickness(0, 0, 0, 12) };
         border.SetResourceReference(StyleProperty, "Card");

         var panel = new StackPanel();
         panel.Children.Add(new TextBlock
         {
            Text = cardTitle,
            FontSize = Typography.SectionHeading,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
         });

         if (!string.IsNullOrEmpty(blurb))
         {
            panel.Children.Add(new TextBlock
            {
               Text = blurb,
               FontSize = Typography.Caption,
               TextWrapping = TextWrapping.Wrap,
               Opacity = 0.65,
               Margin = new Thickness(0, 0, 0, 14)
            });
         }

         border.Child = panel;
         content = panel;
         return border;
      }

      private static TextBlock Caption_(string text)
      {
         return new TextBlock
         {
            Text = text,
            FontSize = Typography.Caption,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.65,
            Margin = new Thickness(0, 4, 0, 0)
         };
      }

      /// <summary>
      /// A labelled text box: the label, the box, and a caption printed under it and
      /// attached as accessible help text - the same double delivery
      /// FeatureSettingsView's Annotate gives, and for the same reason: a caption
      /// sitting loose in the panel is reached only after the editor, and a note
      /// qualifying what the server does with the value has to be heard with it.
      /// </summary>
      private static FrameworkElement LabelledBox_(string label, Wpf.Ui.Controls.TextBox box,
         string automationId, string caption, string placeholder = "")
      {
         var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
         panel.Children.Add(new TextBlock { Text = label, FontSize = Typography.Body, Margin = new Thickness(0, 0, 0, 4) });

         box.FontSize = Typography.Body;
         box.MaxWidth = 520;
         box.MinWidth = 320;
         box.HorizontalAlignment = HorizontalAlignment.Left;
         box.PlaceholderText = placeholder;
         System.Windows.Automation.AutomationProperties.SetName(box, label);
         System.Windows.Automation.AutomationProperties.SetAutomationId(box, automationId);
         panel.Children.Add(box);

         if (!string.IsNullOrEmpty(caption))
         {
            System.Windows.Automation.AutomationProperties.SetHelpText(box, caption);
            panel.Children.Add(Caption_(caption));
         }

         return panel;
      }

      private static FrameworkElement LabelledCheck_(CheckBox box, string label, string automationId, string caption)
      {
         var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };

         box.Content = label;   // a checkbox names itself from its content
         box.FontSize = Typography.Control;
         System.Windows.Automation.AutomationProperties.SetAutomationId(box, automationId);
         panel.Children.Add(box);

         if (!string.IsNullOrEmpty(caption))
         {
            System.Windows.Automation.AutomationProperties.SetHelpText(box, caption);
            panel.Children.Add(Caption_(caption));
         }

         return panel;
      }

      private static FrameworkElement LabelledCombo_(string label, ComboBox combo,
         string automationId, string caption, params (int Value, string Text)[] options)
      {
         var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
         panel.Children.Add(new TextBlock { Text = label, FontSize = Typography.Body, Margin = new Thickness(0, 0, 0, 4) });

         combo.FontSize = Typography.Body;
         combo.MinWidth = 320;
         combo.MaxWidth = 520;
         combo.HorizontalAlignment = HorizontalAlignment.Left;
         foreach ((int value, string text) in options)
            combo.Items.Add(new ComboBoxItem { Content = text, Tag = value });

         System.Windows.Automation.AutomationProperties.SetName(combo, label);
         System.Windows.Automation.AutomationProperties.SetAutomationId(combo, automationId);
         panel.Children.Add(combo);

         if (!string.IsNullOrEmpty(caption))
         {
            System.Windows.Automation.AutomationProperties.SetHelpText(combo, caption);
            panel.Children.Add(Caption_(caption));
         }

         return panel;
      }

      private static void SelectByTag_(ComboBox combo, int value)
      {
         foreach (ComboBoxItem item in combo.Items)
         {
            if ((int) item.Tag == value)
            {
               combo.SelectedItem = item;
               return;
            }
         }

         if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
      }

      private static int SelectedTag_(ComboBox combo, int fallback)
         => combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : fallback;

      // ---- the cards ------------------------------------------------------------

      private Border BuildStateCard_()
      {
         Border card = Card_("Is this configuration complete?", null, out StackPanel content);

         var row = new Grid();
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         stateMark_.Width = 12;
         stateMark_.Height = 12;
         stateMark_.Margin = new Thickness(0, 4, 8, 0);
         stateMark_.VerticalAlignment = VerticalAlignment.Top;
         row.Children.Add(stateMark_);

         stateText_.FontSize = Typography.Body;
         stateText_.TextWrapping = TextWrapping.Wrap;
         System.Windows.Automation.AutomationProperties.SetAutomationId(stateText_, "ldap-state");
         System.Windows.Automation.AutomationProperties.SetLiveSetting(stateText_, System.Windows.Automation.AutomationLiveSetting.Polite);
         Grid.SetColumn(stateText_, 1);
         row.Children.Add(stateText_);

         content.Children.Add(row);

         stateDetail_.FontSize = Typography.Caption;
         stateDetail_.TextWrapping = TextWrapping.Wrap;
         stateDetail_.Opacity = 0.75;
         stateDetail_.Margin = new Thickness(20, 6, 0, 0);
         content.Children.Add(stateDetail_);

         // This mirrors LdapConfiguration::IsComplete and the two refusal gates in
         // LdapDirectoryAuthenticator, evaluated over what is in the editors right
         // now - so it answers "what will happen if I save this", not "what did I
         // save last time".
         content.Children.Add(Caption_("Evaluated from the values in the editors on this page, saved or not. "
            + "The server applies the same checks to what is actually saved in hMailServer.INI."));

         return card;
      }

      private Border BuildConnectionCard_()
      {
         Border card = Card_("Directory server", null, out StackPanel content);

         content.Children.Add(LabelledCheck_(enabled_,
            "Use LDAP directory authentication (Enabled)",
            "ldap-enabled",
            "Off by default, and inert when off: directory-linked accounts are then validated the old way, through "
            + "Windows logon. When on, the same accounts - the ones with the Directory tab filled in - are validated "
            + "by an LDAP bind against the server below instead. Non-directory accounts are never affected either way."));

         content.Children.Add(LabelledBox_("Directory server host name (Server)", server_, "ldap-server",
            "Required. The Active Directory domain controller or other LDAP server to bind against. For LDAPS the "
            + "name must match the server's certificate, so use the DNS name, not an IP address.",
            placeholder: "dc01.example.local"));

         content.Children.Add(LabelledBox_("Port (Port)", port_, "ldap-port",
            "Leave empty (or 0) for automatic: 636 when the connection security is LDAPS, 389 otherwise. The port is "
            + "derived from the security setting so that choosing LDAPS and forgetting the port cannot attempt TLS "
            + "against the cleartext port, which fails in a way that looks like a certificate problem.",
            placeholder: "Automatic - 636 for LDAPS, 389 otherwise"));

         content.Children.Add(LabelledCombo_("Connection security (Security)", security_, "ldap-security",
            "LDAPS is the default. A number in the INI that is not one of these three is read as LDAPS, so a typo "
            + "cannot silently weaken the transport - and whatever this is set to, a password is never sent over an "
            + "unprotected connection unless that is explicitly allowed below.",
            (2, "LDAPS - TLS from the first byte (default, port 636)"),
            (1, "StartTLS - connect on 389, upgrade to TLS before anything is sent"),
            (0, "Unprotected LDAP - cleartext (lab networks only)")));

         content.Children.Add(LabelledCheck_(verifyCertificate_,
            "Verify the directory server's certificate (VerifyCertificate)",
            "ldap-verify-certificate",
            "On by default, and only an explicit 0 in the INI turns it off. Turned off, the encrypted connection no "
            + "longer proves it reaches YOUR directory: anyone who can intercept it can present any certificate and "
            + "collect every password sent over it. The server notes the disabling in its application log once per "
            + "service start. Only meaningful for LDAPS and StartTLS."));

         content.Children.Add(LabelledBox_("Timeout in seconds (TimeoutSeconds)", timeout_, "ldap-timeout",
            "Default 10, allowed range 1 to 90. The server reads values below 1 as 10 and clamps values above 90, "
            + "because \"wait forever\" for an unreachable directory is how connection threads get exhausted.",
            placeholder: "10"));

         return card;
      }

      private Border BuildBindMethodCard_()
      {
         Border card = Card_("How the password is proved",
            "Two bind methods, and the difference matters more than it looks: one sends the password to the "
            + "directory, the other never puts it on the wire at all.", out StackPanel content);

         content.Children.Add(LabelledCombo_("Bind method (BindMethod)", bindMethod_, "ldap-bind-method",
            "Simple is the default. Negotiate runs an SSPI exchange (Kerberos, falling back to NTLM) that never "
            + "transmits the password and signs the connection - it is the only method a default-configured modern "
            + "domain controller accepts when it has no certificate installed, it needs no search, no SearchBase and "
            + "no service account, and it works from a host that is not domain-joined.",
            (0, "Simple bind - the password is sent to the directory (use with TLS)"),
            (1, "Negotiate (Kerberos/NTLM) - the password never crosses the network")));

         content.Children.Add(LabelledCheck_(allowUnprotected_,
            "Allow sending the password over an unprotected connection (AllowUnprotectedPassword)",
            "ldap-allow-unprotected",
            "Off by default. With unprotected LDAP and a simple bind, the server does NOT send the password and does "
            + "NOT guess what you meant: it refuses the logon and reports the contradiction, once per minute. Turning "
            + "this on sends passwords in the clear and should only ever happen on a network you control end to end."));

         return card;
      }

      private Border BuildLookupCard_()
      {
         Border card = Card_("Finding the user's directory entry",
            "A simple bind needs the user's distinguished name. There are two ways to get one - search for it, or "
            + "compute it from a template - and Negotiate needs neither, because SSPI authenticates by user name and "
            + "domain, which the account already carries.", out StackPanel content);

         content.Children.Add(LabelledBox_("Search base (SearchBase)", searchBase_, "ldap-search-base",
            "Where the search starts, e.g. DC=example,DC=local. Required in search mode - that is, when the bind "
            + "method is Simple and no DN template is set.",
            placeholder: "DC=example,DC=local"));

         content.Children.Add(LabelledBox_("User search filter (UserSearchFilter)", userSearchFilter_, "ldap-user-search-filter",
            "Left empty, the server uses the shipped filter shown in grey - objectCategory first because it is "
            + "indexed in Active Directory, and sAMAccountName because that is what the account's Directory tab "
            + "stores. %u is the account's Active Directory user name, %d its AD domain, %m its full mail address; "
            + "all three are escaped before substitution, so a hostile logon name cannot rewrite the filter. A "
            + "filter that matches more than one entry authenticates nobody, by design.",
            placeholder: DefaultUserSearchFilter));

         content.Children.Add(LabelledBox_("User DN template (UserDnTemplate)", userDnTemplate_, "ldap-user-dn-template",
            "The other way to reach a DN: set this and the server skips the search - and with it the need for a "
            + "SearchBase and a service account. A UPN works against Active Directory. Same %u, %d, %m placeholders, "
            + "escaped for a distinguished name.",
            placeholder: "%u@example.local   or   CN=%u,OU=Staff,DC=example,DC=local"));

         return card;
      }

      private Border BuildServiceCredentialCard_()
      {
         Border card = Card_("Service account (search mode only)",
            "Active Directory refuses anonymous searches by default, so search mode normally needs a credential "
            + "that may read the directory. Negotiate and DN-template configurations need none of this.",
            out StackPanel content);

         content.Children.Add(LabelledBox_("Service account user name (ServiceUsername)", serviceUsername_, "ldap-service-username",
            "Whatever your directory accepts for a simple bind: a UPN (svc-mail@example.local), DOMAIN\\name, or a "
            + "full DN. Left empty, the server searches anonymously - which usually fails against Active Directory, "
            + "with a reported reason that names this setting.",
            placeholder: "svc-mail@example.local"));

         var passwordPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
         passwordPanel.Children.Add(new TextBlock
         {
            Text = "Service account password (ServicePassword)",
            FontSize = Typography.Body,
            Margin = new Thickness(0, 0, 0, 4)
         });

         servicePassword_.FontSize = Typography.Body;
         servicePassword_.MaxWidth = 520;
         servicePassword_.MinWidth = 320;
         servicePassword_.HorizontalAlignment = HorizontalAlignment.Left;
         System.Windows.Automation.AutomationProperties.SetName(servicePassword_, "Service account password (ServicePassword)");
         System.Windows.Automation.AutomationProperties.SetAutomationId(servicePassword_, "ldap-service-password");
         passwordPanel.Children.Add(servicePassword_);

         const string passwordCaption =
            "Write-only: a stored password is never shown here again - leave the box blank to keep it. It is stored "
            + "as plain text in hMailServer.INI on the server, because the server has to present it to the directory "
            + "verbatim and so cannot hash it; restrict file access to the INI accordingly. It only ever leaves the "
            + "server over the transport configured above.";
         System.Windows.Automation.AutomationProperties.SetHelpText(servicePassword_, passwordCaption);
         passwordPanel.Children.Add(Caption_(passwordCaption));
         content.Children.Add(passwordPanel);

         content.Children.Add(LabelledCheck_(clearServicePassword_,
            "Remove the stored service account password when saving",
            "ldap-clear-service-password",
            "The blank-keeps-it rule above means there would otherwise be no way to delete a stored password from "
            + "this page - for instance after switching to Negotiate or a DN template, neither of which needs one."));

         // ServiceDomain is deliberately NOT an editor. LdapSettings.cpp reads the
         // key, and nothing anywhere consumes it: the service credential binds with
         // ServiceUsername and ServicePassword alone (LdapDirectoryAuthenticator's
         // BindServiceCredential_). An editable box for a value the server never
         // uses is the WorkerThreadPriority defect again, so the key is shown as a
         // statement instead.
         serviceDomainNote_.FontSize = Typography.Caption;
         serviceDomainNote_.TextWrapping = TextWrapping.Wrap;
         serviceDomainNote_.Opacity = 0.65;
         System.Windows.Automation.AutomationProperties.SetAutomationId(serviceDomainNote_, "ldap-service-domain-note");
         content.Children.Add(serviceDomainNote_);

         return card;
      }

      private Border BuildFallbackCard_()
      {
         Border card = Card_("If the directory cannot answer", null, out StackPanel content);

         content.Children.Add(LabelledCheck_(fallback_,
            "Retry through Windows logon when the directory is unavailable (FallbackToWindowsLogon)",
            "ldap-fallback",
            "Off by default. When off, an unreachable directory refuses the logon and the server reports the real "
            + "reason (throttled to once per minute per distinct problem) instead of retrying through Windows logon "
            + "- which cannot succeed on a host that is not domain-joined, and whose failure would bury a precise "
            + "diagnostic under an indistinguishable one. Turn on only on a domain-joined host that wants LDAP as "
            + "its first choice. A password the directory REJECTED never falls back either way: retrying it would "
            + "double every failed attempt and let one wrong password trigger a directory lockout policy."));

         return card;
      }

      private Border BuildPrerequisitesCard_()
      {
         Border card = Card_("What has to exist outside this server",
            "hMailServer cannot create any of these for itself. Each one missing has its own failure shape, and the "
            + "server reports which one it hit - see the error log.", out StackPanel content);

         AddBullet_(content,
            "A reachable directory server. The hMailServer machine must reach the host above on the effective port "
            + "(636 for LDAPS, 389 otherwise) through any firewall in between. A DMZ host usually needs a rule added "
            + "for exactly this.");

         AddBullet_(content,
            "For LDAPS or StartTLS: a certificate on the directory server that THIS machine trusts - issued by a CA "
            + "in the Windows machine certificate store, with the host name above in it. A domain controller does "
            + "not have one out of the box. The Negotiate bind method needs no certificate at all, because the "
            + "password never crosses the network.");

         AddBullet_(content,
            "For search mode: a service account allowed to read the directory. Any ordinary domain account can, by "
            + "default; it needs no other rights.");

         AddBullet_(content,
            "Accounts marked as directory-linked. That half already lives in this GUI: open Domains, edit the "
            + "account, and on its Directory tab tick the Active Directory option and fill in the AD user name - "
            + "the sAMAccountName, which is what %u carries. Accounts without that mark keep their ordinary "
            + "hMailServer password and never touch the directory.");

         var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
         links.Children.Add(PageLink_("domains", "Open Domains...", "Open Domains, where an account's Directory tab links it to the directory"));
         links.Children.Add(PageLink_("logs", "Open Live logs...", "Open Live logs, where LDAP infrastructure failures are reported"));
         content.Children.Add(links);

         return card;
      }

      private static void AddBullet_(StackPanel content, string text)
      {
         var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         var dot = new TextBlock { Text = "•", FontSize = Typography.Body, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Top };
         row.Children.Add(dot);

         var body = new TextBlock { Text = text, FontSize = Typography.Label, TextWrapping = TextWrapping.Wrap };
         Grid.SetColumn(body, 1);
         row.Children.Add(body);

         content.Children.Add(row);
      }

      private Border BuildTestCard_()
      {
         Border card = Card_("Test the connection",
            "Runs the same steps the server runs, in the same order, using the values in the editors above as they "
            + "stand: connect, protect the transport, bind the service credential, search for the user - and, only "
            + "if a password is typed below, bind as that user to prove it. The password is used once and never "
            + "stored, and the test refuses to run any configuration that would put a password on the network "
            + "unprotected. One honest caveat: the test runs from this computer, so a firewall that treats this "
            + "machine and the hMailServer service differently can make the two disagree.",
            out StackPanel content);

         content.Children.Add(LabelledBox_("User name to look up", testUsername_, "ldap-test-username",
            "The Active Directory user name (sAMAccountName) - the same value the account's Directory tab holds. "
            + "Optional: left empty, the test stops after the connection and service-credential stages.",
            placeholder: "jsmith"));

         content.Children.Add(LabelledBox_("AD domain of that user", testDomain_, "ldap-test-domain",
            "Used for the %d placeholder and for a Negotiate bind (NTLM needs it on a host that is not "
            + "domain-joined). The server takes this from the account's Directory tab; type the same value here.",
            placeholder: "EXAMPLE"));

         var passwordPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
         passwordPanel.Children.Add(new TextBlock
         {
            Text = "Password (optional - only to test a real logon)",
            FontSize = Typography.Body,
            Margin = new Thickness(0, 0, 0, 4)
         });
         testPassword_.FontSize = Typography.Body;
         testPassword_.MaxWidth = 520;
         testPassword_.MinWidth = 320;
         testPassword_.HorizontalAlignment = HorizontalAlignment.Left;
         testPassword_.PlaceholderText = "Leave empty to test only the infrastructure";
         System.Windows.Automation.AutomationProperties.SetName(testPassword_, "Password (optional - only to test a real logon)");
         System.Windows.Automation.AutomationProperties.SetAutomationId(testPassword_, "ldap-test-password");
         System.Windows.Automation.AutomationProperties.SetHelpText(testPassword_,
            "Used once for a test bind and never stored. With no password the test still proves the connection, the "
            + "transport, the service credential and the search.");
         passwordPanel.Children.Add(testPassword_);
         content.Children.Add(passwordPanel);

         testButton_ = new Wpf.Ui.Controls.Button { Content = "Test connection" };
         System.Windows.Automation.AutomationProperties.SetName(testButton_, "Test the directory connection with the values on this page");
         System.Windows.Automation.AutomationProperties.SetAutomationId(testButton_, "ldap-test-button");
         testButton_.Click += async (s, e) => await RunTest_();
         content.Children.Add(testButton_);

         var resultRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
         resultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         resultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

         testMark_.Width = 12;
         testMark_.Height = 12;
         testMark_.Margin = new Thickness(0, 4, 8, 0);
         testMark_.VerticalAlignment = VerticalAlignment.Top;
         testMark_.Visibility = Visibility.Collapsed;
         resultRow.Children.Add(testMark_);

         testText_.FontSize = Typography.Label;
         testText_.TextWrapping = TextWrapping.Wrap;
         System.Windows.Automation.AutomationProperties.SetAutomationId(testText_, "ldap-test-result");
         System.Windows.Automation.AutomationProperties.SetLiveSetting(testText_, System.Windows.Automation.AutomationLiveSetting.Polite);
         Grid.SetColumn(testText_, 1);
         resultRow.Children.Add(testText_);

         content.Children.Add(resultRow);

         return card;
      }

      /// <summary>Same construction as SpamOverviewView.PageLink: the way out to the
      /// page that owns the neighbouring half of this subject.</summary>
      private static FrameworkElement PageLink_(string page, string caption, string accessibleName)
      {
         var button = new Wpf.Ui.Controls.Button
         {
            Content = caption,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            FontSize = Typography.Caption,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = accessibleName
         };
         System.Windows.Automation.AutomationProperties.SetName(button, accessibleName);
         System.Windows.Automation.AutomationProperties.SetAutomationId(button, "ldap-open-" + page);
         button.Click += (s, e) => (Application.Current?.MainWindow as MainWindow)?.NavigateTo(page);
         return button;
      }

      // =========================================================================
      // Reading and writing the [LDAP] section
      // =========================================================================
      //
      // IniFeatureStore writes the [Settings] section - its section name is a
      // private constant - so it cannot be used for these values: every key would
      // silently land where the server never looks for it. The store still answers
      // "where is the INI"; the section-aware reads and writes live here.

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern int GetPrivateProfileString(string section, string key, string defaultValue,
         StringBuilder result, int size, string filePath);

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private string IniRead_(string key, string fallback)
      {
         if (!store_.IsAvailable)
            return fallback;

         // 4096 characters, the same buffer size LdapSettings::ReadString_ uses, so
         // this page sees exactly the value (and exactly the truncation) the server
         // sees.
         var buffer = new StringBuilder(4096);
         GetPrivateProfileString(IniSection, key, fallback, buffer, buffer.Capacity, store_.IniPath);
         return buffer.ToString().Trim();
      }

      private int IniReadInt_(string key, int fallback)
      {
         string raw = IniRead_(key, "");
         if (raw.Length == 0)
            return fallback;

         // The server reads these with GetPrivateProfileInt, which reads a value
         // that is not a number as 0 rather than as the default. Mirrored here so
         // the page shows the configuration the server is actually running.
         return int.TryParse(raw, out int value) ? value : 0;
      }

      private void IniWrite_(string key, string value)
      {
         if (!store_.IsAvailable)
            throw new InvalidOperationException("hMailServer.INI was not found on this machine.");

         if (!WritePrivateProfileString(IniSection, key, value, store_.IniPath))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
               "Could not write " + key + " to " + store_.IniPath);
      }

      private void Load_()
      {
         loading_ = true;

         if (!store_.IsAvailable)
         {
            saveButton_.IsEnabled = false;
            footerStatus_.Text = "hMailServer.INI was not found on this machine. These settings can only be edited "
                                 + "on the server itself; the connection test still works with the values typed here.";
         }
         else
         {
            saveButton_.IsEnabled = true;
            footerStatus_.Text = "Editing " + store_.IniPath + " - saved changes are live within two seconds; no service restart.";
         }

         enabled_.IsChecked = IniReadInt_("Enabled", 0) != 0;
         server_.Text = IniRead_("Server", "");

         int port = IniReadInt_("Port", 0);
         port_.Text = port > 0 && port <= 65535 ? port.ToString() : "";

         // The server maps 0 and 1 to themselves and everything else - including a
         // typo read as 0 by TryParse above - to LDAPS. 0 has to survive as 0 here,
         // because showing "unprotected" as "LDAPS" would hide the one state that
         // matters most.
         int security = IniReadInt_("Security", 2);
         SelectByTag_(security_, security == 0 || security == 1 ? security : 2);

         verifyCertificate_.IsChecked = IniReadInt_("VerifyCertificate", 1) != 0;
         allowUnprotected_.IsChecked = IniReadInt_("AllowUnprotectedPassword", 0) != 0;
         SelectByTag_(bindMethod_, IniReadInt_("BindMethod", 0) == 1 ? 1 : 0);

         searchBase_.Text = IniRead_("SearchBase", "");
         userSearchFilter_.Text = IniRead_("UserSearchFilter", "");
         userDnTemplate_.Text = IniRead_("UserDnTemplate", "");
         serviceUsername_.Text = IniRead_("ServiceUsername", "");

         storedServicePassword_ = IniRead_("ServicePassword", "").Length > 0;
         servicePassword_.Password = "";
         servicePassword_.PlaceholderText = storedServicePassword_
            ? "A password is stored - leave blank to keep it"
            : "Enter the service account password";
         clearServicePassword_.IsChecked = false;

         int timeoutSeconds = IniReadInt_("TimeoutSeconds", 10);
         if (timeoutSeconds < 1)
            timeoutSeconds = 10;
         if (timeoutSeconds > 90)
            timeoutSeconds = 90;
         timeout_.Text = timeoutSeconds.ToString();

         fallback_.IsChecked = IniReadInt_("FallbackToWindowsLogon", 0) != 0;

         string serviceDomain = IniRead_("ServiceDomain", "");
         serviceDomainNote_.Text = "ServiceDomain "
            + (serviceDomain.Length > 0 ? "is set to \"" + serviceDomain + "\" in the INI" : "is not set")
            + ", and is shown here rather than offered for editing because the server reads the key and then never "
            + "uses it: the service credential binds with ServiceUsername and ServicePassword alone. Put the domain "
            + "in the user name instead (DOMAIN\\name or a UPN).";

         loading_ = false;
         RefreshState_();
      }

      private void Save_()
      {
         if (!NumericField.TryValidate(port_.Text, "Port", 0, 65535, out int port, out bool hasPort, out string portError))
         {
            MessageBox.Show(portError, "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
         }

         if (!NumericField.TryValidate(timeout_.Text, "Timeout in seconds", 1, 90, out int timeoutSeconds, out bool hasTimeout, out string timeoutError))
         {
            MessageBox.Show(timeoutError, "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
         }

         string newServicePassword = servicePassword_.Password;
         if (newServicePassword.Length > 0 && clearServicePassword_.IsChecked == true)
         {
            MessageBox.Show("A new service account password has been typed AND \"remove the stored password\" is "
               + "ticked. Untick one of them - guessing which was meant is not this page's decision to make.",
               "Control Panel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
         }

         try
         {
            IniWrite_("Enabled", enabled_.IsChecked == true ? "1" : "0");
            IniWrite_("Server", server_.Text.Trim());
            IniWrite_("Port", hasPort ? port.ToString() : "0");
            IniWrite_("Security", SelectedTag_(security_, 2).ToString());
            IniWrite_("VerifyCertificate", verifyCertificate_.IsChecked == true ? "1" : "0");
            IniWrite_("AllowUnprotectedPassword", allowUnprotected_.IsChecked == true ? "1" : "0");
            IniWrite_("BindMethod", SelectedTag_(bindMethod_, 0).ToString());
            IniWrite_("SearchBase", searchBase_.Text.Trim());
            IniWrite_("UserSearchFilter", userSearchFilter_.Text.Trim());
            IniWrite_("UserDnTemplate", userDnTemplate_.Text.Trim());
            IniWrite_("ServiceUsername", serviceUsername_.Text.Trim());

            // Blank keeps the stored secret; the checkbox is the only way to remove
            // it. ServiceDomain is deliberately never written - see the note beside
            // it.
            if (clearServicePassword_.IsChecked == true)
               IniWrite_("ServicePassword", "");
            else if (newServicePassword.Length > 0)
               IniWrite_("ServicePassword", newServicePassword);

            IniWrite_("TimeoutSeconds", (hasTimeout ? timeoutSeconds : 10).ToString());
            IniWrite_("FallbackToWindowsLogon", fallback_.IsChecked == true ? "1" : "0");
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save: " + ex.Message, "Control Panel", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
         }

         // No restart offer, and that is the point of this section's design: the
         // server keys its cache on the INI's write time and re-reads within two
         // seconds, precisely so a broken directory configuration can be fixed
         // while the administrator is still looking at the screen.
         footerStatus_.Text = "Saved " + DateTime.Now.ToLongTimeString()
            + " - the server picks this up within two seconds. No service restart is needed.";

         storedServicePassword_ = IniRead_("ServicePassword", "").Length > 0;
         servicePassword_.Password = "";
         servicePassword_.PlaceholderText = storedServicePassword_
            ? "A password is stored - leave blank to keep it"
            : "Enter the service account password";
         clearServicePassword_.IsChecked = false;

         RefreshState_();
      }

      // =========================================================================
      // The state card - IsComplete() and the refusal gates, mirrored
      // =========================================================================

      private void WireLiveState_()
      {
         RoutedEventHandler refresh = (s, e) => RefreshState_();
         enabled_.Checked += refresh;
         enabled_.Unchecked += refresh;
         verifyCertificate_.Checked += refresh;
         verifyCertificate_.Unchecked += refresh;
         allowUnprotected_.Checked += refresh;
         allowUnprotected_.Unchecked += refresh;
         fallback_.Checked += refresh;
         fallback_.Unchecked += refresh;

         server_.TextChanged += (s, e) => RefreshState_();
         port_.TextChanged += (s, e) => RefreshState_();
         searchBase_.TextChanged += (s, e) => RefreshState_();
         userSearchFilter_.TextChanged += (s, e) => RefreshState_();
         userDnTemplate_.TextChanged += (s, e) => RefreshState_();
         serviceUsername_.TextChanged += (s, e) => RefreshState_();
         timeout_.TextChanged += (s, e) => RefreshState_();

         security_.SelectionChanged += (s, e) => RefreshState_();
         bindMethod_.SelectionChanged += (s, e) => RefreshState_();
      }

      /// <summary>A snapshot of the editors in the server's own terms, with the
      /// derivations (EffectivePort, PasswordIsProtected, UsesSearch, IsComplete)
      /// ported line for line from LdapSettings.cpp.</summary>
      private sealed class Snapshot
      {
         public bool Enabled;
         public string Server = "";
         public int Port;
         public int Security = 2;           // 0 plain, 1 StartTLS, 2 LDAPS
         public bool VerifyCertificate = true;
         public bool AllowUnprotectedPassword;
         public int BindMethod;             // 0 simple, 1 negotiate
         public string SearchBase = "";
         public string UserSearchFilter = "";
         public string UserDnTemplate = "";
         public string ServiceUsername = "";
         public string ServicePassword = "";
         public int TimeoutSeconds = 10;

         public int EffectivePort()
            => Port > 0 && Port <= 65535 ? Port : (Security == 2 ? 636 : 389);

         public bool PasswordIsProtected()
            => Security != 0 || BindMethod == 1;

         public bool UsesSearch()
            => BindMethod != 1 && UserDnTemplate.Length == 0;

         public string EffectiveFilter()
            => UserSearchFilter.Length == 0 ? DefaultUserSearchFilter : UserSearchFilter;

         public string TransportName()
            => Security == 2 ? "LDAPS" : Security == 1 ? "StartTLS" : "unprotected LDAP";

         public string Target()
            => Server + ":" + EffectivePort();

         /// <summary>Mirrors LdapConfiguration::IsComplete, including its wording,
         /// so the GUI names exactly what the server's own error 5922 would name.</summary>
         public bool IsComplete(out string missing)
         {
            missing = "";

            if (Server.Length == 0)
               missing = "Server";

            if (UsesSearch() && SearchBase.Length == 0)
            {
               if (missing.Length > 0)
                  missing += ", ";
               missing += "SearchBase (or UserDnTemplate, to bind directly without searching)";
            }

            return missing.Length == 0;
         }
      }

      private Snapshot CollectSnapshot_()
      {
         var snapshot = new Snapshot
         {
            Enabled = enabled_.IsChecked == true,
            Server = server_.Text.Trim(),
            Security = SelectedTag_(security_, 2),
            VerifyCertificate = verifyCertificate_.IsChecked == true,
            AllowUnprotectedPassword = allowUnprotected_.IsChecked == true,
            BindMethod = SelectedTag_(bindMethod_, 0),
            SearchBase = searchBase_.Text.Trim(),
            UserSearchFilter = userSearchFilter_.Text.Trim(),
            UserDnTemplate = userDnTemplate_.Text.Trim(),
            ServiceUsername = serviceUsername_.Text.Trim()
         };

         if (int.TryParse(port_.Text.Trim(), out int port))
            snapshot.Port = port;

         // Same clamps as the server: below 1 reads as the default, above 90 as 90.
         if (int.TryParse(timeout_.Text.Trim(), out int timeoutSeconds))
         {
            if (timeoutSeconds < 1)
               timeoutSeconds = 10;
            if (timeoutSeconds > 90)
               timeoutSeconds = 90;
            snapshot.TimeoutSeconds = timeoutSeconds;
         }

         // The typed password wins; otherwise the stored one, read at use and never
         // displayed. The test needs it to exercise the service bind the way the
         // server would.
         string typed = servicePassword_.Password;
         snapshot.ServicePassword = typed.Length > 0 ? typed : IniRead_("ServicePassword", "");

         return snapshot;
      }

      private void RefreshState_()
      {
         if (loading_)
            return;

         Snapshot config = CollectSnapshot_();
         bool complete = config.IsComplete(out string missing);

         StatusLevel level;
         string headline;
         string detail;

         if (!config.Enabled)
         {
            level = StatusLevel.Information;
            headline = "Off. Directory-linked accounts are validated through Windows logon (LogonUser), which only "
                       + "works when this host is joined to the domain - every failure there looks like a wrong password.";
            detail = complete
               ? "The section below is filled in; ticking the switch above would activate it."
               : "Before enabling, the section is also missing: " + missing + ".";
         }
         else if (!complete)
         {
            level = StatusLevel.Warning;
            headline = "Enabled but not configured: " + missing + " is not set.";
            detail = "No LDAP authentication is attempted. Directory-linked accounts keep validating through Windows "
                     + "logon, and the server reports this (error 5922) once per minute so it cannot fail silently.";
         }
         else if (!config.PasswordIsProtected() && !config.AllowUnprotectedPassword)
         {
            level = StatusLevel.Critical;
            headline = "Every LDAP logon will be refused: unprotected LDAP with a simple bind would send passwords "
                       + "in the clear, and AllowUnprotectedPassword is off.";
            detail = "The server fails closed rather than guessing (error 5921): no password is sent and the logon "
                     + "is refused. Fix it by choosing LDAPS or StartTLS, or the Negotiate bind method (which never "
                     + "transmits the password) - or, on a network you control end to end, by explicitly allowing "
                     + "the unprotected password.";
         }
         else if (config.UsesSearch() && config.ServiceUsername.Length == 0)
         {
            level = StatusLevel.Warning;
            headline = "Complete, but the user search will run anonymously - Active Directory refuses anonymous "
                       + "searches by default.";
            detail = "Against " + config.Target() + " over " + config.TransportName() + ". Set ServiceUsername (and "
                     + "its password), or switch to a UserDnTemplate or the Negotiate bind method, neither of which "
                     + "searches at all.";
         }
         else
         {
            level = StatusLevel.Good;
            if (config.BindMethod == 1)
            {
               headline = "Complete. Accounts authenticate with SSPI Negotiate (Kerberos, then NTLM) against "
                          + config.Target() + " - the password never crosses the network, and no search, search "
                          + "base or service account is needed.";
            }
            else if (config.UsesSearch())
            {
               headline = "Complete. The server searches under " + config.SearchBase + " as "
                          + config.ServiceUsername + ", then proves the user's password with a simple bind against "
                          + config.Target() + " over " + config.TransportName() + ".";
            }
            else
            {
               headline = "Complete. The user's DN comes from the template, proved with a simple bind against "
                          + config.Target() + " over " + config.TransportName() + " - no search and no service "
                          + "account needed.";
            }

            detail = config.VerifyCertificate || config.Security == 0
               ? ""
               : "Note: certificate verification is turned off, so the encrypted connection does not prove which "
                 + "directory it reaches. The server logs this once per start.";
         }

         StatusPresentation presentation = StatusSemantics.For(level);
         ShapeMarkVisuals.ApplyMark(stateMark_, presentation.Shape, presentation.BrushKey);

         stateText_.Inlines.Clear();
         stateText_.Inlines.Add(new Run(presentation.SeverityWord + ": ") { FontWeight = FontWeights.SemiBold });
         stateText_.Inlines.Add(new Run(headline));

         // Word first in the accessible name, so a screen reader hears the severity
         // even though the visible badge carries it as shape and colour.
         System.Windows.Automation.AutomationProperties.SetName(stateText_,
            presentation.SeverityWord + ": " + headline + (detail.Length > 0 ? " " + detail : ""));

         stateDetail_.Text = detail;
         stateDetail_.Visibility = detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
      }

      // =========================================================================
      // The connection test
      // =========================================================================

      private void ShowTestResult_(StatusLevel level, string text)
      {
         StatusPresentation presentation = StatusSemantics.For(level);

         testMark_.Visibility = Visibility.Visible;
         ShapeMarkVisuals.ApplyMark(testMark_, presentation.Shape, presentation.BrushKey);

         testText_.Inlines.Clear();
         testText_.Inlines.Add(new Run(presentation.SeverityWord + ": ") { FontWeight = FontWeights.SemiBold });
         testText_.Inlines.Add(new Run(text));

         System.Windows.Automation.AutomationProperties.SetName(testText_, presentation.SeverityWord + ": " + text);
      }

      private async Task RunTest_()
      {
         Snapshot config = CollectSnapshot_();
         string username = testUsername_.Text.Trim();
         string domain = testDomain_.Text.Trim();
         string password = testPassword_.Password;

         if (config.Server.Length == 0)
         {
            ShowTestResult_(StatusLevel.Warning, "Type the directory server host name first.");
            return;
         }

         if (password.Length > 0 && username.Length == 0)
         {
            ShowTestResult_(StatusLevel.Warning, "Type the user name the password belongs to.");
            return;
         }

         // The same gate the server applies before it opens a connection (its error
         // 5921): a configuration that cannot be satisfied without a cleartext
         // password is refused outright, and this test must not do from the
         // administrator's desktop what the server refuses to do from the service.
         if (!config.PasswordIsProtected() && !config.AllowUnprotectedPassword)
         {
            ShowTestResult_(StatusLevel.Critical,
               "Not tested: this configuration would send a password over unprotected LDAP with a simple bind, and "
               + "AllowUnprotectedPassword is off - so the server refuses every logon under it, and this test "
               + "refuses to transmit anything for the same reason. Choose LDAPS or StartTLS, or the Negotiate bind "
               + "method, or explicitly allow the unprotected password.");
            return;
         }

         testButton_.IsEnabled = false;
         ShowTestResult_(StatusLevel.Information, "Testing against " + config.Target() + " over " + config.TransportName() + "...");

         try
         {
            LdapProbeResult result = await Task.Run(() => LdapProbe.Run(config, username, domain, password));
            ShowTestResult_(result.Level, result.Text);
         }
         catch (Exception ex)
         {
            // Mirror of the authenticator's outer barrier: an unexpected failure is
            // an infrastructure answer, never a crash of the page.
            ShowTestResult_(StatusLevel.Critical, "The test itself failed unexpectedly: " + ex.Message);
         }
         finally
         {
            testButton_.IsEnabled = true;
         }
      }

      private sealed class LdapProbeResult
      {
         public StatusLevel Level;
         public string Text;

         public static LdapProbeResult Make(StatusLevel level, string text)
            => new LdapProbeResult { Level = level, Text = text };
      }

      /// <summary>
      /// The test bind, over wldap32.dll - the same client library, the same
      /// options in the same order, and the same outcome classification as the
      /// server's LdapClient, so what this test reports predicts what the service
      /// will do. wldap32 rather than System.DirectoryServices.Protocols because
      /// the latter is a NuGet package this project does not reference, and because
      /// matching the server's library means matching its certificate validation,
      /// its referral policy and its error codes exactly.
      ///
      /// The one distinction this whole feature exists to preserve is carried
      /// through here too: an INFRASTRUCTURE failure (server unreachable,
      /// certificate not trusted, service credential refused, search base wrong)
      /// is reported as Critical and explicitly "unrelated to any password", while
      /// a CREDENTIAL rejection is reported as Information with the directory's
      /// own reason - because "the directory is unreachable" and "that password is
      /// wrong" are completely different problems, and the LogonUser path this
      /// replaces cannot tell them apart at all.
      /// </summary>
      private static class LdapProbe
      {
         // winldap.h option and result constants, by value.
         private const int LDAP_OPT_TIMELIMIT = 0x04;
         private const int LDAP_OPT_REFERRALS = 0x08;
         private const int LDAP_OPT_SSL = 0x0A;
         private const int LDAP_OPT_PROTOCOL_VERSION = 0x11;
         private const int LDAP_OPT_SERVER_CERTIFICATE = 0x81;
         private const int LDAP_VERSION3 = 3;
         private const uint LDAP_SCOPE_SUBTREE = 2;
         private const uint LDAP_MSG_ALL = 1;
         private const uint LDAP_AUTH_NEGOTIATE = 0x0486;
         private const uint SEC_WINNT_AUTH_IDENTITY_UNICODE = 0x2;

         private const uint LDAP_SUCCESS = 0;
         private const uint LDAP_SIZELIMIT_EXCEEDED = 4;
         private const uint LDAP_STRONG_AUTH_REQUIRED = 8;
         private const uint LDAP_NO_SUCH_OBJECT = 32;
         private const uint LDAP_INAPPROPRIATE_AUTH = 48;
         private const uint LDAP_INVALID_CREDENTIALS = 49;
         private const uint LDAP_INSUFFICIENT_RIGHTS = 50;
         private const uint LDAP_UNWILLING_TO_PERFORM = 53;
         private const uint LDAP_SERVER_DOWN = 81;
         private const uint LDAP_LOCAL_ERROR = 82;
         private const uint LDAP_TIMEOUT = 85;
         private const uint LDAP_FILTER_ERROR = 87;

         [StructLayout(LayoutKind.Sequential)]
         private struct LDAP_TIMEVAL
         {
            public int tv_sec;
            public int tv_usec;
         }

         [StructLayout(LayoutKind.Sequential)]
         private struct SEC_WINNT_AUTH_IDENTITY_W
         {
            public IntPtr User;
            public uint UserLength;
            public IntPtr Domain;
            public uint DomainLength;
            public IntPtr Password;
            public uint PasswordLength;
            public uint Flags;
         }

         // VERIFYSERVERCERT: BOOLEAN __cdecl (PLDAP, PCCERT_CONTEXT*). Same null
         // checks as the server's AcceptAnyServerCertificate_, and the delegate is
         // a static field so the garbage collector can never move the callback out
         // from under a handshake in progress.
         [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
         [return: MarshalAs(UnmanagedType.U1)]
         private delegate bool VerifyServerCertificate(IntPtr connection, IntPtr serverCertificate);

         private static readonly VerifyServerCertificate AcceptAnyCertificate_ = (connection, certificate) =>
            connection != IntPtr.Zero && certificate != IntPtr.Zero && Marshal.ReadIntPtr(certificate) != IntPtr.Zero;

         private static readonly IntPtr AcceptAnyCertificatePointer_ =
            Marshal.GetFunctionPointerForDelegate(AcceptAnyCertificate_);

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern IntPtr ldap_initW(string hostName, uint portNumber);

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern IntPtr ldap_sslinitW(string hostName, uint portNumber, int secure);

         [DllImport("wldap32.dll")]
         private static extern uint ldap_set_option(IntPtr session, int option, ref int value);

         [DllImport("wldap32.dll")]
         private static extern uint ldap_set_option(IntPtr session, int option, IntPtr value);

         [DllImport("wldap32.dll", EntryPoint = "ldap_set_option")]
         private static extern uint ldap_set_option_ptr(IntPtr session, int option, ref IntPtr value);

         [DllImport("wldap32.dll")]
         private static extern uint ldap_connect(IntPtr session, ref LDAP_TIMEVAL timeout);

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern uint ldap_start_tls_sW(IntPtr session, out uint serverReturnValue, out IntPtr result,
            IntPtr serverControls, IntPtr clientControls);

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern uint ldap_simple_bindW(IntPtr session, string dn, IntPtr password);

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern uint ldap_bind_sW(IntPtr session, string dn, ref SEC_WINNT_AUTH_IDENTITY_W credentials, uint method);

         [DllImport("wldap32.dll")]
         private static extern uint ldap_result(IntPtr session, uint messageId, uint all, ref LDAP_TIMEVAL timeout, out IntPtr result);

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern uint ldap_parse_resultW(IntPtr session, IntPtr resultMessage, out uint returnCode,
            IntPtr matchedDns, out IntPtr errorMessage, IntPtr referrals, IntPtr serverControls, byte freeIt);

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern uint ldap_search_ext_sW(IntPtr session, string baseDn, uint scope, string filter,
            IntPtr attributes, uint attributesOnly, IntPtr serverControls, IntPtr clientControls,
            ref LDAP_TIMEVAL timeout, uint sizeLimit, out IntPtr result);

         [DllImport("wldap32.dll")]
         private static extern uint ldap_count_entries(IntPtr session, IntPtr result);

         [DllImport("wldap32.dll")]
         private static extern IntPtr ldap_first_entry(IntPtr session, IntPtr result);

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern IntPtr ldap_get_dnW(IntPtr session, IntPtr entry);

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern void ldap_memfreeW(IntPtr value);

         [DllImport("wldap32.dll")]
         private static extern uint ldap_msgfree(IntPtr result);

         [DllImport("wldap32.dll")]
         private static extern uint ldap_abandon(IntPtr session, uint messageId);

         [DllImport("wldap32.dll")]
         private static extern uint ldap_unbind_s(IntPtr session);

         [DllImport("wldap32.dll")]
         private static extern uint LdapGetLastError();

         [DllImport("wldap32.dll", CharSet = CharSet.Unicode)]
         private static extern IntPtr ldap_err2stringW(uint error);

         private enum Outcome
         {
            Success,
            Rejected,
            Unavailable
         }

         /// <summary>One LDAP session, mirroring LdapClient: at most one connection,
         /// closed on dispose so no early return can leak a socket to the directory.</summary>
         private sealed class Session : IDisposable
         {
            private IntPtr session_;
            private readonly int timeoutSeconds_;
            private bool transportProtected_;
            private readonly bool unprotectedPasswordAllowed_;

            public uint LastError { get; private set; }
            public string LastDiagnostic { get; private set; } = "";

            public Session(Snapshot config)
            {
               timeoutSeconds_ = config.TimeoutSeconds;
               unprotectedPasswordAllowed_ = config.AllowUnprotectedPassword;
            }

            public Outcome Connect(Snapshot config)
            {
               bool useLdaps = config.Security == 2;

               session_ = useLdaps
                  ? ldap_sslinitW(config.Server, (uint) config.EffectivePort(), 1)
                  : ldap_initW(config.Server, (uint) config.EffectivePort());

               if (session_ == IntPtr.Zero)
                  return Record_(LdapGetLastError());

               // The same options, for the same reasons, in the same order as
               // LdapClient::Connect: v3 because wldap32 defaults to v2, which a
               // modern directory refuses; referrals off because a chased referral
               // replays the bind - password included - against a host chosen by
               // whatever answered.
               int version = LDAP_VERSION3;
               ldap_set_option(session_, LDAP_OPT_PROTOCOL_VERSION, ref version);
               ldap_set_option(session_, LDAP_OPT_REFERRALS, IntPtr.Zero);

               if (useLdaps)
                  ldap_set_option(session_, LDAP_OPT_SSL, (IntPtr) 1);

               int timeLimit = timeoutSeconds_;
               ldap_set_option(session_, LDAP_OPT_TIMELIMIT, ref timeLimit);

               if (!config.VerifyCertificate && config.Security != 0)
               {
                  IntPtr callback = AcceptAnyCertificatePointer_;
                  ldap_set_option_ptr(session_, LDAP_OPT_SERVER_CERTIFICATE, ref callback);
               }

               var timeout = new LDAP_TIMEVAL { tv_sec = timeoutSeconds_, tv_usec = 0 };
               uint connectResult = ldap_connect(session_, ref timeout);

               if (connectResult != LDAP_SUCCESS)
               {
                  Abandon_();
                  return Record_(connectResult);
               }

               if (useLdaps)
               {
                  transportProtected_ = true;
               }
               else if (config.Security == 1)
               {
                  uint serverReturnValue;
                  IntPtr startTlsResult;
                  uint startTls = ldap_start_tls_sW(session_, out serverReturnValue, out startTlsResult, IntPtr.Zero, IntPtr.Zero);

                  if (startTlsResult != IntPtr.Zero)
                     ldap_msgfree(startTlsResult);

                  if (startTls != LDAP_SUCCESS)
                  {
                     // The StartTLS downgrade case, and a hard failure for the same
                     // reason as the server: carrying on would use exactly the
                     // unprotected connection the upgrade was asked to remove.
                     Abandon_();
                     return Record_(serverReturnValue != LDAP_SUCCESS ? serverReturnValue : startTls);
                  }

                  transportProtected_ = true;
               }

               return Record_(LDAP_SUCCESS);
            }

            public Outcome BindAnonymous()
            {
               if (session_ == IntPtr.Zero)
                  return Record_(LDAP_LOCAL_ERROR);

               uint messageId = ldap_simple_bindW(session_, null, IntPtr.Zero);
               if (messageId == uint.MaxValue)
                  return Record_(LdapGetLastError());

               return AwaitResult_(messageId);
            }

            public Outcome BindSimple(string dn, string password)
            {
               if (session_ == IntPtr.Zero)
                  return Record_(LDAP_LOCAL_ERROR);

               if (string.IsNullOrEmpty(dn) || string.IsNullOrEmpty(password))
               {
                  // Same guard as the server's BindSimple: a simple bind with an
                  // empty password is an RFC 4513 "unauthenticated bind" that many
                  // directories answer with success while treating the session as
                  // anonymous - success there is not proof of anything.
                  return Record_(LDAP_INVALID_CREDENTIALS);
               }

               if (!transportProtected_ && !unprotectedPasswordAllowed_)
               {
                  // Enforced at the session as well as in the caller, exactly as the
                  // server duplicates it: the cost of this check ever being missing
                  // on one path is a cleartext password on the wire.
                  Abandon_();
                  return Record_(LDAP_STRONG_AUTH_REQUIRED);
               }

               IntPtr passwordBuffer = Marshal.StringToHGlobalUni(password);
               uint messageId;
               try
               {
                  messageId = ldap_simple_bindW(session_, dn, passwordBuffer);
               }
               finally
               {
                  // wldap32 copies the credential into its request before returning,
                  // so the native copy can be zeroed immediately - the same single
                  // memset the server judges worth paying.
                  int bytes = (password.Length + 1) * 2;
                  for (int i = 0; i < bytes; i++)
                     Marshal.WriteByte(passwordBuffer, i, 0);
                  Marshal.FreeHGlobal(passwordBuffer);
               }

               if (messageId == uint.MaxValue)
                  return Record_(LdapGetLastError());

               return AwaitResult_(messageId);
            }

            public Outcome BindNegotiate(string username, string domain, string password)
            {
               if (session_ == IntPtr.Zero)
                  return Record_(LDAP_LOCAL_ERROR);

               if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                  return Record_(LDAP_INVALID_CREDENTIALS);

               IntPtr userBuffer = Marshal.StringToHGlobalUni(username);
               IntPtr domainBuffer = Marshal.StringToHGlobalUni(domain ?? "");
               IntPtr passwordBuffer = Marshal.StringToHGlobalUni(password);

               try
               {
                  var identity = new SEC_WINNT_AUTH_IDENTITY_W
                  {
                     User = userBuffer,
                     UserLength = (uint) username.Length,
                     Domain = domainBuffer,
                     DomainLength = (uint) (domain?.Length ?? 0),
                     Password = passwordBuffer,
                     PasswordLength = (uint) password.Length,
                     Flags = SEC_WINNT_AUTH_IDENTITY_UNICODE
                  };

                  // Synchronous, exactly like the server's BindNegotiate, and with
                  // the same stated limitation: the SASL exchange is driven inside
                  // wldap32 with no asynchronous entry point, so a server that
                  // accepts the connection and then stalls mid-exchange is bounded
                  // only by the TCP stack. No diagnostic message is available on
                  // this path either - ldap_bind_s does not hand the result back.
                  uint bindResult = ldap_bind_sW(session_, null, ref identity, LDAP_AUTH_NEGOTIATE);
                  LastDiagnostic = "";
                  return Record_(bindResult);
               }
               finally
               {
                  int bytes = (password.Length + 1) * 2;
                  for (int i = 0; i < bytes; i++)
                     Marshal.WriteByte(passwordBuffer, i, 0);
                  Marshal.FreeHGlobal(passwordBuffer);
                  Marshal.FreeHGlobal(domainBuffer);
                  Marshal.FreeHGlobal(userBuffer);
               }
            }

            /// <summary>The user search, with the server's size limit of two - the
            /// extra entry is how "exactly one match" is distinguished from "the
            /// first of several", and several must authenticate nobody.</summary>
            public Outcome Search(string searchBase, string filter, out string dn, out int matchCount)
            {
               dn = null;
               matchCount = 0;

               if (session_ == IntPtr.Zero)
                  return Record_(LDAP_LOCAL_ERROR);

               var timeout = new LDAP_TIMEVAL { tv_sec = timeoutSeconds_, tv_usec = 0 };
               IntPtr searchResult;

               uint searchStatus = ldap_search_ext_sW(session_, searchBase, LDAP_SCOPE_SUBTREE, filter,
                  IntPtr.Zero, 1 /* attribute types only - the DN is all this needs */,
                  IntPtr.Zero, IntPtr.Zero, ref timeout, 2, out searchResult);

               // LDAP_SIZELIMIT_EXCEEDED is the server saying "more than the two you
               // asked for" - a successful search that found too many, not a failure.
               if (searchStatus != LDAP_SUCCESS && searchStatus != LDAP_SIZELIMIT_EXCEEDED)
               {
                  if (searchResult != IntPtr.Zero)
                     ldap_msgfree(searchResult);
                  return Record_(searchStatus);
               }

               if (searchResult == IntPtr.Zero)
                  return Record_(LDAP_SUCCESS);

               matchCount = (int) ldap_count_entries(session_, searchResult);

               if (searchStatus == LDAP_SIZELIMIT_EXCEEDED && matchCount < 2)
                  matchCount = 2;

               if (matchCount == 1)
               {
                  IntPtr entry = ldap_first_entry(session_, searchResult);
                  if (entry != IntPtr.Zero)
                  {
                     IntPtr entryDn = ldap_get_dnW(session_, entry);
                     if (entryDn != IntPtr.Zero)
                     {
                        dn = Marshal.PtrToStringUni(entryDn);
                        ldap_memfreeW(entryDn);
                     }
                  }

                  if (string.IsNullOrEmpty(dn))
                  {
                     ldap_msgfree(searchResult);
                     matchCount = 0;
                     return Record_(LDAP_LOCAL_ERROR);
                  }
               }

               ldap_msgfree(searchResult);
               return Record_(LDAP_SUCCESS);
            }

            private Outcome AwaitResult_(uint messageId)
            {
               var timeout = new LDAP_TIMEVAL { tv_sec = timeoutSeconds_, tv_usec = 0 };
               IntPtr result;

               uint resultType = ldap_result(session_, messageId, LDAP_MSG_ALL, ref timeout, out result);

               if (resultType == 0)
               {
                  // Timed out: abandon the operation and drop the whole session,
                  // because nobody knows which identity a half-bound connection
                  // carries.
                  ldap_abandon(session_, messageId);
                  Abandon_();
                  return Record_(LDAP_TIMEOUT);
               }

               if (resultType == uint.MaxValue)
               {
                  uint sessionError = LdapGetLastError();
                  Abandon_();
                  return Record_(sessionError != LDAP_SUCCESS ? sessionError : LDAP_LOCAL_ERROR);
               }

               uint returnCode;
               IntPtr diagnostic;
               uint parseResult = ldap_parse_resultW(session_, result, out returnCode, IntPtr.Zero,
                  out diagnostic, IntPtr.Zero, IntPtr.Zero, 1);

               LastDiagnostic = "";
               if (diagnostic != IntPtr.Zero)
               {
                  LastDiagnostic = Marshal.PtrToStringUni(diagnostic) ?? "";
                  ldap_memfreeW(diagnostic);
               }

               if (parseResult != LDAP_SUCCESS)
                  return Record_(parseResult);

               return Record_(returnCode);
            }

            private Outcome Record_(uint error)
            {
               LastError = error;

               if (error == LDAP_SUCCESS)
                  return Outcome.Success;

               return IsCredentialRejection(error) ? Outcome.Rejected : Outcome.Unavailable;
            }

            private void Abandon_()
            {
               if (session_ == IntPtr.Zero)
                  return;

               ldap_unbind_s(session_);
               session_ = IntPtr.Zero;
               transportProtected_ = false;
            }

            public void Dispose() => Abandon_();
         }

         /// <summary>The classification the whole feature exists to preserve, ported
         /// from LdapClient::IsCredentialRejection: 49 means "wrong password" (with
         /// the real reason hidden in the diagnostic), 32 means the entry is gone,
         /// and EVERYTHING else - strongAuthRequired, insufficient rights, server
         /// down, timeout - is the administrator's problem, not the user's.</summary>
         private static bool IsCredentialRejection(uint error)
            => error == LDAP_INVALID_CREDENTIALS || error == LDAP_NO_SUCH_OBJECT;

         /// <summary>LdapClient::DescribeLastError, ported - including the spelled-out
         /// strongAuthRequired case, which is the single most likely first failure of
         /// a new configuration against Windows Server 2019 or later.</summary>
         private static string DescribeError(uint error)
         {
            switch (error)
            {
               case LDAP_SUCCESS:
                  return "no error";
               case LDAP_STRONG_AUTH_REQUIRED:
                  return "the directory refuses simple binds on an unprotected connection (strongAuthRequired). "
                         + "Use LDAPS or StartTLS, or the Negotiate bind method, which satisfies the requirement "
                         + "without TLS";
               case LDAP_SERVER_DOWN:
               case 91:   // LDAP_CONNECT_ERROR
                  return "the directory server could not be contacted";
               case LDAP_TIMEOUT:
                  return "the directory server did not answer within the configured timeout";
               case LDAP_FILTER_ERROR:
                  return "the configured UserSearchFilter is not a valid LDAP filter";
               case LDAP_INSUFFICIENT_RIGHTS:
                  return "the service credential is not permitted to read the directory";
               case LDAP_INAPPROPRIATE_AUTH:
                  return "the directory refused the configured bind method for this entry";
               case LDAP_UNWILLING_TO_PERFORM:
                  return "the directory refused to perform the operation";
               case LDAP_INVALID_CREDENTIALS:
                  return "the credentials were rejected";
               case LDAP_NO_SUCH_OBJECT:
                  return "the entry does not exist (check SearchBase, or UserDnTemplate)";
            }

            IntPtr text = ldap_err2stringW(error);
            return text == IntPtr.Zero ? "an unrecognised LDAP error" : (Marshal.PtrToStringUni(text) ?? "an unrecognised LDAP error");
         }

         /// <summary>LdapClient::DescribeActiveDirectorySubStatus, ported. The
         /// hexadecimal value after "data " in AD's diagnostic is the only place the
         /// real reason for a rejected bind appears - it is the difference between
         /// "the user typed it wrong" and "the account is locked out and no amount
         /// of retyping will help".</summary>
         private static string DescribeAdSubStatus(string diagnostic)
         {
            if (string.IsNullOrEmpty(diagnostic))
               return "";

            int position = diagnostic.IndexOf("data ", StringComparison.Ordinal);
            if (position < 0)
               return "";

            string code = diagnostic.Substring(position + 5);
            int end = code.IndexOf(',');
            if (end >= 0)
               code = code.Substring(0, end);
            code = code.Trim().ToLowerInvariant();

            switch (code)
            {
               case "525": return "the user does not exist in the directory";
               case "52e": return "the password is not correct";
               case "530": return "logon is not permitted at this time of day";
               case "531": return "logon from this computer is not permitted";
               case "532": return "the password has expired";
               case "533": return "the account is disabled";
               case "568": return "too many context IDs (the directory is out of resources)";
               case "701": return "the account has expired";
               case "773": return "the password must be changed before the next logon";
               case "775": return "the account is locked out";
               default: return "";
            }
         }

         /// <summary>RFC 4515 section 3 escaping, ported from LdapClient. A security
         /// boundary, not tidiness: an unescaped username can rewrite the filter's
         /// boolean structure.</summary>
         private static string EscapeFilterValue(string value)
         {
            var escaped = new StringBuilder(value.Length);
            foreach (char c in value)
            {
               switch (c)
               {
                  case '\\': escaped.Append("\\5c"); break;
                  case '(': escaped.Append("\\28"); break;
                  case ')': escaped.Append("\\29"); break;
                  case '*': escaped.Append("\\2a"); break;
                  case '\0': escaped.Append("\\00"); break;
                  default: escaped.Append(c); break;
               }
            }
            return escaped.ToString();
         }

         /// <summary>RFC 4514 section 2.4 escaping, ported from LdapClient -
         /// including its deliberate choice NOT to escape space and '#', because a
         /// DN template is a UPN at least as often as a literal DN and an escaped
         /// space breaks the more common configuration.</summary>
         private static string EscapeDnValue(string value)
         {
            var escaped = new StringBuilder(value.Length);
            foreach (char c in value)
            {
               switch (c)
               {
                  case '\\':
                  case ',':
                  case '+':
                  case '"':
                  case '<':
                  case '>':
                  case ';':
                  case '=':
                     escaped.Append('\\').Append(c);
                     break;
                  default:
                     escaped.Append(c);
                     break;
               }
            }
            return escaped.ToString();
         }

         /// <summary>%u, %d, %m substitution with the escaped values, in the same
         /// order as LdapClient::ExpandTemplate.</summary>
         private static string ExpandTemplate(string template, string username, string domain, string address, bool forFilter)
         {
            string safeUsername = forFilter ? EscapeFilterValue(username) : EscapeDnValue(username);
            string safeDomain = forFilter ? EscapeFilterValue(domain) : EscapeDnValue(domain);
            string safeAddress = forFilter ? EscapeFilterValue(address) : EscapeDnValue(address);

            return template.Replace("%u", safeUsername).Replace("%d", safeDomain).Replace("%m", safeAddress);
         }

         private static LdapProbeResult Unavailable(Session session, Snapshot config, string stage)
         {
            string text = "Infrastructure failure, unrelated to any password - every account authenticated against "
               + "this directory would be unable to log in. Stage: " + stage + ". Directory: " + config.Target()
               + " over " + config.TransportName() + ". Reason: " + DescribeError(session.LastError)
               + " (LDAP error " + session.LastError + ").";

            if (!string.IsNullOrEmpty(session.LastDiagnostic))
               text += " The directory said: " + session.LastDiagnostic;

            return LdapProbeResult.Make(StatusLevel.Critical, text);
         }

         public static LdapProbeResult Run(Snapshot config, string username, string domain, string password)
         {
            bool testLogon = password.Length > 0;

            // The server would use the account's mail address for %m; the test
            // approximates it from what was typed. Only matters when the filter or
            // template actually uses %m.
            string address = username.Contains('@') ? username
               : domain.Length > 0 ? username + "@" + domain
               : username;

            string userDn = null;
            var proven = new StringBuilder();

            if (config.UsesSearch())
            {
               using var search = new Session(config);

               if (search.Connect(config) != Outcome.Success)
                  return Unavailable(search, config, "connecting in order to search for the user");

               proven.Append("Connected to " + config.Target() + " over " + config.TransportName() + ". ");
               if (!config.VerifyCertificate && config.Security != 0)
                  proven.Append("Certificate validation was DISABLED for this test, matching VerifyCertificate=0. ");

               Outcome serviceBind = config.ServiceUsername.Length == 0
                  ? search.BindAnonymous()
                  : search.BindSimple(config.ServiceUsername, config.ServicePassword);

               if (serviceBind != Outcome.Success)
               {
                  // A rejected service credential is an infrastructure failure, not
                  // a rejected user: the password that was refused belongs to the
                  // configuration. Same mapping as the server.
                  string stage = config.ServiceUsername.Length == 0
                     ? "binding anonymously to search for the user (set ServiceUsername)"
                     : "binding with the configured ServiceUsername";

                  if (config.ServiceUsername.Length > 0 && config.ServicePassword.Length == 0)
                     stage += " - no ServicePassword is stored or typed, and an empty password is refused before it is sent";

                  return Unavailable(search, config, stage);
               }

               proven.Append(config.ServiceUsername.Length == 0
                  ? "Anonymous directory read accepted. "
                  : "Service credential accepted. ");

               if (username.Length == 0)
               {
                  return LdapProbeResult.Make(StatusLevel.Good, proven
                     + "Type a user name to also test the search and, with a password, a real logon.");
               }

               string filter = ExpandTemplate(config.EffectiveFilter(), username, domain, address, forFilter: true);

               string dn;
               int matchCount;
               if (search.Search(config.SearchBase, filter, out dn, out matchCount) != Outcome.Success)
                  return Unavailable(search, config, "searching for the user (filter " + filter + ")");

               if (matchCount == 0)
               {
                  return LdapProbeResult.Make(StatusLevel.Warning, proven
                     + "The search matched nothing (filter " + filter + " under " + config.SearchBase + "). As a "
                     + "logon this is a refusal, not an outage - check the user name against the account's "
                     + "Directory tab, and the filter and search base against the directory.");
               }

               if (matchCount > 1)
               {
                  // Mirror of the server's error 5923: several matches authenticate
                  // nobody, because binding as one of them may prove somebody else's
                  // entry.
                  return LdapProbeResult.Make(StatusLevel.Critical, proven
                     + "The search matched MORE than one directory entry (filter " + filter + ", base "
                     + config.SearchBase + "). The server refuses to authenticate against an ambiguous match - "
                     + "make UserSearchFilter or SearchBase select exactly one entry.");
               }

               userDn = dn;
               proven.Append("The search found exactly one entry: " + userDn + ". ");

               // The search connection is closed before any user bind, so the bind
               // that proves the password never shares a connection with the service
               // credential's identity - same separation as the server.
            }

            if (!testLogon)
            {
               if (config.UsesSearch())
                  return LdapProbeResult.Make(StatusLevel.Good, proven
                     + "No password was typed, so no logon was tested - the infrastructure half all works.");

               // Negotiate or DN-template mode with nothing to prove: the useful
               // test left is the connection itself.
               using var probe = new Session(config);
               if (probe.Connect(config) != Outcome.Success)
                  return Unavailable(probe, config, "connecting to the directory");

               string extra = config.BindMethod == 1
                  ? "Negotiate binds authenticate per user, so type a user name, domain and password to test one."
                  : "Type a user name and password to test a real logon through the DN template.";

               return LdapProbeResult.Make(StatusLevel.Good,
                  "Connected to " + config.Target() + " over " + config.TransportName() + ". "
                  + (!config.VerifyCertificate && config.Security != 0
                     ? "Certificate validation was DISABLED for this test, matching VerifyCertificate=0. " : "")
                  + extra);
            }

            using var user = new Session(config);

            if (user.Connect(config) != Outcome.Success)
               return Unavailable(user, config, "connecting in order to authenticate the user");

            Outcome outcome;
            string boundAs;

            if (config.BindMethod == 1)
            {
               outcome = user.BindNegotiate(username, domain, password);
               boundAs = (domain.Length > 0 ? domain + "\\" : "") + username;
            }
            else
            {
               string dn = userDn ?? ExpandTemplate(config.UserDnTemplate, username, domain, address, forFilter: false);
               outcome = user.BindSimple(dn, password);
               boundAs = dn;
            }

            if (outcome == Outcome.Success)
            {
               return LdapProbeResult.Make(StatusLevel.Good, proven
                  + "The directory ACCEPTED the credentials: bound as " + boundAs + ". This account would log on.");
            }

            if (outcome == Outcome.Rejected)
            {
               // The distinction, delivered: the directory answered, so this is a
               // credentials problem and nothing about the infrastructure needs
               // fixing. Active Directory's real reason lives in the diagnostic's
               // sub-status - on the Negotiate path no diagnostic exists at all,
               // which is a limitation of ldap_bind_s, not of this test.
               string reason = DescribeAdSubStatus(user.LastDiagnostic);
               if (reason.Length == 0)
                  reason = DescribeError(user.LastError);

               return LdapProbeResult.Make(StatusLevel.Information, proven
                  + "The directory answered and REFUSED these credentials: " + reason + " (LDAP error "
                  + user.LastError + "). The connection, transport and configuration are all working - this is a "
                  + "credentials problem, which is exactly what a user typing a wrong password would see.");
            }

            return Unavailable(user, config, "binding as the user");
         }
      }
   }
}
