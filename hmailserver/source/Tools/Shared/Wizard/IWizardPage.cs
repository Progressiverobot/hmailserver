// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;

namespace hMailServer.Shared
{
   public interface IWizardPage
   {
      void OnShowPage(Dictionary<string, string> _state);
      bool OnLeavePage(bool next);
      string Title { get; }

   }

}
