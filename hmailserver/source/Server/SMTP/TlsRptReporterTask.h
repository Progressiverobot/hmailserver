// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Scheduled task that generates and sends SMTP TLS reports (RFC 8460)
// to domains publishing a _smtp._tls TXT record with a mailto: rua.

#pragma once

#include "../Common/BO/ScheduledTask.h"
#include "../Common/Util/TlsRptStore.h"

namespace HM
{
   class TlsRptReporterTask : public ScheduledTask
   {
   public:
      TlsRptReporterTask();
      ~TlsRptReporterTask();

      virtual void DoWork();

   private:

      void SendReportForDomain_(const AnsiString &dayKey, const String &domain, const TlsRptStore::DomainBucket &bucket);
      static bool GetReportingAddresses_(const String &domain, std::vector<String> &addresses);
      static AnsiString BuildReportJson_(const AnsiString &dayKey, const String &domain,
                                         const TlsRptStore::DomainBucket &bucket, const AnsiString &reportId,
                                         const String &contactInfo);
      static AnsiString JsonEscape_(const AnsiString &value);
   };
}
