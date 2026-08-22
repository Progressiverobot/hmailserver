// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "DNSResolverWinApi.h"
#include <iphlpapi.h>
#include <windns.h>
#include <boost/asio.hpp>

using boost::asio::ip::tcp;

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool SortDnsRecordsByPreference(DNSRecord first, DNSRecord second) { return (first.GetPreference() < second.GetPreference()); }

   DNSResolverWinApi::DNSResolverWinApi()
   {

   }

   DNSResolverWinApi::~DNSResolverWinApi()
   {

   }
 
   void 
   _FreeDNSRecord(PDNS_RECORD pRecord)
   {
      if (!pRecord) 
         return;

      DNS_FREE_TYPE freetype = DnsFreeRecordListDeep;
      DnsRecordListFree(pRecord, freetype);
   }

   namespace
   {
      //---------------------------------------------------------------------------()
      // DESCRIPTION:
      // State shared between the thread issuing a DNS query and the DnsQueryEx
      // completion callback, which runs on a thread owned by the DNS client.
      //
      // Ownership rule: the block is reference counted. The issuing thread holds one
      // reference. A second reference is taken before DnsQueryEx is called and is
      // owned by the completion callback whenever the query goes asynchronous.
      // Whoever drops the last reference frees the record list, the event and the
      // block. Everything the DNS client retains a pointer to for the lifetime of
      // the query - the request, the result, the cancel handle, the server list and
      // the query name buffer - therefore lives here and not on the issuing thread's
      // stack.
      //
      // This matters because a query that has been started cannot be aborted, only
      // cancelled: after DnsCancelQuery the callback still runs, still writes
      // QueryStatus and pQueryRecords, and still signals CompletionEvent. A thread
      // that abandons the wait drops its reference and must never read the block
      // again; the callback then disposes of the result it was handed. That is what
      // keeps cancellation free of both a use-after-free and a stranded thread.
      //---------------------------------------------------------------------------()
      struct AsyncDnsQuery
      {
         AsyncDnsQuery() :
            CompletionEvent(NULL),
            ReferenceCount(1)
         {
            memset(&Request, 0, sizeof Request);
            memset(&Result, 0, sizeof Result);
            memset(&Cancel, 0, sizeof Cancel);
            memset(&ServerList, 0, sizeof ServerList);
         }

         DNS_QUERY_REQUEST Request;
         DNS_QUERY_RESULT Result;
         DNS_QUERY_CANCEL Cancel;
         DNS_ADDR_ARRAY ServerList;
         String QueryName;
         HANDLE CompletionEvent;
         volatile LONG ReferenceCount;
      };

      void
      _ReleaseAsyncDnsQuery(AsyncDnsQuery *pAsyncQuery)
      {
         if (InterlockedDecrement(&pAsyncQuery->ReferenceCount) != 0)
            return;

         _FreeDNSRecord(pAsyncQuery->Result.pQueryRecords);

         if (pAsyncQuery->CompletionEvent != NULL)
            CloseHandle(pAsyncQuery->CompletionEvent);

         delete pAsyncQuery;
      }

      void WINAPI
      _AsyncDnsQueryCompleted(PVOID pQueryContext, PDNS_QUERY_RESULT /*pQueryResults*/)
      {
         // pQueryResults aliases AsyncDnsQuery::Result, which the DNS client has
         // already filled in. Nothing is parsed here: on the abandoned-wait path this
         // callback holds the last reference, so it must leave the result in a state
         // _ReleaseAsyncDnsQuery can dispose of on its own.
         AsyncDnsQuery *pAsyncQuery = reinterpret_cast<AsyncDnsQuery *>(pQueryContext);

         // Must precede the release - the waiting thread may free the event as soon
         // as it wakes.
         SetEvent(pAsyncQuery->CompletionEvent);

         _ReleaseAsyncDnsQuery(pAsyncQuery);
      }

      DWORD
      _GetQueryTimeoutMilliseconds()
      {
         int iTimeoutSeconds = IniFileSettings::Instance()->GetDNSQueryTimeout();

         // Zero means no bound - wait for the resolver for as long as it takes.
         if (iTimeoutSeconds <= 0)
            return INFINITE;

         // Clamp before scaling so the conversion cannot wrap into a short timeout or
         // collide with INFINITE.
         const int max_timeout_seconds = 24 * 60 * 60;

         if (iTimeoutSeconds > max_timeout_seconds)
            iTimeoutSeconds = max_timeout_seconds;

         return static_cast<DWORD>(iTimeoutSeconds) * 1000;
      }
   }

   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Determines whether the result of a DnsQuery call is an error or not.
   //---------------------------------------------------------------------------()
   bool
   DNSResolverWinApi::IsDNSError_(int iErrorMessage)
   {
      switch (iErrorMessage)
      {
      case DNS_ERROR_RCODE_NAME_ERROR: // Domain doesn't exist
         return false;
      case ERROR_INVALID_NAME:
         return false;
      case DNS_INFO_NO_RECORDS:        // No records were found for the host. Not an error.
         return false;
      case DNS_ERROR_NO_DNS_SERVERS:   // No DNS servers found.
         return true;
      }

      return true;
   }

   bool
   DNSResolverWinApi::Query(const String &query, int resourceType, std::vector<DNSRecord> &foundRecords)
   {
      int ignoredStatus = 0;

      return Query(query, resourceType, foundRecords, ignoredStatus);
   }

   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // As above, but hands back the resolver status as well.
   //
   // Every other caller here only needs to know whether records came back, and for
   // them NXDOMAIN and "the name exists but has no records of this type" are the
   // same answer: nothing to use. They are NOT the same answer for DMARC's np= tag,
   // which applies only to subdomains that do not exist AT ALL, so it needs to tell
   // an absent name from an empty one - the difference between a policy that catches
   // a forged subdomain and a policy that rejects mail from a real one.
   //---------------------------------------------------------------------------()
   bool
   DNSResolverWinApi::Query(const String &query, int resourceType, std::vector<DNSRecord> &foundRecords, int &outStatus)
   {
      DWORD fOptions;
      fOptions = DNS_QUERY_STANDARD;

      if (!IniFileSettings::Instance()->GetUseDNSCache())
      {
         fOptions |= DNS_QUERY_BYPASS_CACHE;
      }

      int dnsStatus = 0;

      if (RunQuery_(query, resourceType, fOptions, foundRecords, dnsStatus))
      {
         outStatus = dnsStatus;
         return true;
      }

      // DNS_ERROR_BAD_PACKET (9502): the server sent a response the resolver could
      // not parse. Measured against a deliberately misbehaving server: a UDP answer
      // whose RDLENGTH overruns the packet, or whose name compression points past
      // the end of it, produces exactly this status - and the same server, asked
      // the same question over TCP, answers correctly whenever only its UDP path is
      // broken. Large multi-string TXT records (DKIM public keys) are where this
      // shows up in the field, because those are the answers that outgrow what such
      // servers assemble properly. Retrying over TCP is what a full resolver does
      // here. A timeout is deliberately NOT retried: against an unreachable server
      // that would double every failed lookup's wait, and a server that says
      // nothing has not demonstrated a working TCP path to try.
      if (dnsStatus == DNS_ERROR_BAD_PACKET)
      {
         LOG_DEBUG(Formatter::Format(
            "DNS - Malformed response (status {0}); retrying the query over TCP. Query: {1}, type {2}.",
            dnsStatus, query, resourceType));

         // foundRecords is NOT cleared here, and that is load-bearing. Callers
         // accumulate several queries into one vector - GetIpAddressesRecursive_
         // runs A and then AAAA into the same list - so clearing on the AAAA
         // retry throws away the A records that already succeeded. A failed
         // RunQuery_ never adds records (every false path returns before the
         // record loop), so there is nothing of the failed attempt to remove.
         bool retried = RunQuery_(query, resourceType, fOptions | DNS_QUERY_USE_TCP_ONLY, foundRecords, dnsStatus);

         outStatus = dnsStatus;

         return retried;
      }

      outStatus = dnsStatus;

      return false;
   }

   bool
   DNSResolverWinApi::RunQuery_(const String &query, int resourceType, unsigned long fOptions, std::vector<DNSRecord> &foundRecords, int &dnsStatus)
   {
      dnsStatus = 0;

      AsyncDnsQuery *pAsyncQuery = new AsyncDnsQuery();

      pAsyncQuery->CompletionEvent = CreateEvent(NULL, TRUE, FALSE, NULL);
      if (!pAsyncQuery->CompletionEvent) {

         String sMessage;
         sMessage.Format(_T("Unable to create completion event for DNS query. Query: %s, Type: %d."), query.c_str(), resourceType);
         ErrorManager::Instance()->ReportError(ErrorManager::Low, 4401, "DNSResolver::_Resolve", sMessage);

         _ReleaseAsyncDnsQuery(pAsyncQuery);

         return false;
      }

      AnsiString sCustomDNS;
      sCustomDNS = IniFileSettings::Instance()->GetDNSServer().Trim();
      if (!sCustomDNS.IsEmpty())
      {
         // Custom DNSServer IPv4 address
         unsigned long customServerAddress = inet_addr(sCustomDNS.c_str()); //Custom DNS server IP address
         if (customServerAddress == INADDR_NONE) {

            String sMessage;
            sMessage.Format(_T("Invalid DNSServer IP address. DNSServer IP: %hs."), sCustomDNS.c_str());
            ErrorManager::Instance()->ReportError(ErrorManager::Low, 4401, "DNSResolver::_Resolve", sMessage);

            // fallback to the system dns servers
         }
         else
         {
            SOCKADDR_IN serverAddress;
            memset(&serverAddress, 0, sizeof serverAddress);

            serverAddress.sin_family = AF_INET;
            serverAddress.sin_addr.s_addr = customServerAddress;

            // The port is deliberately left at zero, and this comment exists because
            // setting it to 53 - which looks obviously right - breaks every lookup.
            //
            // A DNS_ADDR carries a full SOCKADDR, so a destination port of zero looks
            // like an oversight. It is not: the DNS client supplies the port itself and
            // rejects a server entry that specifies one. With htons(53) here, DnsQueryEx
            // returns ERROR_INVALID_PARAMETER (87) for every query type before any packet
            // is sent - so name resolution fails completely for anyone who has configured
            // DNSServer, and fails in a way that looks like a network problem rather than
            // a rejected argument.
            //
            // Established by measurement, in both directions, against a real DNS server:
            // with port 53 all three record types return status 87 and no records; with
            // port 0 the A query returns status 0 with its record and the AAAA query
            // returns DNS_INFO_NO_RECORDS, which is the correct answer for a host that
            // has no AAAA record.
            //
            // Do not "fix" this line. The classic DnsQuery took a PIP4_ARRAY with no port
            // field at all, which is why the question never arose before 6.2.17.
            serverAddress.sin_port = 0;

            pAsyncQuery->ServerList.MaxCount = 1;
            pAsyncQuery->ServerList.AddrCount = 1;
            pAsyncQuery->ServerList.Family = AF_INET;

            // DNS_ADDR_ARRAY is byte packed, so MaxSa is not necessarily aligned for
            // a write through a SOCKADDR_IN pointer.
            memcpy(pAsyncQuery->ServerList.AddrArray[0].MaxSa, &serverAddress, sizeof serverAddress);

            pAsyncQuery->Request.pDnsServerList = &pAsyncQuery->ServerList;

            // A custom server list is only honoured together with BYPASS_CACHE.
            // OR, not +=: the flag may already be set, and these are bit flags.
            fOptions |= DNS_QUERY_BYPASS_CACHE;
         }
      }

      pAsyncQuery->QueryName = query;

      pAsyncQuery->Request.Version = DNS_QUERY_REQUEST_VERSION1;
      pAsyncQuery->Request.QueryName = pAsyncQuery->QueryName.c_str();
      pAsyncQuery->Request.QueryType = static_cast<WORD>(resourceType);
      pAsyncQuery->Request.QueryOptions = fOptions;
      pAsyncQuery->Request.pQueryCompletionCallback = _AsyncDnsQueryCompleted;
      pAsyncQuery->Request.pQueryContext = pAsyncQuery;

      pAsyncQuery->Result.Version = DNS_QUERY_REQUEST_VERSION1;

      DWORD timeoutMilliseconds = _GetQueryTimeoutMilliseconds();

      // The callback's reference has to exist before the query is started - the
      // callback can run before DnsQueryEx has returned.
      InterlockedIncrement(&pAsyncQuery->ReferenceCount);

      DNS_STATUS nDnsStatus = DnsQueryEx(&pAsyncQuery->Request, &pAsyncQuery->Result, &pAsyncQuery->Cancel);

      if (nDnsStatus == DNS_REQUEST_PENDING)
      {
         if (WaitForSingleObject(pAsyncQuery->CompletionEvent, timeoutMilliseconds) != WAIT_OBJECT_0)
         {
            // The query cannot be withdrawn, only cancelled, so hand the block over to
            // the completion callback and stop looking at it here. Cancelling a query
            // that has just completed is a benign race; DnsCancelQuery reports it and
            // the callback runs either way.
            DnsCancelQuery(&pAsyncQuery->Cancel);
            _ReleaseAsyncDnsQuery(pAsyncQuery);

            // Callers already treat a lookup failure as fail-open, which is the same
            // handling a SERVFAIL gets.
            String sMessage;
            sMessage.Format(_T("DNS - Query timed out. Query: %s, Type: %d, Timeout: %d ms."), query.c_str(), resourceType, static_cast<int>(timeoutMilliseconds));
            LOG_TCPIP(sMessage);

            dnsStatus = WAIT_TIMEOUT;
            return false;
         }

         nDnsStatus = pAsyncQuery->Result.QueryStatus;
      }
      else
      {
         // The query completed inline. The completion callback is not invoked in that
         // case, so its reference goes back.
         _ReleaseAsyncDnsQuery(pAsyncQuery);
      }

      // Sole owner from here on: the callback has either finished or was never queued.

      // Logged HERE, before the status is classified, and the placement is the point.
      // A status that IsDNSError_ treats as benign - "no such name", "no records" - returns
      // success with an empty record list and writes nothing, so a lookup that quietly
      // found nothing was indistinguishable from one that was never made. That blind spot
      // is why the custom-DNS-server regression took three wrong theories to corner: the
      // A query was disappearing through this branch while only AAAA and CNAME left a
      // trace. Debug level, so it costs nothing until someone is diagnosing resolution,
      // which is a recurring support question.
      if (Logger::Instance()->GetLogDebug())
      {
         int recordCount = 0;
         for (PDNS_RECORD walk = pAsyncQuery->Result.pQueryRecords; walk != nullptr; walk = walk->pNext)
            recordCount++;

         // Formatter::Format takes at most five arguments after the format string, so the
         // two descriptive fields are folded into one before the call.
         String situation = pAsyncQuery->Request.pDnsServerList != nullptr
            ? _T("custom server, ") : _T("system servers, ");

         situation += nDnsStatus == 0
            ? _T("success")
            : (IsDNSError_(nDnsStatus) ? _T("classified as an error")
                                       : _T("classified as benign - returns success with no records"));

         LOG_DEBUG(Formatter::Format("DNS - Result. Query: {0}, type {1}, status {2}, records {3}, {4}",
            query, resourceType, (int) nDnsStatus, recordCount, situation));
      }

      if (nDnsStatus != 0)
      {
         bool bDNSError = IsDNSError_(nDnsStatus);

         _ReleaseAsyncDnsQuery(pAsyncQuery);

         dnsStatus = nDnsStatus;

         if (bDNSError)
         {
            String sMessage;
            sMessage.Format(_T("DNS - Query failure. Query: %s, Type: %d, DnsQuery return value: %d."), query.c_str(), resourceType, nDnsStatus);
            LOG_TCPIP(sMessage);
            return false;
         }

         return true;
      }

      PDNS_RECORD pDnsRecord = pAsyncQuery->Result.pQueryRecords;

      // Every record the resolver handed back, before any filtering, and what the query
      // itself reported. This exists because a lookup that returns nothing is otherwise
      // indistinguishable from a lookup that returned records this function then rejected -
      // and telling those two apart is exactly what was needed to make progress on the
      // custom-DNS-server regression. Debug level, so it costs nothing until someone is
      // actually diagnosing a resolution problem, which is a recurring support question.
      if (Logger::Instance()->GetLogDebug())
      {
         int recordCount = 0;
         for (PDNS_RECORD walk = pDnsRecord; walk != nullptr; walk = walk->pNext)
            recordCount++;

         LOG_DEBUG(Formatter::Format("DNS - Answered. Query: {0}, Type: {1}, status: {2}, records: {3}, custom server: {4}",
            query, resourceType, (int) nDnsStatus, recordCount,
            pAsyncQuery->Request.pDnsServerList != nullptr ? _T("yes") : _T("no")));

         for (PDNS_RECORD walk = pDnsRecord; walk != nullptr; walk = walk->pNext)
         {
            LOG_DEBUG(Formatter::Format("DNS -   record name '{0}' type {1}{2}",
               String(walk->pName == nullptr ? _T("(null)") : walk->pName), (int) walk->wType,
               walk->wType == resourceType && query.Equals(String(walk->pName)) ? _T("") : _T("  [FILTERED OUT]")));
         }
      }

      while (pDnsRecord != nullptr)
      {
         String name = pDnsRecord->pName;

         if (pDnsRecord->wType == resourceType &&
             query.Equals(name))
         {
            switch (pDnsRecord->wType)
            {
               case DNS_TYPE_A:
               {
                  SOCKADDR_IN addr;
                  memset(&addr, 0, sizeof addr);

                  addr.sin_family = AF_INET;
                  addr.sin_addr = *((in_addr*)&(pDnsRecord->Data.A.IpAddress));

                  char buf[128];
                  DWORD bufSize = sizeof(buf);

                  if (WSAAddressToStringA((sockaddr*)&addr, sizeof addr, NULL, buf, &bufSize) == 0)
                  {
                     DNSRecord record(buf, pDnsRecord->wType, 0);
                     foundRecords.push_back(record);
                  }

                  break;
               }
               case DNS_TYPE_AAAA:
               {
                  SOCKADDR_IN6 addr;
                  memset(&addr, 0, sizeof addr);

                  addr.sin6_family = AF_INET6;
                  addr.sin6_addr = *((in_addr6*)&(pDnsRecord->Data.AAAA.Ip6Address));

                  char buf[128];
                  DWORD bufSize = sizeof(buf);

                  if (WSAAddressToStringA((sockaddr*)&addr, sizeof addr, NULL, buf, &bufSize) == 0)
                  {
                     DNSRecord record(buf, pDnsRecord->wType, 0);
                     foundRecords.push_back(record);
                  }

                  break;
               }
               case DNS_TYPE_CNAME:
               {
                  String sDomainName = pDnsRecord->Data.CNAME.pNameHost;

                  DNSRecord record(sDomainName, pDnsRecord->wType, 0);
                  foundRecords.push_back(record);
                  break;
               }
               case DNS_TYPE_MX:
               {
                  if (pDnsRecord->Flags.S.Section == DNSREC_ANSWER)
                  {
                     DNSRecord record(String(pDnsRecord->Data.MX.pNameExchange), pDnsRecord->wType, pDnsRecord->Data.MX.wPreference);
                     foundRecords.push_back(record);
                  }

                  break;
               }
               case DNS_TYPE_TEXT:
               {
                  AnsiString retVal;

                  for (u_int i = 0; i < pDnsRecord->Data.TXT.dwStringCount; i++)
                     retVal += pDnsRecord->Data.TXT.pStringArray[i];

                  DNSRecord record(retVal, pDnsRecord->wType, 0);
                  foundRecords.push_back(record);
                  break;
               }
               case DNS_TYPE_PTR:
               {
                  AnsiString retVal;
                  retVal = pDnsRecord->Data.PTR.pNameHost;

                  DNSRecord record(retVal, pDnsRecord->wType, 0);
                  foundRecords.push_back(record);
                  break;
               }
               default:
               {
                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5036, "DNSResolverWinApi::Resolve_", Formatter::Format("Queried for {0} but received type {1}", resourceType, pDnsRecord->wType));
                  break;
               }
            }
         }

         pDnsRecord = pDnsRecord->pNext;
      }

      _ReleaseAsyncDnsQuery(pAsyncQuery);

      std::sort(foundRecords.begin(), foundRecords.end(), SortDnsRecordsByPreference);


      return true;
   }
}

