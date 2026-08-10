namespace ImportTool.MboxImport
{
   partial class ucMboxAccount
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
         this.labelDomain = new System.Windows.Forms.Label();
         this.comboDomains = new System.Windows.Forms.ComboBox();
         this.listAccounts = new System.Windows.Forms.ListView();
         this.columnAccount = new System.Windows.Forms.ColumnHeader();
         this.SuspendLayout();
         //
         // labelDomain
         //
         this.labelDomain.AutoSize = true;
         this.labelDomain.Location = new System.Drawing.Point(8, 12);
         this.labelDomain.Name = "labelDomain";
         this.labelDomain.Size = new System.Drawing.Size(46, 13);
         this.labelDomain.TabIndex = 0;
         this.labelDomain.Text = "Domain:";
         //
         // comboDomains
         //
         this.comboDomains.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
         this.comboDomains.FormattingEnabled = true;
         this.comboDomains.Location = new System.Drawing.Point(64, 9);
         this.comboDomains.Name = "comboDomains";
         this.comboDomains.Size = new System.Drawing.Size(200, 21);
         this.comboDomains.TabIndex = 1;
         this.comboDomains.SelectedIndexChanged += new System.EventHandler(this.comboDomains_SelectedIndexChanged);
         //
         // listAccounts
         //
         this.listAccounts.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.columnAccount});
         this.listAccounts.FullRowSelect = true;
         this.listAccounts.HideSelection = false;
         this.listAccounts.Location = new System.Drawing.Point(8, 40);
         this.listAccounts.MultiSelect = false;
         this.listAccounts.Name = "listAccounts";
         this.listAccounts.Size = new System.Drawing.Size(440, 144);
         this.listAccounts.Sorting = System.Windows.Forms.SortOrder.Ascending;
         this.listAccounts.TabIndex = 2;
         this.listAccounts.UseCompatibleStateImageBehavior = false;
         this.listAccounts.View = System.Windows.Forms.View.Details;
         //
         // columnAccount
         //
         this.columnAccount.Text = "Account";
         this.columnAccount.Width = 420;
         //
         // ucMboxAccount
         //
         this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
         this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
         this.Controls.Add(this.listAccounts);
         this.Controls.Add(this.comboDomains);
         this.Controls.Add(this.labelDomain);
         this.Name = "ucMboxAccount";
         this.Size = new System.Drawing.Size(472, 195);
         this.ResumeLayout(false);
         this.PerformLayout();

      }

      #endregion

      private System.Windows.Forms.Label labelDomain;
      private System.Windows.Forms.ComboBox comboDomains;
      private System.Windows.Forms.ListView listAccounts;
      private System.Windows.Forms.ColumnHeader columnAccount;
   }
}
