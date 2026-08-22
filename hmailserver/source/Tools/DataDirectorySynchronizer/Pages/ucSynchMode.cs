// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Windows.Forms;
using hMailServer.Shared;

namespace DataDirectorySynchronizer.Pages
{
    public partial class ucSynchMode : UserControl, IWizardPage
    {
        public ucSynchMode()
        {
            InitializeComponent();
        }


        public void OnShowPage(Dictionary<string, string> _state)
        {

        }

        public bool OnLeavePage(bool next)
        {
           Globals.Mode = radioImport.Checked ? Globals.ModeType.Import : Globals.ModeType.Delete;

           return true;
        }

        public string Title
        {
           get { return "Select mode"; }
        }
    }
}
