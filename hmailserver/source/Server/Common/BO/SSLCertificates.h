// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "Collection.h"
#include "SSLCertificate.h"

#include "../Persistence/PersistentSSLCertificate.h"

namespace HM
{
   class SSLCertificates : public Collection<SSLCertificate, PersistentSSLCertificate>
   {
   public:
      SSLCertificates();
      ~SSLCertificates(void);

      // Refreshes this collection from the database.
      void Refresh();

   protected:
      virtual String GetCollectionName() const {return "SSLCertificates"; }

   private:
     
   };
}