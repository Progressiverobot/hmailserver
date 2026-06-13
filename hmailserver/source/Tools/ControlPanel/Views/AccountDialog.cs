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

      // General
      private readonly CheckBox active_ = new() { Content = "Account enabled", FontSize = 13 };
      private readonly ComboBox adminLevel_ = new();
      private readonly TextBox quota_ = new();
      private readonly TextBox firstName_ = new();
      private readonly TextBox lastName_ = new();
      private readonly PasswordBox password_ = new();
      private readonly TextBlock lastLogon_ = new() { FontSize = 12.5, Margin = new Thickness(0, 0, 0, 8) };

      // Forwarding
      private readonly CheckBox forwardOn_ = new() { Content = "Forward incoming mail", FontSize = 13 };
      private readonly TextBox forwardTo_ = new();
      private readonly CheckBox forwardKeep_ = new() { Content = "Keep original message", FontSize = 13 };
      private readonly CheckBox forwardAbortSpam_ = new() { Content = "Do not forward messages flagged as spam", FontSize = 13 };

      // Auto-reply
      private readonly CheckBox vacationOn_ = new() { Content = "Send automatic reply (vacation message)", FontSize = 13 };
      private readonly TextBox vacationSubject_ = new();
      private readonly TextBox vacationBody_ = NewMemo();
      private readonly CheckBox vacationExpires_ = new() { Content = "Stop sending replies after a date", FontSize = 13 };
      private readonly TextBox vacationExpiresDate_ = new();
      private readonly CheckBox vacationAbortSpam_ = new() { Content = "Do not reply to messages flagged as spam", FontSize = 13 };

      // Signature
      private readonly CheckBox signatureOn_ = new() { Content = "Add signature to outgoing messages", FontSize = 13 };
      private readonly TextBox signaturePlain_ = NewMemo();
      private readonly TextBox signatureHtml_ = NewMemo();

      // Active Directory
      private readonly CheckBox isAd_ = new() { Content = "This account is linked to Active Directory", FontSize = 13 };
      private readonly TextBox adDomain_ = new();
      private readonly TextBox adUser_ = new();

      // External (fetch) accounts, account rules, IMAP folders — embedded editors
      private CollectionEditorView fetchEditor_;
      private CollectionEditorView rulesEditor_;
      private readonly ListBox folderList_ = new() { Height = 220, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
      private readonly TextBlock folderStatus_ = new() { FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };

      public AccountDialog(Window owner, string domainName, string address)
      {
         domainName_ = domainName;
         address_ = address;

         Owner = owner;
         Title = "Account - " + address;
         Width = 640;
         Height = 680;
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
         tabs.Items.Add(new TabItem { Header = "External", Content = BuildExternal() });
         tabs.Items.Add(new TabItem { Header = "Rules", Content = BuildRules() });
         tabs.Items.Add(new TabItem { Header = "Folders", Content = BuildFolders() });
         tabs.Items.Add(new TabItem { Header = "Directory", Content = BuildDirectory() });
         Grid.SetRow(tabs, 1);
         root.Children.Add(tabs);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel" };
         cancel.Click += (s, e) => Close();
         buttons.Children.Add(save);
         buttons.Children.Add(cancel);
         Grid.SetRow(buttons, 2);
         root.Children.Add(buttons);

         Content = root;
         Loaded += (s, e) =>
         {
            Load();
            fetchEditor_?.OnEnter();
            rulesEditor_?.OnEnter();
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
         password_.Margin = new Thickness(0, 0, 0, 12);
         panel.Children.Add(password_);
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
         panel.Children.Add(vacationExpires_);
         panel.Children.Add(Label("Expiry date (YYYY-MM-DD)"));
         panel.Children.Add(Input(vacationExpiresDate_));
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

      private ScrollViewer BuildDirectory()
      {
         var panel = TabPanel();
         panel.Children.Add(isAd_);
         panel.Children.Add(Label("Active Directory domain"));
         panel.Children.Add(Input(adDomain_));
         panel.Children.Add(Label("Active Directory user name"));
         panel.Children.Add(Input(adUser_));
         return Scroll(panel);
      }

      private FrameworkElement BuildExternal()
      {
         fetchEditor_ = CollectionSpecs.FetchAccounts(domainName_, address_);
         fetchEditor_.Margin = new Thickness(4, 8, 4, 4);
         return fetchEditor_;
      }

      private FrameworkElement BuildRules()
      {
         rulesEditor_ = CollectionSpecs.AccountRules(domainName_, address_);
         rulesEditor_.Margin = new Thickness(4, 8, 4, 4);
         return rulesEditor_;
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
         var ok = new Wpf.Ui.Controls.Button { Content = "OK", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
         ok.Click += (s, e) => { result = box.Text; dlg.DialogResult = true; dlg.Close(); };
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", MinWidth = 80 };
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
            vacationExpiresDate_.Text = (string) a.VacationMessageExpiresDate ?? "";
            vacationAbortSpam_.IsChecked = (bool) a.VacationMessageAbortSpamFlagged;

            signatureOn_.IsChecked = (bool) a.SignatureEnabled;
            signaturePlain_.Text = (string) a.SignaturePlainText ?? "";
            signatureHtml_.Text = (string) a.SignatureHTML ?? "";

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

      private void Save()
      {
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic a = OpenAccount(domains);
            a.Active = active_.IsChecked == true;
            int lvl = ComboValue(adminLevel_, -1);
            if (lvl >= 0) a.AdminLevel = lvl;
            if (int.TryParse(quota_.Text.Trim(), out int quota))
               a.MaxSize = quota;
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
            if (vacationExpiresDate_.Text.Trim().Length > 0)
               a.VacationMessageExpiresDate = vacationExpiresDate_.Text.Trim();
            a.VacationMessageAbortSpamFlagged = vacationAbortSpam_.IsChecked == true;

            a.SignatureEnabled = signatureOn_.IsChecked == true;
            a.SignaturePlainText = signaturePlain_.Text;
            a.SignatureHTML = signatureHtml_.Text;

            a.IsAD = isAd_.IsChecked == true;
            a.ADDomain = adDomain_.Text.Trim();
            a.ADUsername = adUser_.Text.Trim();

            a.Save();
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

      private static int ComboValue(ComboBox combo, int fallback) =>
         combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : fallback;
   }
}
