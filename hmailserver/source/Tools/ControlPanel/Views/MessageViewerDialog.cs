// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using hMailServer.ControlPanel.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Views.Scaffold;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Read-only viewer for a queued message's raw source (headers + body), read
   /// straight from the .eml file on disk like hMailServer Administrator does.
   /// On the standard frame, keeping its reading layout: the path above, the
   /// source in a monospaced box with a fixed reading height, Copy at the
   /// footer's left, Close taking Enter and Escape both.
   /// </summary>
   public class MessageViewerDialog : FluentDialogWindow
   {
      // The reading height. The frame sizes the window to its content and a
      // body inside it cannot stretch to a resized window, so the box takes a
      // height of its own rather than the old window's 620 less its chrome.
      private const double ReadingHeight = 400;

      public MessageViewerDialog(Window owner, string filePath)
      {
         Owner = owner;
         Title = L("Message source");

         var body = new StackPanel();

         var pathBox = new Wpf.Ui.Controls.TextBox
         {
            Text = filePath ?? "",
            IsReadOnly = true,
            Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md)
         };
         pathBox.SetResourceReference(FontSizeProperty, "AppFontSizeCaption");
         body.Children.Add(pathBox);

         var content = new Wpf.Ui.Controls.TextBox
         {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily),
            Height = ReadingHeight,
            Text = ReadMessage(filePath)
         };
         content.SetResourceReference(FontSizeProperty, "AppFontSizeCaption");
         body.Children.Add(content);

         var copy = new Wpf.Ui.Controls.Button { Content = L("_Copy") };
         copy.Click += (s, e) =>
         {
            try { Clipboard.SetText(content.Text); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
         };

         // Close is the one thing to do here, so it is the primary and takes
         // Enter; it takes Escape as well, as it always did.
         var close = new Wpf.Ui.Controls.Button { Content = L("Close"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         close.Click += (s, e) => Close();

         DialogFrame frame = UseFrame(L("Message source"), body, close, null, width: 760);
         close.IsCancel = true;
         frame.Footer = copy;
      }

      private static string ReadMessage(string filePath)
      {
         if (string.IsNullOrWhiteSpace(filePath))
            return L("No file is associated with this message.");

         try
         {
            return File.ReadAllText(filePath);
         }
         catch (FileNotFoundException)
         {
            return F("The file\r\n   {0}\r\ncould not be loaded. The message has probably been delivered and is no longer in the queue.", filePath);
         }
         catch (DirectoryNotFoundException)
         {
            return F("The file\r\n   {0}\r\ncould not be loaded. The message has probably been delivered and is no longer in the queue.", filePath);
         }
         catch (UnauthorizedAccessException)
         {
            return F("Access to the message file was denied:\r\n   {0}\r\n\r\nThe Control Panel can only read message files when it runs on the same machine as the server, with sufficient permissions.", filePath);
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            return F("Could not read the message file:\r\n   {0}\r\n\r\n{1}", filePath, ex.Message);
         }
      }
   }
}
