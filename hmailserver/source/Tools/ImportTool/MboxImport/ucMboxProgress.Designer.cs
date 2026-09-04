// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace ImportTool.MboxImport
{
   partial class ucMboxProgress
   {
      private readonly System.ComponentModel.IContainer components = null;

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
         this.labelCurrentTask = new System.Windows.Forms.Label();
         this.labelFileProgress = new System.Windows.Forms.Label();
         this.progressFiles = new System.Windows.Forms.ProgressBar();
         this.labelCurrentFile = new System.Windows.Forms.Label();
         this.progressCurrentFile = new System.Windows.Forms.ProgressBar();
         this.textLog = new System.Windows.Forms.TextBox();
         this.SuspendLayout();
         //
         // labelCurrentTask
         //
         this.labelCurrentTask.Location = new System.Drawing.Point(8, 8);
         this.labelCurrentTask.Name = "labelCurrentTask";
         this.labelCurrentTask.Size = new System.Drawing.Size(440, 13);
         this.labelCurrentTask.TabIndex = 0;
         //
         // labelFileProgress
         //
         this.labelFileProgress.AutoSize = true;
         this.labelFileProgress.Location = new System.Drawing.Point(8, 28);
         this.labelFileProgress.Name = "labelFileProgress";
         this.labelFileProgress.Size = new System.Drawing.Size(66, 13);
         this.labelFileProgress.TabIndex = 1;
         this.labelFileProgress.Text = "File progress:";
         //
         // progressFiles
         //
         this.progressFiles.Location = new System.Drawing.Point(96, 26);
         this.progressFiles.Name = "progressFiles";
         this.progressFiles.Size = new System.Drawing.Size(352, 16);
         this.progressFiles.TabIndex = 2;
         //
         // labelCurrentFile
         //
         this.labelCurrentFile.AutoSize = true;
         this.labelCurrentFile.Location = new System.Drawing.Point(8, 50);
         this.labelCurrentFile.Name = "labelCurrentFile";
         this.labelCurrentFile.Size = new System.Drawing.Size(60, 13);
         this.labelCurrentFile.TabIndex = 3;
         this.labelCurrentFile.Text = "Current file:";
         //
         // progressCurrentFile
         //
         this.progressCurrentFile.Location = new System.Drawing.Point(96, 48);
         this.progressCurrentFile.Name = "progressCurrentFile";
         this.progressCurrentFile.Size = new System.Drawing.Size(352, 16);
         this.progressCurrentFile.TabIndex = 4;
         //
         // textLog
         //
         this.textLog.Location = new System.Drawing.Point(8, 76);
         this.textLog.Multiline = true;
         this.textLog.Name = "textLog";
         this.textLog.ReadOnly = true;
         this.textLog.ScrollBars = System.Windows.Forms.ScrollBars.Both;
         this.textLog.Size = new System.Drawing.Size(440, 108);
         this.textLog.TabIndex = 5;
         //
         // ucMboxProgress
         //
         this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
         this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
         this.Controls.Add(this.textLog);
         this.Controls.Add(this.progressCurrentFile);
         this.Controls.Add(this.labelCurrentFile);
         this.Controls.Add(this.progressFiles);
         this.Controls.Add(this.labelFileProgress);
         this.Controls.Add(this.labelCurrentTask);
         this.Name = "ucMboxProgress";
         this.Size = new System.Drawing.Size(472, 195);
         this.ResumeLayout(false);
         this.PerformLayout();

      }

      #endregion

      private System.Windows.Forms.Label labelCurrentTask;
      private System.Windows.Forms.Label labelFileProgress;
      private System.Windows.Forms.ProgressBar progressFiles;
      private System.Windows.Forms.Label labelCurrentFile;
      private System.Windows.Forms.ProgressBar progressCurrentFile;
      private System.Windows.Forms.TextBox textLog;
   }
}
