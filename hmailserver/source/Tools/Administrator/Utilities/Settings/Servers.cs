// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;

namespace hMailServer.Administrator.Utilities.Settings
{
    public class Servers
    {
        private List<Server> listServers;

        public Servers()
        {
            listServers = new List<Server>();
        }

        public List<Server> List
        {
            get
            {
                return listServers;
            }
            set
            {
                listServers = value;
            }
        }


    }
}
