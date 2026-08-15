using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal, tabbed editor for one domain: general, limits, signature and DKIM.
   /// Aliases and distribution lists are managed from the Domains page itself.
   /// </summary>
   public class DomainDialog : Window
   {
      private readonly string domainName_;

      // General
      private readonly CheckBox active_ = new() { Content = "Domain enabled", FontSize = 13 };
      private readonly TextBox name_ = NewInput();
      private readonly TextBox postmaster_ = NewInput();
      private readonly TextBox adDomain_ = NewInput();

      private readonly TextBlock status_ = new()
      {
         Foreground = Services.ThemeTokens.Danger,
         TextWrapping = TextWrapping.Wrap,
         VerticalAlignment = VerticalAlignment.Center,
         Margin = new Thickness(2, 0, 8, 0)
      };

      // Limits
      private readonly TextBox maxSize_ = NewInput();
      private readonly TextBox maxMessageSize_ = NewInput();
      private readonly TextBox maxAccountSize_ = NewInput();
      private readonly CheckBox maxAccountsOn_ = new() { Content = "Limit number of accounts", FontSize = 13 };
      private readonly TextBox maxAccounts_ = NewInput();
      private readonly CheckBox maxAliasesOn_ = new() { Content = "Limit number of aliases", FontSize = 13 };
      private readonly TextBox maxAliases_ = NewInput();
      private readonly CheckBox maxDistsOn_ = new() { Content = "Limit number of distribution lists", FontSize = 13 };
      private readonly TextBox maxDists_ = NewInput();
      private readonly CheckBox plusAddressingOn_ = new() { Content = "Enable plus addressing", FontSize = 13 };
      private readonly TextBox plusChar_ = NewInput();
      private readonly CheckBox greylisting_ = new() { Content = "Enable greylisting for this domain", FontSize = 13 };

      // Signature
      private readonly CheckBox signatureOn_ = new() { Content = "Add signature to outgoing messages", FontSize = 13 };
      private readonly ComboBox signatureMethod_ = new();
      private readonly CheckBox signReplies_ = new() { Content = "Add signature to replies", FontSize = 13 };
      private readonly CheckBox signLocal_ = new() { Content = "Add signature to local e-mail", FontSize = 13 };
      private readonly TextBox signaturePlain_ = NewMemo();
      private readonly TextBox signatureHtml_ = NewMemo();

      // DKIM
      private readonly CheckBox dkimOn_ = new() { Content = "Enable DKIM signing", FontSize = 13 };
      private readonly CheckBox dkimAliases_ = new() { Content = "Sign aliases too", FontSize = 13 };
      private readonly TextBox dkimSelector_ = NewInput();
      private readonly TextBox dkimKeyFile_ = NewInput();
      private readonly ComboBox dkimHeaderCanon_ = new();
      private readonly ComboBox dkimBodyCanon_ = new();
      private readonly ComboBox dkimAlgorithm_ = new();
      private readonly TextBox dkimDns_ = new()
      {
         IsReadOnly = true,
         AcceptsReturn = true,
         TextWrapping = TextWrapping.Wrap,
         FontSize = 12,
         MinHeight = 70,
         Padding = new Thickness(6),
         Margin = new Thickness(0, 0, 0, 4),
         Background = System.Windows.Media.Brushes.Transparent
      };
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
      private TextBlock rotStatus_;
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
         Title = "Domain - " + domainName;
         Width = 640;
         Height = 680;
         MinWidth = 560;
         MinHeight = 520;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var root = new Grid { Margin = new Thickness(18) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var header = new TextBlock
         {
            Text = domainName,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 12)
         };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetRow(header, 0);
         root.Children.Add(header);

         var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
         tabs.Items.Add(new TabItem { Header = "General", Content = BuildGeneral() });
         tabs.Items.Add(new TabItem { Header = "Names", Content = BuildNames() });
         tabs.Items.Add(new TabItem { Header = "Limits", Content = BuildLimits() });
         tabs.Items.Add(new TabItem { Header = "Signature", Content = BuildSignature() });
         tabs.Items.Add(new TabItem { Header = "DKIM", Content = BuildDkim() });
         Grid.SetRow(tabs, 1);
         root.Children.Add(tabs);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", IsCancel = true };
         cancel.Click += (s, e) => Close();
         buttons.Children.Add(save);
         buttons.Children.Add(cancel);

         var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
         footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
         footer.Children.Add(status_);
         Grid.SetColumn(buttons, 1);
         footer.Children.Add(buttons);
         Grid.SetRow(footer, 2);
         root.Children.Add(footer);

         Content = root;
         Loaded += (s, e) => { Load(); aliasEditor_?.OnEnter(); };
      }

      private ScrollViewer BuildGeneral()
      {
         var panel = TabPanel();
         panel.Children.Add(active_);
         panel.Children.Add(Label("Domain name (changing it renames the domain and moves every account, alias and list with it)"));
         panel.Children.Add(Input(name_));
         panel.Children.Add(Label("Postmaster address (mail to unknown recipients is redirected here)"));
         panel.Children.Add(Input(postmaster_));
         panel.Children.Add(Label("Active Directory domain (for AD-synchronised domains; optional)"));
         var adRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
         adDomain_.MinWidth = 320;
         adRow.Children.Add(adDomain_);
         var adBrowse = new Wpf.Ui.Controls.Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
         adBrowse.Click += BrowseAdDomain;
         adRow.Children.Add(adBrowse);
         panel.Children.Add(adRow);
         return Scroll(panel);
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
            Title = "Save the DKIM private key",
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
         catch (Exception) { }

         if (save.ShowDialog() != true)
            return;

         try
         {
            DkimKeyGenerator.Result result = DkimKeyGenerator.Generate(selector, domainName_);
            System.IO.File.WriteAllText(save.FileName, result.PrivateKeyPem);
            dkimKeyFile_.Text = save.FileName;
            dkimOn_.IsChecked = true;
            dkimDnsValue_ = result.DnsTxtValue;
            dkimDns_.Text = "Host/Name:  " + result.DnsHost + Environment.NewLine +
                            "Type:       TXT" + Environment.NewLine +
                            "Value:      " + result.DnsTxtValue;
            dkimDnsPanel_.Visibility = Visibility.Visible;
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not generate the DKIM key: " + ex.Message, "Control Panel");
         }
      }

      private void BrowseAdDomain(object sender, RoutedEventArgs e)
      {
         if (!ActiveDirectoryService.IsAvailable(out string reason))
         {
            MessageBox.Show("Active Directory is not available on this machine: " + reason, "Control Panel");
            return;
         }

         System.Collections.Generic.List<string> domains = ActiveDirectoryService.ListDomains();
         if (domains == null || domains.Count == 0)
         {
            MessageBox.Show("No Active Directory domains were found.", "Control Panel");
            return;
         }

         var menu = new ContextMenu();
         foreach (string dom in domains)
         {
            var item = new MenuItem { Header = dom };
            item.Click += (s2, e2) => adDomain_.Text = dom;
            menu.Items.Add(item);
         }
         menu.PlacementTarget = (UIElement) sender;
         menu.IsOpen = true;
      }

      private FrameworkElement BuildNames()
      {
         aliasEditor_ = CollectionSpecs.DomainAliases(domainName_);
         aliasEditor_.Margin = new Thickness(4, 8, 4, 4);
         return aliasEditor_;
      }

      private ScrollViewer BuildLimits()
      {
         var panel = TabPanel();
         panel.Children.Add(Label("Maximum domain size (MB, 0 = unlimited)"));
         panel.Children.Add(Input(maxSize_));
         panel.Children.Add(Label("Maximum message size (KB, 0 = unlimited)"));
         panel.Children.Add(Input(maxMessageSize_));
         panel.Children.Add(Label("Maximum size for accounts created in this domain (MB, 0 = unlimited)"));
         panel.Children.Add(Input(maxAccountSize_));
         panel.Children.Add(Separator());
         panel.Children.Add(maxAccountsOn_);
         panel.Children.Add(Input(maxAccounts_));
         panel.Children.Add(maxAliasesOn_);
         panel.Children.Add(Input(maxAliases_));
         panel.Children.Add(maxDistsOn_);
         panel.Children.Add(Input(maxDists_));
         panel.Children.Add(Separator());
         panel.Children.Add(plusAddressingOn_);
         panel.Children.Add(Label("Plus addressing character"));
         panel.Children.Add(Input(plusChar_));
         panel.Children.Add(greylisting_);
         return Scroll(panel);
      }

      private ScrollViewer BuildSignature()
      {
         signatureMethod_.Items.Add(Combo("Use only if account has no signature", 1));
         signatureMethod_.Items.Add(Combo("Overwrite account signature", 2));
         signatureMethod_.Items.Add(Combo("Append to account signature", 3));
         StyleCombo(signatureMethod_);

         var panel = TabPanel();
         panel.Children.Add(signatureOn_);
         panel.Children.Add(Label("Signature method"));
         panel.Children.Add(signatureMethod_);
         panel.Children.Add(signReplies_);
         panel.Children.Add(signLocal_);
         panel.Children.Add(Label("Plain-text signature"));
         panel.Children.Add(signaturePlain_);
         panel.Children.Add(Label("HTML signature"));
         panel.Children.Add(signatureHtml_);
         return Scroll(panel);
      }

      private ScrollViewer BuildDkim()
      {
         dkimHeaderCanon_.Items.Add(Combo("Simple", 1));
         dkimHeaderCanon_.Items.Add(Combo("Relaxed", 2));
         StyleCombo(dkimHeaderCanon_);
         dkimBodyCanon_.Items.Add(Combo("Simple", 1));
         dkimBodyCanon_.Items.Add(Combo("Relaxed", 2));
         StyleCombo(dkimBodyCanon_);
         dkimAlgorithm_.Items.Add(Combo("SHA1", 1));
         dkimAlgorithm_.Items.Add(Combo("SHA256", 2));
         StyleCombo(dkimAlgorithm_);

         var panel = TabPanel();
         panel.Children.Add(dkimOn_);
         panel.Children.Add(dkimAliases_);
         panel.Children.Add(Label("Selector"));
         panel.Children.Add(Input(dkimSelector_));
         panel.Children.Add(Label("Private key file"));
         var dkimKeyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
         Input(dkimKeyFile_);
         dkimKeyFile_.MinWidth = 320;
         dkimKeyFile_.Margin = new Thickness(0);
         dkimKeyRow.Children.Add(dkimKeyFile_);
         var dkimBrowse = new Wpf.Ui.Controls.Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
         System.Windows.Automation.AutomationProperties.SetAutomationId(dkimBrowse, "DkimKeyBrowse");
         dkimBrowse.Click += (s, e) =>
         {
            string file = PathPicker.PickFile(dkimKeyFile_.Text, "PEM/key files (*.pem;*.key)|*.pem;*.key|All files (*.*)|*.*");
            if (file != null)
               dkimKeyFile_.Text = file;
         };
         dkimKeyRow.Children.Add(dkimBrowse);
         panel.Children.Add(dkimKeyRow);

         var dkimGen = new Wpf.Ui.Controls.Button
         {
            Content = "Generate key pair\u2026",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 0, 8)
         };
         System.Windows.Automation.AutomationProperties.SetAutomationId(dkimGen, "DkimGenerate");
         dkimGen.Click += (s, e) => GenerateDkim();
         panel.Children.Add(dkimGen);

         dkimDnsPanel_ = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
         dkimDnsPanel_.Children.Add(Label("Publish this DNS TXT record at your DNS provider, then enable DKIM:"));
         dkimDns_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         dkimDnsPanel_.Children.Add(dkimDns_);
         var dkimCopy = new Wpf.Ui.Controls.Button { Content = "Copy DNS value", Margin = new Thickness(0, 0, 0, 0) };
         System.Windows.Automation.AutomationProperties.SetAutomationId(dkimCopy, "DkimCopyDns");
         dkimCopy.Click += (s, e) =>
         {
            try { if (!string.IsNullOrEmpty(dkimDnsValue_)) Clipboard.SetText(dkimDnsValue_); } catch (Exception) { }
         };
         dkimDnsPanel_.Children.Add(dkimCopy);
         panel.Children.Add(dkimDnsPanel_);

         panel.Children.Add(Label("Header canonicalization"));
         panel.Children.Add(dkimHeaderCanon_);
         panel.Children.Add(Label("Body canonicalization"));
         panel.Children.Add(dkimBodyCanon_);
         panel.Children.Add(Label("Signing algorithm"));
         panel.Children.Add(dkimAlgorithm_);

         panel.Children.Add(Separator());
         panel.Children.Add(BuildRotation());

         return Scroll(panel);
      }

      /// <summary>
      /// The staged key-rotation walkthrough. Rotation is a sequence with an
      /// out-of-band wait in the middle (publishing a DNS record and letting it
      /// propagate), so the section reads as numbered steps and the Promote
      /// button is gated on an actual DNS check instead of trusting the
      /// administrator to have waited long enough.
      /// </summary>
      private StackPanel BuildRotation()
      {
         var section = new StackPanel();

         var title = new TextBlock
         {
            Text = "Key rotation",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2)
         };
         title.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         section.Children.Add(title);

         var intro = Label("Replace the DKIM key without a gap in verification: stage a new key next to " +
                           "the current one, publish its DNS record, wait for the record to propagate, " +
                           "check it, and only then promote it.");
         intro.TextWrapping = TextWrapping.Wrap;
         section.Children.Add(intro);

         rotCurrent_ = Label("");
         rotCurrent_.TextWrapping = TextWrapping.Wrap;
         section.Children.Add(rotCurrent_);

         // Step 1 quotes the primary fields above, so it must follow their edits
         // or it would describe a selector the user has already typed over.
         dkimSelector_.TextChanged += (s, e) => UpdateRotationUi();
         dkimKeyFile_.TextChanged += (s, e) => UpdateRotationUi();

         // Step 2 while nothing is staged: choose a selector name and stage a key.
         rotStartPanel_ = new StackPanel();
         rotStartPanel_.Children.Add(Label("2. Stage a new key pair under a new selector name:"));
         var startRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
         Input(rotSelector_);
         rotSelector_.MinWidth = 200;
         rotSelector_.Margin = new Thickness(0);
         System.Windows.Automation.AutomationProperties.SetAutomationId(rotSelector_, "DkimRotationSelector");
         startRow.Children.Add(rotSelector_);
         var start = new Wpf.Ui.Controls.Button { Content = "Generate and stage key…", Margin = new Thickness(8, 0, 0, 0) };
         System.Windows.Automation.AutomationProperties.SetAutomationId(start, "DkimRotationStart");
         start.Click += (s, e) => StartRotation();
         startRow.Children.Add(start);
         rotStartPanel_.Children.Add(startRow);
         section.Children.Add(rotStartPanel_);

         // Steps 2-5 once a rotation is staged.
         rotStagedPanel_ = new StackPanel { Visibility = Visibility.Collapsed };

         rotStagedInfo_ = Label("");
         rotStagedInfo_.TextWrapping = TextWrapping.Wrap;
         rotStagedPanel_.Children.Add(rotStagedInfo_);

         rotStagedPanel_.Children.Add(Label("3. Publish this DNS TXT record at your DNS provider. Host/Name:"));
         rotDnsHost_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         rotStagedPanel_.Children.Add(rotDnsHost_);
         rotStagedPanel_.Children.Add(Label("TXT value:"));
         rotDnsValue_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         rotStagedPanel_.Children.Add(rotDnsValue_);

         var copyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
         var copyHost = new Wpf.Ui.Controls.Button { Content = "Copy host" };
         System.Windows.Automation.AutomationProperties.SetAutomationId(copyHost, "DkimRotationCopyHost");
         copyHost.Click += (s, e) => CopyToClipboard(rotDnsHost_.Text);
         copyRow.Children.Add(copyHost);
         var copyValue = new Wpf.Ui.Controls.Button { Content = "Copy value", Margin = new Thickness(8, 0, 0, 0) };
         System.Windows.Automation.AutomationProperties.SetAutomationId(copyValue, "DkimRotationCopyValue");
         copyValue.Click += (s, e) => CopyToClipboard(rotExpectedTxt_);
         copyRow.Children.Add(copyValue);
         rotStagedPanel_.Children.Add(copyRow);

         rotStagedPanel_.Children.Add(Label("4. Check that the record is visible in DNS:"));
         rotCheck_ = new Wpf.Ui.Controls.Button { Content = "Check DNS" };
         System.Windows.Automation.AutomationProperties.SetAutomationId(rotCheck_, "DkimRotationCheck");
         rotCheck_.Click += async (s, e) => await CheckRotationDns();
         rotStagedPanel_.Children.Add(rotCheck_);

         rotStatus_ = new TextBlock
         {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Margin = new Thickness(0, 6, 0, 8)
         };
         rotStagedPanel_.Children.Add(rotStatus_);

         rotStagedPanel_.Children.Add(Label("5. Promote the staged key to become the signing key:"));
         var promoteRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

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
            Content = "Promote…",
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            IsEnabled = false,
            ToolTip = "Enabled once \"Check DNS\" has confirmed the published record matches the staged key."
         };
         ToolTipService.SetShowOnDisabled(rotPromote_, true);
         System.Windows.Automation.AutomationProperties.SetAutomationId(rotPromote_, "DkimRotationPromote");
         rotPromote_.Click += (s, e) => PromoteRotation();
         promoteRow.Children.Add(rotPromote_);

         var cancelRotation = new Wpf.Ui.Controls.Button { Content = "Cancel rotation…", Margin = new Thickness(8, 0, 0, 0) };
         System.Windows.Automation.AutomationProperties.SetAutomationId(cancelRotation, "DkimRotationCancel");
         cancelRotation.Click += (s, e) => CancelRotation();
         promoteRow.Children.Add(cancelRotation);

         rotStagedPanel_.Children.Add(promoteRow);
         section.Children.Add(rotStagedPanel_);
         return section;
      }

      // ---- DKIM key rotation ----

      private void StartRotation()
      {
         string selector = rotSelector_.Text.Trim();
         if (selector.Length == 0)
         {
            MessageBox.Show("Enter a selector name for the new key first.", "Control Panel");
            return;
         }

         // Reusing the current selector name is refused outright: it would replace
         // the published key under the same DNS name instead of running two keys
         // side by side, and while resolvers still serve the old record every
         // message signed with the new key fails verification - the exact outage
         // a staged rotation exists to prevent.
         if (string.Equals(selector, dkimSelector_.Text.Trim(), StringComparison.OrdinalIgnoreCase))
         {
            MessageBox.Show("The new selector must be different from the current selector (\"" + selector + "\"). " +
                            "A rotation runs both keys side by side under different DNS names.", "Control Panel");
            return;
         }

         var save = new Microsoft.Win32.SaveFileDialog
         {
            Title = "Save the new DKIM private key",
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
         catch (Exception) { }

         if (save.ShowDialog() != true)
            return;

         DkimKeyGenerator.Result generated;
         try
         {
            generated = DkimKeyGenerator.Generate(selector, domainName_);
            System.IO.File.WriteAllText(save.FileName, generated.PrivateKeyPem);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not generate the DKIM key: " + ex.Message, "Control Panel");
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not stage the rotation on the server: " + ServerSession.DescribeComError(ex),
               "Control Panel");
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
         SetRotationStatus("Not checked yet. Publish the record, allow time for propagation, then press \"Check DNS\".",
            ThemeTokens.Info);
      }

      private const string RotationNotFoundText =
         "The record was not found yet. New DNS records usually appear within minutes but can take hours to " +
         "propagate - publish the record above if you have not already, then check again later.";

      private static string RotationLookupFailedText(string error) =>
         "The DNS lookup itself failed: " + error + " This says nothing about the record - this machine could not " +
         "get an answer from its DNS server. Check the network and try again.";

      private async Task CheckRotationDns()
      {
         if (rotChecking_)
            return;

         if (rotStagedSelector_.Length == 0)
         {
            SetRotationStatus("The staged pair has no selector, so there is no DNS name to check. Cancel the rotation " +
               "and start again.", ThemeTokens.Danger);
            return;
         }

         string host = rotStagedSelector_ + "._domainkey." + domainName_;
         string expected = rotExpectedTxt_;

         rotChecking_ = true;
         rotCheck_.IsEnabled = false;
         SetRotationStatus("Checking " + host + "…", ThemeTokens.Info);

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
                  SetRotationStatus("The published record matches the staged key. It is safe to promote.",
                     ThemeTokens.Success);
                  break;

               case DnsTxtLookup.MatchStatus.NoRecord:
                  SetRotationStatus(RotationNotFoundText, ThemeTokens.Warning);
                  break;

               case DnsTxtLookup.MatchStatus.FoundButDifferent:
                  SetRotationStatus("A TXT record exists at " + host + ", but it does not match the staged key. " +
                     "Waiting will not fix this: correct the published record so it matches the value above, then " +
                     "check again." + DescribeFoundRecords(result.Records), ThemeTokens.Danger);
                  break;

               default:
                  SetRotationStatus(RotationLookupFailedText(result.Error), ThemeTokens.Danger);
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
               SetRotationStatus("A TXT record exists, but the staged private key file (" + rotStagedKeyFile_ + ") " +
                  "could not be read from this machine, so the record cannot be compared against the key. Run this " +
                  "check on the server itself, or cancel the rotation and start it again from here.",
                  ThemeTokens.Warning);
               break;

            case DnsTxtLookup.LookupStatus.NoRecord:
               SetRotationStatus(RotationNotFoundText, ThemeTokens.Warning);
               break;

            default:
               SetRotationStatus(RotationLookupFailedText(found.Error), ThemeTokens.Danger);
               break;
         }
      }

      private void PromoteRotation()
      {
         string oldSelector = dkimSelector_.Text.Trim();
         string oldHost = oldSelector.Length > 0 ? oldSelector + "._domainkey." + domainName_ : "(none)";

         if (MessageBox.Show(
             "Promote the staged selector \"" + rotStagedSelector_ + "\"?\n\n" +
             "From the next message on, mail from " + domainName_ + " is signed with the new key, and the old " +
             "selector is cleared from the configuration.\n\n" +
             "Leave the OLD DNS record (" + oldHost + ") published for at least a few days. Mail signed with the old " +
             "key may still be in transit, and receivers can only verify it for as long as that record exists.",
             "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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

            string newSelector = (string) d.DKIMSelector ?? "";
            string newKeyFile = (string) d.DKIMPrivateKeyFile ?? "";
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

            MessageBox.Show("The rotation is complete: mail is now signed with selector \"" + newSelector + "\".\n\n" +
               "Keep the old DNS record (" + oldHost + ") for a few more days while mail signed with the old key is " +
               "still in transit, then remove it.", "Control Panel");
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not promote the staged key: " + ServerSession.DescribeComError(ex), "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private void CancelRotation()
      {
         if (MessageBox.Show(
             "Cancel this rotation?\n\nThe staged selector \"" + rotStagedSelector_ + "\" is removed from the server " +
             "configuration; signing continues with the current key, untouched. The generated key file stays on disk, " +
             "and the DNS record for the staged selector - if you already published it - can simply be deleted.",
             "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not cancel the rotation: " + ServerSession.DescribeComError(ex), "Control Panel");
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
            SetRotationStatus("The staged rotation is incomplete (the selector or the key file is missing), so the " +
               "server will refuse to promote it. Cancel the rotation and start again.", ThemeTokens.Danger);
         else
            SetRotationStatus("A rotation is already staged. If the DNS record is published, press \"Check DNS\"; " +
               "promoting unlocks once the check passes in this session.", ThemeTokens.Info);
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
            return "v=DKIM1; k=rsa; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
         }
         catch (Exception)
         {
            return null;
         }
      }

      private void UpdateRotationUi()
      {
         string current = dkimSelector_.Text.Trim();
         string currentFile = dkimKeyFile_.Text.Trim();
         rotCurrent_.Text = current.Length > 0
            ? "1. Currently signing with selector \"" + current + "\"" +
              (currentFile.Length > 0 ? " (key file: " + currentFile + ")." : ".")
            : "1. No signing selector is configured yet.";

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

         rotStagedInfo_.Text = "2. A rotation is staged: selector \"" + rotStagedSelector_ + "\", key file " +
            rotStagedKeyFile_ + ".";
         rotDnsHost_.Text = rotStagedSelector_.Length > 0
            ? rotStagedSelector_ + "._domainkey." + domainName_
            : "(the staged pair has no selector)";
         rotDnsValue_.Text = rotExpectedTxt_ ?? "(The staged private key file could not be read from this machine, " +
            "so the record value cannot be shown here. It was shown when the rotation was started; if it is lost, " +
            "cancel the rotation and start again.)";
         rotPromote_.IsEnabled = rotCheckPassed_;
      }

      private void SetRotationStatus(string text, System.Windows.Media.Brush brush)
      {
         rotStatus_.Text = text;
         rotStatus_.Foreground = brush;
      }

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
         try { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); } catch (Exception) { }
      }

      private void Load()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic d = domains.ItemByName[domainName_];
            active_.IsChecked = (bool) d.Active;
            name_.Text = (string) d.Name ?? domainName_;
            postmaster_.Text = (string) d.Postmaster ?? "";
            adDomain_.Text = (string) d.ADDomainName ?? "";

            maxSize_.Text = ((int) d.MaxSize).ToString();
            maxMessageSize_.Text = ((int) d.MaxMessageSize).ToString();
            maxAccountSize_.Text = ((int) d.MaxAccountSize).ToString();
            maxAccountsOn_.IsChecked = (bool) d.MaxNumberOfAccountsEnabled;
            maxAccounts_.Text = ((int) d.MaxNumberOfAccounts).ToString();
            maxAliasesOn_.IsChecked = (bool) d.MaxNumberOfAliasesEnabled;
            maxAliases_.Text = ((int) d.MaxNumberOfAliases).ToString();
            maxDistsOn_.IsChecked = (bool) d.MaxNumberOfDistributionListsEnabled;
            maxDists_.Text = ((int) d.MaxNumberOfDistributionLists).ToString();
            plusAddressingOn_.IsChecked = (bool) d.PlusAddressingEnabled;
            plusChar_.Text = (string) d.PlusAddressingCharacter ?? "";
            greylisting_.IsChecked = (bool) d.AntiSpamEnableGreylisting;

            signatureOn_.IsChecked = (bool) d.SignatureEnabled;
            SelectCombo(signatureMethod_, (int) d.SignatureMethod);
            signReplies_.IsChecked = (bool) d.AddSignaturesToReplies;
            signLocal_.IsChecked = (bool) d.AddSignaturesToLocalMail;
            signaturePlain_.Text = (string) d.SignaturePlainText ?? "";
            signatureHtml_.Text = (string) d.SignatureHTML ?? "";

            dkimOn_.IsChecked = (bool) d.DKIMSignEnabled;
            dkimAliases_.IsChecked = (bool) d.DKIMSignAliasesEnabled;
            dkimSelector_.Text = (string) d.DKIMSelector ?? "";
            dkimKeyFile_.Text = (string) d.DKIMPrivateKeyFile ?? "";
            SelectCombo(dkimHeaderCanon_, (int) d.DKIMHeaderCanonicalizationMethod);
            SelectCombo(dkimBodyCanon_, (int) d.DKIMBodyCanonicalizationMethod);
            SelectCombo(dkimAlgorithm_, (int) d.DKIMSigningAlgorithm);

            // The staged rotation pair, if an earlier session left one behind.
            // Read defensively: against an older server that predates the
            // rotation API these properties do not exist, and the rest of the
            // dialog must keep working without them.
            string stagedSelector = "", stagedKeyFile = "";
            try
            {
               stagedSelector = ((string) d.DKIMSecondarySelector ?? "").Trim();
               stagedKeyFile = ((string) d.DKIMSecondaryPrivateKeyFile ?? "").Trim();
            }
            catch (Exception) { }
            RestoreStagedRotation(stagedSelector, stagedKeyFile);

            ServerSession.Release(d);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load the domain: " + ex.Message, "Control Panel");
            Close();
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private void Save()
      {
         if (!NumericField.TryValidate(maxSize_.Text, "Maximum domain size (MB)", 0, int.MaxValue, out int msV, out bool hasMs, out string error)
          || !NumericField.TryValidate(maxMessageSize_.Text, "Maximum message size (KB)", 0, int.MaxValue, out int mmsV, out bool hasMms, out error)
          || !NumericField.TryValidate(maxAccountSize_.Text, "Maximum account size (MB)", 0, int.MaxValue, out int masV, out bool hasMas, out error)
          || !NumericField.TryValidate(maxAccounts_.Text, "Maximum number of accounts", 0, int.MaxValue, out int mnaV, out bool hasMna, out error)
          || !NumericField.TryValidate(maxAliases_.Text, "Maximum number of aliases", 0, int.MaxValue, out int mnalV, out bool hasMnal, out error)
          || !NumericField.TryValidate(maxDists_.Text, "Maximum number of distribution lists", 0, int.MaxValue, out int mndV, out bool hasMnd, out error))
         {
            status_.Text = error;
            return;
         }
         status_.Text = "";

         string newName = name_.Text.Trim();
         if (newName.Length == 0 || !newName.Contains('.'))
         {
            status_.Text = "Enter a valid domain name.";
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
                "Rename " + domainName_ + " to " + newName + "?\n\nEvery account, alias and distribution " +
                "list moves to the new name, and forwards or memberships in other domains that point at " +
                domainName_ + " addresses are updated to match. Mail sent to the old name will no longer " +
                "be accepted.",
                "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic d = domains.ItemByName[domainName_];
            if (renaming)
               d.Name = newName;
            d.Active = active_.IsChecked == true;
            d.Postmaster = postmaster_.Text.Trim();
            d.ADDomainName = adDomain_.Text.Trim();

            if (hasMs) d.MaxSize = msV;
            if (hasMms) d.MaxMessageSize = mmsV;
            if (hasMas) d.MaxAccountSize = masV;
            d.MaxNumberOfAccountsEnabled = maxAccountsOn_.IsChecked == true;
            if (hasMna) d.MaxNumberOfAccounts = mnaV;
            d.MaxNumberOfAliasesEnabled = maxAliasesOn_.IsChecked == true;
            if (hasMnal) d.MaxNumberOfAliases = mnalV;
            d.MaxNumberOfDistributionListsEnabled = maxDistsOn_.IsChecked == true;
            if (hasMnd) d.MaxNumberOfDistributionLists = mndV;
            d.PlusAddressingEnabled = plusAddressingOn_.IsChecked == true;
            if (plusChar_.Text.Length > 0) d.PlusAddressingCharacter = plusChar_.Text;
            d.AntiSpamEnableGreylisting = greylisting_.IsChecked == true;

            d.SignatureEnabled = signatureOn_.IsChecked == true;
            int sm = ComboValue(signatureMethod_);
            if (sm > 0) d.SignatureMethod = sm;
            d.AddSignaturesToReplies = signReplies_.IsChecked == true;
            d.AddSignaturesToLocalMail = signLocal_.IsChecked == true;
            d.SignaturePlainText = signaturePlain_.Text;
            d.SignatureHTML = signatureHtml_.Text;

            d.DKIMSignEnabled = dkimOn_.IsChecked == true;
            d.DKIMSignAliasesEnabled = dkimAliases_.IsChecked == true;
            d.DKIMSelector = dkimSelector_.Text.Trim();
            d.DKIMPrivateKeyFile = dkimKeyFile_.Text.Trim();
            int hc = ComboValue(dkimHeaderCanon_);
            if (hc > 0) d.DKIMHeaderCanonicalizationMethod = hc;
            int bc = ComboValue(dkimBodyCanon_);
            if (bc > 0) d.DKIMBodyCanonicalizationMethod = bc;
            int alg = ComboValue(dkimAlgorithm_);
            if (alg > 0) d.DKIMSigningAlgorithm = alg;

            d.Save();
            ServerSession.Release(d);
            Close();
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the domain: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      // ---- UI helpers ----

      private static StackPanel TabPanel() => new() { Margin = new Thickness(4, 12, 4, 4) };

      private static ScrollViewer Scroll(StackPanel panel) => new()
      {
         Content = panel,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
         HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
      };

      private static TextBox NewInput() => new Wpf.Ui.Controls.TextBox();

      /// <summary>A selectable, read-only box in the style of the DKIM DNS-record
      /// display above: values the user must copy exactly (host names, TXT
      /// values) live in these rather than in labels, so they can be selected.</summary>
      private static TextBox ReadOnlyBox(double minHeight) => new()
      {
         IsReadOnly = true,
         AcceptsReturn = true,
         TextWrapping = TextWrapping.Wrap,
         FontSize = 12,
         MinHeight = minHeight,
         Padding = new Thickness(6),
         Margin = new Thickness(0, 0, 0, 4),
         Background = System.Windows.Media.Brushes.Transparent
      };

      private static TextBox NewMemo() => new()
      {
         AcceptsReturn = true,
         Height = 80,
         TextWrapping = TextWrapping.Wrap,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

      private static TextBlock Label(string text)
      {
         var t = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }

      private static TextBox Input(TextBox box)
      {
         box.FontSize = 13;
         box.Padding = new Thickness(6);
         box.Margin = new Thickness(0, 0, 0, 8);
         box.Background = System.Windows.Media.Brushes.Transparent;
         box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         return box;
      }

      private static Border Separator() => new()
      {
         Height = 1,
         Margin = new Thickness(0, 12, 0, 12),
         Background = System.Windows.Media.Brushes.Gray,
         Opacity = 0.3
      };

      private static ComboBoxItem Combo(string text, int value) => new() { Content = text, Tag = value };

      private static void StyleCombo(ComboBox combo)
      {
         combo.FontSize = 13;
         combo.Margin = new Thickness(0, 0, 0, 8);
      }

      private static void SelectCombo(ComboBox combo, int value)
      {
         foreach (ComboBoxItem item in combo.Items)
            if ((int) item.Tag == value)
            {
               combo.SelectedItem = item;
               return;
            }
      }

      private static int ComboValue(ComboBox combo) =>
         combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : 0;
   }
}
