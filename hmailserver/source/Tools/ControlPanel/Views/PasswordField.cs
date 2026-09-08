// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows.Controls;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// The password box every page uses (a PasswordField, so that the name is not the base class's), and the fix for issue #156.
   ///
   /// WPF-UI's PasswordBox is a TextBox that shows mask characters and keeps the
   /// real text in Password. On every change it rebuilds the masked text and then
   /// restores the caret to the index it read at the start of its change handler.
   /// That index is right when the caret has already moved past the keystroke
   /// when TextChanged fires - which is the ordinary keyboard path - and wrong on
   /// the paths where it has not: an IME committing a composition, and some
   /// layouts on Windows Server 2016. There the restored caret is the position
   /// BEFORE the keystroke, the next character is inserted one place too early,
   /// and 12345678 comes out as 18765432: the first character stays and every
   /// later one is pushed in at index 1. Revealed text was never affected, since
   /// that path does not rebuild anything - which is exactly what the report
   /// described.
   ///
   /// The change itself says where the caret belongs: after what was inserted,
   /// or at what was removed. This override applies that after the base class
   /// has done its work, on the outermost call only - the base re-enters
   /// OnTextChanged when it writes the mask text, and that inner change is a
   /// whole-text replacement that says nothing about where the user typed.
   /// </summary>
   public class PasswordField : Wpf.Ui.Controls.PasswordBox
   {
      private int depth_;

      protected override void OnTextChanged(TextChangedEventArgs e)
      {
         depth_++;
         try
         {
            base.OnTextChanged(e);
         }
         finally
         {
            depth_--;
         }

         if (depth_ != 0 || !IsKeyboardFocusWithin || e.Changes == null || e.Changes.Count == 0)
            return;

         // A single edit reports one change; a paste over a selection reports a
         // removal and an insertion at the same offset. The caret follows the
         // last one, which is where the user's edit ended.
         TextChange last = null;
         foreach (TextChange change in e.Changes)
            last = change;
         if (last == null)
            return;

         int caret = last.Offset + last.AddedLength;
         if (caret < 0)
            caret = 0;
         if (caret > Text.Length)
            caret = Text.Length;
         if (CaretIndex != caret)
            CaretIndex = caret;
      }
   }
}
