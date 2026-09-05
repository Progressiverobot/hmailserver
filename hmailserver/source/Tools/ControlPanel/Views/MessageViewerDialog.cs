// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using hMailServer.ControlPanel.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Read-only viewer for a queued message's raw source (headers + body), read
   /// straight from the .eml file on disk like hMailServer Administrator does.
   /// </summary>
   public class MessageViewerDialog : FluentDialogWindow
   {
      public MessageViewerDialog(Window owner, string filePath)
      {
         Owner = owner;
         Title = "Message source";
         Width = 760;
         Height = 620;
         WindowStartupLocation = WindowStartupLocation.CenterOwner;
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

         var root = new Grid { Margin = new Thickness(18) };
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
         root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
         root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

         var header = new TextBlock { Text = "Message source", FontSize = Typography.DialogTitle, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 10) };
         header.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         Grid.SetRow(header, 0);
         root.Children.Add(header);

         var pathBox = new TextBox
         {
            Text = filePath ?? "",
            IsReadOnly = true,
            FontSize = Typography.Caption,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 0, 10),
            Background = System.Windows.Media.Brushes.Transparent
         };
         pathBox.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
         Grid.SetRow(pathBox, 1);
         root.Children.Add(pathBox);

         var content = new TextBox
         {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily),
            FontSize = Typography.Label,
            Padding = new Thickness(8),
            Background = System.Windows.Media.Brushes.Transparent,
            Text = ReadMessage(filePath)
         };
         content.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
         var contentBorder = new Border
         {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = content
         };
         contentBorder.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");
         Grid.SetRow(contentBorder, 2);
         root.Children.Add(contentBorder);

         var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
         var copy = new Wpf.Ui.Controls.Button { Content = "_Copy", Margin = new Thickness(0, 0, 8, 0) };
         copy.Click += (s, e) =>
         {
            try { Clipboard.SetText(content.Text); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
         };
         var close = new Wpf.Ui.Controls.Button { Content = "Close", Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, IsCancel = true };
         close.Click += (s, e) => Close();
         buttons.Children.Add(copy);
         buttons.Children.Add(close);
         Grid.SetRow(buttons, 3);
         root.Children.Add(buttons);

         Content = root;
      }

      private static string ReadMessage(string filePath)
      {
         if (string.IsNullOrWhiteSpace(filePath))
            return "No file is associated with this message.";

         try
         {
            return File.ReadAllText(filePath);
         }
         catch (FileNotFoundException)
         {
            return "The file\r\n   " + filePath + "\r\ncould not be loaded. The message has probably been delivered and is no longer in the queue.";
         }
         catch (DirectoryNotFoundException)
         {
            return "The file\r\n   " + filePath + "\r\ncould not be loaded. The message has probably been delivered and is no longer in the queue.";
         }
         catch (UnauthorizedAccessException)
         {
            return "Access to the message file was denied:\r\n   " + filePath + "\r\n\r\nThe Control Panel can only read message files when it runs on the same machine as the server, with sufficient permissions.";
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            return "Could not read the message file:\r\n   " + filePath + "\r\n\r\n" + ex.Message;
         }
      }
   }
}
