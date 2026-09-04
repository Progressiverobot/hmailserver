// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Shared case-insensitive substring filtering for the Control Panel list/grid
   /// pages. Filters the live <see cref="System.Windows.Data.ICollectionView"/> of
   /// whatever is currently bound to a control, so it works regardless of the row
   /// type (plain strings or row objects) and survives a reload as long as the
   /// caller re-applies it.
   /// </summary>
   public static class ListSearch
   {
      /// <summary>
      /// Applies (or clears, when <paramref name="query"/> is empty) a substring
      /// filter over the items currently bound to <paramref name="control"/>.
      /// </summary>
      public static void Apply(ItemsControl control, string query)
      {
         if (control == null)
            return;

         ICollectionView view = CollectionViewSource.GetDefaultView(control.ItemsSource);
         if (view == null)
            return;

         string q = query?.Trim();
         view.Filter = string.IsNullOrEmpty(q) ? null : item => Matches(item, q);
      }

      private static bool Matches(object item, string q)
      {
         if (item == null)
            return false;

         if (item is string s)
            return s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

         Type t = item.GetType();
         if (t.IsPrimitive || item is DateTime)
            return item.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

         // Booleans are skipped, because their text is "True"/"False" and nobody
         // searches for that - while plenty of real queries are substrings of it.
         // With the domain and account lists now carrying an Active flag, typing
         // "al" to find alice also matched every INACTIVE row (a substring of
         // "False"), and "tr"/"ru"/"rue" matched every active one.
         foreach (PropertyInfo p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                     .Where(p => p.GetIndexParameters().Length == 0
                                                 && p.PropertyType != typeof(bool)
                                                 && p.PropertyType != typeof(bool?)))
         {
            object value;
            try
            {
               value = p.GetValue(item);
            }
            catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
            {
               continue;
            }

            if (value != null && value.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
               return true;
         }

         return false;
      }
   }
}
