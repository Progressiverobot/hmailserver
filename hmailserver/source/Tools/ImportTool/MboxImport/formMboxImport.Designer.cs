namespace ImportTool.MboxImport
{
   partial class formMboxImport
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
         this.wizard = new hMailServer.Shared.ucWizard();
         this.SuspendLayout();
         //
         // wizard
         //
         this.wizard.Dock = System.Windows.Forms.DockStyle.Fill;
         this.wizard.Location = new System.Drawing.Point(0, 0);
         this.wizard.Name = "wizard";
         this.wizard.Size = new System.Drawing.Size(484, 311);
         this.wizard.TabIndex = 0;
         this.wizard.PageChanged += new hMailServer.Shared.ucWizard.PageChangedEventHandler(this.wizard_PageChanged);
         this.wizard.Cancel += new System.EventHandler(this.wizard_OnCancel);
         //
         // formMboxImport
         //
         this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
         this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
         this.ClientSize = new System.Drawing.Size(484, 311);
         this.Controls.Add(this.wizard);
         this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
         this.MaximizeBox = false;
         this.MinimizeBox = false;
         this.Name = "formMboxImport";
         this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
         this.Text = "Import mbox files";
         this.Shown += new System.EventHandler(this.formMboxImport_Shown);
         this.ResumeLayout(false);

      }

      #endregion

      private hMailServer.Shared.ucWizard wizard;
   }
}
