// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
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

// Wpf.Ui.Controls has a Card of its own, so the scaffold's components are named
// explicitly here rather than imported wholesale.
using Card = hMailServer.ControlPanel.Views.Scaffold.Card;
using EmptyState = hMailServer.ControlPanel.Views.Scaffold.EmptyState;
using InlineNotice = hMailServer.ControlPanel.Views.Scaffold.InlineNotice;
using PageHeader = hMailServer.ControlPanel.Views.Scaffold.PageHeader;
using StatusPill = hMailServer.ControlPanel.Views.Scaffold.StatusPill;
using Toolbar = hMailServer.ControlPanel.Views.Scaffold.Toolbar;
using StatusLevel = hMailServer.ControlPanel.Services.StatusLevel;
using static hMailServer.ControlPanel.Services.Loc;

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
         public string ItemNoun = L("item");
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

      // How many entries there are, as a pill beside the page title - a state,
      // so it is drawn the way every other state on every other page is. Normal,
      // because a count says nothing on its own.
      private readonly StatusPill countBadge_ = new() { Level = StatusLevel.Normal };

      // What the last action said, at its level, and what an empty list means.
      private readonly InlineNotice status_ = new() { Visibility = Visibility.Collapsed };
      private readonly EmptyState empty_ = new() { Icon = SymbolRegular.DocumentBulletList24, Visibility = Visibility.Collapsed };

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
         var root = new Grid();
         if (embedded_)
            root.Margin = new Thickness(0, 8, 0, 0);
         else
            root.SetResourceReference(MarginProperty, "AppPagePadding");

         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
         if (spec_.CanAdd)
            actions.Children.Add(MakeButton(L("_Add"), ControlAppearance.Primary, SymbolRegular.Add24, (_, _) => OpenDialog(null)));
         var edit = MakeButton(L("_Edit"), ControlAppearance.Secondary, SymbolRegular.Edit24, (_, _) => EditSelected());
         actions.Children.Add(edit);
         Wpf.Ui.Controls.Button del = null;
         if (spec_.CanDelete)
         {
            del = MakeButton(L("_Delete"), ControlAppearance.Secondary, SymbolRegular.Delete24, (_, _) => DeleteSelected());
            del.Foreground = Services.ThemeTokens.Danger;
            actions.Children.Add(del);
         }
         actions.Children.Add(MakeButton(L("_Refresh"), ControlAppearance.Secondary, SymbolRegular.ArrowSync24, (_, _) => Reload()));
         // Edit and Delete act on the selected row, so they are enabled only
         // while there is one; with nothing selected they used to answer a click
         // with "Select a row first." in the status line.
         if (del != null)
            Services.SelectionGate.Bind(grid_, edit, del);
         else
            Services.SelectionGate.Bind(grid_, edit);

         // Page mode puts the title, the count and the actions in one header;
         // embedded mode has no page title to hang them on, so the hint goes
         // above a toolbar that carries the count and the same actions.
         if (embedded_)
         {
            if (!string.IsNullOrEmpty(spec_.Subtitle))
            {
               var hint = new TextBlock { Text = spec_.Subtitle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
               hint.SetResourceReference(StyleProperty, "TextCaption");
               root.Children.Add(hint);
            }

            var toolbar = new Toolbar { ShowSearch = false, Filters = countBadge_, Actions = actions };
            Grid.SetRow(toolbar, 1);
            root.Children.Add(toolbar);
         }
         else
         {
            root.Children.Add(new PageHeader
            {
               Title = spec_.Title,
               Subtitle = spec_.Subtitle,
               Status = countBadge_,
               Actions = actions
            });
         }

         // Grid in a card
         var card = new Card { Padding = new Thickness(6) };
         Grid.SetRow(card, 2);

         grid_.AutoGenerateColumns = false;
         grid_.IsReadOnly = true;
         grid_.SelectionMode = DataGridSelectionMode.Single;
         grid_.ItemsSource = rows_;
         grid_.MouseDoubleClick += (_, _) => EditSelected();
         System.Windows.Automation.AutomationProperties.SetName(grid_, spec_.Title);

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

            // A number column is right-aligned, header and all, so the digits
            // line up and a column of scores can be read down.
            if (f.Kind == FieldKind.Number)
               GridStyles.Number(col);

            grid_.Columns.Add(col);
         }

         var host = new Grid();
         host.Children.Add(grid_);
         host.Children.Add(empty_);
         card.Content = host;
         root.Children.Add(card);

         status_.Margin = new Thickness(0, 12, 0, 0);
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
            // An empty list is a state to name, not a blank to stare at: the
            // empty state says so in the grid's own place.
            // The empty state carries the sentence that was in the status line.
            // Only a list that can be added to has one: the one collection that
            // cannot (the fixed set of server messages) is never empty, and
            // inventing a sentence for it would be a new text for a state that
            // does not occur.
            if (spec_.CanAdd)
               StatusText.Show(empty_, null, rows_.Count, null, L("Nothing here yet - Add creates the first entry."));

            // A load that worked says nothing: the count beside the title and
            // the empty state in the grid's place already say what there is, and
            // a notice on every visit to every list page would be noise.
            status_.Visibility = Visibility.Collapsed;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            empty_.Visibility = Visibility.Collapsed;
            Say_(StatusLevel.Critical, L("Could not load — ") + ServerSession.DescribeComError(ex));
         }
         finally
         {
            ServerSession.Release((object)collection);
         }

         countBadge_.Text = rows_.Count == 1
            ? "1 " + spec_.ItemNoun
            : rows_.Count + " " + Pluralize(spec_.ItemNoun);
      }

      /// <summary>What the last action said, at its level.</summary>
      private void Say_(StatusLevel level, string text)
      {
         status_.Level = level;
         status_.Text = text;
         status_.Visibility = Visibility.Visible;
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
            Say_(StatusLevel.Information, L("Select a row first."));
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
               Say_(StatusLevel.Warning, L("The item no longer exists."));
               return;
            }

            foreach (KeyValuePair<string, object> kv in dlg.Result)
               SetProp(item, kv.Key, kv.Value);

            item.Save();
            Say_(StatusLevel.Good, existing == null ? F("Added at {0}.", DateTime.Now.ToLongTimeString()) : F("Saved at {0}.", DateTime.Now.ToLongTimeString()));
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not save: {0}", ex.Message), L("Control Panel"));
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
            Say_(StatusLevel.Information, L("Select a row first."));
            return;
         }

         if (MessageBox.Show(F("Delete this {0}?", spec_.ItemNoun), L("Control Panel"),
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
            Say_(StatusLevel.Good, "Deleted.");
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not delete: {0}", ex.Message), L("Control Panel"));
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
         if (v is bool b) return b ? L("Yes") : L("No");
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
