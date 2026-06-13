using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Small reusable single-line text prompt.</summary>
   internal static class InputDialog
   {
      public static string Prompt(Window owner, string title, string prompt, string initial = "")
      {
         var dlg = new Window
         {
            Owner = owner,
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
         };
         dlg.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };
         var label = new TextBlock { Text = prompt, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
         label.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         panel.Children.Add(label);
         var box = new TextBox { Text = initial, FontSize = 13, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 12) };
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
         box.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
         return dlg.ShowDialog() == true ? result : null;
      }
   }

   /// <summary>
   /// ACL permission editor for one public IMAP folder. Lists the access-control
   /// entries (user / group / anyone) and lets each be added, edited or removed.
   /// </summary>
   public class FolderPermissionsDialog : Window
   {
      // eACLPermission bit flags.
      private static readonly (string Label, int Bit)[] Flags =
      {
         ("Lookup (folder is visible)", 1),
         ("Read messages", 2),
         ("Keep seen/unseen state", 4),
         ("Set flags", 8),
         ("Insert / append messages", 16),
         ("Post", 32),
         ("Create sub-folders", 64),
         ("Delete folder", 128),
         ("Delete messages", 256),
         ("Expunge", 512),
         ("Administer (manage ACL)", 1024)
      };

      private static readonly (int Value, string Label)[] Types =
      {
         (0, "Account (user)"), (1, "Group"), (2, "Anyone")
      };

      private readonly string folderName_;
      private readonly ListBox list_ = new() { FontSize = 13, Height = 260, Margin = new Thickness(0, 0, 0, 12) };
      private readonly List<int> ids_ = new();

      public FolderPermissionsDialog(Window owner, string folderName)
      {
         folderName_ = folderName;
         Owner = owner;
         Title = "Permissions - " + folderName;
         Width = 520;
         SizeToContent = SizeToContent.Height;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         ResizeMode = ResizeMode.NoResize;
         SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };
         var header = new TextBlock { Text = folderName, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         panel.Children.Add(header);
         panel.Children.Add(list_);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
         var add = new Wpf.Ui.Controls.Button { Content = "Add", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
         add.Click += (s, e) => AddOrEdit(-1);
         var edit = new Wpf.Ui.Controls.Button { Content = "Edit", Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
         edit.Click += (s, e) => { if (list_.SelectedIndex >= 0) AddOrEdit(ids_[list_.SelectedIndex]); };
         var del = new Wpf.Ui.Controls.Button { Content = "Delete", Appearance = Wpf.Ui.Controls.ControlAppearance.Danger, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
         del.Click += (s, e) => DeleteSelected();
         var close = new Wpf.Ui.Controls.Button { Content = "Close", MinWidth = 80 };
         close.Click += (s, e) => Close();
         buttons.Children.Add(add);
         buttons.Children.Add(edit);
         buttons.Children.Add(del);
         buttons.Children.Add(close);
         panel.Children.Add(buttons);

         Content = panel;
         list_.MouseDoubleClick += (s, e) => { if (list_.SelectedIndex >= 0) AddOrEdit(ids_[list_.SelectedIndex]); };
         Loaded += (s, e) => Reload();
      }

      private dynamic OpenFolder(dynamic folders) => folders.ItemByName[folderName_];

      private void Reload()
      {
         list_.Items.Clear();
         ids_.Clear();
         dynamic folders = ServerSession.Current.Application.Settings.PublicFolders;
         try
         {
            dynamic folder = OpenFolder(folders);
            dynamic perms = folder.Permissions;
            int count = (int) perms.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic p = perms.Item[i];
               ids_.Add((int) p.ID);
               list_.Items.Add(Describe(p));
               ServerSession.Release(p);
            }
            ServerSession.Release(perms);
            ServerSession.Release(folder);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load permissions: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(folders);
         }
      }

      private static string Describe(dynamic p)
      {
         int type = (int) p.PermissionType;
         string subject = type switch
         {
            0 => SafeAccount(p),
            1 => SafeGroup(p),
            _ => "Anyone"
         };
         int value = (int) p.Value;
         var rights = new List<string>();
         foreach ((string label, int bit) in Flags)
            if ((value & bit) != 0)
               rights.Add(label.Split(' ')[0]);
         return subject + "   —   " + (rights.Count == 0 ? "no rights" : string.Join(", ", rights));
      }

      private static string SafeAccount(dynamic p)
      {
         try { dynamic a = p.Account; string addr = (string) a.Address; ServerSession.Release(a); return addr; }
         catch (Exception) { return "(account #" + (int) p.PermissionAccountID + ")"; }
      }

      private static string SafeGroup(dynamic p)
      {
         try { dynamic g = p.Group; string n = (string) g.Name; ServerSession.Release(g); return "Group: " + n; }
         catch (Exception) { return "(group #" + (int) p.PermissionGroupID + ")"; }
      }

      private void DeleteSelected()
      {
         if (list_.SelectedIndex < 0)
            return;
         int id = ids_[list_.SelectedIndex];

         dynamic folders = ServerSession.Current.Application.Settings.PublicFolders;
         try
         {
            dynamic folder = OpenFolder(folders);
            dynamic perms = folder.Permissions;
            perms.DeleteByDBID(id);
            ServerSession.Release(perms);
            ServerSession.Release(folder);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the permission: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(folders);
         }
         Reload();
      }

      private void AddOrEdit(int existingId)
      {
         var dlg = new PermissionEditDialog(this, Types, Flags);

         // Pre-fill when editing.
         if (existingId >= 0)
         {
            dynamic folders = ServerSession.Current.Application.Settings.PublicFolders;
            try
            {
               dynamic folder = OpenFolder(folders);
               dynamic perms = folder.Permissions;
               dynamic p = perms.ItemByDBID(existingId);
               int type = (int) p.PermissionType;
               string subject = type == 0 ? SafeAccount(p) : type == 1 ? StripGroup(SafeGroup(p)) : "";
               dlg.Initialize(type, subject, (int) p.Value);
               ServerSession.Release(p);
               ServerSession.Release(perms);
               ServerSession.Release(folder);
            }
            catch (Exception) { }
            finally { ServerSession.Release(folders); }
         }

         if (dlg.ShowDialog() != true)
            return;

         int subjectId = 0;
         if (dlg.SelectedType == 0)
         {
            subjectId = ResolveAccountId(dlg.Subject);
            if (subjectId == 0) { MessageBox.Show("No account found with address '" + dlg.Subject + "'.", "Control Panel"); return; }
         }
         else if (dlg.SelectedType == 1)
         {
            subjectId = ResolveGroupId(dlg.Subject);
            if (subjectId == 0) { MessageBox.Show("No group found named '" + dlg.Subject + "'.", "Control Panel"); return; }
         }

         dynamic folders2 = ServerSession.Current.Application.Settings.PublicFolders;
         try
         {
            dynamic folder = OpenFolder(folders2);
            dynamic perms = folder.Permissions;
            dynamic p = existingId >= 0 ? perms.ItemByDBID(existingId) : perms.Add();
            p.PermissionType = dlg.SelectedType;
            if (dlg.SelectedType == 0) p.PermissionAccountID = subjectId;
            else if (dlg.SelectedType == 1) p.PermissionGroupID = subjectId;
            p.Value = dlg.Value;
            p.Save();
            ServerSession.Release(p);
            ServerSession.Release(perms);
            ServerSession.Release(folder);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the permission: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(folders2);
         }
         Reload();
      }

      private static string StripGroup(string s) => s.StartsWith("Group: ") ? s.Substring(7) : s;

      private static int ResolveAccountId(string address)
      {
         if (string.IsNullOrWhiteSpace(address) || !address.Contains('@'))
            return 0;
         string domainName = address.Split('@')[1];
         dynamic domains = ServerSession.Current.Application.Domains;
         try
         {
            dynamic domain = domains.ItemByName[domainName];
            dynamic accounts = domain.Accounts;
            dynamic account = accounts.ItemByAddress[address];
            int id = (int) account.ID;
            ServerSession.Release(account);
            ServerSession.Release(accounts);
            ServerSession.Release(domain);
            return id;
         }
         catch (Exception) { return 0; }
         finally { ServerSession.Release(domains); }
      }

      private static int ResolveGroupId(string name)
      {
         dynamic groups = ServerSession.Current.Application.Settings.Groups;
         try
         {
            int count = (int) groups.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic g = groups.Item[i];
               bool match = string.Equals((string) g.Name, name, StringComparison.OrdinalIgnoreCase);
               int id = match ? (int) g.ID : 0;
               ServerSession.Release(g);
               if (match)
                  return id;
            }
         }
         catch (Exception) { }
         finally { ServerSession.Release(groups); }
         return 0;
      }
   }

   /// <summary>Add/edit dialog for a single ACL entry.</summary>
   internal class PermissionEditDialog : Window
   {
      private readonly ComboBox typeCombo_ = new() { FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
      private readonly TextBox subject_ = new() { FontSize = 13, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 10) };
      private readonly TextBlock subjectLabel_;
      private readonly List<(CheckBox Box, int Bit)> flagBoxes_ = new();

      public int SelectedType { get; private set; }
      public string Subject { get; private set; }
      public int Value { get; private set; }

      public PermissionEditDialog(Window owner, (int Value, string Label)[] types, (string Label, int Bit)[] flags)
      {
         Owner = owner;
         Title = "Access-control entry";
         Width = 420;
         SizeToContent = SizeToContent.Height;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         ResizeMode = ResizeMode.NoResize;
         SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(20) };
         panel.Children.Add(Label("Applies to"));
         foreach ((int value, string label) in types)
            typeCombo_.Items.Add(new ComboBoxItem { Content = label, Tag = value });
         typeCombo_.SelectedIndex = 0;
         typeCombo_.SelectionChanged += (s, e) => UpdateSubjectState();
         panel.Children.Add(typeCombo_);

         subjectLabel_ = Label("Account address");
         panel.Children.Add(subjectLabel_);
         panel.Children.Add(subject_);

         panel.Children.Add(Label("Permissions"));
         foreach ((string label, int bit) in flags)
         {
            var cb = new CheckBox { Content = label, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 2) };
            flagBoxes_.Add((cb, bit));
            panel.Children.Add(cb);
         }

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
         var ok = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
         ok.Click += (s, e) => Commit();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", MinWidth = 80 };
         cancel.Click += (s, e) => Close();
         buttons.Children.Add(ok);
         buttons.Children.Add(cancel);
         panel.Children.Add(buttons);

         Content = panel;
         UpdateSubjectState();
      }

      public void Initialize(int type, string subject, int value)
      {
         foreach (ComboBoxItem item in typeCombo_.Items)
            if ((int) item.Tag == type)
               typeCombo_.SelectedItem = item;
         subject_.Text = subject ?? "";
         foreach ((CheckBox box, int bit) in flagBoxes_)
            box.IsChecked = (value & bit) != 0;
         UpdateSubjectState();
      }

      private void UpdateSubjectState()
      {
         int type = typeCombo_.SelectedItem is ComboBoxItem cbi ? (int) cbi.Tag : 0;
         bool needsSubject = type != 2; // Anyone needs no subject
         subjectLabel_.Text = type == 1 ? "Group name" : "Account address";
         subjectLabel_.Visibility = needsSubject ? Visibility.Visible : Visibility.Collapsed;
         subject_.Visibility = needsSubject ? Visibility.Visible : Visibility.Collapsed;
      }

      private void Commit()
      {
         SelectedType = typeCombo_.SelectedItem is ComboBoxItem cbi ? (int) cbi.Tag : 0;
         Subject = subject_.Text.Trim();
         int value = 0;
         foreach ((CheckBox box, int bit) in flagBoxes_)
            if (box.IsChecked == true)
               value |= bit;
         Value = value;
         DialogResult = true;
         Close();
      }

      private static TextBlock Label(string text)
      {
         var t = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }
   }
}
