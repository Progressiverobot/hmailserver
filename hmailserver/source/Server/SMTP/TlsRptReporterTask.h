// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Scheduled task that generates and sends SMTP TLS reports (RFC 8460)
// to domains publishing a _smtp._tls TXT record with a mailto: rua.
//
// Reporting is inert until TlsRptFromAddress is set, which it is not by
// default: collected data is popped and discarded rather than sent. The
// constructor says so in the application log - once per service start, because
// the task is constructed once - since the alternative is an administrator
// waiting indefinitely for reports that were never going to be sent.
// SPDX-License-Identifier: AGPL-3.0-or-later

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

      // The send pass itself, callable outside the schedule. Pops each completed
      // day - and, when includeCurrentDay is set, today's still-accumulating
      // bucket as well - and mails one report per domain that requests them.
      // Returns the number of reports submitted.
      //
      // includeCurrentDay exists for COM's SendTlsRptReports: an administrator
      // who has just configured TlsRptFromAddress should be able to see a report
      // arrive now, not tomorrow. RFC 8460 permits more than one report for a
      // day, so sessions recorded after the pop simply form a later report.
      //
      // With no TlsRptFromAddress configured the popped data is DISCARDED, which
      // is what bounds the store's memory on an unconfigured server. A caller
      // acting for an administrator must therefore check the setting first and
      // refuse, rather than let a diagnostic destroy the data it was asked to
      // show - the COM layer does exactly that.
      static int SendReportsNow(bool includeCurrentDay);

      // Parses one TXT record. Returns true when the record is a TLSRPTv1
      // policy, in which case the mailto: rua targets (parameters stripped) are
      // appended to addresses - possibly none, since a policy whose rua names
      // only https endpoints is valid but unusable here. Public and static so
      // the self-tests can pin the parse without a DNS server.
      static bool ParseTlsRptRecord(const AnsiString &record, std::vector<String> &addresses);

      // Builds the RFC 8460 report body. Everything is a parameter - including
      // the organization name, which the production caller reads from the ini -
      // so the self-tests can pin the exact JSON.
      static AnsiString BuildReportJson(const AnsiString &dayKey, const String &domain,
                                        const TlsRptStore::DomainBucket &bucket, const AnsiString &reportId,
                                        const String &contactInfo, const AnsiString &organizationName);

   private:

      static bool SendReportForDomain_(const AnsiString &dayKey, const String &domain, const TlsRptStore::DomainBucket &bucket);
      static bool GetReportingAddresses_(const String &domain, std::vector<String> &addresses);
      static AnsiString JsonEscape_(const AnsiString &value);
   };
}
