// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Scheduled task that generates and sends DMARC aggregate reports (RFC 7489
// section 7.2) to domains publishing a rua= tag in their _dmarc TXT record.
// The sibling of TlsRptReporterTask, and deliberately shaped like it.
//
// Reporting is inert until DmarcRptFromAddress is set, which it is not by
// default: collected data is popped and discarded rather than sent. The
// constructor says so in the application log - once per service start,
// because the task is constructed once - since the alternative is an
// administrator waiting indefinitely for reports that were never going to
// be sent.
//
// Forensic (ruf) reports are deliberately not implemented: the large
// receivers abandoned them years ago because a failure report carries the
// full message - a privacy leak to whoever operates the ruf mailbox - and a
// reporter that sends what Google and Microsoft will not is a liability,
// not a feature.

#pragma once

#include "../Common/BO/ScheduledTask.h"
#include "../Common/Util/DmarcRptStore.h"

namespace HM
{
   class DmarcRptReporterTask : public ScheduledTask
   {
   public:
      DmarcRptReporterTask();
      ~DmarcRptReporterTask();

      virtual void DoWork();

      // The send pass itself, callable outside the schedule. Pops each
      // completed day - and, when includeCurrentDay is set, today's
      // still-accumulating bucket as well - and mails one report per policy
      // domain that requests them. Returns the number of reports submitted.
      //
      // includeCurrentDay exists for COM's SendDmarcReports: an administrator
      // who has just configured DmarcRptFromAddress should be able to see a
      // report arrive now, not tomorrow. RFC 7489 places no uniqueness
      // requirement on a day's coverage; sessions recorded after the pop
      // simply form a later report.
      //
      // With no DmarcRptFromAddress configured the popped data is DISCARDED,
      // which is what bounds the store's memory on an unconfigured server. A
      // caller acting for an administrator must therefore check the setting
      // first and refuse rather than let a diagnostic destroy the data it was
      // asked to show - the COM layer does exactly that.
      static int SendReportsNow(bool includeCurrentDay);

      // Parses one _dmarc TXT record's rua= tag. Returns true when the record
      // is a DMARC policy (v=DMARC1), in which case the mailto: targets are
      // appended to addresses with RFC 7489 size suffixes ("!10m") and URI
      // parameters stripped - possibly none, since a rua naming only https
      // endpoints is valid but undeliverable here. Public and static so the
      // self-tests can pin the parse without a DNS server.
      static bool ParseRuaTargets(const AnsiString &record, std::vector<String> &addresses);

      // RFC 7489 section 7.1: a report may only be mailed to an address
      // outside the policy domain's organizational domain if the receiver
      // published <policy-domain>._report._dmarc.<target-domain> to say it
      // wants them. Without this check a policy naming victim@example.com as
      // its rua turns every DMARC reporter into a mail cannon aimed at the
      // victim. Public and static for the self-tests.
      static bool IsExternalDestination(const String &policyDomain, const String &targetDomain);

      // Builds the RFC 7489 Appendix C report body. Everything is a
      // parameter - including the organization name, which the production
      // caller reads from the ini - so the self-tests can pin the exact XML.
      static AnsiString BuildReportXml(const AnsiString &dayKey,
                                       const DmarcRptStore::DomainBucket &bucket,
                                       const AnsiString &reportId,
                                       const String &contactEmail,
                                       const AnsiString &organizationName);

   private:

      static bool SendReportForDomain_(const AnsiString &dayKey, const DmarcRptStore::DomainBucket &bucket);
      static bool GetReportingAddresses_(const String &policyDomain, std::vector<String> &addresses);
      static bool ExternalDestinationAuthorized_(const String &policyDomain, const String &targetDomain);
      // The policy tags come from somebody else's DNS record and are emitted into
      // a schema-validated document, so they are mapped onto the enumerations RFC
      // 7489 Appendix C actually allows rather than passed through.
      static AnsiString NormalizeDisposition_(const AnsiString &value);
      static AnsiString NormalizeAlignment_(const AnsiString &value);

      static AnsiString XmlEscape_(const AnsiString &value);
   };
}
