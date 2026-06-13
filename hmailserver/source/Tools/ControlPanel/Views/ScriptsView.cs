using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Editor for the server-side event script file (EventHandlers.vbs / .js).
   /// Loads the file referenced by Scripting.CurrentScriptFile, lets the admin
   /// edit and save it, then reloads the script engine and reports the compile
   /// result via Scripting.CheckSyntax.
   /// </summary>
   public class ScriptsView : UserControl, IPageLifecycle
   {
      private readonly TextBox editor_ = new()
      {
         FontFamily = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono, monospace"),
         FontSize = 13,
         AcceptsReturn = true,
         AcceptsTab = true,
         TextWrapping = TextWrapping.NoWrap,
         HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
         Background = System.Windows.Media.Brushes.Transparent
      };
      private readonly TextBlock pathText_ = new() { FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
      private readonly TextBlock status_ = new() { FontSize = 12, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };

      private string scriptPath_;

      public ScriptsView() => Build();

      public void OnEnter() => LoadScript();
      public void OnLeave() { }

      private void Build()
      {
         var root = new Grid { Margin = new Thickness(26, 20, 26, 20) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var head = new StackPanel();
         var title = new TextBlock { Text = "Event scripts" };
         title.SetResourceReference(StyleProperty, "PageTitle");
         head.Children.Add(title);
         var sub = new TextBlock { Text = "Edit the server event-handler script (OnAcceptMessage, OnDeliveryStart, OnHELO, ...). Saving writes the file and reloads the scripting engine." };
         sub.SetResourceReference(StyleProperty, "PageSubtitle");
         head.Children.Add(sub);
         root.Children.Add(head);

         pathText_.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         Grid.SetRow(pathText_, 1);
         root.Children.Add(pathText_);

         var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 10) };
         Grid.SetRow(toolbar, 2);
         toolbar.Children.Add(MakeButton("Save & reload", Wpf.Ui.Controls.ControlAppearance.Primary, (_, _) => SaveScript()));
         toolbar.Children.Add(MakeButton("Check syntax", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => CheckSyntax()));
         toolbar.Children.Add(MakeButton("Reload from disk", Wpf.Ui.Controls.ControlAppearance.Secondary, (_, _) => LoadScript()));
         root.Children.Add(toolbar);

         var card = new Border { Padding = new Thickness(8) };
         card.SetResourceReference(StyleProperty, "Card");
         editor_.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
         card.Child = editor_;
         Grid.SetRow(card, 3);
         root.Children.Add(card);

         status_.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");
         Grid.SetRow(status_, 4);
         root.Children.Add(status_);

         Content = root;
      }

      private static Wpf.Ui.Controls.Button MakeButton(string text, Wpf.Ui.Controls.ControlAppearance appearance, RoutedEventHandler onClick)
      {
         var b = new Wpf.Ui.Controls.Button { Content = text, Appearance = appearance, Margin = new Thickness(8, 0, 0, 0), MinWidth = 110 };
         b.Click += onClick;
         return b;
      }

      private void LoadScript()
      {
         scriptPath_ = ResolveScriptPath();
         if (scriptPath_ == null)
         {
            pathText_.Text = "Could not determine the event-script path.";
            editor_.IsEnabled = false;
            return;
         }

         pathText_.Text = scriptPath_;
         editor_.IsEnabled = true;
         try
         {
            editor_.Text = File.Exists(scriptPath_) ? File.ReadAllText(scriptPath_) : "";
            status_.Text = File.Exists(scriptPath_) ? "Loaded from disk." : "File does not exist yet; saving will create it.";
         }
         catch (Exception ex)
         {
            status_.Text = "Could not read the file: " + ex.Message;
         }
      }

      private string ResolveScriptPath()
      {
         dynamic scripting = ServerSession.Current.Application.Settings.Scripting;
         try
         {
            string file = (string) scripting.CurrentScriptFile;
            string dir = (string) scripting.Directory;
            if (!string.IsNullOrWhiteSpace(file) && Path.IsPathRooted(file))
               return file;
            if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(file))
               return Path.Combine(dir, file);
            if (!string.IsNullOrWhiteSpace(dir))
               return Path.Combine(dir, "EventHandlers.vbs");
            return null;
         }
         catch (Exception)
         {
            return null;
         }
         finally
         {
            ServerSession.Release(scripting);
         }
      }

      private void SaveScript()
      {
         if (scriptPath_ == null)
            return;
         try
         {
            File.WriteAllText(scriptPath_, editor_.Text);
         }
         catch (Exception ex)
         {
            MessageBox.Show("Could not write the script file: " + ex.Message, "Control Panel");
            return;
         }

         dynamic scripting = ServerSession.Current.Application.Settings.Scripting;
         try
         {
            scripting.Reload();
            string result = "";
            try { result = (string) scripting.CheckSyntax(); } catch (Exception) { }
            status_.Text = string.IsNullOrWhiteSpace(result)
               ? "Saved and reloaded at " + DateTime.Now.ToLongTimeString() + ". No syntax errors."
               : "Saved. Compiler reported: " + result;
         }
         catch (Exception ex)
         {
            status_.Text = "Saved, but reload failed: " + ex.Message;
         }
         finally
         {
            ServerSession.Release(scripting);
         }
      }

      private void CheckSyntax()
      {
         dynamic scripting = ServerSession.Current.Application.Settings.Scripting;
         try
         {
            string result = (string) scripting.CheckSyntax();
            status_.Text = string.IsNullOrWhiteSpace(result)
               ? "No syntax errors reported."
               : "Compiler reported: " + result;
         }
         catch (Exception ex)
         {
            status_.Text = "Syntax check failed: " + ex.Message;
         }
         finally
         {
            ServerSession.Release(scripting);
         }
      }
   }
}
