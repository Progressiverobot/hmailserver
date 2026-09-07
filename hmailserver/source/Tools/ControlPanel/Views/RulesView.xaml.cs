// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   public partial class RulesView : UserControl, IPageLifecycle
   {
      public class RuleRow
      {
         public int Id { get; set; }
         public int Position { get; set; }
         public string Name { get; set; }
         public string Enabled { get; set; }
      }

      public class DetailRow
      {
         public int Id { get; set; }
         public string Description { get; set; }
      }

      private int selectedRuleId_;
      private bool suppressMatchMode_;
      private Func<dynamic> rulesProvider_ = () => ServerSession.Current.Application.Rules;
      private bool serverLevel_ = true;

      /// <summary>
      /// Re-targets this view at a different rule collection (e.g. an account's
      /// rules) and hides the page header when embedded in another dialog.
      /// </summary>
      public void ConfigureForRules(Func<dynamic> rulesProvider, bool serverLevel, bool embedded)
      {
         rulesProvider_ = rulesProvider;
         serverLevel_ = serverLevel;
         if (embedded && HeaderPanel != null)
            HeaderPanel.Visibility = Visibility.Collapsed;
      }

      private dynamic OpenRules() => rulesProvider_();

      public RulesView()
      {
         InitializeComponent();

         suppressMatchMode_ = true;
         MatchMode.Items.Add(L("Match ALL criteria (AND)"));
         MatchMode.Items.Add(L("Match ANY criterion (OR)"));
         MatchMode.SelectedIndex = 0;
         suppressMatchMode_ = false;
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private void Reload()
      {
         var rows = new List<RuleRow>();
         dynamic rules = OpenRules();
         try
         {
            int count = (int)rules.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic rule = rules.Item[i];
               rows.Add(new RuleRow
               {
                  Id = (int)rule.ID,
                  Position = i + 1,
                  Name = (string)rule.Name,
                  Enabled = (bool)rule.Active ? L("Yes") : L("No")
               });
               ServerSession.Release(rule);
            }
         }
         finally
         {
            ServerSession.Release(rules);
         }

         RuleGrid.ItemsSource = rows;
         ListSearch.Apply(RuleGrid, SearchBox.Text);
         SubtitleText.Text = rows.Count == 0
            ? L("No rules defined yet - create one below.")
            : F("{0} rule(s), evaluated top to bottom.", rows.Count);

         CriteriaGrid.ItemsSource = null;
         ActionsGrid.ItemsSource = null;
      }

      private void Search_TextChanged(object sender, TextChangedEventArgs e)
         => ListSearch.Apply(RuleGrid, SearchBox.Text);

      private static readonly string[] FieldNames =
         { "?", L("From"), L("To"), L("CC"), L("Subject"), L("Body"), L("Message size"), L("Recipient list"), L("Delivery attempts") };

      private static readonly string[] MatchNames =
         { "?", L("equals"), L("contains"), L("is less than"), L("is greater than"), L("matches regex"), L("does not contain"), L("does not equal"), L("matches wildcard") };

      private static readonly string[] ActionNames =
         { "?", L("Delete e-mail"), L("Forward e-mail"), L("Reply"), L("Move to IMAP folder"), L("Run script function"),
           L("Stop rule processing"), L("Set header value"), L("Send using route"), L("Create copy"), L("Bind to address") };

      private static string Pick(string[] names, int index)
         => index >= 0 && index < names.Length ? names[index] : "#" + index;

      private void RuleGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
      {
         if (RuleGrid.SelectedItem is not RuleRow row)
         {
            selectedRuleId_ = 0;
            CriteriaGrid.ItemsSource = null;
            ActionsGrid.ItemsSource = null;
            return;
         }

         selectedRuleId_ = row.Id;
         RefreshDetails();
      }

      private void RefreshDetails()
      {
         if (selectedRuleId_ == 0)
            return;

         var criteria = new List<DetailRow>();
         var actions = new List<DetailRow>();
         bool useAnd = true;

         dynamic rules = OpenRules();
         try
         {
            dynamic rule = rules.ItemByDBID[selectedRuleId_];
            if (rule == null)
               return;

            useAnd = (bool)rule.UseAND;

            dynamic criterias = rule.Criterias;
            int cc = (int)criterias.Count;
            for (int i = 0; i < cc; i++)
            {
               dynamic c = criterias.Item[i];
               string field = (bool)c.UsePredefined
                  ? Pick(FieldNames, (int)c.PredefinedField)
                  : F("header '{0}'", (string)c.HeaderField);
               criteria.Add(new DetailRow
               {
                  Id = (int)c.ID,
                  Description = field + " " + Pick(MatchNames, (int)c.MatchType) + " '" + (string)c.MatchValue + "'"
               });
               ServerSession.Release(c);
            }
            ServerSession.Release(criterias);

            dynamic acts = rule.Actions;
            int ac = (int)acts.Count;
            for (int i = 0; i < ac; i++)
            {
               dynamic a = acts.Item[i];
               actions.Add(new DetailRow { Id = (int)a.ID, Description = DescribeAction(a) });
               ServerSession.Release(a);
            }
            ServerSession.Release(acts);

            ServerSession.Release(rule);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // leave lists as built so far
         }
         finally
         {
            ServerSession.Release(rules);
         }

         CriteriaGrid.ItemsSource = criteria;
         ActionsGrid.ItemsSource = actions;

         suppressMatchMode_ = true;
         MatchMode.SelectedIndex = useAnd ? 0 : 1;
         suppressMatchMode_ = false;
      }

      private static string DescribeAction(dynamic a)
      {
         int type = (int)a.Type;
         string text = Pick(ActionNames, type);
         try
         {
            switch (type)
            {
               case 2: text += " -> " + (string)a.To; break;
               case 3: text += F(" (subject '{0}')", (string)a.Subject); break;
               case 4: text += " '" + (string)a.IMAPFolder + "'"; break;
               case 5: text += " " + (string)a.ScriptFunction; break;
               case 7: text += " " + (string)a.HeaderName + "=" + (string)a.Value; break;
               case 8: text += F(" (route #{0})", (int)a.RouteID); break;
               case 10: text += " " + (string)a.Value; break;
            }
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
         }
         return text;
      }

      // ---- Rule-level operations ----

      private void WithSelectedRule(Action<dynamic> action)
      {
         if (RuleGrid.SelectedItem is not RuleRow row)
            return;

         int selectedIndex = RuleGrid.SelectedIndex;

         dynamic rules = OpenRules();
         try
         {
            dynamic rule = rules.Item[row.Position - 1];
            action(rule);
            ServerSession.Release(rule);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Rule operation failed: {0}", ex.Message), L("Control Panel"));
         }
         finally
         {
            ServerSession.Release(rules);
         }

         Reload();

         if (selectedIndex >= 0 && selectedIndex < RuleGrid.Items.Count)
            RuleGrid.SelectedIndex = selectedIndex;
      }

      private void MoveUp_Click(object sender, RoutedEventArgs e) => WithSelectedRule(rule => rule.MoveUp());

      private void MoveDown_Click(object sender, RoutedEventArgs e) => WithSelectedRule(rule => rule.MoveDown());

      private void Toggle_Click(object sender, RoutedEventArgs e) => WithSelectedRule(rule =>
      {
         rule.Active = !(bool)rule.Active;
         rule.Save();
      });

      private void Delete_Click(object sender, RoutedEventArgs e)
      {
         if (RuleGrid.SelectedItem is not RuleRow row)
            return;

         if (MessageBox.Show(F("Delete rule '{0}'?", row.Name), L("Control Panel"),
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         WithSelectedRule(rule => rule.Delete());
      }

      private void AddRule_Click(object sender, RoutedEventArgs e)
      {
         string name = NewRuleName.Text.Trim();
         if (name.Length == 0)
         {
            MessageBox.Show(L("Enter a name for the new rule."), L("Control Panel"));
            return;
         }

         dynamic rules = OpenRules();
         try
         {
            dynamic rule = rules.Add();
            rule.Name = name;
            rule.Active = true;
            rule.Save();
            ServerSession.Release(rule);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not create the rule: {0}", ex.Message), L("Control Panel"));
            return;
         }
         finally
         {
            ServerSession.Release(rules);
         }

         NewRuleName.Text = "";
         Reload();
      }

      private void MatchMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
      {
         if (suppressMatchMode_ || selectedRuleId_ == 0)
            return;

         bool useAnd = MatchMode.SelectedIndex == 0;
         dynamic rules = OpenRules();
         try
         {
            dynamic rule = rules.ItemByDBID[selectedRuleId_];
            if (rule != null)
            {
               rule.UseAND = useAnd;
               rule.Save();
               ServerSession.Release(rule);
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not change the match mode: {0}", ex.Message), L("Control Panel"));
         }
         finally
         {
            ServerSession.Release(rules);
         }
      }

      // ---- Criteria operations ----

      private void AddCriterion_Click(object sender, RoutedEventArgs e)
      {
         if (selectedRuleId_ == 0) { MessageBox.Show(L("Select a rule first."), L("Control Panel")); return; }
         new RuleCriteriaDialog(Window.GetWindow(this), selectedRuleId_, 0, rulesProvider_).ShowDialog();
         RefreshDetails();
      }

      private void EditCriterion_Click(object sender, RoutedEventArgs e)
      {
         if (selectedRuleId_ == 0 || CriteriaGrid.SelectedItem is not DetailRow row)
            return;
         new RuleCriteriaDialog(Window.GetWindow(this), selectedRuleId_, row.Id, rulesProvider_).ShowDialog();
         RefreshDetails();
      }

      private void RemoveCriterion_Click(object sender, RoutedEventArgs e)
      {
         if (selectedRuleId_ == 0 || CriteriaGrid.SelectedItem is not DetailRow row)
            return;

         dynamic rules = OpenRules();
         try
         {
            dynamic rule = rules.ItemByDBID[selectedRuleId_];
            dynamic criterias = rule.Criterias;
            criterias.DeleteByDBID(row.Id);
            rule.Save();
            ServerSession.Release(criterias);
            ServerSession.Release(rule);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not remove the criterion: {0}", ex.Message), L("Control Panel"));
         }
         finally
         {
            ServerSession.Release(rules);
         }

         RefreshDetails();
      }

      // ---- Action operations ----

      private void AddAction_Click(object sender, RoutedEventArgs e)
      {
         if (selectedRuleId_ == 0) { MessageBox.Show(L("Select a rule first."), L("Control Panel")); return; }
         new RuleActionDialog(Window.GetWindow(this), selectedRuleId_, 0, rulesProvider_, serverLevel_).ShowDialog();
         RefreshDetails();
      }

      private void EditAction_Click(object sender, RoutedEventArgs e)
      {
         if (selectedRuleId_ == 0 || ActionsGrid.SelectedItem is not DetailRow row)
            return;
         new RuleActionDialog(Window.GetWindow(this), selectedRuleId_, row.Id, rulesProvider_, serverLevel_).ShowDialog();
         RefreshDetails();
      }

      private void RemoveAction_Click(object sender, RoutedEventArgs e)
      {
         if (selectedRuleId_ == 0 || ActionsGrid.SelectedItem is not DetailRow row)
            return;

         dynamic rules = OpenRules();
         try
         {
            dynamic rule = rules.ItemByDBID[selectedRuleId_];
            dynamic actions = rule.Actions;
            actions.DeleteByDBID(row.Id);
            rule.Save();
            ServerSession.Release(actions);
            ServerSession.Release(rule);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not remove the action: {0}", ex.Message), L("Control Panel"));
         }
         finally
         {
            ServerSession.Release(rules);
         }

         RefreshDetails();
      }

      private void ActionUp_Click(object sender, RoutedEventArgs e) => MoveAction(true);

      private void ActionDown_Click(object sender, RoutedEventArgs e) => MoveAction(false);

      private void MoveAction(bool up)
      {
         if (selectedRuleId_ == 0 || ActionsGrid.SelectedItem is not DetailRow row)
            return;

         dynamic rules = OpenRules();
         try
         {
            dynamic rule = rules.ItemByDBID[selectedRuleId_];
            dynamic actions = rule.Actions;
            dynamic a = actions.ItemByDBID[row.Id];
            if (up) a.MoveUp(); else a.MoveDown();
            a.Save();
            rule.Save();
            ServerSession.Release(a);
            ServerSession.Release(actions);
            ServerSession.Release(rule);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show(F("Could not move the action: {0}", ex.Message), L("Control Panel"));
         }
         finally
         {
            ServerSession.Release(rules);
         }

         RefreshDetails();
      }
   }
}
