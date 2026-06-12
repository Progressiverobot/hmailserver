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
