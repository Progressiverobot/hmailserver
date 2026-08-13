// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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
      DWORD fOptions;
      fOptions = DNS_QUERY_STANDARD;

      if (!IniFileSettings::Instance()->GetUseDNSCache())
      {
         fOptions += DNS_QUERY_BYPASS_CACHE;
      }

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

            // The port. Until 6.2.16 the custom server went to the classic DnsQuery as a
            // PIP4_ARRAY - a bare list of IPv4 addresses with no port field, so the DNS
            // client had nothing to use but 53. DnsQueryEx takes a DNS_ADDR_ARRAY, which
            // carries a full SOCKADDR per server, and this code filled in the family and
            // the address and left the port at the zero the memset had put there. A
            // destination port of zero is wrong however you look at it, so it is set.
            //
            // Being straight about what this does and does not fix, because the
            // investigation is not finished: setting it did NOT restore resolution
            // through a custom DNS server. That is still broken, and it is a REGRESSION
            // IN 6.2.17 - see the note in Roadmap.md against the DNS row. What is
            // established: with DNSServer configured, AAAA and CNAME queries come back
            // with ERROR_TIMEOUT (1460) in the same millisecond they were issued - which
            // is not what a real timeout looks like - and the A query returns a status
            // that IsDNSError_ treats as "no records" rather than an error, so the whole
            // lookup yields nothing and the caller reports that the name could not be
            // resolved. The same DNS server answers all three record types correctly when
            // queried directly, so the fault is on this side, not the directory's.
            //
            // What has been ruled out: the DNS_ADDR_ARRAY field layout matches
            // windnsdef.h; the answer-name comparison further down is case-insensitive
            // (String::Equals defaults to bUseCase = false); and the port, here.
            //
            // Why this matters more than the report that surfaced it: SpamAssassin is one
            // of the few callers that reports a failed lookup instead of failing open, so
            // it is the visible symptom (issue #25). DNSBL, SURBL, SPF, DKIM and MX
            // lookups fail the same way in silence, which is worse - a server in that
            // state quietly stops most of its spam filtering and carries on accepting
            // mail.
            serverAddress.sin_port = htons(53);

            pAsyncQuery->ServerList.MaxCount = 1;
            pAsyncQuery->ServerList.AddrCount = 1;
            pAsyncQuery->ServerList.Family = AF_INET;

            // DNS_ADDR_ARRAY is byte packed, so MaxSa is not necessarily aligned for
            // a write through a SOCKADDR_IN pointer.
            memcpy(pAsyncQuery->ServerList.AddrArray[0].MaxSa, &serverAddress, sizeof serverAddress);

            pAsyncQuery->Request.pDnsServerList = &pAsyncQuery->ServerList;

            // We need this if not using system dns servers
            if (fOptions != DNS_QUERY_BYPASS_CACHE)
               fOptions += DNS_QUERY_BYPASS_CACHE;
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
      if (nDnsStatus != 0)
      {
         bool bDNSError = IsDNSError_(nDnsStatus);

         _ReleaseAsyncDnsQuery(pAsyncQuery);

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

