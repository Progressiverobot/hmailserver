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
         public int Position { get; set; }
         public string Name { get; set; }
         public string Enabled { get; set; }
      }

      public RulesView()
      {
         InitializeComponent();
      }

      public void OnEnter() => Reload();

      public void OnLeave()
      {
      }

      private void Reload()
      {
         var rows = new List<RuleRow>();
         dynamic rules = ServerSession.Current.Application.Rules;
         try
         {
            int count = (int) rules.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic rule = rules.Item[i];
               rows.Add(new RuleRow
               {
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
            ? "No global rules defined. Criteria and actions are edited in the classic Administrator for now."
            : rows.Count + " rule(s), evaluated top to bottom.";
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
            DetailText.Text = "Select a rule to see its criteria and actions.";
            return;
         }

         try
         {
            dynamic rules = ServerSession.Current.Application.Rules;
            dynamic rule = rules.Item[row.Position - 1];

            var text = new System.Text.StringBuilder();

            text.Append("IF  ");
            dynamic criterias = rule.Criterias;
            int criteriaCount = (int) criterias.Count;
            for (int i = 0; i < criteriaCount; i++)
            {
               dynamic criteria = criterias.Item[i];
               if (i > 0)
                  text.Append("  AND  ");
               string field = (bool) criteria.UsePredefined
                  ? Pick(FieldNames, (int) criteria.PredefinedField)
                  : "header '" + (string) criteria.HeaderField + "'";
               text.Append(field + " " + Pick(MatchNames, (int) criteria.MatchType) + " '" + (string) criteria.MatchValue + "'");
               ServerSession.Release(criteria);
            }
            if (criteriaCount == 0)
               text.Append("(no criteria - matches everything)");
            ServerSession.Release(criterias);

            text.AppendLine();
            text.Append("THEN  ");
            dynamic actions = rule.Actions;
            int actionCount = (int) actions.Count;
            for (int i = 0; i < actionCount; i++)
            {
               dynamic action = actions.Item[i];
               if (i > 0)
                  text.Append("  ;  ");
               int type = (int) action.Type;
               text.Append(Pick(ActionNames, type));
               try
               {
                  switch (type)
                  {
                     case 2: text.Append(" -> " + (string) action.To); break;
                     case 4: text.Append(" '" + (string) action.IMAPFolder + "'"); break;
                     case 5: text.Append(" " + (string) action.ScriptFunction); break;
                     case 7: text.Append(" " + (string) action.HeaderName + "=" + (string) action.Value); break;
                  }
               }
               catch (Exception)
               {
               }
               ServerSession.Release(action);
            }
            if (actionCount == 0)
               text.Append("(no actions)");
            ServerSession.Release(actions);

            ServerSession.Release(rule);
            ServerSession.Release(rules);

            DetailText.Text = text.ToString();
         }
         catch (Exception ex)
         {
            DetailText.Text = "Could not read the rule details: " + ex.Message;
         }
      }

      private void WithSelectedRule(Action<dynamic> action)
      {
         if (RuleGrid.SelectedItem is not RuleRow row)
            return;

         dynamic rules = ServerSession.Current.Application.Rules;
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
   }
}
