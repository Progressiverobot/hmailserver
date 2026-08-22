// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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
