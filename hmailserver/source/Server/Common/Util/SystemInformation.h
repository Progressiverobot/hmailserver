// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class SystemInformation
   {
   public:
      enum OperatingSystem
      {
         Unknown = 0,
         Windows95 = 1,
         Windows98 = 2,
         WindowsNT = 3,
         Windows2000 = 4,
         WindowsXP = 5,
         Windows2003 = 6,
         Windows3 = 7
      };

      SystemInformation(void);
      ~SystemInformation(void);

      static OperatingSystem GetOperatingSystem();

   private:

      static OperatingSystem operating_system_;
   };
}