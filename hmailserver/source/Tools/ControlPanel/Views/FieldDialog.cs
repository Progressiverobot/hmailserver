// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBox = Wpf.Ui.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using MessageBox = System.Windows.MessageBox;
using hMailServer.ControlPanel.Services;
using System.Linq;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Modal property editor generated from a
   /// <see cref="CollectionEditorView.CollectionSpec"/>. Produces a
   /// dictionary of property =&gt; value on OK.
   ///
   /// This is the editor behind every collection page - SURBL servers, DNS
   /// blacklists, the two white lists, blocked attachments, incoming relays,
   /// groups, server messages - so an accessibility defect here is one defect in
   /// perhaps a dozen places. Until this pass there was one: each field's wording
   /// was a TextBlock placed above the control with nothing connecting the two, so
   /// every box in every one of those dialogs was announced as an unnamed "edit".
   /// </summary>
   internal sealed class FieldDialog : FluentDialogWindow
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
         AutomationProperties.SetName(this, Title);

         var panel = new StackPanel { Margin = new Thickness(22) };

         List<CollectionEditorView.FieldSpec> fields =
            spec.Fields.Where(f => f.Prop != "ID").ToList();

         // Resolved for the whole dialog at once, the same way the settings pages
         // do it, so that two fields with the same wording are told apart by the
         // object they belong to rather than announced identically.
         IReadOnlyList<string> names = AccessibleNames.Resolve(
            fields.Select(f => new LabelledEditor(f.Label, spec.ItemNoun, f.Prop)).ToList());

         for (int i = 0; i < fields.Count; i++)
         {
            CollectionEditorView.FieldSpec f = fields[i];

            object current = existing != null && existing.Values.TryGetValue(f.Prop, out object v)
               ? v
               : f.Default;

            BuildField(panel, f, current, names[i]);
         }

         var buttons = new StackPanel
         {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
         };
         var ok = new Button { Content = "_Save", Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), MinWidth = 88, IsDefault = true };
         ok.Click += (_, _) =>
         {
            if (committers_.Any(commit => !commit()))
               return;
            DialogResult = true;
            Close();
         };
         var cancel = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
         cancel.Click += (_, _) => Close();
         buttons.Children.Add(ok);
         buttons.Children.Add(cancel);
         panel.Children.Add(buttons);

         Content = panel;
      }

      private void BuildField(Panel host, CollectionEditorView.FieldSpec f, object current, string accessibleName)
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
                     FontSize = Typography.Body,
                     Margin = new Thickness(0, 6, 0, 10)
                  };
                  // A checkbox is named by its Content, so it only needs an override
                  // when the name was qualified to tell it from an identically worded
                  // field elsewhere in the dialog.
                  if (!string.Equals(accessibleName, f.Label, StringComparison.Ordinal))
                     Describe(box, prop, accessibleName);
                  else
                     AutomationProperties.SetAutomationId(box, prop);
                  host.Children.Add(box);
                  committers_.Add(() => { Result[prop] = box.IsChecked is true; return true; });
                  break;
               }
            case CollectionEditorView.FieldKind.Combo:
               {
                  host.Children.Add(Label(f.Label));
                  var combo = new ComboBox { FontSize = Typography.Body, Margin = new Thickness(0, 0, 0, 10) };
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
                  Describe(combo, prop, accessibleName);
                  host.Children.Add(combo);
                  committers_.Add(() =>
                  {
                     Result[prop] = combo.SelectedItem is ComboBoxItem cbi ? (int)cbi.Tag : 0;
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
                     FontSize = Typography.Body,
                     AcceptsReturn = true,
                     TextWrapping = TextWrapping.Wrap,
                     MinLines = 4,
                     MaxLines = 10,
                     Margin = new Thickness(0, 0, 0, 10)
                  };
                  Describe(box, prop, accessibleName);
                  host.Children.Add(box);
                  committers_.Add(() => { Result[prop] = box.Text; return true; });
                  break;
               }
            case CollectionEditorView.FieldKind.Password:
               {
                  host.Children.Add(Label(f.Label));
                  var box = new Wpf.Ui.Controls.PasswordBox
                  {
                     Password = Convert.ToString(current) ?? "",
                     FontSize = Typography.Body,
                     Margin = new Thickness(0, 0, 0, 10)
                  };
                  Describe(box, prop, accessibleName);
                  host.Children.Add(box);
                  committers_.Add(() => { Result[prop] = box.Password; return true; });
                  break;
               }
            case CollectionEditorView.FieldKind.Number:
               {
                  host.Children.Add(Label(f.Label));
                  double cur = 0;
                  try { cur = Convert.ToDouble(current ?? 0); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { cur = 0; }
                  var box = new Wpf.Ui.Controls.NumberBox
                  {
                     Value = cur,
                     MaxDecimalPlaces = 0,
                     SmallChange = 1,
                     LargeChange = 10,
                     FontSize = Typography.Body,
                     Margin = new Thickness(0, 0, 0, 10)
                  };
                  Describe(box, prop, accessibleName);
                  host.Children.Add(box);
                  committers_.Add(() => { Result[prop] = (int)(box.Value ?? 0); return true; });
                  break;
               }
            default:
               {
                  host.Children.Add(Label(f.Label));
                  var box = new TextBox { Text = Convert.ToString(current) ?? "", FontSize = Typography.Body, Margin = new Thickness(0, 0, 0, 10) };
                  Describe(box, prop, accessibleName);
                  host.Children.Add(box);
                  committers_.Add(() => { Result[prop] = box.Text; return true; });
                  break;
               }
         }
      }

      /// <summary>
      /// Names the control and gives it a stable automation id. The id is the
      /// property being edited, which is what a UI-automation test would look for;
      /// the name is what a screen reader says, and until this pass there was none.
      /// </summary>
      private static void Describe(FrameworkElement element, string prop, string accessibleName)
      {
         if (element == null)
            return;

         if (!string.IsNullOrEmpty(prop))
            AutomationProperties.SetAutomationId(element, prop);

         if (!string.IsNullOrEmpty(accessibleName))
            AutomationProperties.SetName(element, accessibleName);
      }

      private static TextBlock Label(string text) => new()
      {
         Text = text,
         FontSize = Typography.Label,
         Margin = new Thickness(0, 6, 0, 4)
      };
   }
}
