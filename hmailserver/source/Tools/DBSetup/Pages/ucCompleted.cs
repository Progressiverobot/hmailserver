// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using hMailServer.Shared;

namespace DBSetup.Pages
{
   public partial class ucCompleted : UserControl, IWizardPage
   {
      private Dictionary<string, string> _state;

      public ucCompleted()
      {
         InitializeComponent();
      }

      public string Title
      {
         get
         {
            return "Completed";
         }
      }

      public void OnShowPage(Dictionary<string, string> state)
      {
         _state = state;
      }

      public bool OnLeavePage(bool next)
      {


         return true;
      }
   }
}
