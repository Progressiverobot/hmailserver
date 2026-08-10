namespace ImportTool.TextImport
{
   partial class ucTextSelect
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
         this.labelFile = new System.Windows.Forms.Label();
         this.textFile = new System.Windows.Forms.TextBox();
         this.buttonBrowse = new System.Windows.Forms.Button();
         this.labelDomain = new System.Windows.Forms.Label();
         this.comboDomains = new System.Windows.Forms.ComboBox();
         this.labelFormatHelp = new System.Windows.Forms.Label();
         this.SuspendLayout();
         //
         // labelFile
         //
         this.labelFile.AutoSize = true;
         this.labelFile.Location = new System.Drawing.Point(8, 12);
         this.labelFile.Name = "labelFile";
         this.labelFile.Size = new System.Drawing.Size(50, 13);
         this.labelFile.TabIndex = 0;
         this.labelFile.Text = "Text file:";
         //
         // textFile
         //
         this.textFile.Location = new System.Drawing.Point(96, 9);
         this.textFile.Name = "textFile";
         this.textFile.Size = new System.Drawing.Size(304, 20);
         this.textFile.TabIndex = 1;
         //
         // buttonBrowse
         //
         this.buttonBrowse.Location = new System.Drawing.Point(406, 7);
         this.buttonBrowse.Name = "buttonBrowse";
         this.buttonBrowse.Size = new System.Drawing.Size(32, 23);
         this.buttonBrowse.TabIndex = 2;
         this.buttonBrowse.Text = "...";
         this.buttonBrowse.UseVisualStyleBackColor = true;
         this.buttonBrowse.Click += new System.EventHandler(this.buttonBrowse_Click);
         //
         // labelDomain
         //
         this.labelDomain.AutoSize = true;
         this.labelDomain.Location = new System.Drawing.Point(8, 44);
         this.labelDomain.Name = "labelDomain";
         this.labelDomain.Size = new System.Drawing.Size(46, 13);
         this.labelDomain.TabIndex = 3;
         this.labelDomain.Text = "Domain:";
         //
         // comboDomains
         //
         this.comboDomains.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
         this.comboDomains.FormattingEnabled = true;
         this.comboDomains.Location = new System.Drawing.Point(96, 41);
         this.comboDomains.Name = "comboDomains";
         this.comboDomains.Size = new System.Drawing.Size(200, 21);
         this.comboDomains.TabIndex = 4;
         //
         // labelFormatHelp
         //
         this.labelFormatHelp.Location = new System.Drawing.Point(8, 80);
         this.labelFormatHelp.Name = "labelFormatHelp";
         this.labelFormatHelp.Size = new System.Drawing.Size(440, 100);
         this.labelFormatHelp.TabIndex = 5;
         this.labelFormatHelp.Text = "The file should contain one account per line in the format:\r\n\r\naccountname,pas" +
             "sword,maxsize-in-MB\r\n\r\nLines starting with # are ignored. Accounts that alread" +
             "y exist in the domain are updated with the password and max size from the file.";
         //
         // ucTextSelect
         //
         this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
         this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
         this.Controls.Add(this.labelFormatHelp);
         this.Controls.Add(this.comboDomains);
         this.Controls.Add(this.labelDomain);
         this.Controls.Add(this.buttonBrowse);
         this.Controls.Add(this.textFile);
         this.Controls.Add(this.labelFile);
         this.Name = "ucTextSelect";
         this.Size = new System.Drawing.Size(472, 195);
         this.ResumeLayout(false);
         this.PerformLayout();

      }

      #endregion

      private System.Windows.Forms.Label labelFile;
      private System.Windows.Forms.TextBox textFile;
      private System.Windows.Forms.Button buttonBrowse;
      private System.Windows.Forms.Label labelDomain;
      private System.Windows.Forms.ComboBox comboDomains;
      private System.Windows.Forms.Label labelFormatHelp;
   }
}
