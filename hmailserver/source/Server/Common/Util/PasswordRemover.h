// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once


namespace HM
{
   class PasswordRemover
   {
   public:

      enum PRType
      {
         PRIMAP = 1,
         PRPOP3 = 2,
         PRSMTP = 3
      };

	   PasswordRemover();
	   virtual ~PasswordRemover();

		static void Remove(PRType prt, String &sClientCommand);
   private:
      

   };
}

