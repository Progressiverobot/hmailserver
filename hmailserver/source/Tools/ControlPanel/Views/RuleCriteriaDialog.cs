// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Add/edit a single rule criterion (predefined field or custom header, match type, value).</summary>
   public class RuleCriteriaDialog : FluentDialogWindow
   {
      private readonly int ruleId_;
      private readonly int criteriaId_; // 0 = new
      private readonly Func<dynamic> rulesProvider_;

      private readonly ComboBox field_ = new() { FontSize = Typography.Body, Margin = new Thickness(0, 0, 0, 8) };
      private readonly TextBox header_ = new();
      private readonly StackPanel headerPanel_ = new();
      private readonly ComboBox match_ = new() { FontSize = Typography.Body, Margin = new Thickness(0, 0, 0, 8) };
      private readonly TextBox value_ = new();

      public RuleCriteriaDialog(Window owner, int ruleId, int criteriaId, Func<dynamic> rulesProvider = null)
      {
         ruleId_ = ruleId;
         criteriaId_ = criteriaId;
         rulesProvider_ = rulesProvider ?? (() => ServerSession.Current.Application.Rules);
         Owner = owner;
         Title = criteriaId == 0 ? "Add criterion" : "Edit criterion";
         Width = 480;
         Height = 380;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var root = new Grid { Margin = new Thickness(18) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         // Upper-case deliberately, sentence-case sweep notwithstanding: "IF" is
         // the rule grammar's keyword, not prose. RulesView's editor panes carry
         // the same IF/THEN pair, and this dialog edits one clause of it.
         var header = new TextBlock { Text = "IF", FontSize = Typography.DialogTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 12) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetRow(header, 0);
         root.Children.Add(header);

         var body = new StackPanel();
         body.Children.Add(Label("Field", field_));
         field_.Items.Add(Combo("From", 1));
         field_.Items.Add(Combo("To", 2));
         field_.Items.Add(Combo("CC", 3));
         field_.Items.Add(Combo("Subject", 4));
         field_.Items.Add(Combo("Body", 5));
         field_.Items.Add(Combo("Message size", 6));
         field_.Items.Add(Combo("Recipient list", 7));
         field_.Items.Add(Combo("Delivery attempts", 8));
         field_.Items.Add(Combo("Custom header\u2026", 0));
         field_.SelectionChanged += (s, e) => UpdateVisibility();
         body.Children.Add(field_);

         headerPanel_.Children.Add(Label("Header name (e.g. X-Spam-Status)", header_));
         headerPanel_.Children.Add(Input(header_));
         body.Children.Add(headerPanel_);

         body.Children.Add(Label("Match type", match_));
         match_.Items.Add(Combo("equals", 1));
         match_.Items.Add(Combo("contains", 2));
         match_.Items.Add(Combo("is less than", 3));
         match_.Items.Add(Combo("is greater than", 4));
         match_.Items.Add(Combo("matches regex", 5));
         match_.Items.Add(Combo("does not contain", 6));
         match_.Items.Add(Combo("does not equal", 7));
         match_.Items.Add(Combo("matches wildcard", 8));
         body.Children.Add(match_);

         body.Children.Add(Label("Value", value_));
         body.Children.Add(Input(value_));

         var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
         Grid.SetRow(scroll, 1);
         root.Children.Add(scroll);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
         // Enter saves, Escape cancels. Neither worked before.
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel", IsCancel = true };
         cancel.Click += (s, e) => Close();
         buttons.Children.Add(save);
         buttons.Children.Add(cancel);
         Grid.SetRow(buttons, 2);
         root.Children.Add(buttons);

         Content = root;
         Loaded += (s, e) => Load();
      }

      private void UpdateVisibility()
      {
         headerPanel_.Visibility = ComboValue(field_) == 0 ? Visibility.Visible : Visibility.Collapsed;
      }

      private void Load()
      {
         if (criteriaId_ == 0)
         {
            SelectCombo(field_, 1);
            SelectCombo(match_, 2);
            UpdateVisibility();
            return;
         }

         dynamic rules = rulesProvider_();
         try
         {
            dynamic rule = rules.ItemByDBID[ruleId_];
            if (rule == null) { Close(); return; }
            dynamic criterias = rule.Criterias;
            try
            {
               dynamic c = criterias.ItemByDBID[criteriaId_];
               if ((bool)c.UsePredefined)
                  SelectCombo(field_, (int)c.PredefinedField);
               else
               {
                  SelectCombo(field_, 0);
                  header_.Text = (string)c.HeaderField ?? "";
               }
               SelectCombo(match_, (int)c.MatchType);
               value_.Text = (string)c.MatchValue ?? "";
               ServerSession.Release(c);
            }
            finally
            {
               ServerSession.Release(criterias);
            }
            ServerSession.Release(rule);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not load the criterion: " + ex.Message, "Control Panel");
            Close();
            return;
         }
         finally
         {
            ServerSession.Release(rules);
         }

         UpdateVisibility();
      }

      private void Save()
      {
         int field = ComboValue(field_);

         dynamic rules = rulesProvider_();
         try
         {
            dynamic rule = rules.ItemByDBID[ruleId_];
            if (rule == null) { Close(); return; }
            dynamic criterias = rule.Criterias;
            try
            {
               dynamic c = criteriaId_ == 0 ? criterias.Add() : criterias.ItemByDBID[criteriaId_];
               c.RuleID = ruleId_;
               if (field == 0)
               {
                  c.UsePredefined = false;
                  c.HeaderField = header_.Text.Trim();
               }
               else
               {
                  c.UsePredefined = true;
                  c.PredefinedField = field;
               }
               c.MatchType = ComboValue(match_);
               c.MatchValue = value_.Text;
               c.Save();
               ServerSession.Release(c);
            }
            finally
            {
               ServerSession.Release(criterias);
            }
            rule.Save();
            ServerSession.Release(rule);
            Close();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not save the criterion: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(rules);
         }
      }

      // ---- UI helpers ----

      /// <summary>
      /// A caption, and - when the editor it captions is passed in - that editor's
      /// accessible name. A TextBlock above a control tells UI Automation nothing,
      /// so this dialog announced itself as "combo box, edit, combo box, edit".
      /// </summary>
      private static TextBlock Label(string text, FrameworkElement editor = null)
      {
         var t = new TextBlock { Text = text, FontSize = Typography.Label, Margin = new Thickness(0, 8, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");

         if (editor != null)
            AutomationProperties.SetName(editor, AccessibleNames.Qualify(text, ""));

         return t;
      }

      private static TextBox Input(TextBox box)
      {
         box.FontSize = Typography.Body;
         box.Padding = new Thickness(6);
         box.Margin = new Thickness(0, 0, 0, 8);
         box.Background = System.Windows.Media.Brushes.Transparent;
         box.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         return box;
      }

      private static ComboBoxItem Combo(string text, int value) => new() { Content = text, Tag = value };

      private static void SelectCombo(ComboBox combo, int value)
      {
         foreach (ComboBoxItem item in combo.Items)
            if ((int)item.Tag == value) { combo.SelectedItem = item; return; }
      }

      private static int ComboValue(ComboBox combo) => combo.SelectedItem is ComboBoxItem item ? (int)item.Tag : 0;
   }
}
