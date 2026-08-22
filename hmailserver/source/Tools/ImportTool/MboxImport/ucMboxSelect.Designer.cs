// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace ImportTool.MboxImport
{
   partial class ucMboxSelect
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
         this.buttonSelectDirectory = new System.Windows.Forms.Button();
         this.listFiles = new System.Windows.Forms.ListView();
         this.columnFile = new System.Windows.Forms.ColumnHeader();
         this.columnFolder = new System.Windows.Forms.ColumnHeader();
         this.SuspendLayout();
         //
         // buttonSelectDirectory
         //
         this.buttonSelectDirectory.Location = new System.Drawing.Point(8, 8);
         this.buttonSelectDirectory.Name = "buttonSelectDirectory";
         this.buttonSelectDirectory.Size = new System.Drawing.Size(121, 23);
         this.buttonSelectDirectory.TabIndex = 0;
         this.buttonSelectDirectory.Text = "&Select directory";
         this.buttonSelectDirectory.UseVisualStyleBackColor = true;
         this.buttonSelectDirectory.Click += new System.EventHandler(this.buttonSelectDirectory_Click);
         //
         // listFiles
         //
         this.listFiles.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.columnFile,
            this.columnFolder});
         this.listFiles.FullRowSelect = true;
         this.listFiles.Location = new System.Drawing.Point(8, 40);
         this.listFiles.Name = "listFiles";
         this.listFiles.Size = new System.Drawing.Size(440, 144);
         this.listFiles.TabIndex = 1;
         this.listFiles.UseCompatibleStateImageBehavior = false;
         this.listFiles.View = System.Windows.Forms.View.Details;
         //
         // columnFile
         //
         this.columnFile.Text = "File";
         this.columnFile.Width = 290;
         //
         // columnFolder
         //
         this.columnFolder.Text = "IMAP folder";
         this.columnFolder.Width = 130;
         //
         // ucMboxSelect
         //
         this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
         this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
         this.Controls.Add(this.listFiles);
         this.Controls.Add(this.buttonSelectDirectory);
         this.Name = "ucMboxSelect";
         this.Size = new System.Drawing.Size(472, 195);
         this.ResumeLayout(false);

      }

      #endregion

      private System.Windows.Forms.Button buttonSelectDirectory;
      private System.Windows.Forms.ListView listFiles;
      private System.Windows.Forms.ColumnHeader columnFile;
      private System.Windows.Forms.ColumnHeader columnFolder;
   }
}
