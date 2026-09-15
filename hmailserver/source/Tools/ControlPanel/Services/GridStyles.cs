// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The grid styles the design system keys - GridNumberText in Scaffold.xaml
   /// and GridNumberHeader in App.xaml - for the pages that build their columns
   /// in code, where a XAML page writes ElementStyle and HeaderStyle.
   /// </summary>
   public static class GridStyles
   {
      /// <summary>Right-aligns a number column's cells and its header.</summary>
      public static void Number(DataGridBoundColumn column)
      {
         if (column == null || Application.Current == null)
            return;

         if (Application.Current.TryFindResource("GridNumberText") is Style cells)
            column.ElementStyle = cells;

         if (Application.Current.TryFindResource("GridNumberHeader") is Style header)
            column.HeaderStyle = header;
      }
   }
}
