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
    class APIParameter
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool Input { get; set; }
    }
}
