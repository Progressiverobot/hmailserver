// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Windows.Forms;
using hMailServer.Shared;

namespace hMailServer.Administrator.Dialogs
{
   public partial class formUserAccounts : Form
   {
      public formUserAccounts()
      {
         InitializeComponent();

         new TabOrderManager(this).SetTabOrder(TabOrderManager.TabScheme.AcrossFirst);
         Strings.Localize(this);
      }
   }
}