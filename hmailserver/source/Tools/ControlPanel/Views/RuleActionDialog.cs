using System;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>Add/edit a single rule action, with the contextual fields for each action type.</summary>
   public class RuleActionDialog : Window
   {
      private readonly int ruleId_;
      private readonly int actionId_; // 0 = new

      private readonly ComboBox type_ = new() { FontSize = 13, Margin = new Thickness(0, 0, 0, 12) };

      private readonly TextBox to_ = new();
      private readonly CheckBox abortSpam_ = new() { Content = "Abort on messages marked as spam", FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
      private readonly TextBox fromName_ = new();
      private readonly TextBox fromAddress_ = new();
      private readonly TextBox subject_ = new();
      private readonly TextBox body_ = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
      private readonly TextBox imapFolder_ = new();
      private readonly TextBox scriptFunction_ = new();
      private readonly TextBox headerName_ = new();
      private readonly TextBox value_ = new();
      private readonly ComboBox route_ = new() { FontSize = 13, Margin = new Thickness(0, 0, 0, 8) };
      private readonly TextBox bindAddress_ = new();

      private readonly StackPanel forwardPanel_ = new();
      private readonly StackPanel replyPanel_ = new();
      private readonly StackPanel folderPanel_ = new();
      private readonly StackPanel scriptPanel_ = new();
      private readonly StackPanel headerPanel_ = new();
      private readonly StackPanel routePanel_ = new();
      private readonly StackPanel bindPanel_ = new();
      private readonly TextBlock noParams_ = new() { Text = "This action has no additional parameters.", FontSize = 12.5, Margin = new Thickness(0, 4, 0, 0) };

      public RuleActionDialog(Window owner, int ruleId, int actionId)
      {
         ruleId_ = ruleId;
         actionId_ = actionId;
         Owner = owner;
         Title = actionId == 0 ? "Add action" : "Edit action";
         Width = 520;
         Height = 540;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var root = new Grid { Margin = new Thickness(18) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var header = new TextBlock { Text = "THEN", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 12) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetRow(header, 0);
         root.Children.Add(header);

         var body = new StackPanel();
         body.Children.Add(Label("Action"));
         type_.Items.Add(Combo("Delete e-mail", 1));
         type_.Items.Add(Combo("Forward e-mail", 2));
         type_.Items.Add(Combo("Reply", 3));
         type_.Items.Add(Combo("Move to IMAP folder", 4));
         type_.Items.Add(Combo("Run script function", 5));
         type_.Items.Add(Combo("Stop rule processing", 6));
         type_.Items.Add(Combo("Set header value", 7));
         type_.Items.Add(Combo("Send using route", 8));
         type_.Items.Add(Combo("Create copy", 9));
         type_.Items.Add(Combo("Bind to address", 10));
         type_.SelectionChanged += (s, e) => UpdateVisibility();
         body.Children.Add(type_);

         // Forward
         forwardPanel_.Children.Add(Label("To"));
         forwardPanel_.Children.Add(Input(to_));
         forwardPanel_.Children.Add(abortSpam_);
         body.Children.Add(forwardPanel_);

         // Reply
         replyPanel_.Children.Add(Label("From (name)"));
         replyPanel_.Children.Add(Input(fromName_));
         replyPanel_.Children.Add(Label("From (address)"));
         replyPanel_.Children.Add(Input(fromAddress_));
         replyPanel_.Children.Add(Label("Subject"));
         replyPanel_.Children.Add(Input(subject_));
         replyPanel_.Children.Add(Label("Body"));
         Input(body_);
         replyPanel_.Children.Add(body_);
         body.Children.Add(replyPanel_);

         // Move to folder
         folderPanel_.Children.Add(Label("IMAP folder (e.g. INBOX.Archive)"));
         folderPanel_.Children.Add(Input(imapFolder_));
         body.Children.Add(folderPanel_);

         // Script
         scriptPanel_.Children.Add(Label("Script function"));
         scriptPanel_.Children.Add(Input(scriptFunction_));
         body.Children.Add(scriptPanel_);

         // Set header
         headerPanel_.Children.Add(Label("Header name"));
         headerPanel_.Children.Add(Input(headerName_));
         headerPanel_.Children.Add(Label("Value"));
         headerPanel_.Children.Add(Input(value_));
         body.Children.Add(headerPanel_);

         // Route
         routePanel_.Children.Add(Label("Route"));
         routePanel_.Children.Add(route_);
         body.Children.Add(routePanel_);

         // Bind to address
         bindPanel_.Children.Add(Label("IP address"));
         bindPanel_.Children.Add(Input(bindAddress_));
         body.Children.Add(bindPanel_);

         body.Children.Add(noParams_);

         var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
         Grid.SetRow(scroll, 1);
         root.Children.Add(scroll);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
         var save = new Wpf.Ui.Controls.Button { Content = "Save", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0) };
         save.Click += (s, e) => Save();
         var cancel = new Wpf.Ui.Controls.Button { Content = "Cancel" };
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
         int type = ComboValue(type_);
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
            int count = (int) routes.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic r = routes.Item[i];
               route_.Items.Add(new ComboBoxItem { Content = (string) r.DomainName, Tag = (int) r.ID });
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
            SelectCombo(type_, 1);
            UpdateVisibility();
            return;
         }

         dynamic rules = ServerSession.Current.Application.Rules;
         try
         {
            dynamic rule = FindRule(rules);
            if (rule == null) { Close(); return; }
            dynamic actions = rule.Actions;
            try
            {
               dynamic a = actions.ItemByDBID[actionId_];
               int type = (int) a.Type;
               SelectCombo(type_, type);
               to_.Text = (string) a.To ?? "";
               abortSpam_.IsChecked = (bool) a.AbortSpamFlagged;
               fromName_.Text = (string) a.FromName ?? "";
               fromAddress_.Text = (string) a.FromAddress ?? "";
               subject_.Text = (string) a.Subject ?? "";
               body_.Text = (string) a.Body ?? "";
               imapFolder_.Text = (string) a.IMAPFolder ?? "";
               scriptFunction_.Text = (string) a.ScriptFunction ?? "";
               headerName_.Text = (string) a.HeaderName ?? "";
               value_.Text = (string) a.Value ?? "";
               bindAddress_.Text = (string) a.Value ?? "";
               SelectCombo(route_, (int) a.RouteID);
               ServerSession.Release(a);
            }
            finally
            {
               ServerSession.Release(actions);
            }
            ServerSession.Release(rule);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not load the action: " + ex.Message, "Control Panel");
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
         int type = ComboValue(type_);

         dynamic rules = ServerSession.Current.Application.Rules;
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
                     a.AbortSpamFlagged = abortSpam_.IsChecked == true;
                     break;
                  case 3:
                     a.FromName = fromName_.Text.Trim();
                     a.FromAddress = fromAddress_.Text.Trim();
                     a.Subject = subject_.Text;
                     a.Body = body_.Text;
                     a.AbortSpamFlagged = abortSpam_.IsChecked == true;
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
                     a.RouteID = ComboValue(route_);
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
         catch (Exception ex)
         {
            MessageBox.Show("Could not save the action: " + ex.Message, "Control Panel");
         }
         finally
         {
            ServerSession.Release(rules);
         }
      }

      // ---- UI helpers ----

      private static TextBlock Label(string text)
      {
         var t = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(0, 8, 0, 4) };
         t.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         return t;
      }

      private static TextBox Input(TextBox box)
      {
         box.FontSize = 13;
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
            if ((int) item.Tag == value) { combo.SelectedItem = item; return; }
      }

      private static int ComboValue(ComboBox combo) => combo.SelectedItem is ComboBoxItem item ? (int) item.Tag : 0;
   }
}
