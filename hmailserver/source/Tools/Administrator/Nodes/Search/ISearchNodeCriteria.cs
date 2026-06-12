// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace hMailServer.Administrator.Nodes
{
   public interface ISearchNodeCriteria
   {
      bool IsMatch(TreeNode node);
   }
}
