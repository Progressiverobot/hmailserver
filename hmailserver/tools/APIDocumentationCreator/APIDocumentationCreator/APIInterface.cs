// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace APIDocumentationCreator
{
    class APIInterface
    {
        public string Name { get; set; }
        public string HelpString { get; set; }
        public List<APIProperty> Properties {get;set;}
        public List<APIMethod> Methods { get; set; }


        public APIInterface()
        {
            Properties = new List<APIProperty>();
            Methods = new List<APIMethod>();
        }

        public APIProperty GetProperty(string name)
        {
            return Properties.FirstOrDefault(property => property.Name == name);
        }

    }
}
