// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Windows.Forms;
using hMailServer.Shared;


namespace hMailServer.Administrator.Dialogs
{
   public partial class formBlockedAttachment : Form
   {

      public formBlockedAttachment()
      {
         InitializeComponent();

         new TabOrderManager(this).SetTabOrder(TabOrderManager.TabScheme.AcrossFirst);
         Strings.Localize(this);
      }

      public void LoadProperties(hMailServer.BlockedAttachment ba)
      {
         textDescription.Text = ba.Description;
         textWildcard.Text = ba.Wildcard;
      }

      public void SaveProperties(hMailServer.BlockedAttachment ba)
      {
         ba.Description = textDescription.Text;
         ba.Wildcard = textWildcard.Text;
      }

   }
}