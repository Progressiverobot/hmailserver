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
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

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
      public enum FieldKind { Text, Multiline, Number, Bool, Combo }

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
         public int Id;
         public Dictionary<string, object> Values = new();
         public string Display(string prop) =>
            Values.TryGetValue(prop, out object v) ? FormatCell(v) : "";
      }

      private readonly CollectionSpec spec_;
      private readonly DataGrid grid_ = new();
      private readonly ObservableCollection<Row> rows_ = new();
      private readonly TextBlock countBadge_ = new();
      private readonly TextBlock status_ = new();

      public CollectionEditorView(CollectionSpec spec)
      {
         spec_ = spec;
         Build();
      }

      public void OnEnter() => Reload();
      public void OnLeave() { }

      // ---- UI scaffolding ----------------------------------------------------

      private void Build()
      {
         var root = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         // Title + subtitle
         var head = new StackPanel();
         head.Children.Add(new TextBlock { Text = spec_.Title, Style = (Style) FindResource("PageTitle") });
         head.Children.Add(new TextBlock { Text = spec_.Subtitle, Style = (Style) FindResource("PageSubtitle") });
         root.Children.Add(head);

         // Toolbar: count badge + actions
         var toolbar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
         toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
         toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

         var badge = new Border
         {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush) FindResource("BrandBrush")
         };
         countBadge_.Foreground = Brushes.White;
         countBadge_.FontSize = 12.5;
         countBadge_.FontWeight = FontWeights.SemiBold;
         badge.Child = countBadge_;
         toolbar.Children.Add(badge);

         var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
         Grid.SetColumn(actions, 1);
         if (spec_.CanAdd)
            actions.Children.Add(MakeButton("Add", ControlAppearance.Primary, SymbolRegular.Add24, (_, _) => OpenDialog(null)));
         actions.Children.Add(MakeButton("Edit", ControlAppearance.Secondary, SymbolRegular.Edit24, (_, _) => EditSelected()));
         if (spec_.CanDelete)
            actions.Children.Add(MakeButton("Delete", ControlAppearance.Danger, SymbolRegular.Delete24, (_, _) => DeleteSelected()));
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

         foreach (FieldSpec f in spec_.Fields)
         {
            if (!f.ShowInGrid)
               continue;
            string prop = f.Prop;
            var col = new DataGridTextColumn
            {
               Header = f.Label,
               Binding = new System.Windows.Data.Binding($"Values[{prop}]") { Converter = CellConverter.Instance },
               Width = double.IsNaN(f.GridWidth)
                  ? new DataGridLength(1, DataGridLengthUnitType.Star)
                  : new DataGridLength(f.GridWidth)
            };
            grid_.Columns.Add(col);
         }

         card.Child = grid_;
         root.Children.Add(card);

         status_.Margin = new Thickness(0, 12, 0, 0);
         status_.FontSize = 12;
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
            int count = (int) collection.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic item = collection.Item[i];
               var row = new Row { Id = TryGetId(item) };
               foreach (FieldSpec f in spec_.Fields)
               {
                  try { row.Values[f.Prop] = GetProp(item, f.Prop); }
                  catch (Exception) { row.Values[f.Prop] = null; }
               }
               rows_.Add(row);
               ServerSession.Release(item);
            }
            status_.Text = "Loaded from server.";
         }
         catch (Exception ex)
         {
            status_.Text = "Could not load: " + ex.Message;
         }
         finally
         {
            ServerSession.Release((object) collection);
         }

         countBadge_.Text = rows_.Count == 1
            ? "1 " + spec_.ItemNoun
            : rows_.Count + " " + spec_.ItemNoun + "s";
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not save: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release((object) item);
            ServerSession.Release((object) collection);
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not delete: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release((object) item);
            ServerSession.Release((object) collection);
         }

         Reload();
      }

      private dynamic FindById(dynamic collection, int id)
      {
         int count = (int) collection.Count;
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
         try { return (int) GetProp(item, "ID"); }
         catch (Exception) { return -1; }
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
         public static readonly CellConverter Instance = new();
         public object Convert(object value, Type t, object p, CultureInfo c) => FormatCell(value);
         public object ConvertBack(object value, Type t, object p, CultureInfo c) => value;
      }
   }
}
