using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Public IMAP folders (Settings.PublicFolders): shared mailboxes visible to
   /// multiple accounts. Folders can be added/removed and each folder's ACL
   /// permissions edited via <see cref="FolderPermissionsDialog"/>.
   /// </summary>
   public class PublicFoldersView : UserControl, IPageLifecycle
   {
      private readonly ListBox list_ = new()
      {
         FontSize = 13,
         BorderThickness = new Thickness(0),
         Background = System.Windows.Media.Brushes.Transparent
      };
      private readonly TextBlock status_ = new() { FontSize = 12, Margin = new Thickness(0, 12, 0, 0) };

      public PublicFoldersView() => Build();

      public void OnEnter() => Reload();
      public void OnLeave() { }

      private void Build()
      {
         var root = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var head = new StackPanel();
         var title = new TextBlock { Text = "Public folders" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         head.Children.Add(title);
         var sub = new TextBlock { Text = "Shared IMAP folders that several accounts can access. Use the hierarchy delimiter to create sub-folders, and edit permissions to grant access." };
         sub.SetResourceReference(StyleProperty, "PageSubtitle");
         head.Children.Add(sub);
         root.Children.Add(head);

         var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 12) };
         Grid.SetRow(toolbar, 1);
         toolbar.Children.Add(MakeButton("Add folder", Wpf.Ui.Controls.ControlAppearance.Primary, (_, _) => AddFolder()));
         toolbar.Children.Add(MakeButton("Permissions", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => EditPermissions()));
         toolbar.Children.Add(MakeButton("Delete", Wpf.Ui.Controls.ControlAppearance.Danger, (_, _) => DeleteFolder()));
         toolbar.Children.Add(MakeButton("Refresh", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => Reload()));
         root.Children.Add(toolbar);

         var card = new Border { Padding = new Thickness(8) };
         card.SetResourceReference(StyleProperty, "Card");
         card.Child = list_;
         Grid.SetRow(card, 2);
         root.Children.Add(card);

         list_.MouseDoubleClick += (_, _) => EditPermissions();

         status_.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         Grid.SetRow(status_, 3);
         root.Children.Add(status_);

         Content = root;
      }

      private static Wpf.Ui.Controls.Button MakeButton(string text, Wpf.Ui.Controls.ControlAppearance appearance, RoutedEventHandler onClick)
      {
         var b = new Wpf.Ui.Controls.Button { Content = text, Appearance = appearance, Margin = new Thickness(8, 0, 0, 0), MinWidth = 92 };
         b.Click += onClick;
         return b;
      }

      private void Reload()
      {
         list_.Items.Clear();
         dynamic folders = ServerSession.Current.Application.Settings.PublicFolders;
         try
         {
            int count = (int) folders.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic f = folders.Item[i];
               list_.Items.Add((string) f.Name);
               ServerSession.Release(f);
            }
            status_.Text = count + (count == 1 ? " public folder." : " public folders.");
         }
         catch (Exception ex)
         {
            status_.Text = "Could not load public folders: " + ex.Message;
         }
         finally
         {
            ServerSession.Release(folders);
         }
      }

      private void AddFolder()
      {
         string name = InputDialog.Prompt(Window.GetWindow(this), "New public folder", "Folder name:");
         if (string.IsNullOrWhiteSpace(name))
            return;

         dynamic folders = ServerSession.Current.Application.Settings.PublicFolders;
         try
         {
            dynamic created = folders.Add(name.Trim());
            ServerSession.Release(created);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not create the folder: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(folders);
         }
         Reload();
      }

      private void DeleteFolder()
      {
         if (list_.SelectedItem is not string name)
         {
            status_.Text = "Select a folder first.";
            return;
         }
         if (MessageBox.Show("Delete the public folder '" + name + "' and all messages in it?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic folders = ServerSession.Current.Application.Settings.PublicFolders;
         try
         {
            dynamic folder = folders.ItemByName[name];
            folder.Delete();
            ServerSession.Release(folder);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete the folder: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(folders);
         }
         Reload();
      }

      private void EditPermissions()
      {
         if (list_.SelectedItem is not string name)
         {
            status_.Text = "Select a folder first.";
            return;
         }
         new FolderPermissionsDialog(Window.GetWindow(this), name).ShowDialog();
      }
   }
}
