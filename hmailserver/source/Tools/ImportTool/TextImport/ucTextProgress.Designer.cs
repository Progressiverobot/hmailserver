// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace ImportTool.TextImport
{
   partial class ucTextProgress
   {
      private System.ComponentModel.IContainer components = null;

      protected override void Dispose(bool disposing)
      {
         if (disposing && (components != null))
         {
            components.Dispose();
         }
         base.Dispose(disposing);
      }

      #region Component Designer generated code

      private void InitializeComponent()
      {
         this.labelProgress = new System.Windows.Forms.Label();
         this.textLog = new System.Windows.Forms.TextBox();
         this.SuspendLayout();
         //
         // labelProgress
         //
         this.labelProgress.AutoSize = true;
         this.labelProgress.Location = new System.Drawing.Point(8, 8);
         this.labelProgress.Name = "labelProgress";
         this.labelProgress.Size = new System.Drawing.Size(0, 13);
         this.labelProgress.TabIndex = 0;
         //
         // textLog
         //
         this.textLog.Location = new System.Drawing.Point(8, 28);
         this.textLog.Multiline = true;
         this.textLog.Name = "textLog";
         this.textLog.ReadOnly = true;
         this.textLog.ScrollBars = System.Windows.Forms.ScrollBars.Both;
         this.textLog.Size = new System.Drawing.Size(440, 152);
         this.textLog.TabIndex = 1;
         //
         // ucTextProgress
         //
         this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
         this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
         this.Controls.Add(this.textLog);
         this.Controls.Add(this.labelProgress);
         this.Name = "ucTextProgress";
         this.Size = new System.Drawing.Size(472, 195);
         this.ResumeLayout(false);
         this.PerformLayout();

      }

      #endregion

      private System.Windows.Forms.Label labelProgress;
      private System.Windows.Forms.TextBox textLog;
   }
}
