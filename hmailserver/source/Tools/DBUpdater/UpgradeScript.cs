// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;

namespace DBUpdater
{
   class UpgradeScript
   {
      private readonly int _from = 0;
      private readonly int _to = 0;

      // A three-argument constructor used to sit here alongside a "Dummy"
      // property. It ignored its own bool and assigned "_dummy = true"
      // unconditionally, so passing false produced a step marked true - and
      // nothing in the tool ever read the property, so the whole concept was
      // inert. Both are gone rather than corrected: a parameter that is silently
      // discarded is a trap for whoever reaches for it next, and there is no
      // behaviour here to preserve.

      public UpgradeScript(int from, int to)
      {
         _from = from;
         _to = to;
      }

      public int From
      {
         get
         {
            return _from;
         }
      }

      public int To
      {
         get
         {
            return _to;
         }
      }
   }
}
