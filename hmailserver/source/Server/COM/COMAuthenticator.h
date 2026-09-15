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

      // For a COM method that writes without asking an authorisation question of
      // its own - the settings property setters, guarded by a cached pointer that
      // LoadSettings only fills for a server administrator. One line at the top
      // of such a method, and the change it makes is attributed to whoever is
      // making it. See AuditTrail.h.
      HM::AuditTrail::Actor GetAuditActor() const
      {
         if (!authentication_)
            return HM::AuditTrail::Actor();

         return authentication_->GetAuditActor();
      }

      std::shared_ptr<COMAuthentication> authentication_;

   private:

   };

}