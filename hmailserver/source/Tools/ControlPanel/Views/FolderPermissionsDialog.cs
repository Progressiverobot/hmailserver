// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Small reusable single-line text prompt, on the standard frame through <see cref="DialogFields.PromptText"/>.</summary>
   internal static class InputDialog
   {
      public static string Prompt(Window owner, string title, string prompt, string initial = "")
         => DialogFields.PromptText(owner, title, prompt, initial);
   }

   /// <summary>
   /// ACL permission editor for one public IMAP folder. Lists the access-control
   /// entries (user / group / anyone) and lets each be added, edited or removed.
   /// On the standard frame: the folder is the heading, the three buttons that
   /// act on the list sit under it, what went wrong is a notice above it, and
   /// Close is alone in the footer.
   /// </summary>
   public class FolderPermissionsDialog : FluentDialogWindow
   {
      // eACLPermission bit flags.
      private static readonly (string Label, int Bit)[] Flags =
      {
         (L("Lookup (folder is visible)"), 1),
         (L("Read messages"), 2),
         (L("Keep seen/unseen state"), 4),
         (L("Set flags"), 8),
         (L("Insert / append messages"), 16),
         (L("Post - and send as this mailbox's address over SMTP (Send-As, when SmtpAuthenticatedSenderCheck is on)"), 32),
         (L("Create sub-folders"), 64),
         (L("Delete folder"), 128),
         (L("Delete messages"), 256),
         (L("Expunge"), 512),
         (L("Administer (manage ACL)"), 1024)
      };

      private static readonly (int Value, string Label)[] Types =
      {
         (0, L("Account (user)")), (1, L("Group")), (2, L("Anyone"))
      };

      private readonly string folderName_;
      private readonly ListBox list_ = new() { Height = 260, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
      private readonly List<int> ids_ = new();
      private readonly InlineNotice notice_ = DialogFields.Notice();

      public FolderPermissionsDialog(Window owner, string folderName)
      {
         folderName_ = folderName;
         Owner = owner;
         Title = L("Permissions - ") + folderName;

         var body = new StackPanel();
         body.Children.Add(notice_);

         AutomationProperties.SetName(list_, F("Access-control entries for {0}", folderName));
         body.Children.Add(list_);

         var actions = new StackPanel { Orientation = Orientation.Horizontal };
         var add = new Wpf.Ui.Controls.Button { Content = L("_Add"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0), MinWidth = 88 };
         add.Click += (s, e) => AddOrEdit(-1);
         var edit = new Wpf.Ui.Controls.Button { Content = L("_Edit"), Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0), MinWidth = 88 };
         edit.Click += (s, e) => { if (list_.SelectedIndex >= 0) AddOrEdit(ids_[list_.SelectedIndex]); };
         var del = new Wpf.Ui.Controls.Button { Content = L("_Delete"), Appearance = Wpf.Ui.Controls.ControlAppearance.Danger, MinWidth = 88 };
         del.Click += (s, e) => DeleteSelected();
         actions.Children.Add(add);
         actions.Children.Add(edit);
         actions.Children.Add(del);
         body.Children.Add(actions);

         // Escape only. "Add" is deliberately not the default button: Enter with a
         // row selected in the list must not open the add dialog, and the list's own
         // double-click already covers "open the thing I am looking at".
         var close = new Wpf.Ui.Controls.Button { Content = L("Close"), MinWidth = 88 };
         close.Click += (s, e) => Close();

         UseFrame(folderName, body, null, close, width: 520);
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
            int count = (int)perms.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic p = perms.Item[i];
               ids_.Add((int)p.ID);
               list_.Items.Add(Describe(p));
               ServerSession.Release(p);
            }
            ServerSession.Release(perms);
            ServerSession.Release(folder);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not load permissions: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(folders);
         }
      }

      private static string Describe(dynamic p)
      {
         int type = (int)p.PermissionType;
         string subject = type switch
         {
            0 => SafeAccount(p),
            1 => SafeGroup(p),
            _ => L("Anyone")
         };
         int value = (int)p.Value;
         var rights = new List<string>();
         foreach ((string label, int bit) in Flags)
            if ((value & bit) != 0)
               rights.Add(label.Split(' ')[0]);
         return subject + "   —   " + (rights.Count == 0 ? L("no rights") : string.Join(", ", rights));
      }

      private static string SafeAccount(dynamic p)
      {
         try { dynamic a = p.Account; string addr = (string)a.Address; ServerSession.Release(a); return addr; }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { return F("(account #{0})", (int)p.PermissionAccountID); }
      }

      private static string SafeGroup(dynamic p)
      {
         try { dynamic g = p.Group; string n = (string)g.Name; ServerSession.Release(g); return "Group: " + n; }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { return F("(group #{0})", (int)p.PermissionGroupID); }
      }

      private void DeleteSelected()
      {
         if (list_.SelectedIndex < 0)
            return;
         int id = ids_[list_.SelectedIndex];

         notice_.Hide();
         dynamic folders = ServerSession.Current.Application.Settings.PublicFolders;
         try
         {
            dynamic folder = OpenFolder(folders);
            dynamic perms = folder.Permissions;
            perms.DeleteByDBID(id);
            ServerSession.Release(perms);
            ServerSession.Release(folder);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not delete the permission: {0}", ex.Message));
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
               int type = (int)p.PermissionType;
               string subject = type == 0 ? SafeAccount(p) : type == 1 ? StripGroup(SafeGroup(p)) : "";
               dlg.Initialize(type, subject, (int)p.Value);
               ServerSession.Release(p);
               ServerSession.Release(perms);
               ServerSession.Release(folder);
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
            finally { ServerSession.Release(folders); }
         }

         if (dlg.ShowDialog() != true)
            return;

         notice_.Hide();
         int subjectId = 0;
         if (dlg.SelectedType == 0)
         {
            subjectId = ResolveAccountId(dlg.Subject);
            if (subjectId == 0) { notice_.Show(StatusLevel.Warning, F("No account found with address '{0}'.", dlg.Subject)); return; }
         }
         else if (dlg.SelectedType == 1)
         {
            subjectId = ResolveGroupId(dlg.Subject);
            if (subjectId == 0) { notice_.Show(StatusLevel.Warning, F("No group found named '{0}'.", dlg.Subject)); return; }
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
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not save the permission: {0}", ex.Message));
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
            int id = (int)account.ID;
            ServerSession.Release(account);
            ServerSession.Release(accounts);
            ServerSession.Release(domain);
            return id;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { return 0; }
         finally { ServerSession.Release(domains); }
      }

      private static int ResolveGroupId(string name)
      {
         dynamic groups = ServerSession.Current.Application.Settings.Groups;
         try
         {
            int count = (int)groups.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic g = groups.Item[i];
               bool match = string.Equals((string)g.Name, name, StringComparison.OrdinalIgnoreCase);
               int id = match ? (int)g.ID : 0;
               ServerSession.Release(g);
               if (match)
                  return id;
            }
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
         finally { ServerSession.Release(groups); }
         return 0;
      }
   }

   /// <summary>Add/edit dialog for a single ACL entry, on the standard frame.</summary>
   internal class PermissionEditDialog : FluentDialogWindow
   {
      private readonly ComboBox typeCombo_ = new();
      private readonly TextBox subject_ = new();
      private readonly FieldRow subjectRow_;
      private readonly List<(CheckBox Box, int Bit)> flagBoxes_ = new();

      public int SelectedType { get; private set; }
      public string Subject { get; private set; }
      public int Value { get; private set; }

      public PermissionEditDialog(Window owner, (int Value, string Label)[] types, (string Label, int Bit)[] flags)
      {
         Owner = owner;
         Title = L("Access-control entry");

         var body = new StackPanel();
         foreach ((int value, string label) in types)
            typeCombo_.Items.Add(new ComboBoxItem { Content = label, Tag = value });
         typeCombo_.SelectedIndex = 0;
         typeCombo_.SelectionChanged += (s, e) => UpdateSubjectState();
         body.Children.Add(Label(L("Applies _to"), typeCombo_));

         subjectRow_ = Label(L("_Account address"), subject_);
         body.Children.Add(subjectRow_);

         body.Children.Add(DialogFields.Caption(L("Permissions")));
         foreach ((string label, int bit) in flags)
         {
            var cb = new CheckBox { Content = label, Margin = new Thickness(0, DesignTokens.Space.Xs, 0, DesignTokens.Space.Xs) };
            flagBoxes_.Add((cb, bit));
            body.Children.Add(cb);
         }

         // Enter saves, Escape cancels.
         var ok = new Wpf.Ui.Controls.Button { Content = L("_Save"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         ok.Click += (s, e) => Commit();
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => Close();

         UseFrame(L("Access-control entry"), body, ok, cancel, width: 420);
         UpdateSubjectState();
      }

      public void Initialize(int type, string subject, int value)
      {
         foreach (ComboBoxItem item in typeCombo_.Items)
            if ((int)item.Tag == type)
               typeCombo_.SelectedItem = item;
         subject_.Text = subject ?? "";
         foreach ((CheckBox box, int bit) in flagBoxes_)
            box.IsChecked = (value & bit) != 0;
         UpdateSubjectState();
      }

      private void UpdateSubjectState()
      {
         int type = typeCombo_.SelectedItem is ComboBoxItem cbi ? (int)cbi.Tag : 0;
         bool needsSubject = type != 2; // Anyone needs no subject
         subjectRow_.Label = type == 1 ? L("Group name") : L("Account address");
         subjectRow_.Visibility = needsSubject ? Visibility.Visible : Visibility.Collapsed;

         // The caption above this box changes with the entry type, so its accessible
         // name has to change with it. The row names the editor once, from the
         // caption it was built with; a group entry would otherwise announce
         // itself as "Account address".
         AutomationProperties.SetName(subject_, subjectRow_.Label);
      }

      private void Commit()
      {
         SelectedType = typeCombo_.SelectedItem is ComboBoxItem cbi ? (int)cbi.Tag : 0;
         Subject = subject_.Text.Trim();
         int value = 0;
         foreach ((CheckBox box, int bit) in flagBoxes_)
            if (box.IsChecked is true)
               value |= bit;
         Value = value;
         DialogResult = true;
         Close();
      }

      /// <summary>
      /// A caption and its editor as one row, which names the editor to UI
      /// Automation. The eleven permission checkboxes are already named by their
      /// own Content.
      /// </summary>
      private static FieldRow Label(string text, FrameworkElement editor) => DialogFields.Field(text, editor);
   }
}
