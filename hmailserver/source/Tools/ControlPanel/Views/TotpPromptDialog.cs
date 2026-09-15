// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Windows;
using hMailServer.ControlPanel.Services;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Views
{
   /// <summary>
   /// Prompts for a 6-digit two-factor verification code at login. On the
   /// standard frame: one field, OK takes Enter, Cancel takes Escape, and the
   /// keyboard is in the code box when the dialog opens.
   /// </summary>
   public class TotpPromptDialog : FluentDialogWindow
   {
      private readonly Wpf.Ui.Controls.TextBox code_ = new()
      {
         // 28 is the ramp's Title rung: six digits read from a phone are typed
         // one at a time and checked by eye, and the setup dialog shows the same.
         FontSize = 28,
         FontFamily = new System.Windows.Media.FontFamily(Typography.MonoFontFamily),
         MaxLength = 6,
         Width = 200,
         PlaceholderText = "000000",
         HorizontalAlignment = HorizontalAlignment.Left,
         HorizontalContentAlignment = HorizontalAlignment.Center
      };

      public string Code => code_.Text.Trim();

      public TotpPromptDialog(Window owner)
      {
         Owner = owner;
         Title = L("Two-factor authentication");

         var ok = new Wpf.Ui.Controls.Button { Content = L("OK"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, MinWidth = 88 };
         ok.Click += (s, e) => { DialogResult = true; Close(); };
         var cancel = new Wpf.Ui.Controls.Button { Content = L("Cancel"), MinWidth = 88 };
         cancel.Click += (s, e) => { DialogResult = false; Close(); };

         UseFrame(L("Two-factor authentication"),
            DialogFields.Field(L("Enter the 6-digit code from your authenticator app:"), code_),
            ok, cancel, width: 400);
      }
   }
}
