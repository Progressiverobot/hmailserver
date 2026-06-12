// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Setup dialog for enabling/disabling two-factor authentication for
// the hMailServer Administrator login.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace hMailServer.Administrator.Utilities
{
   internal class formTotpSetup : Form
   {
      private readonly TextBox textSecret;
      private readonly TextBox textUri;
      private readonly TextBox textCode;
      private readonly Button buttonAction;
      private readonly Label labelStatus;

      private string pendingSecret;

      public formTotpSetup()
      {
         Text = "Two-factor authentication setup";
         FormBorderStyle = FormBorderStyle.FixedDialog;
         MaximizeBox = false;
         MinimizeBox = false;
         ShowInTaskbar = false;
         StartPosition = FormStartPosition.CenterParent;
         ClientSize = new Size(480, 250);

         labelStatus = new Label { Location = new Point(12, 12), AutoSize = true };

         var labelSecret = new Label { Text = "Secret (enter manually in your authenticator app):", Location = new Point(12, 44), AutoSize = true };
         textSecret = new TextBox { Location = new Point(15, 62), Width = 450, ReadOnly = true, Font = new Font(FontFamily.GenericMonospace, 9f) };

         var labelUri = new Label { Text = "Or add this otpauth:// URI as a QR code:", Location = new Point(12, 92), AutoSize = true };
         textUri = new TextBox { Location = new Point(15, 110), Width = 450, ReadOnly = true };

         var labelCode = new Label { Text = "Verification code from your authenticator app:", Location = new Point(12, 144), AutoSize = true };
         textCode = new TextBox { Location = new Point(15, 162), Width = 120, MaxLength = 6, Font = new Font(FontFamily.GenericMonospace, 11f, FontStyle.Bold) };

         buttonAction = new Button { Location = new Point(15, 205), Size = new Size(220, 28) };
         buttonAction.Click += buttonAction_Click;

         var buttonClose = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Location = new Point(390, 205), Size = new Size(75, 28) };

         Controls.Add(labelStatus);
         Controls.Add(labelSecret);
         Controls.Add(textSecret);
         Controls.Add(labelUri);
         Controls.Add(textUri);
         Controls.Add(labelCode);
         Controls.Add(textCode);
         Controls.Add(buttonAction);
         Controls.Add(buttonClose);

         CancelButton = buttonClose;

         RefreshState();
      }

      private void RefreshState()
      {
         if (TotpManager.IsConfigured())
         {
            labelStatus.Text = "Two-factor authentication is ENABLED. Enter a valid code to disable it.";
            textSecret.Text = "";
            textUri.Text = "";
            textSecret.Enabled = false;
            textUri.Enabled = false;
            buttonAction.Text = "Disable two-factor authentication";
            pendingSecret = null;
         }
         else
         {
            labelStatus.Text = "Two-factor authentication is DISABLED. Scan the secret below, then confirm with a code.";
            pendingSecret = Totp.GenerateSecret();
            textSecret.Text = pendingSecret;
            textUri.Text = Totp.BuildOtpAuthUri("hMailServer Administrator", pendingSecret);
            textSecret.Enabled = true;
            textUri.Enabled = true;
            buttonAction.Text = "Enable two-factor authentication";
         }

         textCode.Text = "";
      }

      private void buttonAction_Click(object sender, EventArgs e)
      {
         try
         {
            if (TotpManager.IsConfigured())
            {
               // Disabling requires a currently valid code.
               if (!Totp.VerifyCode(TotpManager.ReadSecret(), textCode.Text))
               {
                  MessageBox.Show("The verification code is incorrect.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                  return;
               }

               TotpManager.RemoveSecret();
               MessageBox.Show("Two-factor authentication has been disabled.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
               if (!Totp.VerifyCode(pendingSecret, textCode.Text))
               {
                  MessageBox.Show("The verification code is incorrect. Make sure your authenticator app is set up with the new secret.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                  return;
               }

               TotpManager.SaveSecret(pendingSecret);
               MessageBox.Show("Two-factor authentication has been enabled. The next connection will require a verification code.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            RefreshState();
         }
         catch (UnauthorizedAccessException)
         {
            MessageBox.Show("Changing two-factor authentication settings requires administrator rights. Restart hMailServer Administrator as an administrator and try again.",
               Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
         }
         catch (Exception ex)
         {
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
         }
      }
   }
}
