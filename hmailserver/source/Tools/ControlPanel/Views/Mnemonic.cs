// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using hMailServer.ControlPanel.Services;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Alt-key access mnemonics for the captions that are not buttons. A button or
   /// a check box underlines its own letter and registers the key through its
   /// template; a text box has no caption of its own, and the TextBlock above it
   /// is just text. This gives that TextBlock the same behaviour a Label's Target
   /// has: the marked letter is underlined, Alt+letter moves focus to the editor,
   /// and UI Automation is told the key. (The WPF runtime shipped with .NET 10 does
   /// not yet surface AutomationProperties.AccessKey through its text box peer -
   /// checked in isolation, the peer returns empty even on a plain Button - so a
   /// screen reader hears the key on buttons and check boxes today and on editors
   /// when the peer catches up with the source, which already reads it.)
   ///
   /// From XAML:  &lt;TextBlock Text="_Name" local:Mnemonic.Target="{Binding ElementName=NameBox}" /&gt;
   /// From code:  Mnemonic.Apply(block, "_Name", nameBox)
   ///
   /// Registration goes through the same AccessKeyManager the templates use, so
   /// scope and precedence are WPF's: a dialog's keys stay inside the dialog, and a
   /// key that is not visible is not a target.
   /// </summary>
   public static class Mnemonic
   {
      public static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
         "Target", typeof(FrameworkElement), typeof(Mnemonic), new PropertyMetadata(null, OnTargetChanged));

      // The caption as written, kept so a second Apply on the same block (a
      // re-bound target, a rebuilt page) starts from the marker and not from the
      // already-rendered text.
      private static readonly DependencyProperty CaptionProperty = DependencyProperty.RegisterAttached(
         "Caption", typeof(string), typeof(Mnemonic), new PropertyMetadata(null));

      public static FrameworkElement GetTarget(DependencyObject element) => (FrameworkElement)element.GetValue(TargetProperty);

      public static void SetTarget(DependencyObject element, FrameworkElement value) => element.SetValue(TargetProperty, value);

      private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
      {
         if (d is not TextBlock block || e.NewValue is not FrameworkElement target)
            return;

         string caption = (string)block.GetValue(CaptionProperty) ?? block.Text;
         Apply(block, caption, target);
      }

      /// <summary>
      /// Renders <paramref name="caption"/> into <paramref name="block"/> with the
      /// mnemonic letter underlined and Alt+letter focusing <paramref name="target"/>.
      /// A caption without a marker, or no target, renders as plain text.
      /// </summary>
      public static TextBlock Apply(TextBlock block, string caption, FrameworkElement target)
      {
         block.SetValue(CaptionProperty, caption);
         var (text, keyIndex) = MnemonicText.Parse(caption);

         block.Inlines.Clear();
         if (keyIndex < 0 || target == null)
         {
            block.Text = text;
            return block;
         }

         if (keyIndex > 0)
            block.Inlines.Add(new Run(text.Substring(0, keyIndex)));
         block.Inlines.Add(new Run(text.Substring(keyIndex, 1)) { TextDecorations = TextDecorations.Underline });
         if (keyIndex + 1 < text.Length)
            block.Inlines.Add(new Run(text.Substring(keyIndex + 1)));

         string key = char.ToUpperInvariant(text[keyIndex]).ToString();
         Register(key, target);
         AutomationProperties.SetAccessKey(target, "Alt+" + key);
         return block;
      }

      // Set on an editor once it answers the access-key manager, so a second Apply
      // on a rebuilt caption does not stack handlers.
      private static readonly DependencyProperty AnswersProperty = DependencyProperty.RegisterAttached(
         "Answers", typeof(bool), typeof(Mnemonic), new PropertyMetadata(false));

      private static void Register(string key, FrameworkElement target)
      {
         AccessKeyManager.Register(key, target);

         // The manager asks each registered element who the target is by raising
         // AccessKeyPressed on it, and an element that does not answer is not a
         // target: a Label answers with its Target, a button with itself, a bare
         // text box with nothing. So the editor answers for itself, and the
         // manager then raises AccessKey on it, whose default is to take focus.
         if ((bool)target.GetValue(AnswersProperty))
            return;

         target.SetValue(AnswersProperty, true);
         target.AddHandler(AccessKeyManager.AccessKeyPressedEvent, new AccessKeyPressedEventHandler((sender, e) =>
         {
            if (!e.Handled && e.Scope == null && e.Target == null)
               e.Target = target;
         }));
      }
   }
}
