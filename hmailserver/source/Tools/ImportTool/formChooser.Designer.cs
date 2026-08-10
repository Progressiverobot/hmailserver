namespace ImportTool
{
   partial class formChooser
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

      #region Windows Form Designer generated code

      private void InitializeComponent()
      {
         this.labelIntro = new System.Windows.Forms.Label();
         this.buttonTextImport = new System.Windows.Forms.Button();
         this.labelTextImportHelp = new System.Windows.Forms.Label();
         this.buttonMboxImport = new System.Windows.Forms.Button();
         this.labelMboxImportHelp = new System.Windows.Forms.Label();
         this.buttonClose = new System.Windows.Forms.Button();
         this.SuspendLayout();
         //
         // labelIntro
         //
         this.labelIntro.Location = new System.Drawing.Point(12, 12);
         this.labelIntro.Name = "labelIntro";
         this.labelIntro.Size = new System.Drawing.Size(420, 32);
         this.labelIntro.TabIndex = 0;
         this.labelIntro.Text = "This tool imports data into the hMailServer installation on this machine. Select " +
             "what you want to import.";
         //
         // buttonTextImport
         //
         this.buttonTextImport.Location = new System.Drawing.Point(16, 56);
         this.buttonTextImport.Name = "buttonTextImport";
         this.buttonTextImport.Size = new System.Drawing.Size(200, 28);
         this.buttonTextImport.TabIndex = 1;
         this.buttonTextImport.Text = "Accounts from a text file...";
         this.buttonTextImport.UseVisualStyleBackColor = true;
         this.buttonTextImport.Click += new System.EventHandler(this.buttonTextImport_Click);
         //
         // labelTextImportHelp
         //
         this.labelTextImportHelp.Location = new System.Drawing.Point(32, 88);
         this.labelTextImportHelp.Name = "labelTextImportHelp";
         this.labelTextImportHelp.Size = new System.Drawing.Size(400, 32);
         this.labelTextImportHelp.TabIndex = 2;
         this.labelTextImportHelp.Text = "Creates or updates accounts in a domain from a comma-separated text file (account" +
             " name, password, max size in MB).";
         //
         // buttonMboxImport
         //
         this.buttonMboxImport.Location = new System.Drawing.Point(16, 132);
         this.buttonMboxImport.Name = "buttonMboxImport";
         this.buttonMboxImport.Size = new System.Drawing.Size(200, 28);
         this.buttonMboxImport.TabIndex = 3;
         this.buttonMboxImport.Text = "Messages from mbox files...";
         this.buttonMboxImport.UseVisualStyleBackColor = true;
         this.buttonMboxImport.Click += new System.EventHandler(this.buttonMboxImport_Click);
         //
         // labelMboxImportHelp
         //
         this.labelMboxImportHelp.Location = new System.Drawing.Point(32, 164);
         this.labelMboxImportHelp.Name = "labelMboxImportHelp";
         this.labelMboxImportHelp.Size = new System.Drawing.Size(400, 32);
         this.labelMboxImportHelp.TabIndex = 4;
         this.labelMboxImportHelp.Text = "Imports the messages in a folder of mbox files into an account, one IMAP folder p" +
             "er mbox file.";
         //
         // buttonClose
         //
         this.buttonClose.DialogResult = System.Windows.Forms.DialogResult.Cancel;
         this.buttonClose.Location = new System.Drawing.Point(357, 208);
         this.buttonClose.Name = "buttonClose";
         this.buttonClose.Size = new System.Drawing.Size(75, 23);
         this.buttonClose.TabIndex = 5;
         this.buttonClose.Text = "Close";
         this.buttonClose.UseVisualStyleBackColor = true;
         this.buttonClose.Click += new System.EventHandler(this.buttonClose_Click);
         //
         // formChooser
         //
         this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
         this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
         this.CancelButton = this.buttonClose;
         this.ClientSize = new System.Drawing.Size(444, 243);
         this.Controls.Add(this.buttonClose);
         this.Controls.Add(this.labelMboxImportHelp);
         this.Controls.Add(this.buttonMboxImport);
         this.Controls.Add(this.labelTextImportHelp);
         this.Controls.Add(this.buttonTextImport);
         this.Controls.Add(this.labelIntro);
         this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
         this.MaximizeBox = false;
         this.MinimizeBox = false;
         this.Name = "formChooser";
         this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
         this.Text = "hMailServer Import Tool";
         this.ResumeLayout(false);

      }

      #endregion

      private System.Windows.Forms.Label labelIntro;
      private System.Windows.Forms.Button buttonTextImport;
      private System.Windows.Forms.Label labelTextImportHelp;
      private System.Windows.Forms.Button buttonMboxImport;
      private System.Windows.Forms.Label labelMboxImportHelp;
      private System.Windows.Forms.Button buttonClose;
   }
}
