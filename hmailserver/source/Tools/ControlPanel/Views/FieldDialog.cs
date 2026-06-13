using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBox = Wpf.Ui.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using MessageBox = System.Windows.MessageBox;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal property editor generated from a
   /// <see cref="CollectionEditorView.CollectionSpec"/>. Produces a
   /// dictionary of property =&gt; value on OK.
   /// </summary>
   internal sealed class FieldDialog : Window
   {
      public Dictionary<string, object> Result { get; } = new();

      private readonly List<Func<bool>> committers_ = new();

      public FieldDialog(CollectionEditorView.CollectionSpec spec, CollectionEditorView.Row existing, Window owner)
      {
         Owner = owner;
         Title = (existing == null ? "Add " : "Edit ") + spec.ItemNoun;
         Width = 460;
         SizeToContent = SizeToContent.Height;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         ResizeMode = ResizeMode.NoResize;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var panel = new StackPanel { Margin = new Thickness(22) };

         foreach (CollectionEditorView.FieldSpec f in spec.Fields)
         {
            if (f.Prop == "ID")
               continue;

            object current = existing != null && existing.Values.TryGetValue(f.Prop, out object v)
               ? v
               : f.Default;

            BuildField(panel, f, current);
         }

         var buttons = new StackPanel
         {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
         };
         var ok = new Button { Content = "Save", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 88 };
         ok.Click += (_, _) =>
         {
            foreach (Func<bool> commit in committers_)
               if (!commit())
                  return;
            DialogResult = true;
            Close();
         };
         var cancel = new Button { Content = "Cancel", MinWidth = 88 };
         cancel.Click += (_, _) => Close();
         buttons.Children.Add(ok);
         buttons.Children.Add(cancel);
         panel.Children.Add(buttons);

         Content = panel;
      }

      private void BuildField(Panel host, CollectionEditorView.FieldSpec f, object current)
      {
         string prop = f.Prop;

         switch (f.Kind)
         {
            case CollectionEditorView.FieldKind.Bool:
            {
               var box = new CheckBox
               {
                  Content = f.Label,
                  IsChecked = current is bool b && b,
                  FontSize = 13,
                  Margin = new Thickness(0, 6, 0, 10)
               };
               host.Children.Add(box);
               committers_.Add(() => { Result[prop] = box.IsChecked == true; return true; });
               break;
            }
            case CollectionEditorView.FieldKind.Combo:
            {
               host.Children.Add(Label(f.Label));
               var combo = new ComboBox { FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
               int sel = current is int ci ? ci : Convert.ToInt32(current ?? 0);
               foreach ((int Value, string Label) opt in f.Options)
               {
                  var item = new ComboBoxItem { Content = opt.Label, Tag = opt.Value };
                  combo.Items.Add(item);
                  if (opt.Value == sel)
                     combo.SelectedItem = item;
               }
               if (combo.SelectedItem == null && combo.Items.Count > 0)
                  combo.SelectedIndex = 0;
               host.Children.Add(combo);
               committers_.Add(() =>
               {
                  Result[prop] = combo.SelectedItem is ComboBoxItem cbi ? (int) cbi.Tag : 0;
                  return true;
               });
               break;
            }
            case CollectionEditorView.FieldKind.Multiline:
            {
               host.Children.Add(Label(f.Label));
               var box = new TextBox
               {
                  Text = Convert.ToString(current) ?? "",
                  FontSize = 13,
                  AcceptsReturn = true,
                  TextWrapping = TextWrapping.Wrap,
                  MinLines = 4,
                  MaxLines = 10,
                  Margin = new Thickness(0, 0, 0, 10)
               };
               host.Children.Add(box);
               committers_.Add(() => { Result[prop] = box.Text; return true; });
               break;
            }
            case CollectionEditorView.FieldKind.Number:
            {
               host.Children.Add(Label(f.Label));
               var box = new TextBox { Text = Convert.ToString(current ?? 0), FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
               host.Children.Add(box);
               committers_.Add(() =>
               {
                  if (!int.TryParse(box.Text.Trim(), out int n))
                  {
                     MessageBox.Show(f.Label + " must be a number.", "Control Panel");
                     return false;
                  }
                  Result[prop] = n;
                  return true;
               });
               break;
            }
            default:
            {
               host.Children.Add(Label(f.Label));
               var box = new TextBox { Text = Convert.ToString(current) ?? "", FontSize = 13, Margin = new Thickness(0, 0, 0, 10) };
               host.Children.Add(box);
               committers_.Add(() => { Result[prop] = box.Text; return true; });
               break;
            }
         }
      }

      private static TextBlock Label(string text) => new()
      {
         Text = text,
         FontSize = 12.5,
         Margin = new Thickness(0, 6, 0, 4)
      };
   }
}
