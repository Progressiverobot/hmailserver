// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The base class for every Control Panel dialog.
   ///
   /// Every dialog used to derive from plain System.Windows.Window, which
   /// meant a stock Win32 title bar following the OS app mode rather than the
   /// in-app theme - so on a light-mode OS, every Add/Edit dialog opened with
   /// a white title bar bolted onto dark content. Deriving from WPF-UI's
   /// FluentWindow gives the dialogs the same themed chrome as the shell.
   ///
   /// This class exists so the chrome, the application face and the theme
   /// background are decided once rather than re-decided (or forgotten) per
   /// dialog - the same reasoning as the Dialogs message-box class. It also
   /// carries the standard frame (<see cref="UseFrame"/>): heading, a body that
   /// scrolls, a footer with the primary button first, the window sized to its
   /// content within the token bounds instead of to a number typed into each
   /// dialog, Enter and Escape wired, and the keyboard put in the first field.
   /// The eighteen dialogs that predate it keep their own layout until they are
   /// migrated; nothing here runs unless a dialog asks for it.
   /// </summary>
   public class FluentDialogWindow : Wpf.Ui.Controls.FluentWindow
   {
      public FluentDialogWindow()
      {
         FontFamily = new FontFamily(Typography.UiFontFamily);
         SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
         // The default text colour for everything in the dialog, delivered by
         // property inheritance rather than by an implicit TextBlock style.
         // App.xaml used to carry such a style as a "global contrast
         // guarantee", but an app-level implicit style also reaches the
         // TextBlocks that ContentPresenter generates inside control
         // templates, and a style setter outranks an inherited value - so it
         // overwrote WPF-UI's on-accent button text (near-black on accent
         // blue in the light theme), tab dim states and the sidebar's
         // hover/selected foreground swap. Inheritance gives bare TextBlocks
         // the same default while letting templates and Appearance setters
         // override it, which is the whole point.
         SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
         ShowInTaskbar = false;
      }

      /// <summary>
      /// Puts the standard frame in the window and returns it: the heading, the
      /// body, the primary button (which takes Enter) and the secondary button
      /// (which takes Escape; without one, Escape closes the dialog with no
      /// result). The window is sized to the content at <paramref name="width"/>
      /// - clamped to the dialog tokens - and its height is capped at a fraction
      /// of the work area, past which the body scrolls. On load the keyboard
      /// goes to the first field of the body, not to the title bar's buttons.
      /// </summary>
      protected DialogFrame UseFrame(string heading, UIElement body, Button primary, Button secondary,
                                    string description = null, double width = DesignTokens.Dialog.DefaultWidth)
      {
         var frame = new DialogFrame
         {
            Heading = heading,
            Description = description,
            Body = body,
            PrimaryButton = primary,
            SecondaryButton = secondary
         };

         if (primary != null)
            primary.IsDefault = true;

         if (secondary != null)
            secondary.IsCancel = true;
         else
            PreviewKeyDown += CloseOnEscape_;

         Content = frame;
         SizeToContentWithin(width);
         FocusFirstFieldOnLoad(body);
         return frame;
      }

      /// <summary>
      /// Sizes the window to its content: a fixed width within the dialog
      /// tokens' bounds, the height from the content up to
      /// <see cref="DesignTokens.Dialog.MaxHeightFraction"/> of the work area.
      /// SizeToContent.Height with a fixed width, never WidthAndHeight: auto-
      /// sizing both under FluentWindow's chrome is the combination known to
      /// clip the bottom of the content on first show (see Dialogs.Show).
      /// </summary>
      public void SizeToContentWithin(double width)
      {
         MinWidth = DesignTokens.Dialog.MinWidth;
         MaxWidth = DesignTokens.Dialog.MaxWidth;
         Width = Math.Max(DesignTokens.Dialog.MinWidth, Math.Min(DesignTokens.Dialog.MaxWidth, width));
         SizeToContent = SizeToContent.Height;
         MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height * DesignTokens.Dialog.MaxHeightFraction);
         ResizeMode = ResizeMode.NoResize;
         WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
      }

      /// <summary>
      /// On load, puts the keyboard in the first focusable field inside
      /// <paramref name="root"/>. Without this a FluentWindow opens with the
      /// focus on nothing, and the first Tab lands on the title bar's buttons.
      /// </summary>
      public void FocusFirstFieldOnLoad(UIElement root)
      {
         Loaded += (s, e) =>
         {
            if (Keyboard.FocusedElement is UIElement focused && IsInside_(focused, root))
               return;

            FrameworkElement first = FirstField_(root);
            if (first != null)
               Keyboard.Focus(first);
         };
      }

      private static bool IsInside_(DependencyObject element, DependencyObject root)
      {
         for (DependencyObject current = element; current != null; current = VisualTreeHelper.GetParent(current))
         {
            if (ReferenceEquals(current, root))
               return true;
         }

         return false;
      }

      /// <summary>Depth-first, in visual order, which is tab order for everything here.</summary>
      private static FrameworkElement FirstField_(DependencyObject root)
      {
         if (root is FrameworkElement element && element.Focusable && element.IsVisible && element.IsEnabled
             && KeyboardNavigation.GetIsTabStop(element) && root is not ScrollViewer)
            return element;

         int count = VisualTreeHelper.GetChildrenCount(root);
         for (int i = 0; i < count; i++)
         {
            FrameworkElement found = FirstField_(VisualTreeHelper.GetChild(root, i));
            if (found != null)
               return found;
         }

         return null;
      }

      private void CloseOnEscape_(object sender, KeyEventArgs e)
      {
         if (e.Key != Key.Escape)
            return;

         e.Handled = true;
         Close();
      }
   }
}
