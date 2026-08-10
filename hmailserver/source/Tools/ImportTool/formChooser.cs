// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Windows.Forms;
using ImportTool.MboxImport;
using ImportTool.TextImport;

namespace ImportTool
{
   public partial class formChooser : Form
   {
      public formChooser()
      {
         InitializeComponent();
      }

      private void buttonTextImport_Click(object sender, EventArgs e)
      {
         using (var form = new formTextImport())
         {
            Hide();
            form.ShowDialog(this);
            Show();
         }
      }

      private void buttonMboxImport_Click(object sender, EventArgs e)
      {
         using (var form = new formMboxImport())
         {
            Hide();
            form.ShowDialog(this);
            Show();
         }
      }

      private void buttonClose_Click(object sender, EventArgs e)
      {
         Close();
      }
   }
}
