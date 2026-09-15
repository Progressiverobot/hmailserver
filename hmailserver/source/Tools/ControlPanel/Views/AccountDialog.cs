// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal, tabbed editor for one account: general, forwarding, auto-reply,
   /// signature and Active Directory.
   ///
   /// On the standard frame: the address is the heading, the twelve tabs all take
   /// the height the dialog allows so the window never resizes on a click of the
   /// strip, every field is a <see cref="FieldRow"/> carrying its own caption,
   /// hint and validation message, and a number or an address that will not do is
   /// said on that field - which brings its tab forward and puts the keyboard in
   /// it - rather than in a line at the foot of a dialog the reader has already
   /// left. The two message boxes that remain are the two real questions: saving
   /// a weak password, and renaming a mailbox.
   /// </summary>
   public class AccountDialog : FluentDialogWindow
   {
      /// <summary>The width this dialog asks the frame for.</summary>
      private const double DialogWidth = 640;

      private readonly string domainName_;
      private readonly string address_;

      private readonly InlineNotice notice_ = DialogFields.Notice();

      // General
      private readonly CheckBox active_ = new() { Content = L("Account _enabled") };
      private readonly TextBox addressBox_ = NewInput();
      private FieldRow addressRow_;
      private readonly ComboBox adminLevel_ = new();
      private readonly TextBox quota_ = NewInput();
      private FieldRow quotaRow_;
      private readonly TextBox retentionDays_ = NewInput();
      private FieldRow retentionRow_;
      private readonly CheckBox spamFilterOn_ = new() { Content = L("Apply the server's spam _filtering to this account") };
      private readonly TextBox spamMark_ = NewInput();
      private FieldRow spamMarkRow_;
      private readonly TextBox spamDelete_ = NewInput();
      private FieldRow spamDeleteRow_;
      private readonly TextBox firstName_ = NewInput();
      private readonly TextBox lastName_ = NewInput();
      private readonly hMailServer.ControlPanel.Views.PasswordField password_ = new();
      private readonly Wpf.Ui.Controls.TextBox generatedShow_ = new();
      private readonly TextBlock pwStrength_ = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
      private readonly TextBlock lastLogon_ = new();

      // Forwarding
      private readonly CheckBox forwardOn_ = new() { Content = L("_Forward incoming mail") };
      private readonly TextBox forwardTo_ = NewInput();
      private readonly CheckBox forwardKeep_ = new() { Content = L("_Keep original message") };
      private readonly CheckBox forwardAbortSpam_ = new() { Content = L("Do _not forward messages flagged as spam") };

      // Auto-reply
      private readonly CheckBox vacationOn_ = new() { Content = L("Send _automatic reply (vacation message)") };
      private readonly TextBox vacationSubject_ = NewInput();
      private readonly TextBox vacationBody_ = NewMemo();
      private readonly CheckBox vacationExpires_ = new() { Content = L("Stop sending replies after a _date") };
      private readonly DatePicker vacationExpiresDate_ = new();
      private readonly DatePicker vacationBeginDate_ = new();
      private readonly CheckBox vacationAbortSpam_ = new() { Content = L("Do _not reply to messages flagged as spam") };

      // Signature
      private readonly CheckBox signatureOn_ = new() { Content = L("_Add signature to outgoing messages") };
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
         FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily),
         FontSize = Typography.Label
      };

      // Active Directory
      // "or a local Windows account" is not padding. The tab is titled Active Directory
      // throughout, and an empty domain quietly means "a local Windows account on this
      // computer" - which is the only form of this feature available to anyone with no
      // domain at all, and was undiscoverable from the interface.
      private readonly CheckBox isAd_ = new() { Content = L("_Check this password against Windows (Active Directory, or a local Windows account)") };

      // What the values below actually do, restated as a sentence. Updated as the
      // domain box is typed in, because the difference between the two behaviours is
      // an empty box rather than anything the reader can see.
      private readonly TextBlock directoryEffect_ = new()
      {
         TextWrapping = TextWrapping.Wrap,
         Margin = new Thickness(0, 0, 0, DesignTokens.Space.Sm)
      };
      private readonly TextBox adDomain_ = NewInput();
      private readonly TextBox adUser_ = NewInput();

      // External (fetch) accounts, account rules, IMAP folders — embedded editors
      private CollectionEditorView fetchEditor_;
      private AppPasswordsPanel appPasswords_;
      private AccountTwoFactorPanel twoFactor_;
      private RulesView accountRules_;
      private readonly ListBox folderList_ = new() { Height = 220 };
      private readonly TextBlock folderStatus_ = new();
      private readonly InlineNotice folderNotice_ = DialogFields.Notice();

      // What Load() read from the server, so Save can write the Sieve file only on
      // a real edit. Null until Load runs.
      private string loadedSieveScript_;

      public AccountDialog(Window owner, string domainName, string address)
      {
         domainName_ = domainName;
         address_ = address;

         Owner = owner;
         Title = L("Account - ") + address;

         var tabs = new TabControl { Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
         tabs.Items.Add(new TabItem { Header = L("General"), Content = BuildGeneral() });
         tabs.Items.Add(new TabItem { Header = L("Forwarding"), Content = BuildForwarding() });
         tabs.Items.Add(new TabItem { Header = L("Auto-reply"), Content = BuildAutoReply() });
         tabs.Items.Add(new TabItem { Header = L("Spam"), Content = BuildSpam() });
         tabs.Items.Add(new TabItem { Header = L("Signature"), Content = BuildSignature() });
         tabs.Items.Add(new TabItem { Header = L("Sieve"), Content = BuildSieve() });
         tabs.Items.Add(new TabItem { Header = L("External"), Content = BuildExternal() });
         tabs.Items.Add(new TabItem { Header = L("App passwords"), Content = BuildAppPasswords() });
         tabs.Items.Add(new TabItem { Header = L("Two-factor"), Content = BuildTwoFactor() });
         tabs.Items.Add(new TabItem { Header = L("Rules"), Content = BuildRules() });
         tabs.Items.Add(new TabItem { Header = L("Folders"), Content = BuildFolders() });
         tabs.Items.Add(new TabItem { Header = L("Directory"), Content = BuildDirectory() });
         DialogFields.FillTabs(tabs);

         var body = new StackPanel();
         body.Children.Add(notice_);
         body.Children.Add(tabs);

         var save = new Wpf.Ui.Controls.Button { Content = L("_Save"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => Close();

         UseFrame(address, body, save, cancel, width: DialogWidth);

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
         adminLevel_.Items.Add(DialogFields.Combo(L("Normal user"), 0));
         adminLevel_.Items.Add(DialogFields.Combo(L("Domain administrator"), 1));
         adminLevel_.Items.Add(DialogFields.Combo(L("Server administrator"), 2));

         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Check(active_));

         addressRow_ = Label(L("_Address (changing it renames the mailbox; it must stay in this domain)"), addressBox_);
         panel.Children.Add(addressRow_);

         panel.Children.Add(Label(L("Ad_ministration level"), adminLevel_));

         quotaRow_ = Label(L("_Quota (MB, 0 = unlimited)"), quota_);
         panel.Children.Add(quotaRow_);

         retentionRow_ = Label(L("_Delete messages older than (days; 0 = the domain's policy, -1 = keep forever)"), retentionDays_);
         panel.Children.Add(retentionRow_);

         panel.Children.Add(Label(L("_First name"), firstName_));
         panel.Children.Add(Label(L("_Last name"), lastName_));

         // The password, the button that invents one, the one-time display of
         // what it invented and the strength verdict are one field: a single row
         // around the lot keeps the verdict attached to the box it is about, and
         // gives the box its accessible name from the caption.
         password_.PasswordChanged += (s, e) =>
         {
            UpdatePasswordStrength();
            generatedShow_.Visibility = Visibility.Collapsed;
         };

         var genBtn = new Wpf.Ui.Controls.Button { Content = L("_Generate strong password"), Margin = new Thickness(0, DesignTokens.Space.Sm, 0, 0) };
         AutomationProperties.SetAutomationId(genBtn, "GeneratePassword");
         genBtn.Click += (s, e) => GeneratePassword();

         generatedShow_.IsReadOnly = true;
         generatedShow_.FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily);
         // No caption of its own, so name it directly - otherwise the one-time
         // password value is announced as an anonymous read-only "edit".
         AutomationProperties.SetName(generatedShow_, L("Generated password"));
         AutomationProperties.SetAutomationId(generatedShow_, "GeneratedPassword");
         generatedShow_.Visibility = Visibility.Collapsed;
         generatedShow_.Margin = new Thickness(0, DesignTokens.Space.Sm, 0, 0);
         generatedShow_.MaxWidth = 320;
         generatedShow_.HorizontalAlignment = HorizontalAlignment.Left;

         var passwordBlock = new StackPanel();
         passwordBlock.Children.Add(password_);
         passwordBlock.Children.Add(genBtn);
         passwordBlock.Children.Add(generatedShow_);
         pwStrength_.Margin = new Thickness(0, DesignTokens.Space.Sm, 0, 0);
         passwordBlock.Children.Add(pwStrength_);
         UpdatePasswordStrength();
         panel.Children.Add(Label(L("New _password (leave empty to keep current)"), passwordBlock));

         lastLogon_.SetResourceReference(FrameworkElement.StyleProperty, "TextBody");
         panel.Children.Add(Label(L("Last l_ogon"), lastLogon_));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildForwarding()
      {
         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Check(forwardOn_));
         panel.Children.Add(Label(L("Forward _to"), forwardTo_));
         panel.Children.Add(DialogFields.Check(forwardKeep_));
         panel.Children.Add(DialogFields.Check(forwardAbortSpam_));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildAutoReply()
      {
         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Check(vacationOn_));
         panel.Children.Add(Label(L("Reply su_bject"), vacationSubject_));
         panel.Children.Add(Label(L("Reply _message"), vacationBody_));
         panel.Children.Add(DialogFields.Separator());

         vacationBeginDate_.HorizontalAlignment = HorizontalAlignment.Left;
         vacationBeginDate_.MinWidth = 160;
         panel.Children.Add(Label(L("Sta_rt date"), vacationBeginDate_));

         panel.Children.Add(DialogFields.Check(vacationExpires_));
         vacationExpiresDate_.HorizontalAlignment = HorizontalAlignment.Left;
         vacationExpiresDate_.MinWidth = 160;
         panel.Children.Add(Label(L("_Expiry date"), vacationExpiresDate_));

         panel.Children.Add(DialogFields.Check(vacationAbortSpam_));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildSpam()
      {
         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Note(L("Per-account overrides of the server-wide spam handling, applied to THIS account's copy at delivery. They cannot reach back into the SMTP conversation: a message the global settings refuse, quarantine or greylist is stopped for every recipient before any per-account setting can run.")));
         panel.Children.Add(DialogFields.Check(spamFilterOn_));
         panel.Children.Add(DialogFields.Note(L("Unticked, a message the server classified as spam is still delivered here, unmarked - the spam headers and subject tag are removed from this account's copy. For the address that must never lose a mail.")));

         spamMarkRow_ = Label(L("_Mark threshold override (-1 = use the global setting, 0 = never mark)"), spamMark_);
         panel.Children.Add(spamMarkRow_);
         spamDeleteRow_ = Label(L("_Delete threshold override (-1 or 0 = off)"), spamDelete_);
         panel.Children.Add(spamDeleteRow_);

         panel.Children.Add(DialogFields.Note(L("Both overrides read the score this server recorded in the message, so they need \"Add reason to header\" switched on (Anti-spam settings). With it off no score is recorded, nothing is provable, and neither override acts - the account keeps the server-wide behaviour. The value in the file is not trusted in that case because nothing stops a SENDER putting one there.")));
         panel.Children.Add(DialogFields.Note(L("The delete override never deletes on a guess: the copy is removed only when the recorded score actually reached the value, and it goes to the quarantine store instead when that is enabled.")));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildSignature()
      {
         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Check(signatureOn_));
         panel.Children.Add(Label(L("_Plain-text signature"), signaturePlain_));
         panel.Children.Add(Label(L("_HTML signature"), signatureHtml_));
         return DialogFields.Scroll(panel);
      }

      private ScrollViewer BuildSieve()
      {
         var panel = DialogFields.TabPanel();
         // The explanation is the row's hint, so the editor is helped by it to a
         // screen reader as well as captioned - which the prose block above it,
         // and the accessible name set by hand beside it, never managed together.
         panel.Children.Add(DialogFields.Field(L("Sieve filter script"), sieveScript_,
            L("Active Sieve (RFC 5228) filter script for this account. It runs during local delivery and supports keep, fileinto, discard and redirect. Leave empty to disable. Multiple named scripts can be managed over ManageSieve.")));
         return DialogFields.Scroll(panel);
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
         var panel = DialogFields.TabPanel();
         panel.Children.Add(DialogFields.Check(isAd_));

         panel.Children.Add(DialogFields.Note(L("With this on, the password is not stored here at all - Windows is asked to check it. Leave it off and the account uses the password on the Account tab.")));

         // The sentence under the domain box changes as the box is typed in, so
         // it is the row's own hint rather than a block beside it: a hint is read
         // out with the editor, and this one is the whole difference between
         // "a local Windows account" and "a domain that must be joined".
         FieldRow domainRow = Label(L("Windows _domain (leave empty for a local Windows account)"), adDomain_);
         panel.Children.Add(domainRow);
         panel.Children.Add(directoryEffect_);
         directoryEffect_.SetResourceReference(FrameworkElement.StyleProperty, "TextCaption");

         panel.Children.Add(Label(L("Windows _user name"), adUser_));

         var browse = new Wpf.Ui.Controls.Button
         {
            Content = L("_Browse Active Directory…"),
            Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md)
         };
         browse.Click += (s, e) => BrowseActiveDirectory();
         panel.Children.Add(browse);

         panel.Children.Add(DialogFields.Note(L("A domain name here needs the SERVER's own computer to be joined to that domain, because Windows validates it with LogonUser. From a computer that is not joined, every attempt fails as though the password were wrong - including when the domain name is simply misspelt - so a mailbox configured this way on an unjoined server can never log in and never says why. If the server is not domain-joined, use LDAP directory authentication on the Directory authentication page instead: it binds to the directory over the network and needs no domain join.")));

         adDomain_.TextChanged += (s, e) => RefreshDirectoryEffect_();
         isAd_.Checked += (s, e) => RefreshDirectoryEffect_();
         isAd_.Unchecked += (s, e) => RefreshDirectoryEffect_();

         RefreshDirectoryEffect_();

         return DialogFields.Scroll(panel);
      }

      /// <summary>
      ///    Says, in plain words, which of the two things the values above actually do -
      ///    because "Active Directory domain: (empty)" reads as "not configured" and is
      ///    in fact a working configuration with completely different behaviour.
      /// </summary>
      private void RefreshDirectoryEffect_()
      {
         if (isAd_.IsChecked is not true)
         {
            directoryEffect_.Text = "";
            directoryEffect_.Visibility = Visibility.Collapsed;
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
            ? L("This validates against a LOCAL Windows account on the server's own computer, not against a domain. That is the right setting when there is no Active Directory.")
            : F("This validates against the domain \"{0}\". The server's own computer must be joined to it.", domain);
         directoryEffect_.Visibility = Visibility.Visible;
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
         fetchEditor_.Margin = new Thickness(0, DesignTokens.Space.Md, 0, 0);
         return fetchEditor_;
      }

      private FrameworkElement BuildAppPasswords()
      {
         appPasswords_ = new AppPasswordsPanel(domainName_, address_) { Margin = new Thickness(0, DesignTokens.Space.Md, 0, 0) };
         appPasswords_.Reload();
         return appPasswords_;
      }

      // Placed immediately after App passwords, because the order is the advice: the
      // app password has to exist before the second factor is switched on, or the
      // account's mail clients stop working with no way back in.
      private FrameworkElement BuildTwoFactor()
      {
         twoFactor_ = new AccountTwoFactorPanel(domainName_, address_) { Margin = new Thickness(0, DesignTokens.Space.Md, 0, 0) };
         twoFactor_.Reload();
         return twoFactor_;
      }

      private FrameworkElement BuildRules()
      {
         accountRules_ = new RulesView();
         accountRules_.ConfigureForRules(OpenAccountRules, serverLevel: false, embedded: true);
         accountRules_.Margin = new Thickness(0, DesignTokens.Space.Md, 0, 0);
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
         var panel = DialogFields.TabPanel();
         panel.Children.Add(folderNotice_);
         panel.Children.Add(Label(L("_IMAP folders in this mailbox"), folderList_));

         var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
         var add = new Wpf.Ui.Controls.Button { Content = L("_Add folder"), Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0) };
         add.Click += (s, e) => AddFolder();
         var del = new Wpf.Ui.Controls.Button { Content = L("_Delete folder"), Appearance = Wpf.Ui.Controls.ControlAppearance.Danger, Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0) };
         del.Click += (s, e) => DeleteFolder();
         var refresh = new Wpf.Ui.Controls.Button { Content = L("_Refresh") };
         refresh.Click += (s, e) => LoadFolders();
         actions.Children.Add(add);
         actions.Children.Add(del);
         actions.Children.Add(refresh);
         panel.Children.Add(actions);

         folderStatus_.SetResourceReference(FrameworkElement.StyleProperty, "TextCaption");
         panel.Children.Add(folderStatus_);

         panel.Children.Add(DialogFields.Separator());
         panel.Children.Add(DialogFields.Caption(L("Maintenance")));
         var maint = new StackPanel { Orientation = Orientation.Horizontal };
         var empty = new Wpf.Ui.Controls.Button { Content = L("_Empty mailbox"), Appearance = Wpf.Ui.Controls.ControlAppearance.Danger, Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0) };
         empty.Click += (s, e) => EmptyMailbox();
         var unlock = new Wpf.Ui.Controls.Button { Content = L("_Unlock mailbox") };
         unlock.Click += (s, e) => UnlockMailbox();
         maint.Children.Add(empty);
         maint.Children.Add(unlock);
         panel.Children.Add(maint);

         return DialogFields.Scroll(panel);
      }

      private void LoadFolders()
      {
         folderList_.Items.Clear();
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            dynamic folders = a.IMAPFolders;
            int count = (int)folders.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic f = folders.Item[i];
               string name = (string)f.Name;
               bool sub = (bool)f.Subscribed;
               folderList_.Items.Add(sub ? name : F("{0}  (not subscribed)", name));
               ServerSession.Release(f);
            }
            ServerSession.Release(folders);
            ServerSession.Release(a);
            folderStatus_.Text = count + (count == 1 ? " folder." : " folders.");
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            folderStatus_.Text = "";
            folderNotice_.Show(StatusLevel.Critical, L("Could not load folders: ") + ex.Message);
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      private void AddFolder()
      {
         folderNotice_.Hide();
         string name = DialogFields.PromptText(this, L("New IMAP folder"), L("Folder name (use the hierarchy delimiter for sub-folders):"));
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
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            folderNotice_.Show(StatusLevel.Critical, F("Could not create the folder: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(domains);
         }
         LoadFolders();
      }

      private void DeleteFolder()
      {
         folderNotice_.Hide();
         if (folderList_.SelectedItem is not string display)
         {
            folderNotice_.Show(StatusLevel.Warning, L("Select a folder first."));
            return;
         }
         string name = display.Replace("  (not subscribed)", "");

         // Still a question, and still a destructive one, so it stays a box.
         if (MessageBox.Show(F("Delete the folder '{0}' and all messages in it?", name), L("Control Panel"),
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
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            folderNotice_.Show(StatusLevel.Critical, F("Could not delete the folder: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(domains);
         }
         LoadFolders();
      }

      private void EmptyMailbox()
      {
         folderNotice_.Hide();
         if (MessageBox.Show(L("Permanently delete ALL folders and messages in this mailbox?"), L("Control Panel"),
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            a.DeleteMessages();
            ServerSession.Release(a);
            folderNotice_.Show(StatusLevel.Good, L("Mailbox emptied."));
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            folderNotice_.Show(StatusLevel.Critical, F("Could not empty the mailbox: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(domains);
         }
         LoadFolders();
      }

      private void UnlockMailbox()
      {
         folderNotice_.Hide();
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            a.UnlockMailbox();
            ServerSession.Release(a);
            folderNotice_.Show(StatusLevel.Good, L("Mailbox unlocked."));
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            folderNotice_.Show(StatusLevel.Critical, F("Could not unlock the mailbox: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(domains);
         }
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
            active_.IsChecked = (bool)a.Active;
            addressBox_.Text = (string)a.Address ?? address_;
            DialogFields.SelectCombo(adminLevel_, (int)a.AdminLevel);
            quota_.Text = ((int)a.MaxSize).ToString();
            retentionDays_.Text = ((int)a.MessageRetentionDays).ToString();
            spamFilterOn_.IsChecked = (bool)a.AntiSpamEnabled;
            spamMark_.Text = ((int)a.SpamMarkThreshold).ToString();
            spamDelete_.Text = ((int)a.SpamDeleteThreshold).ToString();
            firstName_.Text = (string)a.PersonFirstName ?? "";
            lastName_.Text = (string)a.PersonLastName ?? "";
            try { lastLogon_.Text = Convert.ToString(a.LastLogonTime); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { lastLogon_.Text = L("Never"); }
            if (string.IsNullOrWhiteSpace(lastLogon_.Text)) lastLogon_.Text = L("Never");

            forwardOn_.IsChecked = (bool)a.ForwardEnabled;
            forwardTo_.Text = (string)a.ForwardAddress ?? "";
            forwardKeep_.IsChecked = (bool)a.ForwardKeepOriginal;
            forwardAbortSpam_.IsChecked = (bool)a.ForwardAbortSpamFlagged;

            vacationOn_.IsChecked = (bool)a.VacationMessageIsOn;
            vacationSubject_.Text = (string)a.VacationSubject ?? "";
            vacationBody_.Text = (string)a.VacationMessage ?? "";
            vacationExpires_.IsChecked = (bool)a.VacationMessageExpires;
            string expiryText = (string)a.VacationMessageExpiresDate ?? "";
            vacationExpiresDate_.SelectedDate =
               DateTime.TryParse(expiryText, out DateTime expiry) ? expiry : (DateTime?)null;

            // Added alongside schema 6012; tolerate a server that predates it.
            try
            {
               string beginText = (string)a.VacationMessageBeginDate ?? "";
               vacationBeginDate_.SelectedDate =
                  DateTime.TryParse(beginText, out DateTime begin) ? begin : (DateTime?)null;
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { vacationBeginDate_.SelectedDate = null; }
            vacationAbortSpam_.IsChecked = (bool)a.VacationMessageAbortSpamFlagged;

            signatureOn_.IsChecked = (bool)a.SignatureEnabled;
            signaturePlain_.Text = (string)a.SignaturePlainText ?? "";
            signatureHtml_.Text = (string)a.SignatureHTML ?? "";

            // SieveScript is a file-backed property added in 6.x; tolerate older servers.
            try { sieveScript_.Text = (string)a.SieveScript ?? ""; } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { sieveScript_.Text = ""; }

            // Remembered so Save can tell an edited script from an untouched one and
            // write the file only when it actually changed - see the note there.
            loadedSieveScript_ = sieveScript_.Text;

            isAd_.IsChecked = (bool)a.IsAD;
            adDomain_.Text = (string)a.ADDomain ?? "";
            adUser_.Text = (string)a.ADUsername ?? "";

            ServerSession.Release(a);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not load the account: {0}", ex.Message), L("Control Panel"));
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
         try { Clipboard.SetText(pw); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
         UpdatePasswordStrength();
      }

      private void UpdatePasswordStrength()
      {
         (PasswordStrength.Level level, string summary) = PasswordStrength.Evaluate(password_.Password);
         pwStrength_.Text = summary;

         // By key, never by a held brush: a resolved brush keeps the colour of
         // the theme it was resolved under, and this line outlives a theme
         // switch. The verdict is in the words too - the colour only repeats it.
         string key = level switch
         {
            PasswordStrength.Level.Empty => "TextFillColorSecondaryBrush",
            PasswordStrength.Level.Strong => StatusSemantics.For(StatusLevel.Good).BrushKey,
            PasswordStrength.Level.Fair => StatusSemantics.For(StatusLevel.Warning).BrushKey,
            _ => StatusSemantics.For(StatusLevel.Critical).BrushKey,
         };
         pwStrength_.SetResourceReference(TextBlock.ForegroundProperty, key);
      }

      private void Save()
      {
         notice_.Hide();
         DialogFields.ClearErrors(addressRow_, quotaRow_, retentionRow_, spamMarkRow_, spamDeleteRow_);

         if (!NumericField.TryValidate(quota_.Text, L("Maximum size (MB)"), 0, int.MaxValue, out int quotaV, out bool hasQuota, out string error))
         {
            DialogFields.ShowError(quotaRow_, error);
            return;
         }

         if (!NumericField.TryValidate(retentionDays_.Text, L("Delete messages older than (days)"), -1, int.MaxValue, out int retentionV, out bool hasRetention, out error))
         {
            DialogFields.ShowError(retentionRow_, error);
            return;
         }

         // Both thresholds used to be dropped silently when they would not parse:
         // the dialog closed and the account kept its old override, which on this
         // tab is the difference between "spam is deleted at 12" and "spam is
         // never deleted". A field that is wrong says so.
         if (spamMark_.Text.Trim().Length > 0 && !int.TryParse(spamMark_.Text.Trim(), out _))
         {
            DialogFields.ShowError(spamMarkRow_, L("Enter a whole number."));
            return;
         }

         if (spamDelete_.Text.Trim().Length > 0 && !int.TryParse(spamDelete_.Text.Trim(), out _))
         {
            DialogFields.ShowError(spamDeleteRow_, L("Enter a whole number."));
            return;
         }

         if (password_.Password.Length > 0)
         {
            (PasswordStrength.Level level, string summary) = PasswordStrength.Evaluate(password_.Password);
            if (level == PasswordStrength.Level.Weak &&
                MessageBox.Show(summary + L("\n\nSave this weak password anyway?"), L("Control Panel"),
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
               DialogFields.ShowError(addressRow_, L("The address must be a name followed by @") + domainName_ + ".");
               return;
            }

            if (MessageBox.Show(
                   F("Rename {0} to {1}?\n\nThe mailbox and its messages move to the new address. Mail sent to the old address will no longer reach this account unless an alias is created for it.", address_, newAddress),
                   L("Control Panel"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
               return;
         }

         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            if (renaming)
               a.Address = newAddress;
            a.Active = active_.IsChecked is true;
            int lvl = DialogFields.ComboValue(adminLevel_, -1);
            if (lvl >= 0) a.AdminLevel = lvl;
            if (hasQuota)
               a.MaxSize = quotaV;
            if (hasRetention)
               a.MessageRetentionDays = retentionV;
            a.AntiSpamEnabled = spamFilterOn_.IsChecked is true;
            if (int.TryParse(spamMark_.Text.Trim(), out int markValue)) a.SpamMarkThreshold = markValue;
            if (int.TryParse(spamDelete_.Text.Trim(), out int deleteValue)) a.SpamDeleteThreshold = deleteValue;
            a.PersonFirstName = firstName_.Text.Trim();
            a.PersonLastName = lastName_.Text.Trim();
            if (password_.Password.Length > 0)
               a.Password = password_.Password;

            a.ForwardEnabled = forwardOn_.IsChecked is true;
            a.ForwardAddress = forwardTo_.Text.Trim();
            a.ForwardKeepOriginal = forwardKeep_.IsChecked is true;
            a.ForwardAbortSpamFlagged = forwardAbortSpam_.IsChecked is true;

            a.VacationMessageIsOn = vacationOn_.IsChecked is true;
            a.VacationSubject = vacationSubject_.Text;
            a.VacationMessage = vacationBody_.Text;
            a.VacationMessageExpires = vacationExpires_.IsChecked is true;
            if (vacationExpiresDate_.SelectedDate.HasValue)
               a.VacationMessageExpiresDate = vacationExpiresDate_.SelectedDate.Value.ToString("yyyy-MM-dd");
            try
            {
               if (vacationBeginDate_.SelectedDate.HasValue)
                  a.VacationMessageBeginDate = vacationBeginDate_.SelectedDate.Value.ToString("yyyy-MM-dd");
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* a server without schema 6012 has nowhere to put it */ }
            a.VacationMessageAbortSpamFlagged = vacationAbortSpam_.IsChecked is true;

            a.SignatureEnabled = signatureOn_.IsChecked is true;
            a.SignaturePlainText = signaturePlain_.Text;
            a.SignatureHTML = signatureHtml_.Text;

            a.IsAD = isAd_.IsChecked is true;
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
               try { a.SieveScript = sieveText; } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
            }

            ServerSession.Release(a);
            Close();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not save the account: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(domains);
         }
      }

      // ---- UI helpers ----

      private static TextBox NewInput() => new Wpf.Ui.Controls.TextBox();

      private static TextBox NewMemo() => new()
      {
         AcceptsReturn = true,
         Height = 80,
         TextWrapping = TextWrapping.Wrap,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto
      };

      /// <summary>
      /// A caption and its editor as one row, which names the editor to UI
      /// Automation. A TextBlock above a control tells UI Automation nothing, so
      /// every box on the account editor announced as a bare "edit". Same helper
      /// as RuleActionDialog and RuleCriteriaDialog.
      /// </summary>
      private static FieldRow Label(string text, FrameworkElement editor) => DialogFields.Field(text, editor);
   }
}
