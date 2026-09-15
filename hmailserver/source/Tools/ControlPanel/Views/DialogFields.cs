// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using hMailServer.ControlPanel.Services;
using hMailServer.ControlPanel.Views.Scaffold;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// What the code-built dialogs share now that they sit on the standard frame:
   /// a field is a <see cref="FieldRow"/> around its editor, a note is a caption
   /// in the type ramp, a divider is the token hairline, and a message that only
   /// says a field is wrong goes on the field rather than into a message box.
   ///
   /// Seven dialogs carried a private copy of the same Label, Input, Combo,
   /// SelectCombo and ComboValue helpers; they now keep a one-line
   /// <c>Label</c> that forwards to <see cref="Field"/> - kept by that name
   /// because build/check-mnemonics.py reads every call of a caption-taking
   /// helper by that name for the Alt key it carries, and a caption the checker
   /// cannot see is a caption whose key can collide unnoticed.
   ///
   /// Two things here are a workaround for what the dialog base does not offer,
   /// and belong in it: <see cref="FitTabs"/>, because a window sized to its
   /// content resizes on every tab switch unless the tab control is sized to
   /// its tallest tab first; and <see cref="FramedDialog"/>, because
   /// <see cref="FluentDialogWindow.UseFrame"/> is protected and the static
   /// prompt helpers build a bare window.
   /// </summary>
   internal static class DialogFields
   {
      /// <summary>A field: the caption (with its Alt key) above the editor, the blurb under it.</summary>
      public static FieldRow Field(string caption, FrameworkElement editor, string hint = null)
         => new() { Label = caption, Content = editor, Hint = hint };

      /// <summary>Adds the line under the editor after the row was built by a dialog's own <c>Label</c>.</summary>
      public static FieldRow WithHint(this FieldRow row, string hint)
      {
         row.Hint = hint;
         return row;
      }

      /// <summary>
      /// A check box in a run of them. A check box captions itself, so it needs
      /// no <see cref="FieldRow"/> around it; it is spaced by the small step
      /// rather than the field gap, so that four related boxes read as one group
      /// and not as four separate fields.
      /// </summary>
      public static CheckBox Check(CheckBox box)
      {
         box.Margin = new Thickness(0, 0, 0, DesignTokens.Space.Sm);
         return box;
      }

      /// <summary>A caption with no editor of its own - the heading over a run of check boxes.</summary>
      public static TextBlock Caption(string text)
      {
         var block = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Xs) };
         block.SetResourceReference(FrameworkElement.StyleProperty, "TextCaption");
         return block;
      }

      /// <summary>A sentence under a control saying what the setting actually does.</summary>
      public static TextBlock Note(string text)
      {
         var block = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
         block.SetResourceReference(FrameworkElement.StyleProperty, "TextCaption");
         return block;
      }

      /// <summary>Body text in the ramp - a dialog's explanation, a status line.</summary>
      public static TextBlock Body(string text)
      {
         var block = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, DesignTokens.Space.Md) };
         block.SetResourceReference(FrameworkElement.StyleProperty, "TextBody");
         return block;
      }

      /// <summary>The token hairline between two groups of fields.</summary>
      public static Border Separator()
      {
         var divider = new Border { Height = 1, Margin = new Thickness(0, DesignTokens.Space.Xs, 0, DesignTokens.Space.Lg) };
         divider.SetResourceReference(Border.BackgroundProperty, "AppDividerBrush");
         return divider;
      }

      /// <summary>The stack a tab's fields go in, set off from the tab strip by one step.</summary>
      public static StackPanel TabPanel() => new() { Margin = new Thickness(0, DesignTokens.Space.Md, 0, 0) };

      /// <summary>A tab's content: the fields, scrolling when the tab is taller than the dialog allows.</summary>
      public static ScrollViewer Scroll(UIElement content) => new()
      {
         Content = content,
         VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
         HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
         Focusable = false
      };

      /// <summary>A collapsed notice, shown by <see cref="Show"/> when there is something to say.</summary>
      public static InlineNotice Notice() => new() { Visibility = Visibility.Collapsed };

      public static void Show(this InlineNotice notice, StatusLevel level, string text)
      {
         notice.Level = level;
         notice.Text = text;
         notice.Visibility = Visibility.Visible;
      }

      public static void Hide(this InlineNotice notice) => notice.Visibility = Visibility.Collapsed;

      /// <summary>
      /// Puts a message on the field it is about, brings the tab that holds the
      /// field to the front and puts the keyboard in the editor - so a save that
      /// stops on a bad number lands the administrator on the number, where a
      /// message box left them on the Save button with a sentence to remember.
      /// </summary>
      public static void ShowError(FieldRow row, string error)
      {
         row.Error = error;

         for (DependencyObject current = row; current != null; current = LogicalTreeHelper.GetParent(current))
         {
            if (current is TabItem tab && tab.Parent is TabControl tabs)
            {
               tabs.SelectedItem = tab;
               break;
            }
         }

         if (row.Content is not UIElement editor)
            return;

         // After layout: a tab just brought to the front has no visual yet.
         row.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
         {
            if (editor is Control control && control.Focusable)
               control.Focus();
            else
               editor.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
         }));
      }

      public static void ClearErrors(params FieldRow[] rows)
      {
         foreach (FieldRow row in rows)
            row.Error = null;
      }

      /// <summary>
      /// Sizes a tab control to its tallest tab, within what the dialog may be.
      /// A window sized to its content takes the height of the tab on screen,
      /// and would grow and shrink with every click on the strip; measured once
      /// here, every tab gets the same height and the shorter ones scroll nothing.
      /// The ceiling comes from <see cref="DialogLayout"/>, past which the tallest
      /// tab scrolls inside its own viewer rather than the dialog's.
      /// </summary>
      public static void FitTabs(TabControl tabs, double contentWidth)
      {
         var available = new Size(contentWidth, double.PositiveInfinity);

         // Each tab's content is measured on its own rather than through the tab
         // control. Selecting a tab and measuring the control does not work here:
         // the control is not in a tree yet, and the selected item reaches its
         // content presenter on a later layout pass, so every measure reports the
         // tab that was showing before - which is how the route editor's address
         // list came out a third short and scrolled out of sight. The contents
         // are ordinary elements this file built, and measuring one applies its
         // template and the templates of the scaffold controls inside it.
         double tallest = 0;
         foreach (object item in tabs.Items)
         {
            if (item is TabItem { Content: FrameworkElement content })
            {
               content.Measure(available);
               tallest = Math.Max(tallest, content.DesiredSize.Height);
            }
         }

         // The strip above the content, taken from the control rather than
         // guessed: one row of headers plus whatever the theme puts around them.
         tabs.Measure(available);
         double strip = Math.Max(0, tabs.DesiredSize.Height - TallestSelectedContent_(tabs, available));

         tabs.Height = DialogLayout.TabControlHeight(tallest + strip,
            DialogLayout.MaxWindowHeight(SystemParameters.WorkArea.Height));
      }

      private static double TallestSelectedContent_(TabControl tabs, Size available)
      {
         if (tabs.SelectedItem is TabItem { Content: FrameworkElement selected })
         {
            selected.Measure(available);
            return selected.DesiredSize.Height;
         }

         return 0;
      }

      /// <summary>
      /// Gives a tab control the tallest height the dialog allows, without
      /// measuring anything. For the two big editors, whose tabs hold whole
      /// embedded views - a grid of fetch accounts, a rules editor, an app
      /// password panel - <see cref="FitTabs"/> would realise every one of the
      /// twelve at construction to settle a height that the tallest of them
      /// exceeds anyway. The window still never resizes on a tab switch, which
      /// is the reason for sizing the strip at all; the tabs that are shorter
      /// than the ceiling simply have room under them.
      /// </summary>
      public static void FillTabs(TabControl tabs)
         => tabs.Height = DialogLayout.TabControlHeight(double.PositiveInfinity, DialogLayout.MaxWindowHeight(SystemParameters.WorkArea.Height));

      /// <summary>The width the body of a dialog gets: the dialog's width less the frame's inset on both sides.</summary>
      public static double BodyWidth(double dialogWidth) => dialogWidth - 2 * DesignTokens.Space.Xl;

      public static ComboBoxItem Combo(string text, int value) => new() { Content = text, Tag = value };

      /// <summary>Selects the item tagged with the value; when none is, the first item if <paramref name="orFirst"/>.</summary>
      public static void SelectCombo(ComboBox combo, int value, bool orFirst = false)
      {
         foreach (ComboBoxItem item in combo.Items)
         {
            if ((int)item.Tag == value)
            {
               combo.SelectedItem = item;
               return;
            }
         }

         if (orFirst && combo.Items.Count > 0)
            combo.SelectedIndex = 0;
      }

      public static int ComboValue(ComboBox combo, int fallback = 0)
         => combo.SelectedItem is ComboBoxItem item ? (int)item.Tag : fallback;

      /// <summary>
      /// One line of text, asked for in a framed dialog. Null when cancelled. The
      /// caption names the box to a screen reader through the field row, which
      /// is what the two hand-built prompts this replaces had to do by hand.
      /// </summary>
      public static string PromptText(Window owner, string title, string caption, string initial = "")
      {
         var dialog = new FramedDialog { Owner = owner, Title = title };
         var box = new Wpf.Ui.Controls.TextBox { Text = initial ?? "" };
         string result = null;

         var ok = new Wpf.Ui.Controls.Button { Content = L("OK"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         ok.Click += (s, e) => { result = box.Text; dialog.DialogResult = true; dialog.Close(); };
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => dialog.Close();

         dialog.Frame(title, Field(caption, box), ok, cancel, width: 420);
         box.Loaded += (s, e) => box.SelectAll();
         return dialog.ShowDialog() == true ? result : null;
      }
   }

   /// <summary>
   /// A dialog whose content is put together outside a subclass - the prompt
   /// helpers - reaching the base class's frame through a public method.
   /// </summary>
   internal sealed class FramedDialog : FluentDialogWindow
   {
      public DialogFrame Frame(string heading, UIElement body, Button primary, Button secondary,
                               string description = null, double width = DesignTokens.Dialog.DefaultWidth)
         => UseFrame(heading, body, primary, secondary, description, width);
   }
}
