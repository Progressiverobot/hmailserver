// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal, tabbed editor for one domain: general, limits, signature and DKIM.
   /// Aliases and distribution lists are managed from the Domains page itself.
   ///
   /// On the standard frame: the domain name is the heading, the seven tabs all
   /// take the height the dialog allows so the window never resizes on a click of
   /// the strip, every field is a <see cref="FieldRow"/>, each of the eight
   /// numbers that can be wrong is said on its own field rather than in one line
   /// at the foot of the dialog, and the rotation walkthrough's running commentary
   /// is an <see cref="InlineNotice"/> that carries its level in colour, shape and
   /// word instead of in a held brush. The message boxes that remain are the real
   /// questions - renaming a domain, promoting a key, cancelling a rotation - and
   /// the file dialogs they lead to.
   /// </summary>
   public class DomainDialog : FluentDialogWindow
   {
      /// <summary>The width this dialog asks the frame for.</summary>
      private const double DialogWidth = 640;

      private readonly string domainName_;

      private readonly InlineNotice notice_ = DialogFields.Notice();

      // General
      private readonly CheckBox active_ = new() { Content = L("Domain _enabled") };
      private readonly TextBox name_ = NewInput();
      private FieldRow nameRow_;
      private readonly TextBox postmaster_ = NewInput();
      private readonly TextBox adDomain_ = NewInput();

      // Limits
      private readonly TextBox maxSize_ = NewInput();
      private FieldRow maxSizeRow_;
      private readonly TextBox maxMessageSize_ = NewInput();
      private FieldRow maxMessageSizeRow_;
      private readonly TextBox maxAccountSize_ = NewInput();
      private FieldRow maxAccountSizeRow_;
      private readonly TextBox retentionDays_ = NewInput();
      private FieldRow retentionRow_;
      private readonly CheckBox maxAccountsOn_ = new() { Content = L("_Limit number of accounts") };
      private readonly TextBox maxAccounts_ = NewInput();
      private FieldRow maxAccountsRow_;
      private readonly CheckBox maxAliasesOn_ = new() { Content = L("Limit number of al_iases") };
      private readonly TextBox maxAliases_ = NewInput();
      private FieldRow maxAliasesRow_;
      private readonly CheckBox maxDistsOn_ = new() { Content = L("Limit _number of distribution lists") };
      private readonly TextBox maxDists_ = NewInput();
      private FieldRow maxDistsRow_;
      private readonly CheckBox plusAddressingOn_ = new() { Content = L("_Enable plus addressing") };
      private readonly TextBox plusChar_ = NewInput();
      private readonly CheckBox greylisting_ = new() { Content = L("Enable _greylisting for this domain") };

      // Signature
      private readonly CheckBox signatureOn_ = new() { Content = L("_Add signature to outgoing messages") };
      private readonly ComboBox signatureMethod_ = new();
      private readonly CheckBox signReplies_ = new() { Content = L("Add signature to _replies") };
      private readonly CheckBox signLocal_ = new() { Content = L("Add signature to _local e-mail") };
      private readonly TextBox signaturePlain_ = NewMemo();
      private readonly TextBox signatureHtml_ = NewMemo();

      // Outbound relay for this domain
      // Domain-wide out-of-office. The server sends this only for accounts that
      // have no vacation message of their own; the override below additionally
      // replaces an account's personal text for senders outside this server.
      private readonly CheckBox oooOn_ = new() { Content = L("Send a _domain-wide out-of-office reply") };
      private readonly TextBox oooSubject_ = NewInput();
      private readonly TextBox oooMessage_ = NewMemo();
      private readonly TextBox oooInternalSubject_ = NewInput();
      private readonly TextBox oooInternalMessage_ = NewMemo();
      private readonly CheckBox oooExternalOverride_ = new()
      {
         Content = L("_Outside senders always get the domain's text, even when the account has its own")
      };

      // What this domain does to a message: the external-sender tag, the
      // first-contact note and the disclaimer. See Common/Util/MessageOrigin.h
      // for the rule that decides who is outside.
      private readonly CheckBox tagSubject_ = new() { Content = L("_Tag the subject of mail from outside") };
      private readonly TextBox tagText_ = NewInput();
      private FieldRow tagTextRow_;
      private readonly CheckBox tagHeader_ = new() { Content = L("Add a _header the reader shows as a banner") };
      private readonly CheckBox firstContact_ = new() { Content = L("Tell the reader about a _first contact from outside") };
      private readonly CheckBox disclaimerOn_ = new()
      {
         Content = L("_Append a disclaimer to mail leaving the organisation")
      };
      private readonly TextBox disclaimerPlain_ = NewMemo();
      private readonly TextBox disclaimerHtml_ = NewMemo();

      private readonly TextBox relayHost_ = NewInput();
      private readonly TextBox relayPort_ = NewInput();
      private FieldRow relayPortRow_;
      private readonly CheckBox relayAuthOn_ = new() { Content = L("The relay requires _authentication") };
      private readonly TextBox relayUser_ = NewInput();
      private readonly hMailServer.ControlPanel.Views.PasswordField relayPassword_ = new();
      private readonly ComboBox relaySecurity_ = new();

      // DKIM
      private readonly CheckBox dkimOn_ = new() { Content = L("_Enable DKIM signing") };
      private readonly CheckBox dkimAliases_ = new() { Content = L("Sign aliases _too") };
      private readonly TextBox dkimSelector_ = NewInput();
      private readonly TextBox dkimKeyFile_ = NewInput();
      private readonly ComboBox dkimHeaderCanon_ = new();
      private readonly ComboBox dkimBodyCanon_ = new();
      private readonly ComboBox dkimAlgorithm_ = new();
      private readonly TextBox dkimDns_ = ReadOnlyBox(70);
      private StackPanel dkimDnsPanel_;
      private string dkimDnsValue_ = "";

      // DKIM key rotation. Unlike the rest of this dialog, the staged (secondary)
      // pair is written to the server the moment it changes: a rotation spans
      // hours of DNS propagation, the administrator will close this dialog in
      // between, and a staged pair that lived only in the dialog would be gone
      // when they come back to promote it.
      private readonly TextBox rotSelector_ = NewInput();
      private readonly TextBox rotDnsHost_ = ReadOnlyBox(0);
      private readonly TextBox rotDnsValue_ = ReadOnlyBox(70);
      private TextBlock rotCurrent_;
      private TextBlock rotStagedInfo_;
      private readonly InlineNotice rotStatus_ = DialogFields.Notice();
      private StackPanel rotStartPanel_;
      private StackPanel rotStagedPanel_;
      private Wpf.Ui.Controls.Button rotCheck_;
      private Wpf.Ui.Controls.Button rotPromote_;
      private string rotStagedSelector_ = "";
      private string rotStagedKeyFile_ = "";
      private string rotExpectedTxt_;
      private bool rotCheckPassed_;
      private bool rotChecking_;

      // Names (domain aliases) — embedded list editor
      private CollectionEditorView aliasEditor_;

      public DomainDialog(Window owner, string domainName)
      {
         domainName_ = domainName;

         Owner = owner;
         Title = L("Domain - ") + domainName;

         var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
         tabs.Items.Add(new TabItem { Header = L("General"), Content = BuildGeneral() });
         tabs.Items.Add(new TabItem { Header = L("Names"), Content = BuildNames() });
         tabs.Items.Add(new TabItem { Header = L("Limits"), Content = BuildLimits() });
         tabs.Items.Add(new TabItem { Header = L("Signature"), Content = BuildSignature() });
         tabs.Items.Add(new TabItem { Header = L("Relay"), Content = BuildRelay() });
         tabs.Items.Add(new TabItem { Header = L("Out of office"), Content = BuildOutOfOffice() });
         tabs.Items.Add(new TabItem { Header = L("Tagging and disclaimer"), Content = BuildTransforms() });
         tabs.Items.Add(new TabItem { Header = L("DKIM"), Content = BuildDkim() });
         DialogFields.FillTabs(tabs);

         var body = new StackPanel();
         body.Children.Add(notice_);
         body.Children.Add(tabs);

         var save = new Wpf.Ui.Controls.Button { Content = L("_Save"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => Close();

         UseFrame(domainName, body, save, cancel, width: DialogWidth);
         Loaded += (s, e) => { Load(); aliasEditor_?.OnEnter(); };
      }

      private ScrollViewer BuildGeneral()
      {
         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Check(active_));
         nameRow_ = Label(L("_Domain name (changing it renames the domain and moves every account, alias and list with it)"), name_);
         panel.Children.Add(nameRow_);
         panel.Children.Add(Label(L("_Postmaster address (mail to unknown recipients is redirected here)"), postmaster_));

         var adRow = new Grid();
         adRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         adRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         Grid.SetColumn(adDomain_, 0);
         adRow.Children.Add(adDomain_);
         var adBrowse = new Wpf.Ui.Controls.Button { Content = L("_Browse…"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         adBrowse.Click += BrowseAdDomain;
         Grid.SetColumn(adBrowse, 1);
         adRow.Children.Add(adBrowse);
         panel.Children.Add(DialogFields.Field(L("Active Directory domain (for AD-synchronised domains; optional)"), adRow));
         return DialogFields.Scroll(panel);
      }

      private void GenerateDkim()
      {
         string selector = dkimSelector_.Text.Trim();
         if (selector.Length == 0)
         {
            selector = "dkim";
            dkimSelector_.Text = selector;
         }

         var save = new Microsoft.Win32.SaveFileDialog
         {
            Title = L("Save the DKIM private key"),
            Filter = "PEM key files (*.pem)|*.pem|All files (*.*)|*.*",
            FileName = selector + "._domainkey." + domainName_ + ".pem"
         };
         try
         {
            string existing = dkimKeyFile_.Text.Trim();
            if (existing.Length > 0)
            {
               string dir = System.IO.Path.GetDirectoryName(existing);
               if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                  save.InitialDirectory = dir;
            }
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }

         if (save.ShowDialog() != true)
            return;

         try
         {
            DkimKeyGenerator.Result result = DkimKeyGenerator.Generate(selector, domainName_);
            System.IO.File.WriteAllText(save.FileName, result.PrivateKeyPem);
            dkimKeyFile_.Text = save.FileName;
            dkimOn_.IsChecked = true;
            dkimDnsValue_ = result.DnsTxtValue;
            dkimDns_.Text = F("Host/Name:  {0}", result.DnsHost) + Environment.NewLine +
                            L("Type:       TXT") + Environment.NewLine +
                            F("Value:      {0}", result.DnsTxtValue);
            dkimDnsPanel_.Visibility = Visibility.Visible;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not generate the DKIM key: {0}", ex.Message));
         }
      }

      private void BrowseAdDomain(object sender, RoutedEventArgs e)
      {
         notice_.Hide();

         if (!ActiveDirectoryService.IsAvailable(out string reason))
         {
            notice_.Show(StatusLevel.Warning, F("Active Directory is not available on this machine: {0}", reason));
            return;
         }

         System.Collections.Generic.List<string> domains = ActiveDirectoryService.ListDomains();
         if (domains == null || domains.Count == 0)
         {
            notice_.Show(StatusLevel.Warning, L("No Active Directory domains were found."));
            return;
         }

         var menu = new ContextMenu();
         foreach (string dom in domains)
         {
            var item = new MenuItem { Header = dom };
            item.Click += (s2, e2) => adDomain_.Text = dom;
            menu.Items.Add(item);
         }
         menu.PlacementTarget = (UIElement)sender;
         menu.IsOpen = true;
      }

      private FrameworkElement BuildNames()
      {
         aliasEditor_ = CollectionSpecs.DomainAliases(domainName_);
         aliasEditor_.Margin = new Thickness(0, DesignTokens.Space.Md, 0, 0);
         return aliasEditor_;
      }

      private ScrollViewer BuildLimits()
      {
         var panel = DialogFields.TabPanel();
         maxSizeRow_ = Label(L("Maximum _domain size (MB, 0 = unlimited)"), maxSize_);
         panel.Children.Add(maxSizeRow_);
         maxMessageSizeRow_ = Label(L("Maximum _message size (KB, 0 = unlimited)"), maxMessageSize_);
         panel.Children.Add(maxMessageSizeRow_);
         maxAccountSizeRow_ = Label(L("Maximum size for _accounts created in this domain (MB, 0 = unlimited)"), maxAccountSize_);
         panel.Children.Add(maxAccountSizeRow_);
         retentionRow_ = Label(L("Delete messages in this domain's mailboxes _older than (days; 0 = no policy, an account's own value overrides it)"), retentionDays_);
         panel.Children.Add(retentionRow_);
         panel.Children.Add(DialogFields.Separator());

         panel.Children.Add(DialogFields.Check(maxAccountsOn_));
         maxAccountsRow_ = NumberUnder(maxAccountsOn_, maxAccounts_);
         panel.Children.Add(maxAccountsRow_);
         panel.Children.Add(DialogFields.Check(maxAliasesOn_));
         maxAliasesRow_ = NumberUnder(maxAliasesOn_, maxAliases_);
         panel.Children.Add(maxAliasesRow_);
         panel.Children.Add(DialogFields.Check(maxDistsOn_));
         maxDistsRow_ = NumberUnder(maxDistsOn_, maxDists_);
         panel.Children.Add(maxDistsRow_);

         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(DialogFields.Check(plusAddressingOn_));
         panel.Children.Add(Label(L("_Plus addressing character"), plusChar_));
         panel.Children.Add(DialogFields.Check(greylisting_));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildSignature()
      {
         signatureMethod_.Items.Add(DialogFields.Combo(L("Use only if account has no signature"), 1));
         signatureMethod_.Items.Add(DialogFields.Combo(L("Overwrite account signature"), 2));
         signatureMethod_.Items.Add(DialogFields.Combo(L("Append to account signature"), 3));

         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Check(signatureOn_));
         panel.Children.Add(Label(L("Signature _method"), signatureMethod_));
         panel.Children.Add(DialogFields.Check(signReplies_));
         panel.Children.Add(DialogFields.Check(signLocal_));
         panel.Children.Add(Label(L("_Plain-text signature"), signaturePlain_));
         panel.Children.Add(Label(L("_HTML signature"), signatureHtml_));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildRelay()
      {
         relaySecurity_.Items.Add(DialogFields.Combo(L("None"), 0));
         relaySecurity_.Items.Add(DialogFields.Combo(L("SSL/TLS"), 1));
         relaySecurity_.Items.Add(DialogFields.Combo(L("STARTTLS (optional)"), 2));
         relaySecurity_.Items.Add(DialogFields.Combo(L("STARTTLS (required)"), 3));

         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Note(L("Where mail FROM this domain leaves through. This is a different question from a route, which decides where mail addressed TO a domain is sent - it is for a server hosting several independent domains where each has its own delivery provider.")));
         panel.Children.Add(DialogFields.Note(L("Leave the host empty and the domain has no opinion: the server-wide SMTP relayer applies, exactly as it did before this setting existed. A route still wins over both, because a route is a statement about the destination.")));
         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(Label(L("_Relay host (empty = use the server-wide relayer)"), relayHost_));
         relayPortRow_ = Label(L("_Port (0 = 25)"), relayPort_);
         panel.Children.Add(relayPortRow_);
         panel.Children.Add(Label(L("_Connection security"), relaySecurity_));
         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(DialogFields.Check(relayAuthOn_));
         panel.Children.Add(Label(L("_User name"), relayUser_));
         panel.Children.Add(Label(L("Pass_word (leave blank to keep the stored one)"), relayPassword_));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildOutOfOffice()
      {
         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Note(L("A reply for the whole domain - a closed office, a decommissioned department. It answers only for accounts that have NO vacation message of their own: an account's own message always wins, and at most one reply answers any message.")));
         panel.Children.Add(DialogFields.Note(L("The usual auto-reply protections apply and cannot be switched off here: no reply to bounces, mailing lists or other auto-replies, one reply per sender, and the reply itself is marked Auto-Submitted so two servers cannot loop.")));
         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(DialogFields.Check(oooOn_));
         panel.Children.Add(Label(L("Su_bject"), oooSubject_));
         panel.Children.Add(Label(L("_Message"), oooMessage_));
         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(DialogFields.Note(L("Optional different text for local senders - colleagues can be told more than strangers. Empty means everyone gets the text above. Note this identifies the sender's ADDRESS, which is forgeable: treat it as a courtesy, never as a place for anything confidential.")));
         panel.Children.Add(Label(L("Subject for _local senders (empty = same as above)"), oooInternalSubject_));
         panel.Children.Add(Label(L("Message for local s_enders (empty = same as above)"), oooInternalMessage_));
         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(DialogFields.Check(oooExternalOverride_));
         panel.Children.Add(DialogFields.Note(L("With this on, an account's own vacation message still answers colleagues, but outside senders get the domain's generic text instead - so personal detail in a vacation message stays inside the organisation.")));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildTransforms()
      {
         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Note(L("Mail from outside the organisation can be marked so that a reader sees it at a glance. A sender is outside when the session did not sign in, the message did not arrive through a trusted incoming relay, and neither the envelope sender nor the From address belongs to a domain this server hosts.")));
         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(DialogFields.Check(tagSubject_));
         panel.Children.Add(DialogFields.Note(L("The subject tag breaks any DKIM signature the sender made over their subject, exactly as the anti-spam subject prefix does. The header does not, so prefer the header where the reader can show it.")));
         tagTextRow_ = Label(L("Tag te_xt (empty = [EXTERNAL], at most 100 characters)"), tagText_);
         panel.Children.Add(tagTextRow_);
         panel.Children.Add(DialogFields.Check(tagHeader_));
         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(DialogFields.Check(firstContact_));
         panel.Children.Add(DialogFields.Note(L("The server remembers who has written to each account and who each account has written to, and says so in the reader the first time an outside sender appears. It costs one database lookup for each message delivered to this domain, and nothing at all while this is off.")));
         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(DialogFields.Check(disclaimerOn_));
         panel.Children.Add(DialogFields.Note(L("Added once, to mail with at least one recipient outside this server. Never to a signed or encrypted message, an automatic reply, a bounce or a list posting, and never a second time to a reply that already quotes it.")));
         panel.Children.Add(Label(L("_Plain-text disclaimer"), disclaimerPlain_));
         panel.Children.Add(Label(L("HTM_L disclaimer (empty = the plain text, with its line breaks)"), disclaimerHtml_));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildDkim()
      {
         dkimHeaderCanon_.Items.Add(DialogFields.Combo(L("Simple"), 1));
         dkimHeaderCanon_.Items.Add(DialogFields.Combo(L("Relaxed"), 2));
         dkimBodyCanon_.Items.Add(DialogFields.Combo(L("Simple"), 1));
         dkimBodyCanon_.Items.Add(DialogFields.Combo(L("Relaxed"), 2));
         dkimAlgorithm_.Items.Add(DialogFields.Combo(L("SHA1"), 1));
         dkimAlgorithm_.Items.Add(DialogFields.Combo(L("SHA256"), 2));

         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Check(dkimOn_));
         panel.Children.Add(DialogFields.Check(dkimAliases_));
         panel.Children.Add(Label(L("Se_lector"), dkimSelector_));

         var dkimKeyRow = new Grid();
         dkimKeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         dkimKeyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         Grid.SetColumn(dkimKeyFile_, 0);
         dkimKeyRow.Children.Add(dkimKeyFile_);
         var dkimBrowse = new Wpf.Ui.Controls.Button { Content = L("B_rowse…"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         AutomationProperties.SetAutomationId(dkimBrowse, "DkimKeyBrowse");
         dkimBrowse.Click += (s, e) =>
         {
            string file = PathPicker.PickFile(dkimKeyFile_.Text, "PEM/key files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*");
            if (file != null)
               dkimKeyFile_.Text = file;
         };
         Grid.SetColumn(dkimBrowse, 1);
         dkimKeyRow.Children.Add(dkimBrowse);
         panel.Children.Add(DialogFields.Field(L("Private key file"), dkimKeyRow));

         var dkimGen = new Wpf.Ui.Controls.Button
         {
            Content = L("_Generate key pair…"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md)
         };
         AutomationProperties.SetAutomationId(dkimGen, "DkimGenerate");
         dkimGen.Click += (s, e) => GenerateDkim();
         panel.Children.Add(dkimGen);

         dkimDnsPanel_ = new StackPanel { Visibility = Visibility.Collapsed };
         dkimDnsPanel_.Children.Add(DialogFields.Field(L("Publish this DNS TXT record at your DNS provider, then enable DKIM:"), dkimDns_));
         var dkimCopy = new Wpf.Ui.Controls.Button { Content = L("_Copy DNS value"), Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
         AutomationProperties.SetAutomationId(dkimCopy, "DkimCopyDns");
         dkimCopy.Click += (s, e) => CopyToClipboard(dkimDnsValue_);
         dkimDnsPanel_.Children.Add(dkimCopy);
         panel.Children.Add(dkimDnsPanel_);

         panel.Children.Add(Label(L("_Header canonicalization"), dkimHeaderCanon_));
         panel.Children.Add(Label(L("_Body canonicalization"), dkimBodyCanon_));
         panel.Children.Add(Label(L("Signing _algorithm"), dkimAlgorithm_));

         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(BuildRotation());

         return DialogFields.Scroll(panel);
      }

      /// <summary>
      /// The staged key-rotation walkthrough. Rotation is a sequence with an
      /// out-of-band wait in the middle (publishing a DNS record and letting it
      /// propagate), so the section reads as numbered steps and the Promote
      /// button is gated on an actual DNS check instead of trusting the
      /// administrator to have waited long enough. It is a
      /// <see cref="SettingsSection"/>, which makes its title a level-2 heading
      /// under the dialog's own - a screen reader can jump to it, which a
      /// semibold TextBlock never allowed.
      /// </summary>
      private SettingsSection BuildRotation()
      {
         var section = new StackPanel();

         // Step 1 quotes the primary fields above, so it must follow their edits
         // or it would describe a selector the user has already typed over.
         dkimSelector_.TextChanged += (s, e) => UpdateRotationUi();
         dkimKeyFile_.TextChanged += (s, e) => UpdateRotationUi();

         rotCurrent_ = StepLine();
         section.Children.Add(rotCurrent_);

         // Step 2 while nothing is staged: choose a selector name and stage a key.
         rotStartPanel_ = new StackPanel();
         var startRow = new Grid();
         startRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         startRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         AutomationProperties.SetAutomationId(rotSelector_, "DkimRotationSelector");
         Grid.SetColumn(rotSelector_, 0);
         startRow.Children.Add(rotSelector_);
         var start = new Wpf.Ui.Controls.Button { Content = L("Generate and stage _key…"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         AutomationProperties.SetAutomationId(start, "DkimRotationStart");
         start.Click += (s, e) => StartRotation();
         Grid.SetColumn(start, 1);
         startRow.Children.Add(start);
         rotStartPanel_.Children.Add(DialogFields.Field(L("2. Stage a new key pair under a new selector name:"), startRow));
         section.Children.Add(rotStartPanel_);

         // Steps 2-5 once a rotation is staged.
         rotStagedPanel_ = new StackPanel { Visibility = Visibility.Collapsed };

         rotStagedInfo_ = StepLine();
         rotStagedPanel_.Children.Add(rotStagedInfo_);

         rotStagedPanel_.Children.Add(DialogFields.Field(L("3. Publish this DNS TXT record at your DNS provider. Host/Name:"), rotDnsHost_));
         rotStagedPanel_.Children.Add(DialogFields.Field(L("TXT value:"), rotDnsValue_));

         var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
         var copyHost = new Wpf.Ui.Controls.Button { Content = L("Copy h_ost") };
         AutomationProperties.SetAutomationId(copyHost, "DkimRotationCopyHost");
         copyHost.Click += (s, e) => CopyToClipboard(rotDnsHost_.Text);
         copyRow.Children.Add(copyHost);
         var copyValue = new Wpf.Ui.Controls.Button { Content = L("Copy _value"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         AutomationProperties.SetAutomationId(copyValue, "DkimRotationCopyValue");
         copyValue.Click += (s, e) => CopyToClipboard(rotExpectedTxt_);
         copyRow.Children.Add(copyValue);
         rotStagedPanel_.Children.Add(copyRow);

         rotStagedPanel_.Children.Add(DialogFields.Caption(L("4. Check that the record is visible in DNS:")));
         rotCheck_ = new Wpf.Ui.Controls.Button { Content = L("Check D_NS"), Margin = new Thickness(0, 0, 0, DesignTokens.Space.Sm) };
         AutomationProperties.SetAutomationId(rotCheck_, "DkimRotationCheck");
         rotCheck_.Click += async (s, e) => await CheckRotationDns();
         rotStagedPanel_.Children.Add(rotCheck_);

         // The running commentary of the check. A notice rather than a coloured
         // line: the level reaches a reader who cannot tell the four colours
         // apart, as a mark and a word, and it is a live region, so the outcome
         // of a check that took a resolver timeout to come back is announced.
         rotStagedPanel_.Children.Add(rotStatus_);

         rotStagedPanel_.Children.Add(DialogFields.Caption(L("5. Promote the staged key to become the signing key:")));
         var promoteRow = new StackPanel { Orientation = Orientation.Horizontal };

         // Promote stays locked until a "Check DNS" in this session has seen the
         // new record on the wire and confirmed it matches the staged key.
         // Promoting before the record has propagated makes the server sign
         // immediately with a key whose public half much of the world cannot look
         // up yet: every receiver whose resolver has not caught up fails the DKIM
         // signature, and under a DMARC quarantine or reject policy that sends
         // freshly signed mail to spam folders or bounces it outright until
         // propagation completes.
         rotPromote_ = new Wpf.Ui.Controls.Button
         {
            Content = L("_Promote…"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            IsEnabled = false,
            ToolTip = L("Enabled once \"Check DNS\" has confirmed the published record matches the staged key.")
         };
         ToolTipService.SetShowOnDisabled(rotPromote_, true);
         AutomationProperties.SetAutomationId(rotPromote_, "DkimRotationPromote");
         rotPromote_.Click += (s, e) => PromoteRotation();
         promoteRow.Children.Add(rotPromote_);

         var cancelRotation = new Wpf.Ui.Controls.Button { Content = L("Cancel rotat_ion…"), Margin = new Thickness(DesignTokens.Space.Sm, 0, 0, 0) };
         AutomationProperties.SetAutomationId(cancelRotation, "DkimRotationCancel");
         cancelRotation.Click += (s, e) => CancelRotation();
         promoteRow.Children.Add(cancelRotation);

         rotStagedPanel_.Children.Add(promoteRow);
         section.Children.Add(rotStagedPanel_);

         return new SettingsSection
         {
            Heading = L("Key rotation"),
            Description = L("Replace the DKIM key without a gap in verification: stage a new key next to the current one, publish its DNS record, wait for the record to propagate, check it, and only then promote it."),
            Content = section
         };
      }

      // ---- DKIM key rotation ----

      private void StartRotation()
      {
         rotStatus_.Hide();
         string selector = rotSelector_.Text.Trim();
         if (selector.Length == 0)
         {
            SetRotationStatus(L("Enter a selector name for the new key first."), StatusLevel.Warning);
            return;
         }

         // Reusing the current selector name is refused outright: it would replace
         // the published key under the same DNS name instead of running two keys
         // side by side, and while resolvers still serve the old record every
         // message signed with the new key fails verification - the exact outage
         // a staged rotation exists to prevent.
         if (string.Equals(selector, dkimSelector_.Text.Trim(), StringComparison.OrdinalIgnoreCase))
         {
            SetRotationStatus(F("The new selector must be different from the current selector (\"{0}\"). A rotation runs both keys side by side under different DNS names.", selector), StatusLevel.Critical);
            return;
         }

         var save = new Microsoft.Win32.SaveFileDialog
         {
            Title = L("Save the new DKIM private key"),
            Filter = "PEM key files (*.pem)|*.pem|All files (*.*)|*.*",
            FileName = selector + "._domainkey." + domainName_ + ".pem"
         };
         try
         {
            // Default to the folder the current key lives in, like the generator above.
            string existing = dkimKeyFile_.Text.Trim();
            if (existing.Length > 0)
            {
               string dir = System.IO.Path.GetDirectoryName(existing);
               if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
                  save.InitialDirectory = dir;
            }
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }

         if (save.ShowDialog() != true)
            return;

         DkimKeyGenerator.Result generated;
         try
         {
            generated = DkimKeyGenerator.Generate(selector, domainName_);
            System.IO.File.WriteAllText(save.FileName, generated.PrivateKeyPem);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            SetRotationStatus(F("Could not generate the DKIM key: {0}", ex.Message), StatusLevel.Critical);
            return;
         }

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic d = domains.ItemByName[domainName_];
            d.DKIMSecondarySelector = selector;
            d.DKIMSecondaryPrivateKeyFile = save.FileName;
            d.Save();
            ServerSession.Release(d);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            SetRotationStatus(F("Could not stage the rotation on the server: {0}", ServerSession.DescribeComError(ex)), StatusLevel.Critical);
            return;
         }
         finally
         {
            ServerSession.Release(domains);
         }

         rotStagedSelector_ = selector;
         rotStagedKeyFile_ = save.FileName;
         rotExpectedTxt_ = generated.DnsTxtValue;
         rotCheckPassed_ = false;
         UpdateRotationUi();
         SetRotationStatus(L("Not checked yet. Publish the record, allow time for propagation, then press \"Check DNS\"."),
            StatusLevel.Information);
      }

      private static string RotationNotFoundText =>
         L("The record was not found yet. New DNS records usually appear within minutes but can take hours to propagate - publish the record above if you have not already, then check again later.");

      private static string RotationLookupFailedText(string error) =>
         F("The DNS lookup itself failed: {0} This says nothing about the record - this machine could not get an answer from its DNS server. Check the network and try again.", error);

      private async Task CheckRotationDns()
      {
         if (rotChecking_)
            return;

         if (rotStagedSelector_.Length == 0)
         {
            SetRotationStatus(L("The staged pair has no selector, so there is no DNS name to check. Cancel the rotation and start again."), StatusLevel.Critical);
            return;
         }

         string host = rotStagedSelector_ + "._domainkey." + domainName_;
         string expected = rotExpectedTxt_;

         rotChecking_ = true;
         rotCheck_.IsEnabled = false;
         SetRotationStatus(F("Checking {0}…", host), StatusLevel.Information);

         try
         {
            // The lookup blocks for the resolver's full timeout when the record is
            // missing - the common case right after publishing - so it runs off the
            // UI thread. DnsTxtLookup never throws; it reports through the result.
            if (expected == null)
            {
               DnsTxtLookup.LookupResult found = await Task.Run(() => DnsTxtLookup.Query(host));
               ReportUnverifiableCheck(found);
               return;
            }

            DnsTxtLookup.MatchResult result = await Task.Run(() => DnsTxtLookup.CheckExpected(host, expected));

            // Any outcome short of a confirmed match locks Promote (again): a
            // pass from earlier in the session is stale evidence the moment a
            // fresh check stops confirming it.
            rotCheckPassed_ = result.Status == DnsTxtLookup.MatchStatus.FoundAndMatches;

            switch (result.Status)
            {
               case DnsTxtLookup.MatchStatus.FoundAndMatches:
                  SetRotationStatus(L("The published record matches the staged key. It is safe to promote."),
                     StatusLevel.Good);
                  break;

               case DnsTxtLookup.MatchStatus.NoRecord:
                  SetRotationStatus(RotationNotFoundText, StatusLevel.Warning);
                  break;

               case DnsTxtLookup.MatchStatus.FoundButDifferent:
                  SetRotationStatus(F("A TXT record exists at {0}, but it does not match the staged key. Waiting will not fix this: correct the published record so it matches the value above, then check again.", host) + DescribeFoundRecords(result.Records), StatusLevel.Critical);
                  break;

               default:
                  SetRotationStatus(RotationLookupFailedText(result.Error), StatusLevel.Critical);
                  break;
            }
         }
         finally
         {
            rotPromote_.IsEnabled = rotCheckPassed_;
            rotChecking_ = false;
            rotCheck_.IsEnabled = true;
         }
      }

      /// <summary>
      /// Words the check when the staged private key file could not be read from
      /// this machine (typically when administering a remote server, where the
      /// path is on the server's disk): the record can still be looked up, but
      /// not compared - and a record that cannot be verified must not unlock
      /// Promote, however plausible it looks.
      /// </summary>
      private void ReportUnverifiableCheck(DnsTxtLookup.LookupResult found)
      {
         rotCheckPassed_ = false;

         switch (found.Status)
         {
            case DnsTxtLookup.LookupStatus.Found:
               SetRotationStatus(F("A TXT record exists, but the staged private key file ({0}) could not be read from this machine, so the record cannot be compared against the key. Run this check on the server itself, or cancel the rotation and start it again from here.", rotStagedKeyFile_),
                  StatusLevel.Warning);
               break;

            case DnsTxtLookup.LookupStatus.NoRecord:
               SetRotationStatus(RotationNotFoundText, StatusLevel.Warning);
               break;

            default:
               SetRotationStatus(RotationLookupFailedText(found.Error), StatusLevel.Critical);
               break;
         }
      }

      private void PromoteRotation()
      {
         string oldSelector = dkimSelector_.Text.Trim();
         string oldHost = oldSelector.Length > 0 ? oldSelector + "._domainkey." + domainName_ : "(none)";

         if (MessageBox.Show(
             F("Promote the staged selector \"{0}\"?\n\nFrom the next message on, mail from {1} is signed with the new key, and the old selector is cleared from the configuration.\n\nLeave the OLD DNS record ({2}) published for at least a few days. Mail signed with the old key may still be in transit, and receivers can only verify it for as long as that record exists.", rotStagedSelector_, domainName_, oldHost),
             L("Control Panel"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic d = domains.ItemByName[domainName_];

            // The server refuses to promote a half-staged pair, and refuses when
            // the staged key file is missing from its disk - either would leave
            // the domain unable to sign. Its explanation reaches the administrator
            // verbatim through the catch below instead of being swallowed.
            d.DKIMPromoteSecondary();
            d.Save();

            string newSelector = (string)d.DKIMSelector ?? "";
            string newKeyFile = (string)d.DKIMPrivateKeyFile ?? "";
            ServerSession.Release(d);

            // Mirror the promoted values into the primary fields: they now hold
            // stale text, and a later Save of this dialog would otherwise write
            // the old selector back and silently undo the promote.
            dkimSelector_.Text = newSelector;
            dkimKeyFile_.Text = newKeyFile;

            rotStagedSelector_ = "";
            rotStagedKeyFile_ = "";
            rotExpectedTxt_ = null;
            rotCheckPassed_ = false;
            UpdateRotationUi();

            SetRotationStatus(F("The rotation is complete: mail is now signed with selector \"{0}\".\n\nKeep the old DNS record ({1}) for a few more days while mail signed with the old key is still in transit, then remove it.", newSelector, oldHost), StatusLevel.Good);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            SetRotationStatus(F("Could not promote the staged key: {0}", ServerSession.DescribeComError(ex)), StatusLevel.Critical);
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private void CancelRotation()
      {
         if (MessageBox.Show(
             F("Cancel this rotation?\n\nThe staged selector \"{0}\" is removed from the server configuration; signing continues with the current key, untouched. The generated key file stays on disk, and the DNS record for the staged selector - if you already published it - can simply be deleted.", rotStagedSelector_),
             L("Control Panel"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic d = domains.ItemByName[domainName_];
            d.DKIMSecondarySelector = "";
            d.DKIMSecondaryPrivateKeyFile = "";
            d.Save();
            ServerSession.Release(d);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            SetRotationStatus(F("Could not cancel the rotation: {0}", ServerSession.DescribeComError(ex)), StatusLevel.Critical);
            return;
         }
         finally
         {
            ServerSession.Release(domains);
         }

         rotStagedSelector_ = "";
         rotStagedKeyFile_ = "";
         rotExpectedTxt_ = null;
         rotCheckPassed_ = false;
         rotStatus_.Hide();
         UpdateRotationUi();
      }

      /// <summary>
      /// Puts the rotation section into the right step when the dialog opens and
      /// a rotation is already staged on the server - staged pairs outlive the
      /// dialog by design, because DNS propagation is waited out between
      /// sessions. A check that passed in an earlier session deliberately does
      /// not count: DNS can change while the dialog is closed, so Promote only
      /// unlocks after a fresh check in this session.
      /// </summary>
      private void RestoreStagedRotation(string selector, string keyFile)
      {
         rotStagedSelector_ = selector;
         rotStagedKeyFile_ = keyFile;
         rotCheckPassed_ = false;
         rotExpectedTxt_ = null;

         if (selector.Length == 0 && keyFile.Length == 0)
         {
            UpdateRotationUi();
            return;
         }

         if (keyFile.Length > 0)
            rotExpectedTxt_ = DeriveDnsTxtValue(keyFile);

         UpdateRotationUi();

         if (selector.Length == 0 || keyFile.Length == 0)
            SetRotationStatus(L("The staged rotation is incomplete (the selector or the key file is missing), so the server will refuse to promote it. Cancel the rotation and start again."), StatusLevel.Critical);
         else
            SetRotationStatus(L("A rotation is already staged. If the DNS record is published, press \"Check DNS\"; promoting unlocks once the check passes in this session."), StatusLevel.Information);
      }

      /// <summary>
      /// Rebuilds the DNS TXT value from the staged private key's PEM file, so a
      /// rotation staged in an earlier session can still show its record and have
      /// "Check DNS" compare against it. Returns null when the file cannot be
      /// read from this machine (for example when administering a remote server,
      /// where the path is on the server's disk); the check then reports that it
      /// cannot verify a match, and Promote stays locked.
      /// </summary>
      private static string DeriveDnsTxtValue(string keyFile)
      {
         try
         {
            string pem = System.IO.File.ReadAllText(keyFile);
            using var rsa = System.Security.Cryptography.RSA.Create();
            rsa.ImportFromPem(pem);
            // The same record format DkimKeyGenerator emits when a key is first made.
            return "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()); // no-loc
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return null;
         }
      }

      private void UpdateRotationUi()
      {
         string current = dkimSelector_.Text.Trim();
         string currentFile = dkimKeyFile_.Text.Trim();
         rotCurrent_.Text = current.Length > 0
            ? F("1. Currently signing with selector \"{0}\"", current) +
              (currentFile.Length > 0 ? F(" (key file: {0}).", currentFile) : ".")
            : L("1. No signing selector is configured yet.");

         bool staged = rotStagedSelector_.Length > 0 || rotStagedKeyFile_.Length > 0;
         rotStartPanel_.Visibility = staged ? Visibility.Collapsed : Visibility.Visible;
         rotStagedPanel_.Visibility = staged ? Visibility.Visible : Visibility.Collapsed;

         if (!staged)
         {
            rotPromote_.IsEnabled = false;

            // A dated selector name never collides with the old one and makes it
            // obvious in DNS which key is the newer.
            if (rotSelector_.Text.Trim().Length == 0)
            {
               string suggestion = "s" + DateTime.Now.ToString("yyyyMMdd");
               if (!string.Equals(suggestion, current, StringComparison.OrdinalIgnoreCase))
                  rotSelector_.Text = suggestion;
            }
            return;
         }

         rotStagedInfo_.Text = F("2. A rotation is staged: selector \"{0}\", key file {1}.", rotStagedSelector_, rotStagedKeyFile_);
         rotDnsHost_.Text = rotStagedSelector_.Length > 0
            ? rotStagedSelector_ + "._domainkey." + domainName_
            : L("(the staged pair has no selector)");
         rotDnsValue_.Text = rotExpectedTxt_ ?? L("(The staged private key file could not be read from this machine, so the record value cannot be shown here. It was shown when the rotation was started; if it is lost, cancel the rotation and start again.)");
         rotPromote_.IsEnabled = rotCheckPassed_;
      }

      private void SetRotationStatus(string text, StatusLevel level) => rotStatus_.Show(level, text);

      private static string DescribeFoundRecords(System.Collections.Generic.List<string> records)
      {
         if (records == null || records.Count == 0)
            return "";

         string first = records[0];
         if (first.Length > 120)
            first = first.Substring(0, 120) + "…";
         return Environment.NewLine + "Found: " + first;
      }

      private static void CopyToClipboard(string text)
      {
         try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
      }

      private void Load()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic d = domains.ItemByName[domainName_];
            active_.IsChecked = (bool)d.Active;
            name_.Text = (string)d.Name ?? domainName_;
            postmaster_.Text = (string)d.Postmaster ?? "";
            adDomain_.Text = (string)d.ADDomainName ?? "";

            maxSize_.Text = ((int)d.MaxSize).ToString();
            maxMessageSize_.Text = ((int)d.MaxMessageSize).ToString();
            maxAccountSize_.Text = ((int)d.MaxAccountSize).ToString();
            retentionDays_.Text = ((int)d.MessageRetentionDays).ToString();
            maxAccountsOn_.IsChecked = (bool)d.MaxNumberOfAccountsEnabled;
            maxAccounts_.Text = ((int)d.MaxNumberOfAccounts).ToString();
            maxAliasesOn_.IsChecked = (bool)d.MaxNumberOfAliasesEnabled;
            maxAliases_.Text = ((int)d.MaxNumberOfAliases).ToString();
            maxDistsOn_.IsChecked = (bool)d.MaxNumberOfDistributionListsEnabled;
            maxDists_.Text = ((int)d.MaxNumberOfDistributionLists).ToString();
            plusAddressingOn_.IsChecked = (bool)d.PlusAddressingEnabled;
            plusChar_.Text = (string)d.PlusAddressingCharacter ?? "";
            greylisting_.IsChecked = (bool)d.AntiSpamEnableGreylisting;

            signatureOn_.IsChecked = (bool)d.SignatureEnabled;
            DialogFields.SelectCombo(signatureMethod_, (int)d.SignatureMethod);
            signReplies_.IsChecked = (bool)d.AddSignaturesToReplies;
            signLocal_.IsChecked = (bool)d.AddSignaturesToLocalMail;
            signaturePlain_.Text = (string)d.SignaturePlainText ?? "";
            signatureHtml_.Text = (string)d.SignatureHTML ?? "";

            relayHost_.Text = (string)d.RelayHost ?? "";
            relayPort_.Text = ((long)d.RelayPort).ToString();
            relayAuthOn_.IsChecked = (bool)d.RelayRequiresAuthentication;
            relayUser_.Text = (string)d.RelayUsername ?? "";
            DialogFields.SelectCombo(relaySecurity_, (int)d.RelayConnectionSecurity);

            // The password box is deliberately left empty rather than filled with the
            // stored secret: a dialog that shows a password back is a dialog that hands
            // it to whoever is standing behind you, and it buys nothing - Save keeps the
            // stored value when the box is blank.
            relayPassword_.Password = "";

            oooOn_.IsChecked = (bool)d.VacationMessageIsOn;
            oooSubject_.Text = (string)d.VacationSubject ?? "";
            oooMessage_.Text = (string)d.VacationMessage ?? "";
            oooInternalSubject_.Text = (string)d.VacationInternalSubject ?? "";
            oooInternalMessage_.Text = (string)d.VacationInternalMessage ?? "";
            oooExternalOverride_.IsChecked = (bool)d.VacationExternalOverride;

            tagSubject_.IsChecked = (bool)d.ExternalTagSubject;
            tagHeader_.IsChecked = (bool)d.ExternalTagHeader;
            tagText_.Text = (string)d.ExternalTagText ?? "";
            firstContact_.IsChecked = (bool)d.FirstContactTip;
            disclaimerOn_.IsChecked = (bool)d.DisclaimerEnabled;
            disclaimerPlain_.Text = (string)d.DisclaimerPlainText ?? "";
            disclaimerHtml_.Text = (string)d.DisclaimerHTML ?? "";

            dkimOn_.IsChecked = (bool)d.DKIMSignEnabled;
            dkimAliases_.IsChecked = (bool)d.DKIMSignAliasesEnabled;
            dkimSelector_.Text = (string)d.DKIMSelector ?? "";
            dkimKeyFile_.Text = (string)d.DKIMPrivateKeyFile ?? "";
            DialogFields.SelectCombo(dkimHeaderCanon_, (int)d.DKIMHeaderCanonicalizationMethod);
            DialogFields.SelectCombo(dkimBodyCanon_, (int)d.DKIMBodyCanonicalizationMethod);
            DialogFields.SelectCombo(dkimAlgorithm_, (int)d.DKIMSigningAlgorithm);

            // The staged rotation pair, if an earlier session left one behind.
            // Read defensively: against an older server that predates the
            // rotation API these properties do not exist, and the rest of the
            // dialog must keep working without them.
            string stagedSelector = "", stagedKeyFile = "";
            try
            {
               stagedSelector = ((string)d.DKIMSecondarySelector ?? "").Trim();
               stagedKeyFile = ((string)d.DKIMSecondaryPrivateKeyFile ?? "").Trim();
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
            RestoreStagedRotation(stagedSelector, stagedKeyFile);

            ServerSession.Release(d);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not load the domain: {0}", ex.Message), L("Control Panel"));
            Close();
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private void Save()
      {
         notice_.Hide();
         DialogFields.ClearErrors(nameRow_, maxSizeRow_, maxMessageSizeRow_, maxAccountSizeRow_, retentionRow_,
                                  maxAccountsRow_, maxAliasesRow_, maxDistsRow_, relayPortRow_, tagTextRow_);

         // Eight numbers, each said on its own field. They used to share one line
         // at the foot of the dialog, which named the field in words and left the
         // reader to find it again among seven tabs; ShowError brings the tab
         // forward and puts the keyboard in the box itself.
         if (!NumericField.TryValidate(maxSize_.Text, L("Maximum domain size (MB)"), 0, int.MaxValue, out int msV, out bool hasMs, out string error))
         { DialogFields.ShowError(maxSizeRow_, error); return; }

         if (!NumericField.TryValidate(maxMessageSize_.Text, L("Maximum message size (KB)"), 0, int.MaxValue, out int mmsV, out bool hasMms, out error))
         { DialogFields.ShowError(maxMessageSizeRow_, error); return; }

         if (!NumericField.TryValidate(maxAccountSize_.Text, L("Maximum account size (MB)"), 0, int.MaxValue, out int masV, out bool hasMas, out error))
         { DialogFields.ShowError(maxAccountSizeRow_, error); return; }

         if (!NumericField.TryValidate(retentionDays_.Text, L("Delete messages older than (days)"), 0, int.MaxValue, out int retentionV, out bool hasRetention, out error))
         { DialogFields.ShowError(retentionRow_, error); return; }

         if (!NumericField.TryValidate(maxAccounts_.Text, L("Maximum number of accounts"), 0, int.MaxValue, out int mnaV, out bool hasMna, out error))
         { DialogFields.ShowError(maxAccountsRow_, error); return; }

         if (!NumericField.TryValidate(maxAliases_.Text, L("Maximum number of aliases"), 0, int.MaxValue, out int mnalV, out bool hasMnal, out error))
         { DialogFields.ShowError(maxAliasesRow_, error); return; }

         if (!NumericField.TryValidate(maxDists_.Text, L("Maximum number of distribution lists"), 0, int.MaxValue, out int mndV, out bool hasMnd, out error))
         { DialogFields.ShowError(maxDistsRow_, error); return; }

         if (!NumericField.TryValidate(relayPort_.Text, L("Relay port"), 0, 65535, out int relayPortValue, out bool _, out error))
         { DialogFields.ShowError(relayPortRow_, error); return; }

         // Said here as well as by the server, because a tag the server truncated
         // would be a tag every reader learns to ignore.
         if (tagText_.Text.Trim().Length > 100)
         {
            DialogFields.ShowError(tagTextRow_, L("The tag text must be 100 characters or fewer."));
            return;
         }

         string newName = name_.Text.Trim();
         if (newName.Length == 0 || !newName.Contains('.'))
         {
            DialogFields.ShowError(nameRow_, L("Enter a valid domain name."));
            return;
         }

         // Renaming asks first, because it is not a label edit: the server renames
         // every account, alias and distribution list, rewrites references held by
         // OTHER domains (forwards, alias targets, list members), and moves the
         // message directories. The old admin did all of this silently on Save,
         // which is exactly how domains got renamed by accident.
         bool renaming = !string.Equals(newName, domainName_, StringComparison.OrdinalIgnoreCase);
         if (renaming &&
             MessageBox.Show(
                F("Rename {0} to {1}?\n\nEvery account, alias and distribution list moves to the new name, and forwards or memberships in other domains that point at {0} addresses are updated to match. Mail sent to the old name will no longer be accepted.", domainName_, newName),
                L("Control Panel"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic d = domains.ItemByName[domainName_];
            if (renaming)
               d.Name = newName;
            d.Active = active_.IsChecked is true;
            d.Postmaster = postmaster_.Text.Trim();
            d.ADDomainName = adDomain_.Text.Trim();

            if (hasMs) d.MaxSize = msV;
            if (hasMms) d.MaxMessageSize = mmsV;
            if (hasMas) d.MaxAccountSize = masV;
            if (hasRetention) d.MessageRetentionDays = retentionV;
            d.MaxNumberOfAccountsEnabled = maxAccountsOn_.IsChecked is true;
            if (hasMna) d.MaxNumberOfAccounts = mnaV;
            d.MaxNumberOfAliasesEnabled = maxAliasesOn_.IsChecked is true;
            if (hasMnal) d.MaxNumberOfAliases = mnalV;
            d.MaxNumberOfDistributionListsEnabled = maxDistsOn_.IsChecked is true;
            if (hasMnd) d.MaxNumberOfDistributionLists = mndV;
            d.PlusAddressingEnabled = plusAddressingOn_.IsChecked is true;
            if (plusChar_.Text.Length > 0) d.PlusAddressingCharacter = plusChar_.Text;
            d.AntiSpamEnableGreylisting = greylisting_.IsChecked is true;

            d.SignatureEnabled = signatureOn_.IsChecked is true;
            int sm = DialogFields.ComboValue(signatureMethod_);
            if (sm > 0) d.SignatureMethod = sm;
            d.AddSignaturesToReplies = signReplies_.IsChecked is true;
            d.AddSignaturesToLocalMail = signLocal_.IsChecked is true;
            d.SignaturePlainText = signaturePlain_.Text;
            d.SignatureHTML = signatureHtml_.Text;

            d.RelayHost = relayHost_.Text.Trim();
            // Unconditional, as before: an empty box means port 0, which this
            // dialog's own caption reads as "0 = 25".
            d.RelayPort = relayPortValue;
            d.RelayRequiresAuthentication = relayAuthOn_.IsChecked is true;
            d.RelayUsername = relayUser_.Text.Trim();
            d.RelayConnectionSecurity = DialogFields.ComboValue(relaySecurity_);

            // Only written when something was typed, so re-saving the dialog for an
            // unrelated change does not silently blank the relay password.
            if (relayPassword_.Password.Length > 0)
               d.RelayPassword = relayPassword_.Password;

            d.VacationMessageIsOn = oooOn_.IsChecked is true;
            d.VacationSubject = oooSubject_.Text.Trim();
            d.VacationMessage = oooMessage_.Text;
            d.VacationInternalSubject = oooInternalSubject_.Text.Trim();
            d.VacationInternalMessage = oooInternalMessage_.Text;
            d.VacationExternalOverride = oooExternalOverride_.IsChecked is true;

            d.ExternalTagSubject = tagSubject_.IsChecked is true;
            d.ExternalTagHeader = tagHeader_.IsChecked is true;
            d.ExternalTagText = tagText_.Text.Trim();
            d.FirstContactTip = firstContact_.IsChecked is true;
            d.DisclaimerEnabled = disclaimerOn_.IsChecked is true;
            d.DisclaimerPlainText = disclaimerPlain_.Text;
            d.DisclaimerHTML = disclaimerHtml_.Text;

            d.DKIMSignEnabled = dkimOn_.IsChecked is true;
            d.DKIMSignAliasesEnabled = dkimAliases_.IsChecked is true;
            d.DKIMSelector = dkimSelector_.Text.Trim();
            d.DKIMPrivateKeyFile = dkimKeyFile_.Text.Trim();
            int hc = DialogFields.ComboValue(dkimHeaderCanon_);
            if (hc > 0) d.DKIMHeaderCanonicalizationMethod = hc;
            int bc = DialogFields.ComboValue(dkimBodyCanon_);
            if (bc > 0) d.DKIMBodyCanonicalizationMethod = bc;
            int alg = DialogFields.ComboValue(dkimAlgorithm_);
            if (alg > 0) d.DKIMSigningAlgorithm = alg;

            d.Save();
            ServerSession.Release(d);
            Close();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not save the domain: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      // ---- UI helpers ----

      private static TextBox NewInput() => new Wpf.Ui.Controls.TextBox();

      /// <summary>A selectable, read-only box in the style of the DKIM DNS-record
      /// display above: values the user must copy exactly (host names, TXT
      /// values) live in these rather than in labels, so they can be selected.</summary>
      private static TextBox ReadOnlyBox(double minHeight) => new()
      {
         IsReadOnly = true,
         AcceptsReturn = true,
         TextWrapping = TextWrapping.Wrap,
         FontSize = Typography.Caption,
         MinHeight = minHeight,
         Padding = new Thickness(DesignTokens.Space.Sm)
      };

      private static TextBox NewMemo() => new()
      {
         AcceptsReturn = true,
         Height = 80,
         TextWrapping = TextWrapping.Wrap,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

      /// <summary>One line of the rotation walkthrough, written into as the state changes.</summary>
      private static TextBlock StepLine()
      {
         var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
         t.SetResourceReference(FrameworkElement.StyleProperty, "TextCaption");
         return t;
      }

      /// <summary>
      /// The number that belongs to the tick box above it. The three limit boxes
      /// have no caption of their own and had no accessible name either, so each
      /// announced as a bare "edit" under a tick box whose words it needs; the box
      /// takes its name from that tick box, which is the only text there is, and
      /// the row it sits in carries the validation message for it.
      /// </summary>
      private static FieldRow NumberUnder(CheckBox owner, TextBox box)
      {
         AutomationProperties.SetName(box, MnemonicText.Strip((string)owner.Content));
         box.HorizontalAlignment = HorizontalAlignment.Left;
         box.MinWidth = 160;
         return DialogFields.Field(null, box);
      }

      /// <summary>
      /// A caption and its editor as one row, which names the editor to UI
      /// Automation. A TextBlock above a control tells UI Automation nothing, so
      /// without this every field in this dialog announced to a screen reader as
      /// an anonymous "edit".
      /// </summary>
      private static FieldRow Label(string text, FrameworkElement editor) => DialogFields.Field(text, editor);
   }
}
