// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using hMailServer.ControlPanel.Services;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBox = Wpf.Ui.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using DataGrid = System.Windows.Controls.DataGrid;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;
using System.Linq;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Generic, data-driven editor for any hMailServer COM collection
   /// (SURBL servers, DNS blacklists, white-list addresses, blocked
   /// attachments, groups, server messages, ...). A <see cref="CollectionSpec"/>
   /// describes the fields; this view renders a polished list with a live
   /// count badge, add / edit / delete and a generated property dialog.
   /// </summary>
   public class CollectionEditorView : UserControl, IPageLifecycle
   {
      public enum FieldKind { Text, Multiline, Number, Bool, Combo, Password }

      public class FieldSpec
      {
         public string Prop;
         public string Label;
         public FieldKind Kind = FieldKind.Text;
         public (int Value, string Label)[] Options;   // for Combo
         public bool ShowInGrid = true;
         public double GridWidth = double.NaN;          // NaN => *
         public object Default;
      }

      public class CollectionSpec
      {
         public string Title;
         public string Subtitle;
         public Func<dynamic> GetCollection;            // returns the COM collection (caller releases)
         public List<FieldSpec> Fields = new();
         public bool CanAdd = true;
         public bool CanDelete = true;
         public string ItemNoun = "item";
      }

      internal sealed class Row
      {
         // These must stay properties: the generated grid columns bind to
         // "Values[<prop>]", and WPF data binding resolves properties only -
         // a public field silently binds to nothing and every cell renders blank.
         public int Id { get; set; }
         public Dictionary<string, object> Values { get; } = new();
         public string Display(string prop) =>
            Values.TryGetValue(prop, out object v) ? FormatCell(v) : "";
      }

      private readonly CollectionSpec spec_;
      private readonly bool embedded_;
      private readonly DataGrid grid_ = new();
      private readonly ObservableCollection<Row> rows_ = new();
      private readonly TextBlock countBadge_ = new();
      private readonly TextBlock status_ = new();

      public CollectionEditorView(CollectionSpec spec) : this(spec, false)
      {
      }

      /// <summary>Creates the editor for one collection, optionally without page chrome.</summary>
      /// <param name="embedded">
      /// When true the page chrome (large title/subtitle and outer page margins)
      /// is dropped so the editor can be hosted inside a dialog tab. Only a
      /// compact one-line hint is shown above the grid.
      /// </param>
      public CollectionEditorView(CollectionSpec spec, bool embedded)
      {
         spec_ = spec;
         embedded_ = embedded;
         Build();
      }

      public void OnEnter() => Reload();
      public void OnLeave() { }

      /// <summary>Loads (or reloads) the collection. Used when embedded in a dialog.</summary>
      public void Refresh() => Reload();

      // ---- UI scaffolding ----------------------------------------------------

      private void Build()
      {
         var root = new Grid { Margin = embedded_ ? new Thickness(0, 8, 0, 0) : new Thickness(26, 20, 26, 20) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         // Title + subtitle (page mode) or a compact hint (embedded mode).
         if (embedded_)
         {
            if (!string.IsNullOrEmpty(spec_.Subtitle))
            {
               var hint = new TextBlock
               {
                  Text = spec_.Subtitle,
                  FontSize = Typography.Caption,
                  TextWrapping = TextWrapping.Wrap,
                  Margin = new Thickness(0, 0, 0, 10)
               };
               hint.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
               root.Children.Add(hint);
            }
         }
         else
         {
            var head = new StackPanel();
            head.Children.Add(new TextBlock { Text = spec_.Title, Style = (Style)FindResource("PageTitle") });
            head.Children.Add(new TextBlock { Text = spec_.Subtitle, Style = (Style)FindResource("PageSubtitle") });
            root.Children.Add(head);
         }

         // Toolbar: count badge + actions
         var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
         toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var badge = new Border
         {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
         };
         // The accent-button pair, exactly as Appearance=Primary buttons use it,
         // so the badge always carries whatever contrast the theme's own accent
         // buttons do. The previous hardcoded White on BrandBrush failed in the
         // dark theme: ThemeTokens retints BrandBrush to #4C8DFF there, and
         // white on #4C8DFF is 3.2:1 - below the 4.5:1 required for this 12px
         // SemiBold text. The pair is defined in every WPF-UI theme dictionary,
         // High Contrast included, and both references re-resolve on theme flips.
         badge.SetResourceReference(Border.BackgroundProperty, "AccentButtonBackground");
         countBadge_.SetResourceReference(TextBlock.ForegroundProperty, "AccentButtonForeground");
         countBadge_.FontSize = Typography.Label;
         countBadge_.FontWeight = FontWeights.SemiBold;
         badge.Child = countBadge_;
         toolbar.Children.Add(badge);

         var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
         Grid.SetColumn(actions, 1);
         if (spec_.CanAdd)
            actions.Children.Add(MakeButton("Add", ControlAppearance.Primary, SymbolRegular.Add24, (_, _) => OpenDialog(null)));
         actions.Children.Add(MakeButton("Edit", ControlAppearance.Secondary, SymbolRegular.Edit24, (_, _) => EditSelected()));
         if (spec_.CanDelete)
         {
            var del = MakeButton("Delete", ControlAppearance.Secondary, SymbolRegular.Delete24, (_, _) => DeleteSelected());
            del.Foreground = Services.ThemeTokens.Danger;
            actions.Children.Add(del);
         }
         actions.Children.Add(MakeButton("Refresh", ControlAppearance.Secondary, SymbolRegular.ArrowSync24, (_, _) => Reload()));
         toolbar.Children.Add(actions);
         Grid.SetRow(toolbar, 1);
         root.Children.Add(toolbar);

         // Grid in a card
         var card = new Border { Padding = new Thickness(6) };
         card.SetResourceReference(StyleProperty, "Card");
         Grid.SetRow(card, 2);

         grid_.AutoGenerateColumns = false;
         grid_.IsReadOnly = true;
         grid_.HeadersVisibility = DataGridHeadersVisibility.Column;
         grid_.GridLinesVisibility = DataGridGridLinesVisibility.None;
         grid_.Background = Brushes.Transparent;
         grid_.BorderThickness = new Thickness(0);
         grid_.RowHeight = 34;
         grid_.SelectionMode = DataGridSelectionMode.Single;
         grid_.ItemsSource = rows_;
         grid_.MouseDoubleClick += (_, _) => EditSelected();

         foreach (FieldSpec f in spec_.Fields.Where(f => f.ShowInGrid))
         {
            string prop = f.Prop;
            var col = new DataGridTextColumn
            {
               Header = f.Label,
               // A Combo column stores the enum's number, and a grid cell reading
               // "1" where the dialog above it reads "SSL/TLS" is not a display
               // detail - it is the difference between a list an administrator can
               // scan for the unencrypted row and one they cannot.
               Binding = new System.Windows.Data.Binding($"Values[{prop}]")
               {
                  Converter = f.Kind == FieldKind.Combo && f.Options != null
                     ? new CellConverter(f.Options)
                     : CellConverter.Instance
               },
               Width = double.IsNaN(f.GridWidth)
                  ? new DataGridLength(1, DataGridLengthUnitType.Star)
                  : new DataGridLength(f.GridWidth)
            };
            grid_.Columns.Add(col);
         }

         card.Child = grid_;
         root.Children.Add(card);

         status_.Margin = new Thickness(0, 12, 0, 0);
         status_.FontSize = Typography.Caption;
         status_.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         Grid.SetRow(status_, 3);
         root.Children.Add(status_);

         Content = root;
      }

      private static Button MakeButton(string text, ControlAppearance appearance, SymbolRegular icon, RoutedEventHandler onClick)
      {
         var b = new Button
         {
            Content = text,
            Appearance = appearance,
            Icon = new SymbolIcon { Symbol = icon },
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 92
         };
         b.Click += onClick;
         return b;
      }

      // ---- Data --------------------------------------------------------------

      private void Reload()
      {
         rows_.Clear();
         dynamic collection = null;
         try
         {
            collection = spec_.GetCollection();
            int count = (int)collection.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic item = collection.Item[i];
               var row = new Row { Id = TryGetId(item) };
               foreach (FieldSpec f in spec_.Fields)
               {
                  try { row.Values[f.Prop] = GetProp(item, f.Prop); }
                  catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { row.Values[f.Prop] = null; }
               }
               rows_.Add(row);
               ServerSession.Release(item);
            }
            status_.Text = "Loaded from server.";
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            status_.Text = "Could not load — " + ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object)collection);
         }

         countBadge_.Text = rows_.Count == 1
            ? "1 " + spec_.ItemNoun
            : rows_.Count + " " + Pluralize(spec_.ItemNoun);
      }

      private static string Pluralize(string noun)
      {
         if (string.IsNullOrEmpty(noun))
            return noun;
         if (noun.EndsWith("s") || noun.EndsWith("x") || noun.EndsWith("z") ||
             noun.EndsWith("ch") || noun.EndsWith("sh"))
            return noun + "es";
         if (noun.EndsWith("y") && noun.Length > 1 && !"aeiou".Contains(noun[^2]))
            return noun.Substring(0, noun.Length - 1) + "ies";
         return noun + "s";
      }

      private void EditSelected()
      {
         if (grid_.SelectedItem is Row row)
            OpenDialog(row);
         else
            status_.Text = "Select a row first.";
      }

      private void OpenDialog(Row existing)
      {
         var dlg = new FieldDialog(spec_, existing, Window.GetWindow(this));
         if (dlg.ShowDialog() != true)
            return;

         dynamic collection = null;
         dynamic item = null;
         try
         {
            collection = spec_.GetCollection();
            item = existing == null ? collection.Add() : FindById(collection, existing.Id);
            if (item == null)
            {
               status_.Text = "The item no longer exists.";
               return;
            }

            foreach (KeyValuePair<string, object> kv in dlg.Result)
               SetProp(item, kv.Key, kv.Value);

            item.Save();
            status_.Text = (existing == null ? "Added" : "Saved") + " at " + DateTime.Now.ToLongTimeString() + ".";
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not save: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release((object)item);
            ServerSession.Release((object)collection);
         }

         Reload();
      }

      private void DeleteSelected()
      {
         if (grid_.SelectedItem is not Row row)
         {
            status_.Text = "Select a row first.";
            return;
         }

         if (MessageBox.Show($"Delete this {spec_.ItemNoun}?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         dynamic collection = null;
         dynamic item = null;
         try
         {
            collection = spec_.GetCollection();
            item = FindById(collection, row.Id);
            if (item != null)
               item.Delete();
            status_.Text = "Deleted.";
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not delete: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release((object)item);
            ServerSession.Release((object)collection);
         }

         Reload();
      }

      private dynamic FindById(dynamic collection, int id)
      {
         int count = (int)collection.Count;
         for (int i = 0; i < count; i++)
         {
            dynamic item = collection.Item[i];
            if (TryGetId(item) == id)
               return item;
            ServerSession.Release(item);
         }
         return null;
      }

      // ---- COM reflection helpers -------------------------------------------

      private static int TryGetId(dynamic item)
      {
         try { return (int)GetProp(item, "ID"); }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { return -1; }
      }

      internal static object GetProp(object owner, string name)
         => owner.GetType().InvokeMember(name, BindingFlags.GetProperty, null, owner, null);

      internal static void SetProp(object owner, string name, object value)
         => owner.GetType().InvokeMember(name, BindingFlags.SetProperty, null, owner, new[] { value });

      internal static string FormatCell(object v)
      {
         if (v == null) return "";
         if (v is bool b) return b ? "Yes" : "No";
         return Convert.ToString(v, CultureInfo.CurrentCulture);
      }

      private sealed class CellConverter : System.Windows.Data.IValueConverter
      {
         /// <summary>Option labels for a Combo column, or null for every other kind.</summary>
         private readonly (int Value, string Label)[] options_;

         private CellConverter()
         {
         }

         public CellConverter((int Value, string Label)[] options) => options_ = options;

         public static readonly CellConverter Instance = new();

         public object Convert(object value, Type t, object p, CultureInfo c)
         {
            if (options_ != null && value != null)
            {
               // A value with no matching option falls through to the number
               // rather than being shown as blank or as the first option: an
               // enum the GUI does not know about is a real thing to notice,
               // and blanking it would hide it.
               try
               {
                  int number = System.Convert.ToInt32(value, CultureInfo.InvariantCulture);
                  foreach ((int Value, string Label) option in options_.Where(o => o.Value == number))
                     return option.Label;
               }
               catch (FormatException)
               {
                  // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
               }
               catch (InvalidCastException)
               {
                  // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
               }
               catch (OverflowException)
               {
                  // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
               }
            }

            return FormatCell(value);
         }

         public object ConvertBack(object value, Type t, object p, CultureInfo c) => value;
      }
   }
}
