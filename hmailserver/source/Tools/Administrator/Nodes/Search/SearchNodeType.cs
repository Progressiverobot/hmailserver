// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace hMailServer.Administrator.Nodes
{
   public class SearchNodeType : ISearchNodeCriteria
   {
      private Type _type;

      public SearchNodeType(Type type)
      {
         _type = type;
      }

      public bool IsMatch(TreeNode node)
      {
         INode internalNode = node.Tag as INode;

         if (internalNode.GetType() == _type)
            return true;
         else
            return false;
      }


   }
}
