// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SQLScriptCreator.Statements
{
   class DropColumnObjects : IStatement
   {
      public string TableName {get;set;}
      public string ColumnName {get;set;}
   }
}
