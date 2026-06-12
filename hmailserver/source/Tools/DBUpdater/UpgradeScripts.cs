// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;

namespace DBUpdater
{
   class UpgradeScripts
   {
      List<UpgradeScript> _upgradeScripts;

      public UpgradeScripts()
      {
         _upgradeScripts = new List<UpgradeScript>();
      }

      public void Add(UpgradeScript script)
      {
         _upgradeScripts.Add(script);
      }

      public List<UpgradeScript> GetList()
      {
         return _upgradeScripts;
      }

      public UpgradeScript GetScriptUpgradingFrom(int from)
      {
         foreach (UpgradeScript script in _upgradeScripts)
         {
            if (script.From == from)
               return script;
         }

         return null;
      }
   }
}
