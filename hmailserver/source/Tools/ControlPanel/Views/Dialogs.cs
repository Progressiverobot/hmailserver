// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The Fluent replacement for System.Windows.MessageBox.
   ///
   /// The Control Panel had 168 Win32 message boxes - every save error, every
   /// delete confirmation, every validation nag was the grey system box with
   /// system-font buttons, instantly old against a Mica window. This class
   /// deliberately exposes the SAME static Show(...) overloads the old class
   /// did, so each call site moves by a single using-alias at the top of its
   /// file (`using MessageBox = hMailServer.ControlPanel.Views.Dialogs;`)
   /// rather than by editing the call - which is what makes replacing all 168
   /// a mechanical change instead of a risky one. Forty-three of the sites
   /// spread their arguments over several lines; none of them had to be
   /// touched.
   ///
   /// The dialogs themselves follow the app's own conventions: theme
   /// background, the shared type ramp, primary button first then Cancel
   /// (Windows order, as every existing dialog footer already does), Enter and
   /// Esc wired via IsDefault/IsCancel, and a severity icon in the theme's
   /// status colours rather than the Win32 stock bitmaps.
   /// </summary>
   public static class Dialogs
   {
      private const string DefaultCaption = "hMailServer Control Panel";

      public static MessageBoxResult Show(string messageBoxText)
      {
         return Show(messageBoxText, DefaultCaption, MessageBoxButton.OK, MessageBoxImage.None);
      }

      public static MessageBoxResult Show(string messageBoxText, string caption)
      {
         return Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);
      }

      public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
      {
         return Show(messageBoxText, caption, button, MessageBoxImage.None);
      }

      public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
      {
         return Show(messageBoxText, caption, button, icon, MessageBoxResult.None);
      }

      public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
      {
         // The Win32 box was callable from any thread; a WPF window is not -
         // constructing one off the UI thread throws from the dispatcher. No
         // current caller is off-thread, but a background worker reporting an
         // error is the natural future caller, so marshal instead of trusting
         // that audit to stay true. Invoke is synchronous, so the caller still
         // blocks for the answer exactly as it always did.
         var dispatcher = Application.Current?.Dispatcher;
         if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Show(messageBoxText, caption, button, icon, defaultResult));

         // Win32 fell back to the first button when the named default was not in
         // the set; honouring the name literally left NO default at all, so
         // Enter did nothing. None restores the Win32 behaviour: the primary
         // (always first here) takes Enter.
         if (defaultResult != MessageBoxResult.None && !Offers_(button, defaultResult))
            defaultResult = MessageBoxResult.None;

         // SizeToContent.Height with a fixed width, not WidthAndHeight: auto-
         // sizing both dimensions under FluentWindow's WindowChrome is the WPF
         // combination known to clip the bottom of content on first show, and
         // this is the most-shown window in the application. The 18 re-based
         // dialogs all use exactly this fixed-width shape.
         var window = new FluentDialogWindow
         {
            Title = string.IsNullOrWhiteSpace(caption) ? DefaultCaption : caption,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            Width = 480
         };

         Window owner = ActiveWindow_();
         if (owner != null && !ReferenceEquals(owner, window))
         {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
         }
         else
         {
            // Startup errors can fire before any window exists; centre on the
            // screen rather than on nothing.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
         }

         var root = new StackPanel { Margin = new Thickness(24) };

         var messageRow = new StackPanel { Orientation = Orientation.Horizontal };

         var symbol = IconFor_(icon);
         if (symbol != null)
         {
            messageRow.Children.Add(symbol);
         }

         var text = new TextBlock
         {
            Text = messageBoxText,
            FontSize = Typography.Body,
            TextWrapping = TextWrapping.Wrap,
            // The window is 480 wide; 24px margins each side and the 36px icon
            // column leave 396. A horizontal StackPanel measures its children
            // with infinite width, so this MaxWidth is what makes the text wrap
            // at all rather than run off the right edge.
            MaxWidth = 380,
            VerticalAlignment = VerticalAlignment.Center
         };
         text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
         messageRow.Children.Add(text);

         root.Children.Add(messageRow);

         var result = MessageBoxResult.None;

         var footer = new StackPanel
         {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
         };

         void AddButton(string label, MessageBoxResult value, bool primary, bool isCancel)
         {
            // The Win32 box let a caller name the DEFAULT button - used here for
            // confirmations whose safe answer is Cancel, so Enter declines. The
            // named button takes Enter; visual prominence stays with the primary.
            bool isDefault = defaultResult == MessageBoxResult.None ? primary : value == defaultResult;

            var buttonControl = new Wpf.Ui.Controls.Button
            {
               Content = label,
               MinWidth = 88,
               Margin = new Thickness(0, 0, isCancel ? 0 : 8, 0),
               IsDefault = isDefault,
               IsCancel = isCancel
            };

            if (primary)
            {
               // A destructive confirmation gets the danger appearance, so the
               // button that deletes something never looks like the safe one.
               //
               // The rule is Warning + a confirmation shape, and deliberately
               // NOT Error. In this application a Warning icon on a two-answer
               // box always means "the affirmative destroys or risks something"
               // (every such call site is a delete, revoke, replace or
               // proceed-against-advice), and that includes OKCancel - the
               // directory-synchronisation apply, which rewrites accounts in
               // bulk, asks with OKCancel. An Error icon means a failure
               // already happened, and the affirmative there acknowledges or
               // RECOVERS - the crash dialog's "Yes" restarts the application -
               // so painting it Danger would mark the recovery button as the
               // destructive one.
               buttonControl.Appearance = icon == MessageBoxImage.Warning &&
                                          (button == MessageBoxButton.OKCancel ||
                                           button == MessageBoxButton.YesNo ||
                                           button == MessageBoxButton.YesNoCancel)
                  ? Wpf.Ui.Controls.ControlAppearance.Danger
                  : Wpf.Ui.Controls.ControlAppearance.Primary;
            }

            buttonControl.Click += (s, e) => { result = value; window.Close(); };
            footer.Children.Add(buttonControl);
         }

         switch (button)
         {
            case MessageBoxButton.OKCancel:
               AddButton("OK", MessageBoxResult.OK, primary: true, isCancel: false);
               AddButton("Cancel", MessageBoxResult.Cancel, primary: false, isCancel: true);
               break;

            case MessageBoxButton.YesNo:
               AddButton("Yes", MessageBoxResult.Yes, primary: true, isCancel: false);
               // Esc answering "No" preserves the old semantics: closing the
               // Win32 YesNo box without choosing was impossible, and every
               // caller treats anything-but-Yes as "do nothing".
               AddButton("No", MessageBoxResult.No, primary: false, isCancel: true);
               break;

            case MessageBoxButton.YesNoCancel:
               AddButton("Yes", MessageBoxResult.Yes, primary: true, isCancel: false);
               AddButton("No", MessageBoxResult.No, primary: false, isCancel: false);
               AddButton("Cancel", MessageBoxResult.Cancel, primary: false, isCancel: true);
               break;

            default:
               AddButton("OK", MessageBoxResult.OK, primary: true, isCancel: true);
               break;
         }

         root.Children.Add(footer);
         window.Content = root;

         window.ShowDialog();

         // The Win32 box never returned None for OK-only; closing it was OK.
         if (result == MessageBoxResult.None && button == MessageBoxButton.OK)
            result = MessageBoxResult.OK;

         // And a YesNo closed via the title bar is No, not None, for the same
         // treat-anything-but-Yes-as-decline reason as the Esc wiring above.
         if (result == MessageBoxResult.None && (button == MessageBoxButton.YesNo || button == MessageBoxButton.YesNoCancel))
            result = button == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;

         if (result == MessageBoxResult.None && button == MessageBoxButton.OKCancel)
            result = MessageBoxResult.Cancel;

         return result;
      }

      /// <summary>Whether this button set actually offers the given result.</summary>
      private static bool Offers_(MessageBoxButton button, MessageBoxResult result)
      {
         switch (button)
         {
            case MessageBoxButton.OKCancel:
               return result == MessageBoxResult.OK || result == MessageBoxResult.Cancel;

            case MessageBoxButton.YesNo:
               return result == MessageBoxResult.Yes || result == MessageBoxResult.No;

            case MessageBoxButton.YesNoCancel:
               return result == MessageBoxResult.Yes || result == MessageBoxResult.No ||
                      result == MessageBoxResult.Cancel;

            default:
               return result == MessageBoxResult.OK;
         }
      }

      private static Wpf.Ui.Controls.SymbolIcon IconFor_(MessageBoxImage image)
      {
         Wpf.Ui.Controls.SymbolRegular symbol;
         System.Windows.Media.Brush brush = null;
         string themeBrushKey = null;

         switch (image)
         {
            case MessageBoxImage.Error:
               symbol = Wpf.Ui.Controls.SymbolRegular.DismissCircle24;
               brush = ThemeTokens.Danger;   // live-retinted with the theme
               break;

            case MessageBoxImage.Warning:
               symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
               brush = ThemeTokens.Warning;
               break;

            case MessageBoxImage.Question:
               symbol = Wpf.Ui.Controls.SymbolRegular.QuestionCircle24;
               themeBrushKey = "TextFillColorSecondaryBrush";
               break;

            case MessageBoxImage.Information:
               symbol = Wpf.Ui.Controls.SymbolRegular.Info24;
               themeBrushKey = "TextFillColorSecondaryBrush";
               break;

            default:
               return null;
         }

         var icon = new Wpf.Ui.Controls.SymbolIcon(symbol)
         {
            FontSize = 24,
            Margin = new Thickness(0, 2, 12, 0),
            VerticalAlignment = VerticalAlignment.Top
         };

         if (brush != null)
            icon.Foreground = brush;
         else
            icon.SetResourceReference(Control.ForegroundProperty, themeBrushKey);

         return icon;
      }

      private static Window ActiveWindow_()
      {
         var application = Application.Current;
         if (application == null)
            return null;

         foreach (Window candidate in application.Windows)
         {
            if (candidate.IsActive && candidate.IsVisible)
               return candidate;
         }

         return application.MainWindow != null && application.MainWindow.IsVisible
            ? application.MainWindow
            : null;
      }
   }
}
