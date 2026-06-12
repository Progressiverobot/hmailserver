// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SQLScriptCreator.Statements
{
   class RawSQL : IStatement
   {
      public string Statement { get; set; }
      public List<string> Engines { get; set; }

      public RawSQL()
      {
         Engines = new List<string>();
      }
   }
}
