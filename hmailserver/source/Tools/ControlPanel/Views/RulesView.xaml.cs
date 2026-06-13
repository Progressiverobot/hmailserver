using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

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
         MatchMode.Items.Add("Match ALL criteria (AND)");
         MatchMode.Items.Add("Match ANY criterion (OR)");
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
            int count = (int) rules.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic rule = rules.Item[i];
               rows.Add(new RuleRow
               {
                  Id = (int) rule.ID,
                  Position = i + 1,
                  Name = (string) rule.Name,
                  Enabled = (bool) rule.Active ? "Yes" : "No"
               });
               ServerSession.Release(rule);
            }
         }
         finally
         {
            ServerSession.Release(rules);
         }

         RuleGrid.ItemsSource = rows;
         SubtitleText.Text = rows.Count == 0
            ? "No rules defined yet - create one below."
            : rows.Count + " rule(s), evaluated top to bottom.";

         CriteriaGrid.ItemsSource = null;
         ActionsGrid.ItemsSource = null;
      }

      private static readonly string[] FieldNames =
         { "?", "From", "To", "CC", "Subject", "Body", "Message size", "Recipient list", "Delivery attempts" };

      private static readonly string[] MatchNames =
         { "?", "equals", "contains", "is less than", "is greater than", "matches regex", "does not contain", "does not equal", "matches wildcard" };

      private static readonly string[] ActionNames =
         { "?", "Delete e-mail", "Forward e-mail", "Reply", "Move to IMAP folder", "Run script function",
           "Stop rule processing", "Set header value", "Send using route", "Create copy", "Bind to address" };

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

            useAnd = (bool) rule.UseAND;

            dynamic criterias = rule.Criterias;
            int cc = (int) criterias.Count;
            for (int i = 0; i < cc; i++)
            {
               dynamic c = criterias.Item[i];
               string field = (bool) c.UsePredefined
                  ? Pick(FieldNames, (int) c.PredefinedField)
                  : "header '" + (string) c.HeaderField + "'";
               criteria.Add(new DetailRow
               {
                  Id = (int) c.ID,
                  Description = field + " " + Pick(MatchNames, (int) c.MatchType) + " '" + (string) c.MatchValue + "'"
               });
               ServerSession.Release(c);
            }
            ServerSession.Release(criterias);

            dynamic acts = rule.Actions;
            int ac = (int) acts.Count;
            for (int i = 0; i < ac; i++)
            {
               dynamic a = acts.Item[i];
               actions.Add(new DetailRow { Id = (int) a.ID, Description = DescribeAction(a) });
               ServerSession.Release(a);
            }
            ServerSession.Release(acts);

            ServerSession.Release(rule);
         }
         catch (Exception)
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
         int type = (int) a.Type;
         string text = Pick(ActionNames, type);
         try
         {
            switch (type)
            {
               case 2: text += " -> " + (string) a.To; break;
               case 3: text += " (subject '" + (string) a.Subject + "')"; break;
               case 4: text += " '" + (string) a.IMAPFolder + "'"; break;
               case 5: text += " " + (string) a.ScriptFunction; break;
               case 7: text += " " + (string) a.HeaderName + "=" + (string) a.Value; break;
               case 8: text += " (route #" + (int) a.RouteID + ")"; break;
               case 10: text += " " + (string) a.Value; break;
            }
         }
         catch (Exception)
         {
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
         catch (Exception ex)
         {
            MessageBox.Show("Rule operation failed: " + ex.Message, "Control Panel");
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
         rule.Active = !(bool) rule.Active;
         rule.Save();
      });

      private void Delete_Click(object sender, RoutedEventArgs e)
      {
         if (RuleGrid.SelectedItem is not RuleRow row)
            return;

         if (MessageBox.Show("Delete rule '" + row.Name + "'?", "Control Panel",
             MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

         WithSelectedRule(rule => rule.Delete());
      }

      private void AddRule_Click(object sender, RoutedEventArgs e)
      {
         string name = NewRuleName.Text.Trim();
         if (name.Length == 0)
         {
            MessageBox.Show("Enter a name for the new rule.", "Control Panel");
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not create the rule: " + ex.Message, "Control Panel");
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not change the match mode: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(rules);
         }
      }

      // ---- Criteria operations ----

      private void AddCriterion_Click(object sender, RoutedEventArgs e)
      {
         if (selectedRuleId_ == 0) { MessageBox.Show("Select a rule first.", "Control Panel"); return; }
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not remove the criterion: " + ex.Message, "Control Panel");
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
         if (selectedRuleId_ == 0) { MessageBox.Show("Select a rule first.", "Control Panel"); return; }
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not remove the action: " + ex.Message, "Control Panel");
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not move the action: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(rules);
         }

         RefreshDetails();
      }
   }
}
