// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class COMAuthenticator
   {
   public:

      COMAuthenticator()
      {
         // Create a dummy object so that it always exists.
         authentication_ = std::shared_ptr<HM::COMAuthentication>(new COMAuthentication);

         authentication_->AttempAnonymousAuthentication();
      }
      
      void SetAuthentication(std::shared_ptr<HM::COMAuthentication> pAuthentication)
      {
         authentication_ = pAuthentication; 
      }

   protected:

      bool GetIsServerAdmin()
      {
         if (!authentication_)
            return false;

         if (!authentication_->GetIsServerAdmin())
            return false;

         return true;
      }

      int GetAccessDenied()
      {
         if (!authentication_)
            return -1;

         return authentication_->GetAccessDenied();
      }

      std::shared_ptr<COMAuthentication> authentication_;

   private:

   };

}