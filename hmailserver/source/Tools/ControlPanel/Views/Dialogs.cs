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
         var window = new Wpf.Ui.Controls.FluentWindow
         {
            Title = string.IsNullOrWhiteSpace(caption) ? DefaultCaption : caption,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            MaxWidth = 520,
            MinWidth = 320,
            FontFamily = new System.Windows.Media.FontFamily(Typography.UiFontFamily)
         };
         window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");

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
            MaxWidth = 420,
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
            var buttonControl = new Wpf.Ui.Controls.Button
            {
               Content = label,
               MinWidth = 88,
               Margin = new Thickness(0, 0, isCancel ? 0 : 8, 0),
               IsDefault = primary,
               IsCancel = isCancel
            };

            if (primary)
            {
               // A destructive confirmation gets the danger appearance, so the
               // button that deletes something never looks like the safe one.
               buttonControl.Appearance = (icon == MessageBoxImage.Warning || icon == MessageBoxImage.Error) &&
                                          (button == MessageBoxButton.YesNo || button == MessageBoxButton.YesNoCancel)
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
