// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class GreyListing
   {
   public:
      GreyListing(void);
      ~GreyListing(void);
   
      bool GetAllowSend(const String &sSenderAddress, const String &sRecipientAddress, const IPAddress & remoteIP);

   private:
   };
}