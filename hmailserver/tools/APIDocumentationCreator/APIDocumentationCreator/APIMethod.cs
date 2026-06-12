// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace APIDocumentationCreator
{
    class APIMethod
    {
        public string Name { get; set; }
        public string HelpString { get; set; }

        public List<APIParameter> Parameters {get;set;}

        public APIMethod()
        {
            Parameters = new List<APIParameter>();
        }
    }
}
