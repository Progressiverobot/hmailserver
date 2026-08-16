using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal, tabbed editor for one account: general, forwarding, auto-reply,
   /// signature and Active Directory.
   /// </summary>
   public class AccountDialog : Window
   {
      private readonly string domainName_;
      private readonly string address_;

      private readonly TextBlock status_ = new()
      {
         Foreground = Services.ThemeTokens.Danger,
         TextWrapping = TextWrapping.Wrap,
         VerticalAlignment = VerticalAlignment.Center,
         Margin = new Thickness(2, 0, 8, 0)
      };

      // General
      private readonly CheckBox active_ = new() { Content = "Account enabled", FontSize = 13 };
      private readonly TextBox addressBox_ = NewInput();
      private readonly ComboBox adminLevel_ = new();
      private readonly TextBox quota_ = NewInput();
      private readonly TextBox firstName_ = NewInput();
      private readonly TextBox lastName_ = NewInput();
      private readonly Wpf.Ui.Controls.PasswordBox password_ = new();
      private readonly Wpf.Ui.Controls.TextBox generatedShow_ = new();
      private readonly TextBlock pwStrength_ = new() { FontSize = 11.5, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
      private readonly TextBlock lastLogon_ = new() { FontSize = 12.5, Margin = new Thickness(0, 0, 0, 8) };

      // Forwarding
      private readonly CheckBox forwardOn_ = new() { Content = "Forward incoming mail", FontSize = 13 };
      private readonly TextBox forwardTo_ = NewInput();
      private readonly CheckBox forwardKeep_ = new() { Content = "Keep original message", FontSize = 13 };
      private readonly CheckBox forwardAbortSpam_ = new() { Content = "Do not forward messages flagged as spam", FontSize = 13 };

      // Auto-reply
      private readonly CheckBox vacationOn_ = new() { Content = "Send automatic reply (vacation message)", FontSize = 13 };
      private readonly TextBox vacationSubject_ = NewInput();
      private readonly TextBox vacationBody_ = NewMemo();
      private readonly CheckBox vacationExpires_ = new() { Content = "Stop sending replies after a date", FontSize = 13 };
      private readonly DatePicker vacationExpiresDate_ = new();
      private readonly DatePicker vacationBeginDate_ = new();
      private readonly CheckBox vacationAbortSpam_ = new() { Content = "Do not reply to messages flagged as spam", FontSize = 13 };

      // Signature
      private readonly CheckBox signatureOn_ = new() { Content = "Add signature to outgoing messages", FontSize = 13 };
      private readonly TextBox signaturePlain_ = NewMemo();
      private readonly TextBox signatureHtml_ = NewMemo();

      // Sieve filter (RFC 5228) - the account's active script
      private readonly TextBox sieveScript_ = new()
      {
         AcceptsReturn = true,
         Height = 300,
         TextWrapping = TextWrapping.NoWrap,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
         HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
         FontFamily = new System.Windows.Media.FontFamily("Consolas"),
         FontSize = 12.5
      };

      // Active Directory
      // "or a local Windows account" is not padding. The tab is titled Active Directory
      // throughout, and an empty domain quietly means "a local Windows account on this
      // computer" - which is the only form of this feature available to anyone with no
      // domain at all, and was undiscoverable from the interface.
      private readonly CheckBox isAd_ = new() { Content = "Check this password against Windows (Active Directory, or a local Windows account)", FontSize = 13 };

      // What the values below actually do, restated as a sentence. Updated as the
      // domain box is typed in, because the difference between the two behaviours is
      // an empty box rather than anything the reader can see.
      private readonly TextBlock directoryEffect_ = new()
      {
         FontSize = 12,
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(0, 0, 0, 8)
      };
      private readonly TextBox adDomain_ = NewInput();
      private readonly TextBox adUser_ = NewInput();

      // External (fetch) accounts, account rules, IMAP folders — embedded editors
      private CollectionEditorView fetchEditor_;
      private RulesView accountRules_;
      private readonly ListBox folderList_ = new() { Height = 220, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
      private readonly TextBlock folderStatus_ = new() { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };

      // What Load() read from the server, so Save can write the Sieve file only on
      // a real edit. Null until Load runs.
      private string loadedSieveScript_;

      public AccountDialog(Window owner, string domainName, string address)
      {
         domainName_ = domainName;
         address_ = address;

         Owner = owner;
         Title = "Account - " + address;
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
            Text = address,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 0, 0, 12)
         };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetRow(header, 0);
         root.Children.Add(header);

         var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
         tabs.Items.Add(new TabItem { Header = "General", Content = BuildGeneral() });
         tabs.Items.Add(new TabItem { Header = "Forwarding", Content = BuildForwarding() });
         tabs.Items.Add(new TabItem { Header = "Auto-reply", Content = BuildAutoReply() });
         tabs.Items.Add(new TabItem { Header = "Signature", Content = BuildSignature() });
         tabs.Items.Add(new TabItem { Header = "Sieve", Content = BuildSieve() });
         tabs.Items.Add(new TabItem { Header = "External", Content = BuildExternal() });
         tabs.Items.Add(new TabItem { Header = "Rules", Content = BuildRules() });
         tabs.Items.Add(new TabItem { Header = "Folders", Content = BuildFolders() });
         tabs.Items.Add(new TabItem { Header = "Directory", Content = BuildDirectory() });
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
         Loaded += (s, e) =>
         {
            Load();
            fetchEditor_?.OnEnter();
            accountRules_?.OnEnter();
            LoadFolders();
         };
      }

      private ScrollViewer BuildGeneral()
      {
         adminLevel_.Items.Add(Combo("Normal user", 0));
         adminLevel_.Items.Add(Combo("Domain administrator", 1));
         adminLevel_.Items.Add(Combo("Server administrator", 2));
         StyleCombo(adminLevel_);

         var panel = TabPanel();
         panel.Children.Add(active_);
         panel.Children.Add(Label("Address (changing it renames the mailbox; it must stay in this domain)"));
         panel.Children.Add(Input(addressBox_));
         panel.Children.Add(Label("Administration level"));
         panel.Children.Add(adminLevel_);
         panel.Children.Add(Label("Quota (MB, 0 = unlimited)"));
         panel.Children.Add(Input(quota_));
         panel.Children.Add(Label("First name"));
         panel.Children.Add(Input(firstName_));
         panel.Children.Add(Label("Last name"));
         panel.Children.Add(Input(lastName_));
         panel.Children.Add(Label("New password (leave empty to keep current)"));
         password_.FontSize = 13;
         password_.Padding = new Thickness(6);
         password_.Margin = new Thickness(0, 0, 0, 6);
         password_.PasswordChanged += (s, e) =>
         {
            UpdatePasswordStrength();
            generatedShow_.Visibility = Visibility.Collapsed;
         };
         panel.Children.Add(password_);

         var genBtn = new Wpf.Ui.Controls.Button { Content = "Generate strong password", Margin = new Thickness(0, 0, 0, 6) };
         System.Windows.Automation.AutomationProperties.SetAutomationId(genBtn, "GeneratePassword");
         genBtn.Click += (s, e) => GeneratePassword();
         panel.Children.Add(genBtn);

         generatedShow_.IsReadOnly = true;
         generatedShow_.FontSize = 13;
         generatedShow_.FontFamily = new System.Windows.Media.FontFamily("Consolas");
         generatedShow_.Visibility = Visibility.Collapsed;
         generatedShow_.Margin = new Thickness(0, 0, 0, 6);
         generatedShow_.MaxWidth = 320;
         generatedShow_.HorizontalAlignment = HorizontalAlignment.Left;
         System.Windows.Automation.AutomationProperties.SetAutomationId(generatedShow_, "GeneratedPassword");
         panel.Children.Add(generatedShow_);

         panel.Children.Add(pwStrength_);
         UpdatePasswordStrength();
         panel.Children.Add(Label("Last logon"));
         lastLogon_.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(lastLogon_);
         return Scroll(panel);
      }

      private ScrollViewer BuildForwarding()
      {
         var panel = TabPanel();
         panel.Children.Add(forwardOn_);
         panel.Children.Add(Label("Forward to"));
         panel.Children.Add(Input(forwardTo_));
         panel.Children.Add(forwardKeep_);
         panel.Children.Add(forwardAbortSpam_);
         return Scroll(panel);
      }

      private ScrollViewer BuildAutoReply()
      {
         var panel = TabPanel();
         panel.Children.Add(vacationOn_);
         panel.Children.Add(Label("Reply subject"));
         panel.Children.Add(Input(vacationSubject_));
         panel.Children.Add(Label("Reply message"));
         panel.Children.Add(vacationBody_);
         panel.Children.Add(Separator());
         panel.Children.Add(Label("Start date"));
         vacationBeginDate_.HorizontalAlignment = HorizontalAlignment.Left;
         vacationBeginDate_.MinWidth = 160;
         vacationBeginDate_.Margin = new Thickness(0, 0, 0, 8);
         panel.Children.Add(vacationBeginDate_);

         panel.Children.Add(vacationExpires_);
         panel.Children.Add(Label("Expiry date"));
         vacationExpiresDate_.HorizontalAlignment = HorizontalAlignment.Left;
         vacationExpiresDate_.MinWidth = 160;
         vacationExpiresDate_.Margin = new Thickness(0, 0, 0, 8);
         panel.Children.Add(vacationExpiresDate_);
         panel.Children.Add(vacationAbortSpam_);
         return Scroll(panel);
      }

      private ScrollViewer BuildSignature()
      {
         var panel = TabPanel();
         panel.Children.Add(signatureOn_);
         panel.Children.Add(Label("Plain-text signature"));
         panel.Children.Add(signaturePlain_);
         panel.Children.Add(Label("HTML signature"));
         panel.Children.Add(signatureHtml_);
         return Scroll(panel);
      }

      private ScrollViewer BuildSieve()
      {
         var panel = TabPanel();
         panel.Children.Add(new TextBlock
         {
            Text = "Active Sieve (RFC 5228) filter script for this account. It runs during local " +
                   "delivery and supports keep, fileinto, discard and redirect. Leave empty to disable. " +
                   "Multiple named scripts can be managed over ManageSieve.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
         });
         panel.Children.Add(sieveScript_);
         return Scroll(panel);
      }

      /// <summary>
      ///    The Directory tab, which used to be a checkbox and two unexplained boxes.
      ///
      ///    Three things decide whether this account can ever log in, and none of them
      ///    was stated anywhere the administrator could see:
      ///
      ///    1. An EMPTY domain does not mean "no directory". SSPIValidation treats an
      ///       empty domain - and "." and this computer's own name - as "validate a
      ///       LOCAL Windows account". That is a legitimate and useful way to run the
      ///       server for anyone who has no Active Directory at all, and it was
      ///       completely undiscoverable: nothing on this tab, which is titled Active
      ///       Directory throughout, hints that local Windows accounts are an option.
      ///    2. A REAL domain requires the SERVER's host to be domain-joined, because
      ///       LogonUser does. On a workgroup host every attempt returns
      ///       ERROR_LOGON_FAILURE - measured as 1326 for a nonexistent domain, a real
      ///       but unreachable one, and a wrong password alike - so the account cannot
      ///       log in and cannot be told why.
      ///    3. There is a way round (2), and it is on a different page entirely: LDAP
      ///       directory authentication binds to the directory over the network and
      ///       needs no domain join.
      ///
      ///    The domain-join state deliberately is NOT asserted here. It is the SERVER's
      ///    host that must be joined, and this Control Panel may be running somewhere
      ///    else - claiming otherwise would be the same mistake the LDAP test card is
      ///    careful to disclose about itself.
      /// </summary>
      private ScrollViewer BuildDirectory()
      {
         var panel = TabPanel();
         panel.Children.Add(isAd_);

         panel.Children.Add(Note(
            "With this on, the password is not stored here at all - Windows is asked to check it. Leave it off "
            + "and the account uses the password on the Account tab."));

         panel.Children.Add(Label("Windows domain (leave empty for a local Windows account)"));
         panel.Children.Add(Input(adDomain_));
         panel.Children.Add(directoryEffect_);

         panel.Children.Add(Label("Windows user name"));
         panel.Children.Add(Input(adUser_));

         var browse = new Wpf.Ui.Controls.Button
         {
            Content = "Browse Active Directory\u2026",
            Margin = new Thickness(0, 4, 0, 0)
         };
         browse.Click += (s, e) => BrowseActiveDirectory();
         panel.Children.Add(browse);

         panel.Children.Add(Note(
            "A domain name here needs the SERVER's own computer to be joined to that domain, because Windows "
            + "validates it with LogonUser. From a computer that is not joined, every attempt fails as though the "
            + "password were wrong - including when the domain name is simply misspelt - so a mailbox configured "
            + "this way on an unjoined server can never log in and never says why. If the server is not "
            + "domain-joined, use LDAP directory authentication on the Directory authentication page instead: it "
            + "binds to the directory over the network and needs no domain join."));

         adDomain_.TextChanged += (s, e) => RefreshDirectoryEffect_();
         isAd_.Checked += (s, e) => RefreshDirectoryEffect_();
         isAd_.Unchecked += (s, e) => RefreshDirectoryEffect_();

         RefreshDirectoryEffect_();

         return Scroll(panel);
      }

      /// <summary>
      ///    Says, in plain words, which of the two things the values above actually do -
      ///    because "Active Directory domain: (empty)" reads as "not configured" and is
      ///    in fact a working configuration with completely different behaviour.
      /// </summary>
      private void RefreshDirectoryEffect_()
      {
         if (isAd_.IsChecked != true)
         {
            directoryEffect_.Text = "";
            return;
         }

         string domain = adDomain_.Text.Trim();

         // The same three forms SSPIValidation treats as "this computer". The local
         // computer name compared here is the one the CONTROL PANEL is running on,
         // which is why it is only used to recognise the intent - the sentence below
         // says "the server's own computer", not this one.
         bool local = domain.Length == 0
            || domain == "."
            || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

         directoryEffect_.Text = local
            ? "This validates against a LOCAL Windows account on the server's own computer, not against a domain. "
              + "That is the right setting when there is no Active Directory."
            : "This validates against the domain \"" + domain + "\". The server's own computer must be joined to it.";

         directoryEffect_.SetResourceReference(Control.ForegroundProperty,
            local ? "TextFillColorSecondaryBrush" : "TextFillColorSecondaryBrush");
      }

      private void BrowseActiveDirectory()
      {
         var picker = new ActiveDirectoryPickerDialog(this, multiSelect: false);
         if (picker.ShowDialog() == true && picker.SelectedUsers.Count > 0)
         {
            AdUser user = picker.SelectedUsers[0];
            adDomain_.Text = picker.SelectedDomain ?? "";
            adUser_.Text = user.SamAccountName;
            isAd_.IsChecked = true;
         }
      }

      private FrameworkElement BuildExternal()
      {
         fetchEditor_ = CollectionSpecs.FetchAccounts(domainName_, address_);
         fetchEditor_.Margin = new Thickness(4, 8, 4, 4);
         return fetchEditor_;
      }

      private FrameworkElement BuildRules()
      {
         accountRules_ = new RulesView();
         accountRules_.ConfigureForRules(OpenAccountRules, serverLevel: false, embedded: true);
         accountRules_.Margin = new Thickness(4, 8, 4, 4);
         return accountRules_;
      }

      private dynamic OpenAccountRules()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         dynamic account = OpenAccount(domains);
         dynamic rules = account.Rules;
         ServerSession.Release(account);
         ServerSession.Release(domains);
         return rules;
      }

      private FrameworkElement BuildFolders()
      {
         var panel = TabPanel();
         panel.Children.Add(Label("IMAP folders in this mailbox"));
         panel.Children.Add(folderList_);

         var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
         var add = new Wpf.Ui.Controls.Button { Content = "Add folder", Margin = new Thickness(0, 0, 8, 0) };
         add.Click += (s, e) => AddFolder();
         var del = new Wpf.Ui.Controls.Button { Content = "Delete folder", Appearance = Wpf.Ui.Controls.ControlAppearance.Danger, Margin = new Thickness(0, 0, 8, 0) };
         del.Click += (s, e) => DeleteFolder();
         var refresh = new Wpf.Ui.Controls.Button { Content = "Refresh" };
         refresh.Click += (s, e) => LoadFolders();
         actions.Children.Add(add);
         actions.Children.Add(del);
         actions.Children.Add(refresh);
         panel.Children.Add(actions);

         panel.Children.Add(Separator());
         panel.Children.Add(Label("Maintenance"));
         var maint = new StackPanel { Orientation = Orientation.Horizontal };
         var empty = new Wpf.Ui.Controls.Button { Content = "Empty mailbox", Appearance = Wpf.Ui.Controls.ControlAppearance.Danger, Margin = new Thickness(0, 0, 8, 0) };
         empty.Click += (s, e) => EmptyMailbox();
         var unlock = new Wpf.Ui.Controls.Button { Content = "Unlock mailbox" };
         unlock.Click += (s, e) => UnlockMailbox();
         maint.Children.Add(empty);
         maint.Children.Add(unlock);
         panel.Children.Add(maint);

         folderStatus_.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         panel.Children.Add(folderStatus_);
         return Scroll(panel);
      }

      private void LoadFolders()
      {
         folderList_.Items.Clear();
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            dynamic folders = a.IMAPFolders;
            int count = (int) folders.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic f = folders.Item[i];
               string name = (string) f.Name;
               bool sub = (bool) f.Subscribed;
               folderList_.Items.Add(sub ? name : name + "  (not subscribed)");
               ServerSession.Release(f);
            }
            ServerSession.Release(folders);
            ServerSession.Release(a);
            folderStatus_.Text = count + (count == 1 ? " folder." : " folders.");
         }
         catch (Exception ex)
         {
            folderStatus_.Text = "Could not load folders: " + ex.Message;
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private void AddFolder()
      {
         string name = PromptText("New IMAP folder", "Folder name (use the hierarchy delimiter for sub-folders):");
         if (string.IsNullOrWhiteSpace(name))
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            dynamic folders = a.IMAPFolders;
            dynamic created = folders.Add(name.Trim());
            ServerSession.Release(created);
            ServerSession.Release(folders);
            ServerSession.Release(a);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not create the folder: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }
         LoadFolders();
      }

      private void DeleteFolder()
      {
         if (folderList_.SelectedItem is not string display)
         {
            folderStatus_.Text = "Select a folder first.";
            return;
         }
         string name = display.Replace("  (not subscribed)", "");
         if (MessageBox.Show("Delete the folder '" + name + "' and all messages in it?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            dynamic folders = a.IMAPFolders;
            dynamic folder = folders.ItemByName[name];
            folder.Delete();
            ServerSession.Release(folder);
            ServerSession.Release(folders);
            ServerSession.Release(a);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the folder: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }
         LoadFolders();
      }

      private void EmptyMailbox()
      {
         if (MessageBox.Show("Permanently delete ALL folders and messages in this mailbox?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            a.DeleteMessages();
            ServerSession.Release(a);
            folderStatus_.Text = "Mailbox emptied.";
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not empty the mailbox: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }
         LoadFolders();
      }

      private void UnlockMailbox()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            a.UnlockMailbox();
            ServerSession.Release(a);
            folderStatus_.Text = "Mailbox unlocked.";
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not unlock the mailbox: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private string PromptText(string title, string prompt)
      {
         var dlg = new Window
         {
            Owner = this,
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
         };
         dlg.SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
         var panel = new StackPanel { Margin = new Thickness(20) };
         panel.Children.Add(Label(prompt));
         var box = new TextBox { FontSize = 13, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 12) };
         panel.Children.Add(box);
         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
         string result = null;
         var ok = new Wpf.Ui.Controls.Button { Content = "OK", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80, IsDefault = true };
         ok.Click += (s, e) => { result = box.Text; dlg.DialogResult = true; dlg.Close(); };
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", MinWidth = 80, IsCancel = true };
         cancel.Click += (s, e) => dlg.Close();
         buttons.Children.Add(ok);
         buttons.Children.Add(cancel);
         panel.Children.Add(buttons);
         dlg.Content = panel;
         box.Loaded += (s, e) => box.Focus();
         return dlg.ShowDialog() == true ? result : null;
      }

      private dynamic OpenAccount(dynamic domains)
      {
         dynamic domain = domains.ItemByName[domainName_];
         dynamic accounts = domain.Accounts;
         dynamic account = accounts.ItemByAddress[address_];
         ServerSession.Release(accounts);
         ServerSession.Release(domain);
         return account;
      }

      private void Load()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            active_.IsChecked = (bool) a.Active;
            addressBox_.Text = (string) a.Address ?? address_;
            SelectCombo(adminLevel_, (int) a.AdminLevel);
            quota_.Text = ((int) a.MaxSize).ToString();
            firstName_.Text = (string) a.PersonFirstName ?? "";
            lastName_.Text = (string) a.PersonLastName ?? "";
            try { lastLogon_.Text = Convert.ToString(a.LastLogonTime); } catch { lastLogon_.Text = "Never"; }
            if (string.IsNullOrWhiteSpace(lastLogon_.Text)) lastLogon_.Text = "Never";

            forwardOn_.IsChecked = (bool) a.ForwardEnabled;
            forwardTo_.Text = (string) a.ForwardAddress ?? "";
            forwardKeep_.IsChecked = (bool) a.ForwardKeepOriginal;
            forwardAbortSpam_.IsChecked = (bool) a.ForwardAbortSpamFlagged;

            vacationOn_.IsChecked = (bool) a.VacationMessageIsOn;
            vacationSubject_.Text = (string) a.VacationSubject ?? "";
            vacationBody_.Text = (string) a.VacationMessage ?? "";
            vacationExpires_.IsChecked = (bool) a.VacationMessageExpires;
            string expiryText = (string) a.VacationMessageExpiresDate ?? "";
            vacationExpiresDate_.SelectedDate =
               DateTime.TryParse(expiryText, out DateTime expiry) ? expiry : (DateTime?) null;

            // Added alongside schema 6012; tolerate a server that predates it.
            try
            {
               string beginText = (string) a.VacationMessageBeginDate ?? "";
               vacationBeginDate_.SelectedDate =
                  DateTime.TryParse(beginText, out DateTime begin) ? begin : (DateTime?) null;
            }
            catch { vacationBeginDate_.SelectedDate = null; }
            vacationAbortSpam_.IsChecked = (bool) a.VacationMessageAbortSpamFlagged;

            signatureOn_.IsChecked = (bool) a.SignatureEnabled;
            signaturePlain_.Text = (string) a.SignaturePlainText ?? "";
            signatureHtml_.Text = (string) a.SignatureHTML ?? "";

            // SieveScript is a file-backed property added in 6.x; tolerate older servers.
            try { sieveScript_.Text = (string) a.SieveScript ?? ""; } catch { sieveScript_.Text = ""; }

            // Remembered so Save can tell an edited script from an untouched one and
            // write the file only when it actually changed - see the note there.
            loadedSieveScript_ = sieveScript_.Text;

            isAd_.IsChecked = (bool) a.IsAD;
            adDomain_.Text = (string) a.ADDomain ?? "";
            adUser_.Text = (string) a.ADUsername ?? "";

            ServerSession.Release(a);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load the account: " + ex.Message, "Control Panel");
            Close();
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private void GeneratePassword()
      {
         string pw = Services.PasswordGenerator.Generate(16);
         password_.Password = pw;
         generatedShow_.Text = pw;
         generatedShow_.Visibility = Visibility.Visible;
         try { Clipboard.SetText(pw); } catch (Exception) { }
         UpdatePasswordStrength();
      }

      private void UpdatePasswordStrength()
      {
         (PasswordStrength.Level level, string summary) = PasswordStrength.Evaluate(password_.Password);
         pwStrength_.Text = summary;
         pwStrength_.Foreground = level switch
         {
            PasswordStrength.Level.Strong => Services.ThemeTokens.Success,
            PasswordStrength.Level.Fair => Services.ThemeTokens.Warning,
            PasswordStrength.Level.Weak => Services.ThemeTokens.Danger,
            _ => System.Windows.Media.Brushes.Gray,
         };
      }

      private void Save()
      {
         if (!NumericField.TryValidate(quota_.Text, "Maximum size (MB)", 0, int.MaxValue, out int quotaV, out bool hasQuota, out string error))
         {
            status_.Text = error;
            return;
         }
         status_.Text = "";

         if (password_.Password.Length > 0)
         {
            (PasswordStrength.Level level, string summary) = PasswordStrength.Evaluate(password_.Password);
            if (level == PasswordStrength.Level.Weak &&
                MessageBox.Show(summary + "\n\nSave this weak password anyway?", "Control Panel",
                   MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
               return;
            }
         }

         // An account rename stays inside its domain: the row keeps its domain id,
         // so an address in another domain would produce an account the server can
         // no longer find. The server moves the message directory itself.
         string newAddress = addressBox_.Text.Trim();
         bool renaming = !string.Equals(newAddress, address_, StringComparison.OrdinalIgnoreCase);
         if (renaming)
         {
            if (!newAddress.ToLowerInvariant().EndsWith("@" + domainName_.ToLowerInvariant()) ||
                newAddress.IndexOf('@') <= 0)
            {
               status_.Text = "The address must be a name followed by @" + domainName_ + ".";
               return;
            }

            if (MessageBox.Show(
                   "Rename " + address_ + " to " + newAddress + "?\n\nThe mailbox and its messages move to " +
                   "the new address. Mail sent to the old address will no longer reach this account unless " +
                   "an alias is created for it.",
                   "Control Panel", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
               return;
         }

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            if (renaming)
               a.Address = newAddress;
            a.Active = active_.IsChecked == true;
            int lvl = ComboValue(adminLevel_, -1);
            if (lvl >= 0) a.AdminLevel = lvl;
            if (hasQuota)
               a.MaxSize = quotaV;
            a.PersonFirstName = firstName_.Text.Trim();
            a.PersonLastName = lastName_.Text.Trim();
            if (password_.Password.Length > 0)
               a.Password = password_.Password;

            a.ForwardEnabled = forwardOn_.IsChecked == true;
            a.ForwardAddress = forwardTo_.Text.Trim();
            a.ForwardKeepOriginal = forwardKeep_.IsChecked == true;
            a.ForwardAbortSpamFlagged = forwardAbortSpam_.IsChecked == true;

            a.VacationMessageIsOn = vacationOn_.IsChecked == true;
            a.VacationSubject = vacationSubject_.Text;
            a.VacationMessage = vacationBody_.Text;
            a.VacationMessageExpires = vacationExpires_.IsChecked == true;
            if (vacationExpiresDate_.SelectedDate.HasValue)
               a.VacationMessageExpiresDate = vacationExpiresDate_.SelectedDate.Value.ToString("yyyy-MM-dd");
            try
            {
               if (vacationBeginDate_.SelectedDate.HasValue)
                  a.VacationMessageBeginDate = vacationBeginDate_.SelectedDate.Value.ToString("yyyy-MM-dd");
            }
            catch { /* a server without schema 6012 has nowhere to put it */ }
            a.VacationMessageAbortSpamFlagged = vacationAbortSpam_.IsChecked == true;

            a.SignatureEnabled = signatureOn_.IsChecked == true;
            a.SignaturePlainText = signaturePlain_.Text;
            a.SignatureHTML = signatureHtml_.Text;

            a.IsAD = isAd_.IsChecked == true;
            a.ADDomain = adDomain_.Text.Trim();
            a.ADUsername = adUser_.Text.Trim();

            a.Save();

            // AFTER Save, and only when it actually changed. SieveScript is not an
            // in-memory property: the setter writes the file immediately, keyed on
            // the account object's CURRENT address. Written before Save during a
            // rename, it landed on the NEW address - so a rename the server then
            // REFUSED (a duplicate address, or an installation that still needs
            // Data Directory Synchronizer) had already overwritten the script of
            // whoever holds that address, or, with an empty editor, deleted it
            // along with their vacation state. Nothing said so: the dialog only
            // reported that the account could not be saved.
            //
            // Writing it unconditionally was the other half. Opening an account and
            // pressing Save with the Sieve tab untouched rewrote the file, so a
            // script edited elsewhere (ManageSieve) was replaced by whatever this
            // dialog happened to have loaded.
            string sieveText = sieveScript_.Text ?? "";
            if (sieveText != (loadedSieveScript_ ?? ""))
            {
               // Tolerate older servers, which do not have this property at all.
               try { a.SieveScript = sieveText; } catch { }
            }

            ServerSession.Release(a);
            Close();
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the account: " + ex.Message, "Control Panel");
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

      private static TextBox NewMemo() => new()
      {
         AcceptsReturn = true,
         Height = 80,
         TextWrapping = TextWrapping.Wrap,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

      /// <summary>
      ///    Explanatory prose beside a control, for the cases where the control's own
      ///    label cannot carry the consequence of setting it.
      /// </summary>
      private static TextBlock Note(string text)
      {
         var t = new TextBlock
         {
            Text = text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8),
            Opacity = 0.75
         };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }

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

      private static int ComboValue(ComboBox combo, int fallback) =>
         combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : fallback;
   }
}
