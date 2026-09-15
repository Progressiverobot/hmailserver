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
   /// <summary>Add/edit a single rule action, with the contextual fields for each action type, on the standard frame.</summary>
   public class RuleActionDialog : FluentDialogWindow
   {
      private readonly int ruleId_;
      private readonly int actionId_; // 0 = new
      private readonly Func<dynamic> rulesProvider_;
      private readonly bool serverLevel_;

      private readonly ComboBox type_ = new();

      private readonly TextBox to_ = new();
      private readonly CheckBox abortSpam_ = new() { Content = L("Abort on messages _marked as spam") };
      private readonly TextBox fromName_ = new();
      private readonly TextBox fromAddress_ = new();
      private readonly TextBox subject_ = new();
      private readonly TextBox body_ = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
      private readonly TextBox imapFolder_ = new();
      private readonly TextBox scriptFunction_ = new();
      private readonly TextBox headerName_ = new();
      private readonly TextBox value_ = new();
      private readonly ComboBox route_ = new();
      private readonly TextBox bindAddress_ = new();

      private readonly StackPanel forwardPanel_ = new();
      private readonly StackPanel replyPanel_ = new();
      private readonly StackPanel folderPanel_ = new();
      private readonly StackPanel scriptPanel_ = new();
      private readonly StackPanel headerPanel_ = new();
      private readonly StackPanel routePanel_ = new();
      private readonly StackPanel bindPanel_ = new();
      private readonly TextBlock noParams_ = DialogFields.Note(L("This action has no additional parameters."));
      private readonly InlineNotice notice_ = DialogFields.Notice();

      public RuleActionDialog(Window owner, int ruleId, int actionId, Func<dynamic> rulesProvider = null, bool serverLevel = true)
      {
         ruleId_ = ruleId;
         actionId_ = actionId;
         rulesProvider_ = rulesProvider ?? (() => ServerSession.Current.Application.Rules);
         serverLevel_ = serverLevel;
         Owner = owner;
         Title = actionId == 0 ? L("Add action") : L("Edit action");

         var body = new StackPanel();
         body.Children.Add(notice_);

         body.Children.Add(Label(L("_Action"), type_));
         type_.Items.Add(DialogFields.Combo(L("Delete e-mail"), 1));
         type_.Items.Add(DialogFields.Combo(L("Forward e-mail"), 2));
         type_.Items.Add(DialogFields.Combo(L("Reply"), 3));
         type_.Items.Add(DialogFields.Combo(L("Move to IMAP folder"), 4));
         type_.Items.Add(DialogFields.Combo(L("Run script function"), 5));
         type_.Items.Add(DialogFields.Combo(L("Stop rule processing"), 6));
         type_.Items.Add(DialogFields.Combo(L("Set header value"), 7));
         if (serverLevel)
            type_.Items.Add(DialogFields.Combo(L("Send using route"), 8));
         type_.Items.Add(DialogFields.Combo(L("Create copy"), 9));
         if (serverLevel)
            type_.Items.Add(DialogFields.Combo(L("Bind to address"), 10));
         type_.SelectionChanged += (s, e) => UpdateVisibility();

         // Forward
         forwardPanel_.Children.Add(Label(L("_To"), to_));
         forwardPanel_.Children.Add(DialogFields.Field(null, abortSpam_));
         body.Children.Add(forwardPanel_);

         // Reply
         replyPanel_.Children.Add(Label(L("From (_name)"), fromName_));
         replyPanel_.Children.Add(Label(L("From (a_ddress)"), fromAddress_));
         replyPanel_.Children.Add(Label(L("Su_bject"), subject_));
         replyPanel_.Children.Add(Label(L("Bod_y"), body_));
         body.Children.Add(replyPanel_);

         // Move to folder
         folderPanel_.Children.Add(Label(L("_IMAP folder (e.g. INBOX.Archive)"), imapFolder_));
         body.Children.Add(folderPanel_);

         // Script
         scriptPanel_.Children.Add(Label(L("Script _function"), scriptFunction_));
         body.Children.Add(scriptPanel_);

         // Set header
         headerPanel_.Children.Add(Label(L("_Header name"), headerName_));
         headerPanel_.Children.Add(Label(L("_Value"), value_));
         body.Children.Add(headerPanel_);

         // Route
         routePanel_.Children.Add(Label(L("_Route"), route_));
         body.Children.Add(routePanel_);

         // Bind to address
         bindPanel_.Children.Add(Label(L("I_P address"), bindAddress_));
         body.Children.Add(bindPanel_);

         body.Children.Add(noParams_);

         // Enter saves, Escape cancels. Safe alongside the multi-line reply body:
         // a TextBox with AcceptsReturn handles Enter itself and marks the key
         // handled, so it never reaches the default button.
         var save = new Wpf.Ui.Controls.Button { Content = L("_Save"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => Close();

         // Upper-case deliberately, sentence-case sweep notwithstanding: "THEN"
         // is the rule grammar's keyword, not prose - the pair to RuleCriteria-
         // Dialog's "IF", both echoing RulesView's editor panes.
         UseFrame(L("THEN"), body, save, cancel, width: 520);
         Loaded += (s, e) => Load();
      }

      private void UpdateVisibility()
      {
         int type = DialogFields.ComboValue(type_);
         forwardPanel_.Visibility = type == 2 ? Visibility.Visible : Visibility.Collapsed;
         replyPanel_.Visibility = type == 3 ? Visibility.Visible : Visibility.Collapsed;
         folderPanel_.Visibility = type == 4 ? Visibility.Visible : Visibility.Collapsed;
         scriptPanel_.Visibility = type == 5 ? Visibility.Visible : Visibility.Collapsed;
         headerPanel_.Visibility = type == 7 ? Visibility.Visible : Visibility.Collapsed;
         routePanel_.Visibility = type == 8 ? Visibility.Visible : Visibility.Collapsed;
         bindPanel_.Visibility = type == 10 ? Visibility.Visible : Visibility.Collapsed;
         noParams_.Visibility = (type == 1 || type == 6 || type == 9) ? Visibility.Visible : Visibility.Collapsed;
      }

      private void LoadRoutes()
      {
         route_.Items.Clear();
         dynamic routes = ServerSession.Current.Application.Settings.Routes;
         try
         {
            int count = (int)routes.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic r = routes.Item[i];
               route_.Items.Add(new ComboBoxItem { Content = (string)r.DomainName, Tag = (int)r.ID });
               ServerSession.Release(r);
            }
         }
         finally
         {
            ServerSession.Release(routes);
         }
      }

      private dynamic FindRule(dynamic rules) => rules.ItemByDBID[ruleId_];

      private void Load()
      {
         LoadRoutes();

         if (actionId_ == 0)
         {
            DialogFields.SelectCombo(type_, 1);
            UpdateVisibility();
            return;
         }

         dynamic rules = rulesProvider_();
         try
         {
            dynamic rule = FindRule(rules);
            if (rule == null) { Close(); return; }
            dynamic actions = rule.Actions;
            try
            {
               dynamic a = actions.ItemByDBID[actionId_];
               int type = (int)a.Type;
               DialogFields.SelectCombo(type_, type);
               to_.Text = (string)a.To ?? "";
               abortSpam_.IsChecked = (bool)a.AbortSpamFlagged;
               fromName_.Text = (string)a.FromName ?? "";
               fromAddress_.Text = (string)a.FromAddress ?? "";
               subject_.Text = (string)a.Subject ?? "";
               body_.Text = (string)a.Body ?? "";
               imapFolder_.Text = (string)a.IMAPFolder ?? "";
               scriptFunction_.Text = (string)a.ScriptFunction ?? "";
               headerName_.Text = (string)a.HeaderName ?? "";
               value_.Text = (string)a.Value ?? "";
               bindAddress_.Text = (string)a.Value ?? "";
               DialogFields.SelectCombo(route_, (int)a.RouteID);
               ServerSession.Release(a);
            }
            finally
            {
               ServerSession.Release(actions);
            }
            ServerSession.Release(rule);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not load the action: {0}", ex.Message), L("Control Panel"));
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
         int type = DialogFields.ComboValue(type_);

         dynamic rules = rulesProvider_();
         try
         {
            dynamic rule = FindRule(rules);
            if (rule == null) { Close(); return; }
            dynamic actions = rule.Actions;
            try
            {
               dynamic a = actionId_ == 0 ? actions.Add() : actions.ItemByDBID[actionId_];
               a.RuleID = ruleId_;
               a.Type = type;
               switch (type)
               {
                  case 2:
                     a.To = to_.Text.Trim();
                     a.AbortSpamFlagged = abortSpam_.IsChecked is true;
                     break;
                  case 3:
                     a.FromName = fromName_.Text.Trim();
                     a.FromAddress = fromAddress_.Text.Trim();
                     a.Subject = subject_.Text;
                     a.Body = body_.Text;
                     a.AbortSpamFlagged = abortSpam_.IsChecked is true;
                     break;
                  case 4:
                     a.IMAPFolder = imapFolder_.Text.Trim();
                     break;
                  case 5:
                     a.ScriptFunction = scriptFunction_.Text.Trim();
                     break;
                  case 7:
                     a.HeaderName = headerName_.Text.Trim();
                     a.Value = value_.Text;
                     break;
                  case 8:
                     a.RouteID = DialogFields.ComboValue(route_);
                     break;
                  case 10:
                     a.Value = bindAddress_.Text.Trim();
                     break;
               }
               a.Save();
               ServerSession.Release(a);
            }
            finally
            {
               ServerSession.Release(actions);
            }
            rule.Save();
            ServerSession.Release(rule);
            Close();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            notice_.Show(StatusLevel.Critical, F("Could not save the action: {0}", ex.Message));
         }
         finally
         {
            ServerSession.Release(rules);
         }
      }

      /// <summary>
      /// A caption and its editor as one row, which names the editor to UI
      /// Automation - this dialog has eleven of them, and to a screen reader
      /// every parameter of every rule action was an anonymous "edit" once.
      /// </summary>
      private static FieldRow Label(string text, FrameworkElement editor) => DialogFields.Field(text, editor);
   }
}
