// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;

namespace DBUpdater
{
   class UpgradeScripts
   {
      readonly List<UpgradeScript> _upgradeScripts;

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
         return _upgradeScripts.FirstOrDefault(script => script.From == from);
      }
   }
}
