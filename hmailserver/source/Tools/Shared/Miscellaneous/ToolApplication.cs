// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Drawing;
using System.Windows.Forms;

namespace hMailServer.Shared
{
   public static class ToolApplication
   {
      /// <summary>
      /// Standard WinForms bootstrap for the setup tools. The forms were designed
      /// against the .NET Framework defaults (Microsoft Sans Serif 8.25pt,
      /// system-DPI aware) with absolute layouts, so the modern .NET defaults are
      /// pinned back to those values to keep every dialog's metrics unchanged.
      /// </summary>
      public static void Initialize()
      {
         System.Windows.Forms.Application.EnableVisualStyles();
         System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
         System.Windows.Forms.Application.SetDefaultFont(new Font("Microsoft Sans Serif", 8.25f));
         System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.SystemAware);
      }
   }
}
