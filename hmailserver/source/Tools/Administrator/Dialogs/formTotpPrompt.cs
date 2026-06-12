// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Prompt dialog for the two-factor authentication verification code.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace hMailServer.Administrator.Utilities
{
   internal class formTotpPrompt : Form
   {
      private readonly TextBox textCode;

      public string Code
      {
         get { return textCode.Text.Trim(); }
      }

      public formTotpPrompt()
      {
         Text = "Two-factor authentication";
         FormBorderStyle = FormBorderStyle.FixedDialog;
         MaximizeBox = false;
         MinimizeBox = false;
         ShowInTaskbar = false;
         StartPosition = FormStartPosition.CenterParent;
         ClientSize = new Size(320, 110);

         var labelInfo = new Label
         {
            Text = "Enter the 6-digit code from your authenticator app:",
            Location = new Point(12, 12),
            AutoSize = true
         };

         textCode = new TextBox
         {
            Location = new Point(15, 38),
            Width = 120,
            MaxLength = 6,
            Font = new Font(FontFamily.GenericMonospace, 12f, FontStyle.Bold)
         };

         var buttonOk = new Button
         {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(151, 75),
            Size = new Size(75, 25)
         };

         var buttonCancel = new Button
         {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(232, 75),
            Size = new Size(75, 25)
         };

         Controls.Add(labelInfo);
         Controls.Add(textCode);
         Controls.Add(buttonOk);
         Controls.Add(buttonCancel);

         AcceptButton = buttonOk;
         CancelButton = buttonCancel;
      }
   }
}
