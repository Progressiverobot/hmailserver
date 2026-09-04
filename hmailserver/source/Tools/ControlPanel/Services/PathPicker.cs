// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using Microsoft.Win32;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Shared file/folder browse dialogs for the path-entry boxes (backup
   /// destination, archive folder, certificate files, public-key file, ...).
   /// Uses the WPF common dialogs so there is no dependency on WinForms.
   /// </summary>
   public static class PathPicker
   {
      /// <summary>
      /// Shows a folder picker, seeded with <paramref name="current"/> when it points
      /// at an existing directory. Returns the chosen path, or null if cancelled.
      /// </summary>
      public static string PickFolder(string current)
      {
         var dialog = new OpenFolderDialog();
         SeedInitialDirectory(dialog, current);
         return dialog.ShowDialog() == true ? dialog.FolderName : null;
      }

      /// <summary>
      /// Shows a file picker, seeded with <paramref name="current"/> when it points
      /// at an existing file/directory. Returns the chosen path, or null if cancelled.
      /// </summary>
      public static string PickFile(string current, string filter)
      {
         // Fully qualified: the WPF dialog, which is not disposable, not the
         // WinForms one of the same name.
         var dialog = new Microsoft.Win32.OpenFileDialog
         {
            Filter = string.IsNullOrEmpty(filter) ? "All files (*.*)|*.*" : filter
         };
         SeedInitialDirectory(dialog, current);
         return dialog.ShowDialog() == true ? dialog.FileName : null;
      }

      private static void SeedInitialDirectory(FileDialog dialog, string current)
      {
         string dir = ResolveExistingDirectory(current);
         if (dir != null)
            dialog.InitialDirectory = dir;
      }

      private static void SeedInitialDirectory(OpenFolderDialog dialog, string current)
      {
         string dir = ResolveExistingDirectory(current);
         if (dir != null)
            dialog.InitialDirectory = dir;
      }

      private static string ResolveExistingDirectory(string current)
      {
         if (string.IsNullOrWhiteSpace(current))
            return null;

         try
         {
            current = current.Trim();
            if (Directory.Exists(current))
               return current;

            string parent = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
               return parent;
         }
         catch
         {
            // Ignore malformed paths; just open the dialog at its default location.
         }

         return null;
      }
   }
}
