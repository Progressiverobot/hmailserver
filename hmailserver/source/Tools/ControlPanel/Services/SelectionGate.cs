// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// A button that acts on the selected row is enabled only while there is one.
   /// Every list page had these buttons live all the time - Delete, Edit,
   /// Properties, Release, Deliver now - and a click with nothing selected either
   /// did nothing or wrote "Select a row first" into a status line the eye is not
   /// on. A disabled button says the same thing before the click, where the eye
   /// is. One call per page, after the list and its buttons exist:
   /// <c>SelectionGate.Bind(grid, editButton, deleteButton)</c>.
   ///
   /// The gate watches the selector's SelectedItem through its dependency property,
   /// not only SelectionChanged, because replacing ItemsSource clears the selection
   /// and a Reload does exactly that on every page.
   /// </summary>
   public static class SelectionGate
   {
      public static void Bind(Selector list, params UIElement[] targets)
      {
         if (list == null || targets == null || targets.Length == 0)
            return;

         void Apply()
         {
            bool any = list.SelectedItem != null;
            foreach (UIElement target in targets)
            {
               if (target != null)
                  target.IsEnabled = any;
            }
         }

         DependencyPropertyDescriptor
            .FromProperty(Selector.SelectedItemProperty, list.GetType())
            .AddValueChanged(list, (_, _) => Apply());
         list.SelectionChanged += (_, _) => Apply();
         Apply();
      }
   }
}
