// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Add/edit a single rule criterion (predefined field or custom header, match type, value), on the standard frame.</summary>
   public class RuleCriteriaDialog : FluentDialogWindow
   {
      private readonly int ruleId_;
      private readonly int criteriaId_; // 0 = new
      private readonly Func<dynamic> rulesProvider_;

      private readonly ComboBox field_ = new();
      private readonly TextBox header_ = new();
      private readonly FieldRow headerRow_;
      private readonly ComboBox match_ = new();
      private readonly TextBox value_ = new();
      private readonly InlineNotice notice_ = DialogFields.Notice();

      public RuleCriteriaDialog(Window owner, int ruleId, int criteriaId, Func<dynamic> rulesProvider = null)
      {
         ruleId_ = ruleId;
         criteriaId_ = criteriaId;
         rulesProvider_ = rulesProvider ?? (() => ServerSession.Current.Application.Rules);
         Owner = owner;
         Title = criteriaId == 0 ? L("Add criterion") : L("Edit criterion");

         var body = new StackPanel();
         body.Children.Add(notice_);

         body.Children.Add(Label(L("_Field"), field_));
         field_.Items.Add(DialogFields.Combo(L("From"), 1));
         field_.Items.Add(DialogFields.Combo(L("To"), 2));
         field_.Items.Add(DialogFields.Combo(L("CC"), 3));
         field_.Items.Add(DialogFields.Combo(L("Subject"), 4));
         field_.Items.Add(DialogFields.Combo(L("Body"), 5));
         field_.Items.Add(DialogFields.Combo(L("Message size"), 6));
         field_.Items.Add(DialogFields.Combo(L("Recipient list"), 7));
         field_.Items.Add(DialogFields.Combo(L("Delivery attempts"), 8));
         field_.Items.Add(DialogFields.Combo(L("Custom header…"), 0));
         field_.SelectionChanged += (s, e) => UpdateVisibility();

         headerRow_ = Label(L("_Header name (e.g. X-Spam-Status)"), header_);
         body.Children.Add(headerRow_);

         body.Children.Add(Label(L("_Match type"), match_));
         match_.Items.Add(DialogFields.Combo(L("equals"), 1));
         match_.Items.Add(DialogFields.Combo(L("contains"), 2));
         match_.Items.Add(DialogFields.Combo(L("is less than"), 3));
         match_.Items.Add(DialogFields.Combo(L("is greater than"), 4));
         match_.Items.Add(DialogFields.Combo(L("matches regex"), 5));
         match_.Items.Add(DialogFields.Combo(L("does not contain"), 6));
         match_.Items.Add(DialogFields.Combo(L("does not equal"), 7));
         match_.Items.Add(DialogFields.Combo(L("matches wildcard"), 8));

         body.Children.Add(Label(L("_Value"), value_));

         // Enter saves, Escape cancels.
         var save = new Wpf.Ui.Controls.Button { Content = L("_Save"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => Close();

         // Upper-case deliberately, sentence-case sweep notwithstanding: "IF" is
         // the rule grammar's keyword, not prose. RulesView's editor panes carry
         // the same IF/THEN pair, and this dialog edits one clause of it.
         UseFrame(L("IF"), body, save, cancel, width: 480);
         Loaded += (s, e) => Load();
      }

      private void UpdateVisibility()
      {
         headerRow_.Visibility = DialogFields.ComboValue(field_) == 0 ? Visibility.Visible : Visibility.Collapsed;
      }

      private void Load()
      {
         if (criteriaId_ == 0)
         {
            DialogFields.SelectCombo(field_, 1);
            DialogFields.SelectCombo(match_, 2);
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
                  DialogFields.SelectCombo(field_, (int)c.PredefinedField);
               else
               {
                  DialogFields.SelectCombo(field_, 0);
                  header_.Text = (string)c.HeaderField ?? "";
               }
               DialogFields.SelectCombo(match_, (int)c.MatchType);
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
            MessageBox.Show(F("Could not load the criterion: {0}", ex.Message), L("Control Panel"));
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
         notice_.Hide();
         int field = DialogFields.ComboValue(field_);

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
               c.MatchType = DialogFields.ComboValue(match_);
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
            notice_.Show(StatusLevel.Critical, F("Could not save the criterion: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(rules);
         }
      }

      /// <summary>
      /// A caption and its editor as one row, which names the editor to UI
      /// Automation - without it this dialog announced itself as "combo box,
      /// edit, combo box, edit".
      /// </summary>
      private static FieldRow Label(string text, FrameworkElement editor) => DialogFields.Field(text, editor);
   }
}
