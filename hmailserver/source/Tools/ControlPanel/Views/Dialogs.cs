// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using static hMailServer.ControlPanel.Services.Loc;

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
   /// The box is a dialog on the standard frame (<see cref="FluentDialogWindow.UseFrame"/>),
   /// with the primary button first then Cancel (Windows order, as every
   /// dialog footer here has), Enter and Escape wired, and the severity shown
   /// the way every status in the application is shown: an <see cref="InlineNotice"/>
   /// carrying the level's colour, its shape and its word, so an error reads
   /// as one in greyscale and under High Contrast, where the Win32 bitmaps and
   /// a tinted icon both fail. <see cref="Confirm"/> is the question whose
   /// affirmative is named for what it does - Delete, Remove - rather than Yes,
   /// for the call sites that can say so.
   /// </summary>
   public static class Dialogs
   {
      private static string DefaultCaption => L("hMailServer Control Panel");

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

         var dialog = new MessageDialog(ActiveWindow_(), CaptionOrDefault_(caption), messageBoxText,
            LevelFor(icon), ChoicesFor_(button, icon, defaultResult));
         dialog.ShowDialog();

         MessageBoxResult result = dialog.Result;

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

      /// <summary>
      /// A question whose affirmative is named for what it does. "Remove the
      /// recipient?" answered with a button that says Remove leaves no room for
      /// the Yes-means-which reading a Yes/No pair invites, and a destructive
      /// one is drawn in the danger appearance so the button that deletes
      /// something never looks like the safe one. Escape and the title bar's
      /// close both decline. True when the action was chosen.
      /// </summary>
      /// <param name="action">The caption of the affirmative, with its Alt key - one of the catalogued button captions.</param>
      /// <param name="destructive">Whether the affirmative deletes, revokes or replaces something.</param>
      public static bool Confirm(string text, string caption, string action, bool destructive = false)
      {
         var dispatcher = Application.Current?.Dispatcher;
         if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.Invoke(() => Confirm(text, caption, action, destructive));

         var dialog = new MessageDialog(ActiveWindow_(), CaptionOrDefault_(caption), text,
            destructive ? StatusLevel.Warning : StatusLevel.Normal,
            new[]
            {
               new Choice(action, MessageBoxResult.Yes, Primary: true, IsCancel: false, IsDefault: true, Destructive: destructive),
               new Choice(L("Cancel"), MessageBoxResult.Cancel, Primary: false, IsCancel: true, IsDefault: false, Destructive: false)
            });
         dialog.ShowDialog();
         return dialog.Result == MessageBoxResult.Yes;
      }

      /// <summary>A fact the user must acknowledge, at the information level.</summary>
      public static void Info(string text, string caption = null)
         => Show(text, CaptionOrDefault_(caption), MessageBoxButton.OK, MessageBoxImage.Information);

      /// <summary>Something that went wrong and stops here, at the critical level.</summary>
      public static void Error(string text, string caption = null)
         => Show(text, CaptionOrDefault_(caption), MessageBoxButton.OK, MessageBoxImage.Error);

      /// <summary>Something that went partly wrong, at the warning level.</summary>
      public static void Warn(string text, string caption = null)
         => Show(text, CaptionOrDefault_(caption), MessageBoxButton.OK, MessageBoxImage.Warning);

      /// <summary>
      /// The status level a message-box image maps onto. Error, Warning and
      /// Information carry a level and are drawn as a notice; a question and a
      /// bare message carry none and are drawn as body text - a diamond and the
      /// word Information in front of "Restart it now?" would be noise.
      /// </summary>
      public static StatusLevel LevelFor(MessageBoxImage image)
      {
         switch (image)
         {
            case MessageBoxImage.Error:
               return StatusLevel.Critical;
            case MessageBoxImage.Warning:
               return StatusLevel.Warning;
            case MessageBoxImage.Information:
               return StatusLevel.Information;
            default:
               return StatusLevel.Normal;
         }
      }

      private static string CaptionOrDefault_(string caption)
         => string.IsNullOrWhiteSpace(caption) ? DefaultCaption : caption;

      /// <summary>One button of the box: its caption, what it answers, and how it is drawn and wired.</summary>
      private sealed record Choice(string Label, MessageBoxResult Value, bool Primary, bool IsCancel, bool IsDefault, bool Destructive);

      private static IReadOnlyList<Choice> ChoicesFor_(MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
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
         bool destructive = icon == MessageBoxImage.Warning && button != MessageBoxButton.OK;

         // The Win32 box let a caller name the DEFAULT button - used here for
         // confirmations whose safe answer is Cancel, so Enter declines. The
         // named button takes Enter; visual prominence stays with the primary.
         bool IsDefault(bool primary, MessageBoxResult value)
            => defaultResult == MessageBoxResult.None ? primary : value == defaultResult;

         switch (button)
         {
            case MessageBoxButton.OKCancel:
               return new[]
               {
                  new Choice(L("OK"), MessageBoxResult.OK, true, false, IsDefault(true, MessageBoxResult.OK), destructive),
                  new Choice(L("Cancel"), MessageBoxResult.Cancel, false, true, IsDefault(false, MessageBoxResult.Cancel), false)
               };

            case MessageBoxButton.YesNo:
               // Esc answering "No" preserves the old semantics: closing the
               // Win32 YesNo box without choosing was impossible, and every
               // caller treats anything-but-Yes as "do nothing".
               return new[]
               {
                  new Choice(L("Yes"), MessageBoxResult.Yes, true, false, IsDefault(true, MessageBoxResult.Yes), destructive),
                  new Choice(L("No"), MessageBoxResult.No, false, true, IsDefault(false, MessageBoxResult.No), false)
               };

            case MessageBoxButton.YesNoCancel:
               return new[]
               {
                  new Choice(L("Yes"), MessageBoxResult.Yes, true, false, IsDefault(true, MessageBoxResult.Yes), destructive),
                  new Choice(L("No"), MessageBoxResult.No, false, false, IsDefault(false, MessageBoxResult.No), false),
                  new Choice(L("Cancel"), MessageBoxResult.Cancel, false, true, IsDefault(false, MessageBoxResult.Cancel), false)
               };

            default:
               return new[]
               {
                  new Choice(L("OK"), MessageBoxResult.OK, true, true, true, false)
               };
         }
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

      /// <summary>
      /// The box itself: the message as a notice at its level (or as body text
      /// when it has none), the buttons in the frame's footer with the primary
      /// first, the window sized to the message. A third button (YesNoCancel's
      /// No) sits between the primary and Cancel, in the secondary slot beside
      /// it, so the order Yes, No, Cancel is kept.
      /// </summary>
      private sealed class MessageDialog : FluentDialogWindow
      {
         public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

         public MessageDialog(Window owner, string caption, string text, StatusLevel level, IReadOnlyList<Choice> choices)
         {
            // Startup errors can fire before any window exists; centre on the
            // screen rather than on nothing. The owner is set before the frame,
            // which reads it to decide where the window opens.
            if (owner != null)
               Owner = owner;

            Title = caption;

            UIElement body;
            if (level == StatusLevel.Normal)
            {
               var block = new TextBlock { Text = text };
               block.SetResourceReference(StyleProperty, "TextBody");
               body = block;
            }
            else
            {
               body = new InlineNotice { Level = level, Text = text, Margin = new Thickness(0) };
            }

            Wpf.Ui.Controls.Button primary = null, cancel = null;
            var others = new List<Wpf.Ui.Controls.Button>();
            var buttons = new List<(Choice Choice, Wpf.Ui.Controls.Button Button)>();

            foreach (Choice choice in choices)
            {
               var button = new Wpf.Ui.Controls.Button { Content = choice.Label, MinWidth = 88 };
               if (choice.Primary)
                  button.Appearance = choice.Destructive ? Wpf.Ui.Controls.ControlAppearance.Danger : Wpf.Ui.Controls.ControlAppearance.Primary;

               MessageBoxResult value = choice.Value;
               button.Click += (s, e) => { Result = value; Close(); };
               buttons.Add((choice, button));

               if (choice.Primary)
                  primary = button;
               else if (choice.IsCancel)
                  cancel = button;
               else
                  others.Add(button);
            }

            // Every message box is the same width; the height follows the text.
            DialogFrame frame = UseFrame(null, body, primary, cancel, null, 480);
            AutomationProperties.SetName(frame, caption);

            // The frame gave Enter to the primary and Escape to Cancel; the
            // choices say which button actually takes each, since a caller may
            // name a non-primary default, and the OK of an OK-only box is both.
            foreach ((Choice choice, Wpf.Ui.Controls.Button button) in buttons)
            {
               button.IsDefault = choice.IsDefault;
               button.IsCancel = choice.IsCancel;
            }

            if (others.Count > 0)
            {
               var trailing = new StackPanel { Orientation = Orientation.Horizontal };
               foreach (Wpf.Ui.Controls.Button other in others)
               {
                  other.Margin = new Thickness(0, 0, DesignTokens.Space.Sm, 0);
                  trailing.Children.Add(other);
               }

               if (cancel != null)
                  trailing.Children.Add(cancel);

               frame.SecondaryButton = trailing;
            }
         }
      }
   }
}
