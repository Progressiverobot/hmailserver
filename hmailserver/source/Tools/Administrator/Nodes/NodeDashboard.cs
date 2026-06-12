// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace hMailServer.Administrator.Nodes
{
    class NodeDashboard : INode
    {
        public string Title
        {
            get
            {
                return "Dashboard";
            }
            set { }
        }

        public System.Drawing.Color ForeColor { get { return System.Drawing.SystemColors.WindowText; } set { } }

        public bool IsUserCreated
        {
            get { return false; }
        }

        public string Icon
        {
            get
            {
                return "speedometer.ico";
            }
        }

        public UserControl CreateControl()
        {
            return new ucDashboard();
        }

        public List<INode> SubNodes
        {
            get
            {
                List<INode> subNodes = new List<INode>();
                return subNodes;
            }
        }

        public ContextMenuStrip CreateContextMenu()
        {
            return null;
        }
    }
}
