// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using MessageBox = hMailServer.ControlPanel.Views.Dialogs;

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
         FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily),
         FontSize = Typography.Body,
         AcceptsReturn = true,
         AcceptsTab = true,
         TextWrapping = TextWrapping.NoWrap,
         HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
         Background = System.Windows.Media.Brushes.Transparent
      };
      private readonly TextBlock pathText_ = new() { FontSize = Typography.Caption, Margin = new Thickness(0, 0, 0, 8) };
      private readonly TextBlock status_ = new() { FontSize = Typography.Caption, Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };

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

         // The editor is the only control in the Control Panel with AcceptsTab, so
         // it is the only one where Tab types a character instead of moving on.
         // WPF's own KeyboardNavigation makes Ctrl+Tab the way out, so this is not a
         // keyboard trap - but nothing said so, which for a keyboard-only user is
         // very nearly as bad. The name says it, and so does the tool tip.
         //
         // It also had no accessible name at all: a screen reader announced the
         // server's entire event-handler script as an unnamed "edit".
         System.Windows.Automation.AutomationProperties.SetName(editor_,
            "Event handler script. Tab inserts a tab character; press Control and Tab together to move to the next control.");
         editor_.ToolTip = "Tab indents. Use Ctrl+Tab to move focus out of the editor.";

         var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 10) };
         Grid.SetRow(toolbar, 2);

         var templateCombo = new ComboBox { MinWidth = 240, VerticalAlignment = VerticalAlignment.Center };
         templateCombo.Items.Add(new ComboBoxItem { Content = "Insert template\u2026", Tag = "" });
         foreach ((string name, string body) in ScriptTemplates)
            templateCombo.Items.Add(new ComboBoxItem { Content = name, Tag = body });
         templateCombo.SelectedIndex = 0;
         templateCombo.SelectionChanged += (s, e) =>
         {
            if (templateCombo.SelectedItem is ComboBoxItem cbi && cbi.Tag is string body && body.Length > 0)
            {
               InsertTemplate(body);
               templateCombo.SelectedIndex = 0;
            }
         };
         toolbar.Children.Add(templateCombo);

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

      // VBScript starter snippets for common integrations. Each is a complete
      // OnAcceptMessage handler; if the script already has one, the two must be
      // merged into a single Sub before saving.
      private static readonly (string Name, string Body)[] ScriptTemplates =
      {
         ("OnAcceptMessage \u2192 external AV / DLP scanner",
@"' --- Run an external AV / DLP scanner on the received message ----------------
' The message is already on disk at oMessage.Filename. A non-zero exit code is
' treated as ""infected/blocked"". Adjust the path and exit-code handling.
Sub OnAcceptMessage(oClient, oMessage)
   Dim oShell, sCmd, nResult
   sCmd = ""C:\Tools\scan.exe "" & Chr(34) & oMessage.Filename & Chr(34)
   Set oShell = CreateObject(""WScript.Shell"")
   nResult = oShell.Run(sCmd, 0, True)   ' 0 = hidden window, True = wait for exit
   If nResult <> 0 Then
      Result.Value = 2                   ' 0 = accept, 1 = move to queue, 2 = delete
      Result.Message = ""Rejected by external scanner (exit "" & nResult & "")""
   End If
End Sub
"),
         ("OnAcceptMessage \u2192 webhook (SIEM / Slack / Teams)",
@"' --- Notify a webhook about the received message ----------------------------
' Fire-and-forget POST; delivery is not blocked if the webhook is unavailable.
Sub OnAcceptMessage(oClient, oMessage)
   Dim oHttp, q, sUrl, sBody
   q = Chr(34)
   sUrl = ""https://example.com/webhook""
   sBody = ""{"" & q & ""from"" & q & "":"" & q & oMessage.FromAddress & q & ""}""
   On Error Resume Next
   Set oHttp = CreateObject(""MSXML2.ServerXMLHTTP.6.0"")
   oHttp.Open ""POST"", sUrl, False
   oHttp.setRequestHeader ""Content-Type"", ""application/json""
   oHttp.send sBody
   EventLog.Write ""Webhook notified, status "" & oHttp.Status
   On Error Goto 0
End Sub
"),
         ("OnAcceptMessage \u2192 external HTTP API verdict",
@"' --- Ask an external API whether to block the message -----------------------
' Calls a classification/threat API and deletes the message on a 'block' verdict.
Sub OnAcceptMessage(oClient, oMessage)
   Dim oHttp, sUrl
   sUrl = ""https://api.example.com/classify?from="" & oMessage.FromAddress
   Set oHttp = CreateObject(""MSXML2.ServerXMLHTTP.6.0"")
   oHttp.Open ""GET"", sUrl, False
   oHttp.send
   If oHttp.Status = 200 And InStr(oHttp.responseText, ""block"") > 0 Then
      Result.Value = 2
      Result.Message = ""Blocked by classification API""
   End If
End Sub
"),
      };

      private void InsertTemplate(string body)
      {
         if (!editor_.IsEnabled)
            return;

         string separator = editor_.Text.Length > 0 ? "\r\n\r\n" : "";
         editor_.Text = editor_.Text + separator + body;
         editor_.CaretIndex = editor_.Text.Length;
         editor_.ScrollToEnd();
         status_.Text = "Template appended. If you already have an OnAcceptMessage handler, merge the two into a single Sub before saving.";
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
         try
         {
            editor_.Text = File.Exists(scriptPath_) ? File.ReadAllText(scriptPath_) : "";
            status_.Text = File.Exists(scriptPath_) ? "Loaded from disk." : "File does not exist yet; saving will create it.";
            editor_.IsEnabled = true;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            // Keep the editor disabled: enabling it after a failed load would let a
            // save overwrite the file on disk with the empty text shown here.
            editor_.Text = "";
            editor_.IsEnabled = false;
            status_.Text = "Could not read the file: " + ex.Message;
         }
      }

      private string ResolveScriptPath()
      {
         dynamic scripting = ServerSession.Current.Application.Settings.Scripting;
         try
         {
            string file = (string)scripting.CurrentScriptFile;
            string dir = (string)scripting.Directory;
            if (!string.IsNullOrWhiteSpace(file) && Path.IsPathRooted(file))
               return file;
            if (!string.IsNullOrWhiteSpace(dir) && !string.IsNullOrWhiteSpace(file))
               return Path.Join(dir, file);
            if (!string.IsNullOrWhiteSpace(dir))
               return Path.Join(dir, "EventHandlers.vbs");
            return null;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
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
         if (scriptPath_ == null || !editor_.IsEnabled)
            return;
         try
         {
            // Keep the previous version next to the file: event handlers are
            // hand-written administrator code with no other undo.
            if (File.Exists(scriptPath_))
               File.Copy(scriptPath_, scriptPath_ + ".bak", true);

            File.WriteAllText(scriptPath_, editor_.Text);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            MessageBox.Show("Could not write the script file: " + ex.Message, "Control Panel");
            return;
         }

         dynamic scripting = ServerSession.Current.Application.Settings.Scripting;
         try
         {
            scripting.Reload();
            string result = "";
            try { result = (string)scripting.CheckSyntax(); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
            status_.Text = string.IsNullOrWhiteSpace(result)
               ? "Saved and reloaded at " + DateTime.Now.ToLongTimeString() + ". No syntax errors."
               : "Saved. Compiler reported: " + result;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
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
            string result = (string)scripting.CheckSyntax();
            status_.Text = string.IsNullOrWhiteSpace(result)
               ? "No syntax errors reported."
               : "Compiler reported: " + result;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
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
