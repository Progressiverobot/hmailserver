// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "./SURBL.h"

#include "../../Common/AntiSpam/AntiSpamDiagnostics.h"
#include "../../Common/BO/MessageData.h"
#include "../../Common/BO/SURBLServer.h"
#include "../../Common/TCPIP/DNSResolver.h"
#include "../../SMTP/BLCheck.h"

#include "../../Common/Util/TLD.h"
#include <boost/regex.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SURBL::SURBL(void)
   {
      
   }

   SURBL::~SURBL(void)
   {
   }

   const int maxURLsToProcess = 15;

   bool
   SURBL::ExtractUrls(std::shared_ptr<MessageData> pMessageData, std::vector<String> &vecUrls)
   {
      LOG_DEBUG("SURBL: Execute");

      // Extract body
      String sBody = pMessageData->GetBody() + pMessageData->GetHTMLBody();

      // Extract URL's from the mail body:
      // Original: (?:(?>https?)?(?>:\/\/|\%3A\%2F\%2F))(?:www\.)?([a-z0-9\-\.\=\r\n]+)

      String sRegex = "(?:(?>https?)?(?>:\\/\\/|\\%3A\\%2F\\%2F))(?:www\\.)?([a-z0-9\\-\\.\\=\\r\\n]+)";

      std::set<String> addresses;

      try
      {
         boost::wregex expression(sRegex, boost::wregex::icase);
         boost::wsmatch matches;

         String sRemainingSearchSpace = sBody;

         while (boost::regex_search(sRemainingSearchSpace, matches, expression))
         {
            if (matches.size() < 2)
            {
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5702, "SURBL::Run", "A regex match was found, but there were <2 matches in the result");
               break;
            }

            // sub_match converts to a std::basic_string, and String is built from
            // one of those - two user-defined conversions, which only MSVC will
            // chain on its own. Asking the match for its string first makes the
            // one conversion that is left the ordinary String(std::wstring) one.
            String sURL = matches[1].str();

            // Clean the URL from linefeeds
            CleanURL_(sURL);

            // ignore some HTML doctype url's, which are default in for example Outlook composed mails
            // www.w3.org
            // www.w3c.org
            // schemas.microsoft.com
            // fonts.googleapis.com
            // fonts.gstatic.com
            sRegex = "(?=.*)(w3(?:c)?\\.org|(?:schemas\\.microsoft|fonts\\.(?:googleapis|gstatic))\\.com)";
            boost::wregex expr(sRegex, boost::wregex::icase);
            if (boost::regex_match(sURL, expr))
            {
               sRemainingSearchSpace = matches.suffix();
               continue;
            }

            if (addresses.find(sURL) == addresses.end())
            {
               String slogMessage;
               slogMessage.Format(_T("SURBL: Found URL: %s"), sURL.c_str());
               LOG_DEBUG(slogMessage);

               addresses.insert(sURL);

               if (addresses.size() > maxURLsToProcess)
               {
                  break;
               }
            }

            // Trim away top domain
            if (!CleanHost_(sURL))
            {
               sRemainingSearchSpace = matches.suffix();
               continue;
            }

            if (addresses.find(sURL) == addresses.end())
            {
               String logMessage;
               logMessage.Format(_T("SURBL: Found URL: %s"), sURL.c_str());
               LOG_DEBUG(logMessage);

               addresses.insert(sURL);

               if (addresses.size() > maxURLsToProcess)
               {
                  break;
               }
            }

            sRemainingSearchSpace = matches.suffix();
         }
      }
      catch (std::runtime_error &err) // regex_match will throw runtime_error if regexp is too complex.
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5701, "SURBL::Run", "Parsing HTML body with regular expression threw a runtime_error", err);
         return true;
      }

      if (addresses.size() > 0)
      {
         // The cast names FormatArgument's unsigned __int64 constructor, which is the
         // one a size_t binds to exactly on Windows. On a 64-bit POSIX build size_t is
         // unsigned long, which matches none of the constructors exactly and is no
         // closer to one than to the others. The cast is an identity on Windows.
         String logMessage = Formatter::Format("SURBL: {0} unique domain addresses found.", (unsigned __int64) addresses.size());
         LOG_DEBUG(logMessage);
      }

      for (auto address : addresses)
      { 
         // store found domain addresses in std::vector<String>
         vecUrls.push_back(address);
      }

      if (vecUrls.empty())
      {
         LOG_DEBUG("SURBL: No domain addresses found.");
         return false;
      }

      return true;
   }

   bool 
   SURBL::Run(std::shared_ptr<SURBLServer> pSURBLServer, std::vector<String> &vecUrls)
   {
      boost::chrono::system_clock::time_point start_time = boost::chrono::system_clock::now();

      int processedAddresses = 0;
      for (auto sURL : vecUrls)
      {
         boost::chrono::duration<double> elapsed_seconds = boost::chrono::system_clock::now() - start_time;

         if (elapsed_seconds.count() > 10)
         {
            LOG_DEBUG("SURBL: Aborting. Too long time elapsed.");
            return true;
         }

         if (processedAddresses > maxURLsToProcess)
         {
            LOG_DEBUG("SURBL: Aborting. Too many urls.");
            return true;
         }

         String sHostToLookup = sURL + "." + pSURBLServer->GetDNSHost();

         LOG_DEBUG(Formatter::Format(_T("SURBL: Lookup: {0}"), sHostToLookup));

         std::vector<String> saFoundNames;
         DNSResolver resolver;
         if (!resolver.GetIpAddresses(sHostToLookup, saFoundNames, false))
         {
            LOG_DEBUG("SURBL: DNS query failed.");

            // Abandoning the whole list on the first failed lookup means the URLs
            // after it are never checked either, and the message is reported as
            // clean. That is the safe direction to fail, but it was completely
            // silent, so a resolver that had stopped answering presented as a
            // SURBL that never matches anything.
            AntiSpamDiagnostics::ReportCheckIncomplete(AntiSpamDiagnostics::CheckSurbl,
               Formatter::Format("SURBL: The lookup of {0} did not complete. URI blacklists cannot be checked while that continues, and affected messages are accepted without a SURBL verdict.", sHostToLookup));

            return true;
         }

         // What an answer means is the server's to say, as it is for a DNSBL.
         // With an expected result set - the DNSBL syntax, 127.0.1.0-255 or
         // 127.0.0.2*, ranges and wildcards joined by | - only an answer it names
         // is a listing. With none set, any answer is, except the codes in
         // 127.255.255.0/24: the Spamhaus zones answer those to refuse the query
         // itself - a public resolver (.254), too many queries (.255), a mistyped
         // zone (.252) - and, taken as listings, they tagged every message with a
         // link as spam on a server that resolves through 8.8.8.8 (discussion
         // #167). Until 12 September 2026 any answer at all was a listing.
         bool listed = false;
         const std::set<String> expected = BLCheck::ExpandAddresses(pSURBLServer->GetExpectedResult());
         for (const String &found : saFoundNames)
         {
            if (expected.empty())
            {
               if (found.Left(12).Compare(_T("127.255.255.")) != 0)
                  listed = true;
            }
            else
            {
               for (const String &wanted : expected)
                  if (StringParser::WildcardMatch(wanted, found))
                     listed = true;
            }
         }

         LOG_DEBUG(Formatter::Format(_T("SURBL: {0} answered {1}: {2}"), sHostToLookup,
            StringParser::JoinVector(saFoundNames, ", "), listed ? _T("listed") : _T("not a listing")));

         if (listed)
         {
            LOG_DEBUG("SURBL: Match found");
            return false;
         }

         processedAddresses++;
      }

      LOG_DEBUG("SURBL: Match not found");
      return true;
   }

   void
   SURBL::CleanURL_(String &url) const
   {
      if (url.empty())
         return;

      url.Replace(_T("=\r\n"), _T(""));
      url.Replace(_T("=\r"), _T(""));
      url.Replace(_T("=\n"), _T(""));
      if (url.EndsWith(_T("=0D=0A")))
         url = url.substr(0, url.length() - 6);
      url.MakeLower();

      int newLinePosition = url.FindOneOf(_T("\r\n"));

      if (newLinePosition >= 0)
         url = url.Left(newLinePosition);
   }

   bool
   SURBL::CleanHost_(String &sDomain) const
   {
      bool bIsIPAddress = false;
      return TLD::Instance()->GetDomainNameFromHost(sDomain, bIsIPAddress);
   }
}