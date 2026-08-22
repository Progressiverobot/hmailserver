// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SQLScriptCreator.Statements
{
   class RenameColumn : IStatement
   {
      public string Table { get; set; }
      public string OldName { get; set; }
      public string NewName { get; set; }
      
      public string DataType { get; set; }
      public bool Nullable { get; set; }

      public string Default { get; set; }
   }
}
