// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "ClassTester.h"
#include "../Application/BackupManager.h"
#include "../Application/TimeoutCalculator.h"
#include "../BO/MessageData.h"
#include "../BO/Message.h"
#include "../Mime/MimeTester.h"
#include "../Util/Utilities.h"
#include "../Util/MessageUtilities.h"
#include "../Util/AcmeClient.h"
#include "../Util/Charset.h"
#include "../Util/RegularExpression.h"
#include "../TCPIP/LocalIPAddresses.h"
#include "../TCPIP/IPAddress.h"
#include "Time.h"
#include "Utilities.h"
#include "Parsing\AddresslistParser.h"
#include "../../IMAP/IMAPSimpleCommandParser.h"
#include "BlowFish.h"
#include "Crypt.h"
#include "DataProtector.h"
#include "SRS.h"
#include "BATV.h"
#include "AccountLockout.h"
#include "Totp.h"
#include "../AntiSpam/DMARC/DmarcTreeWalk.h"
#include "RateLimiter.h"
#include "TransparentTransmissionBuffer.h"
#include "../Cache/Cache.h"
#include "../Persistence/PersistentMessage.h"
#include "../../SMTP/SPF/SPF.h"
#include "../../SMTP/BLCheck.h"
#include "../../SMTP/TlsRptReporterTask.h"
#include "../../SMTP/DmarcRptReporterTask.h"
#include "../Application/BackupManager.h"
#include "../Util/Encoding/Base64.h"
#include "../Util/Encoding/ModifiedUTF7.h"
#include "../Util/Hashing/HashCreator.h"
#include "../Util/OAuth2TokenValidator.h"
#include "../Util/EventTester.h"
#include "../SQL/SQLStatement.h"
#include <boost/pool/object_pool.hpp>

#ifdef _DEBUG
   #define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
   #define new DEBUG_NEW
#endif

namespace HM
{

   ClassTester::ClassTester()
   {

   }

   ClassTester::~ClassTester()
   {

   }

   void
   ClassTester::DoTests()
   {
      EventTester *pEventTester = new EventTester;
      pEventTester->Test();
      delete pEventTester;

      OutputDebugString(_T("hMailServer: Testing mime parser\n"));
	   MimeTester *pMimeTester = new MimeTester;
      pMimeTester->Test();
	   delete pMimeTester;

      OutputDebugString(_T("hMailServer: Testing StringParser\n"));
      StringParserTester *pParser = new StringParserTester();
      pParser->Test();
      delete pParser;
  
      OutputDebugString(_T("hMailServer: Testing FileUtilities\n"));
      FileUtilitiesTester fileUtilitiesTester;
      fileUtilitiesTester.Test();

      OutputDebugString(_T("hMailServer: Testing IPAddress\n"));
      IPAddressTester ipAddressTester;
      ipAddressTester.Test();

      OutputDebugString(_T("hMailServer: Testing MessageUtilities\n"));
      MessageUtilitiesTester messageUtilitiesTester;
      messageUtilitiesTester.Test();

      OutputDebugString(_T("hMailServer: Testing Formatter\n"));
      FormatterTester formatterTester;
      formatterTester.Test();

      // Parameter substitution matters most on the two backends the bench does not
      // usually run: MySQL and PostgreSQL report GetSupportsCommandParameters() as
      // false, so they take the string-substitution path, while MSSQL and SQL CE use
      // real parameters and never exercise it. That is why this is tested here in
      // process rather than through a query - the defect is invisible on the backend
      // the suite runs against.
      OutputDebugString(_T("hMailServer: Testing SQLStatement parameter substitution\n"));
      SQLStatementTester sqlStatementTester;
      sqlStatementTester.Test();

      OutputDebugString(_T("hMailServer: Testing TimeoutCalculator\n"));
      TimeoutCalculatorTester timeoutCalculatortester;
      timeoutCalculatortester.Test();

      OutputDebugString(_T("hMailServer: Test DateTime\n"));
      DateTimeTests tests;
      tests.Test();

      OutputDebugString(_T("hMailServer: Testing Utilities\n"));
      UtilitiesTester *pTestUtilities = new UtilitiesTester();
      pTestUtilities->Test();
      delete pTestUtilities;

      OutputDebugString(_T("hMailServer: Test BLChecktester\n"));
      BLCheckTester blchecktester;
      blchecktester.Test();

      OutputDebugString(_T("hMailServer: Testing SPF\n"));
      SPFTester *pSPF = new SPFTester();
      pSPF->Test();
      delete pSPF;

      OutputDebugString(_T("hMailServer: Testing SHA256\n"));
      HashCreatorTester tester;
      tester.Test();

      OutputDebugString(_T("hMailServer: Testing password hash dispatch (Crypt)\n"));
      {
         // Validate the exact server-side login dispatch (EnCrypt -> GetHashType ->
         // Validate) for the strong password KDFs, including Argon2id.
         const String testPwd = _T("Corr3ct H0rse Battery Staple!");

         String argonHash = Crypt::Instance()->EnCrypt(testPwd, Crypt::ETArgon2id);
         if (Crypt::Instance()->GetHashType(argonHash) != Crypt::ETArgon2id)
            throw 0;
         if (!Crypt::Instance()->Validate(testPwd, argonHash, Crypt::ETArgon2id))
            throw 0;
         if (Crypt::Instance()->Validate(_T("the wrong password"), argonHash, Crypt::ETArgon2id))
            throw 0;

         String pbkdf2Hash = Crypt::Instance()->EnCrypt(testPwd, Crypt::ETPBKDF2);
         if (Crypt::Instance()->GetHashType(pbkdf2Hash) != Crypt::ETPBKDF2)
            throw 0;
         if (!Crypt::Instance()->Validate(testPwd, pbkdf2Hash, Crypt::ETPBKDF2))
            throw 0;
      }

      OutputDebugString(_T("hMailServer: Testing OAuth2TokenValidator\n"));
      {
         OAuth2TokenValidatorTester oauth2Tester;
         oauth2Tester.Test();
      }

      OutputDebugString(_T("hMailServer: Testing TOTP against the RFC 6238 vectors\n"));
      {
         TotpTester totpTester;
         totpTester.Test();
      }

      OutputDebugString(_T("hMailServer: Testing the DMARC tree walk (RFC 9989 4.10.2)\n"));
      {
         DmarcTreeWalkTester treeWalkTester;
         treeWalkTester.Test();
      }

      OutputDebugString(_T("hMailServer: Testing SRS\n"));
      {
         // Sender Rewriting Scheme: sign/verify round-trip + tamper detection.
         const String secret = _T("srs-test-secret-key");
         const String original = _T("alice@external-sender.com");
         const String forwarder = _T("relay.example.com");

         String srs = SRS::Forward(original, forwarder, secret);

         if (srs.IsEmpty())
            throw 0;
         if (!SRS::IsSRS0(srs))
            throw 0;
         if (srs.Find(_T("@relay.example.com")) < 0)
            throw 0;

         // A valid SRS0 address reverses to the original sender.
         String reversed;
         if (!SRS::Reverse(srs, secret, reversed))
            throw 0;
         if (reversed.CompareNoCase(original) != 0)
            throw 0;

         // Tamper detection: a flipped character in the signed local-part fails.
         wchar_t hashChar = srs.GetAt(5);
         String tampered = srs.Mid(0, 5) + ((hashChar == L'0') ? _T("1") : _T("0")) + srs.Mid(6);
         if (SRS::Reverse(tampered, secret, reversed))
            throw 0;

         // A different secret must not validate.
         if (SRS::Reverse(srs, _T("a-different-secret"), reversed))
            throw 0;

         // A plain address is neither detected as SRS nor reversible.
         if (SRS::IsSRS0(_T("bob@example.com")))
            throw 0;
         if (SRS::Reverse(_T("bob@example.com"), secret, reversed))
            throw 0;

         // An original local-part containing '=' and '+' survives the round trip.
         const String original2 = _T("a=b+tag@ext.example.org");
         String srs2 = SRS::Forward(original2, forwarder, secret);
         if (srs2.IsEmpty())
            throw 0;
         String reversed2;
         if (!SRS::Reverse(srs2, secret, reversed2))
            throw 0;
         if (reversed2.CompareNoCase(original2) != 0)
            throw 0;
      }

      OutputDebugString(_T("hMailServer: Testing BATV\n"));
      {
         // BATV (prvs): sign/verify round-trip + tamper and wrong-secret detection.
         const String secret = _T("batv-test-secret-key");
         const String mailbox = _T("alice@example.test");

         String tagged = BATV::Sign(mailbox, secret);

         if (tagged.IsEmpty())
            throw 0;
         if (!BATV::IsPrvs(tagged))
            throw 0;
         // The original domain is preserved (so SPF stays aligned).
         if (tagged.Find(_T("@example.test")) < 0)
            throw 0;
         if (tagged.Find(_T("prvs=")) != 0)
            throw 0;

         // A valid prvs address verifies back to the original mailbox.
         String original;
         if (!BATV::Verify(tagged, secret, original))
            throw 0;
         if (original.CompareNoCase(mailbox) != 0)
            throw 0;

         // Tamper detection: flip a character of the signature (the last hex digit
         // of the 10-char tag value, at index 5 + 9 = 14 of the local-part).
         wchar_t sigChar = tagged.GetAt(14);
         String tampered = tagged.Mid(0, 14) + ((sigChar == L'0') ? _T("1") : _T("0")) + tagged.Mid(15);
         if (BATV::Verify(tampered, secret, original))
            throw 0;

         // A different secret must not validate.
         if (BATV::Verify(tagged, _T("a-different-secret"), original))
            throw 0;

         // A plain address is neither detected as prvs nor verifiable, and an
         // empty sender (a bounce) is never signed.
         if (BATV::IsPrvs(_T("bob@example.test")))
            throw 0;
         if (BATV::Verify(_T("bob@example.test"), secret, original))
            throw 0;
         if (!BATV::Sign(_T(""), secret).IsEmpty())
            throw 0;
         // An already-tagged address is never double-signed.
         if (!BATV::Sign(tagged, secret).IsEmpty())
            throw 0;

         // An original local-part containing '=' survives the round trip.
         const String mailbox2 = _T("a=b@example.test");
         String tagged2 = BATV::Sign(mailbox2, secret);
         if (tagged2.IsEmpty())
            throw 0;
         String original2;
         if (!BATV::Verify(tagged2, secret, original2))
            throw 0;
         if (original2.CompareNoCase(mailbox2) != 0)
            throw 0;
      }

      OutputDebugString(_T("hMailServer: Testing RateLimiter\n"));
      {
         // Sliding-window rate limiter: limit is enforced per key, 0 = unlimited.
         RateLimiter::Instance()->Clear();

         // A maxPerMinute of 0 never throttles.
         for (int i = 0; i < 1000; i++)
         {
            if (!RateLimiter::Instance()->TryConsume(_T("unlimited"), 0))
               throw 0;
         }

         // With a limit of 3, the first three succeed and the fourth fails.
         if (!RateLimiter::Instance()->TryConsume(_T("keyA"), 3))
            throw 0;
         if (!RateLimiter::Instance()->TryConsume(_T("keyA"), 3))
            throw 0;
         if (!RateLimiter::Instance()->TryConsume(_T("keyA"), 3))
            throw 0;
         if (RateLimiter::Instance()->TryConsume(_T("keyA"), 3))
            throw 0;

         // A different key has an independent budget.
         if (!RateLimiter::Instance()->TryConsume(_T("keyB"), 3))
            throw 0;

         RateLimiter::Instance()->Clear();
      }

      OutputDebugString(_T("hMailServer: Testing DataProtector (DPAPI)\n"));
      {
         DataProtectorTester dpapiTester;
         dpapiTester.Test();

         // Also exercise the Crypt secret envelope used for stored secrets.
         const String secret = _T("route-relay-p@ssw0rd");
         String envelope = Crypt::Instance()->ProtectSecret(secret);
         if (envelope.IsEmpty())
            throw 0;
         if (Crypt::Instance()->UnprotectSecret(envelope) != secret)
            throw 0;
         // A legacy Blowfish value (no DPAPI prefix) must still round-trip.
         String legacy = Crypt::Instance()->EnCrypt(secret, Crypt::ETBlowFish);
         if (Crypt::Instance()->UnprotectSecret(legacy) != secret)
            throw 0;
      }


      OutputDebugString(_T("hMailServer: Testing TransparentTransmissionBuffer (SMTP dot transparency)\n"));
      {
         TransparentTransmissionBufferTester dotTester;
         dotTester.Test();
      }

      OutputDebugString(_T("hMailServer: Testing Cache size accounting\n"));
      {
         CacheAccountingTester cacheTester;
         cacheTester.Test();
      }

      OutputDebugString(_T("hMailServer: Testing RegularExpressionTester\n"));
      RegularExpressionTester *pRegExTest = new RegularExpressionTester();
      pRegExTest->Test();
      delete pRegExTest;

      OutputDebugString(_T("hMailServer: Testing Base64\n"));
      Base64Tester base64Tester;
      base64Tester.Test();

      OutputDebugString(_T("hMailServer: Testing the ACME challenge locator\n"));
      {
         // Boulder pretty-prints its JSON - '"type": "http-01"', space included -
         // so the compact-form search this locator replaced never matched a real
         // Let's Encrypt response and every issuance failed (issue #34). The
         // pretty shape below is the real CA's, byte for byte in the ways that
         // matter.
         AnsiString pretty =
            "{\n"
            "  \"identifier\": {\n"
            "    \"type\": \"dns\",\n"
            "    \"value\": \"mta-sts.example.com\"\n"
            "  },\n"
            "  \"status\": \"pending\",\n"
            "  \"challenges\": [\n"
            "    {\n"
            "      \"type\": \"tls-alpn-01\",\n"
            "      \"url\": \"https://ca.example/chall/1\",\n"
            "      \"token\": \"alpntoken\"\n"
            "    },\n"
            "    {\n"
            "      \"type\": \"http-01\",\n"
            "      \"url\": \"https://ca.example/chall/2\",\n"
            "      \"token\": \"httptoken\"\n"
            "    }\n"
            "  ]\n"
            "}";
         if (AcmeClient::FindChallengeOfType(pretty, "http-01") < 0)
            throw 0;

         // The compact form stays accepted - simulators and other CAs emit it.
         AnsiString compact = "{\"challenges\":[{\"type\":\"http-01\",\"url\":\"u\",\"token\":\"t\"}]}";
         if (AcmeClient::FindChallengeOfType(compact, "http-01") < 0)
            throw 0;

         // A wildcard authorization offers dns-01 only: not-found is the answer.
         AnsiString dnsOnly = "{\"challenges\": [{\"type\": \"dns-01\", \"url\": \"u\", \"token\": \"t\"}]}";
         if (AcmeClient::FindChallengeOfType(dnsOnly, "http-01") >= 0)
            throw 0;

         // The type string appearing as some OTHER key's value must not count.
         AnsiString decoy = "{\"failedChallenge\": \"http-01\", \"challenges\": []}";
         if (AcmeClient::FindChallengeOfType(decoy, "http-01") >= 0)
            throw 0;
      }

      OutputDebugString(_T("hMailServer: Testing the ACME renewal window against short certificate lifetimes\n"));
      {
         // The renewal decision is a fraction of the lifetime, not a fixed number of
         // days, and these vectors are the reason. Certificate lifetimes are falling
         // on a published schedule - Let us Encrypt to 64 days in February 2027, the
         // maximum to 100 days in March 2027 and to 47 in March 2029 - and the fixed
         // 30-day window this replaced is longer than half of the last of those.
         const time_t day = 86400;
         const time_t issued = 1800000000;

         // 64 days and 47 days - Let us Encrypt's 2027 default and the 2029 maximum.
         // Neither divides by three, so these assert the PROPERTY rather than
         // restating the arithmetic: about a third of the lifetime is left when
         // renewal begins. Restating it would pass against any implementation,
         // including a wrong one copied from the same expression.
         const time_t lifetimes[] = { 64 * day, 47 * day, 100 * day, 10 * day };

         for (time_t lifetime : lifetimes)
         {
            time_t renewAt = AcmeClient::GetRenewalTime(issued, issued + lifetime);
            time_t remaining = (issued + lifetime) - renewAt;

            // A third, give or take the integer division.
            if (remaining < lifetime / 3 || remaining > lifetime / 3 + day)
               throw 0;

            // And never so late that a failure has no room for another attempt.
            if (remaining < day)
               throw 0;
         }

         // The one exact vector, because it is the claim that nothing moved for
         // anybody today: a 90-day certificate still renews with 30 days left.
         if (AcmeClient::GetRenewalTime(issued, issued + 90 * day) != issued + 60 * day)
            throw 0;


         // The floor. A two-day certificate would otherwise be renewed with 16 hours
         // left, and the renewal task runs hourly - one failed attempt and there is
         // no certificate. A full day leaves room for two dozen attempts.
         if (AcmeClient::GetRenewalTime(issued, issued + 2 * day) != issued + 1 * day)
            throw 0;

         // Shorter than the floor: renew immediately, which is the only honest
         // answer for a certificate that cannot be given a day of margin.
         if (AcmeClient::GetRenewalTime(issued, issued + 12 * 3600) != issued)
            throw 0;

         // An unreadable notBefore falls back to the 90-day assumption rather than
         // refusing to renew - a certificate with a bad start date still has a
         // perfectly good expiry to work back from.
         if (AcmeClient::GetRenewalTime(0, issued + 90 * day) != issued + 60 * day)
            throw 0;
      }

      OutputDebugString(_T("hMailServer: Testing the TLS-RPT record parser and report builder\n"));
      {
         // The shape RFC 8460 section 3 gives, with two mailto targets and the
         // URI parameter a real-world record carries.
         std::vector<String> addresses;
         if (!TlsRptReporterTask::ParseTlsRptRecord(
                "v=TLSRPTv1; rua=mailto:reports@example.com,mailto:backup@example.net?subject=tls", addresses))
            throw 0;
         if (addresses.size() != 2)
            throw 0;
         if (addresses[0] != _T("reports@example.com"))
            throw 0;
         // The ?subject parameter is part of the URI, not the mailbox.
         if (addresses[1] != _T("backup@example.net"))
            throw 0;

         // A policy whose rua names only https endpoints is a valid TLSRPTv1
         // record this implementation cannot deliver to: recognized, zero
         // addresses. The distinction matters - "not a policy" would make the
         // caller keep scanning TXT records that will never match.
         addresses.clear();
         if (!TlsRptReporterTask::ParseTlsRptRecord(
                "v=TLSRPTv1;rua=https://reporting.example.com/v1/tlsrpt", addresses))
            throw 0;
         if (!addresses.empty())
            throw 0;

         // An unrelated TXT record at the same name (an SPF policy, say) is not
         // a TLSRPT record at all.
         addresses.clear();
         if (TlsRptReporterTask::ParseTlsRptRecord("v=spf1 mx -all", addresses))
            throw 0;
         if (!addresses.empty())
            throw 0;

         // The report body: RFC 8460 section 4's required members, the counts,
         // the failure detail, and JSON escaping of a quote in a value that
         // reaches the report verbatim (the organization name is the
         // administrator's own text).
         TlsRptStore::DomainBucket bucket;
         bucket.policy_type = "no-policy-found";
         bucket.successful_sessions = 3;

         TlsRptStore::FailureDetail failure;
         failure.result_type = "validation-failure";
         failure.receiving_mx = "mx1.example.org";
         failure.count = 2;
         bucket.failures.push_back(failure);

         AnsiString json = TlsRptReporterTask::BuildReportJson(
            "2026-08-16", _T("example.org"), bucket, "report-id-1", _T("postmaster@sender.test"), "Acme \"Mail\" Ltd");

         if (json.Find("\"start-datetime\":\"2026-08-16T00:00:00Z\"") < 0)
            throw 0;
         if (json.Find("\"end-datetime\":\"2026-08-16T23:59:59Z\"") < 0)
            throw 0;
         if (json.Find("\"policy-domain\":\"example.org\"") < 0)
            throw 0;
         if (json.Find("\"policy-type\":\"no-policy-found\"") < 0)
            throw 0;
         if (json.Find("\"total-successful-session-count\":3") < 0)
            throw 0;
         if (json.Find("\"total-failure-session-count\":2") < 0)
            throw 0;
         if (json.Find("\"result-type\":\"validation-failure\"") < 0)
            throw 0;
         if (json.Find("\"receiving-mx-hostname\":\"mx1.example.org\"") < 0)
            throw 0;
         if (json.Find("\"failed-session-count\":2") < 0)
            throw 0;
         if (json.Find("\"organization-name\":\"Acme \\\"Mail\\\" Ltd\"") < 0)
            throw 0;
         if (json.Find("\"contact-info\":\"postmaster@sender.test\"") < 0)
            throw 0;
         if (json.Find("\"report-id\":\"report-id-1\"") < 0)
            throw 0;

         // An empty policy_string must not emit an empty policy-string array -
         // the member is simply absent.
         if (json.Find("policy-string") >= 0)
            throw 0;
      }

      OutputDebugString(_T("hMailServer: Testing the DMARC rua parser and report builder\n"));
      {
         // The rua tag with two mailto targets, one carrying the RFC 7489 6.2
         // size suffix and one a URI parameter: neither is part of the mailbox.
         std::vector<String> addresses;
         if (!DmarcRptReporterTask::ParseRuaTargets(
                "v=DMARC1; p=reject; rua=mailto:agg@example.com!10m,mailto:second@example.net?x=1", addresses))
            throw 0;
         if (addresses.size() != 2)
            throw 0;
         if (addresses[0] != _T("agg@example.com"))
            throw 0;
         if (addresses[1] != _T("second@example.net"))
            throw 0;

         // https-only rua: a recognized policy with zero deliverable targets.
         addresses.clear();
         if (!DmarcRptReporterTask::ParseRuaTargets(
                "v=DMARC1;p=none;rua=https://collector.example/dmarc", addresses))
            throw 0;
         if (!addresses.empty())
            throw 0;

         // An SPF record at the same name is not a DMARC policy at all.
         addresses.clear();
         if (DmarcRptReporterTask::ParseRuaTargets("v=spf1 mx -all", addresses))
            throw 0;

         // RFC 7489 7.1 draws the external-destination line at the
         // organizational domain: a report mailbox on another host of the same
         // registrant needs no consent record; anywhere else does.
         if (DmarcRptReporterTask::IsExternalDestination(_T("mail.example.com"), _T("example.com")))
            throw 0;
         if (!DmarcRptReporterTask::IsExternalDestination(_T("example.com"), _T("collector.example")))
            throw 0;

         // The report body: RFC 7489 Appendix C's members, and XML escaping of
         // a value the administrator controls.
         DmarcRptStore::DomainBucket bucket;
         bucket.policy_domain = "example.org";
         bucket.adkim = "s";
         bucket.p = "quarantine";
         bucket.pct = 50;

         DmarcRptStore::Row row;
         row.source_ip = "192.0.2.7";
         row.disposition = "quarantine";
         row.dkim = "fail";
         row.spf = "fail";
         row.header_from = "example.org";
         row.envelope_from_domain = "bounce.example.org";
         row.spf_passed = true;
         row.dkim_passing_domains.push_back("other.example");
         row.count = 3;
         bucket.rows.push_back(row);

         AnsiString xml = DmarcRptReporterTask::BuildReportXml(
            "2026-08-16", bucket, "rpt-1", _T("postmaster@sender.test"), "A & B <Ltd>");

         // The day's epoch range, computed the same way the builder computes
         // it rather than hand-derived, so the assertion cannot drift from a
         // timezone or leap assumption.
         tm dayStart = {};
         dayStart.tm_year = 2026 - 1900;
         dayStart.tm_mon = 7;
         dayStart.tm_mday = 16;
         __int64 expectedBegin = _mkgmtime64(&dayStart);

         AnsiString expectedRange;
         expectedRange.Format("<date_range><begin>%I64d</begin><end>%I64d</end></date_range>",
            expectedBegin, expectedBegin + 86399);
         if (xml.Find(expectedRange) < 0)
            throw 0;

         if (xml.Find("<org_name>A &amp; B &lt;Ltd&gt;</org_name>") < 0)
            throw 0;
         if (xml.Find("<email>postmaster@sender.test</email>") < 0)
            throw 0;
         if (xml.Find("<report_id>rpt-1</report_id>") < 0)
            throw 0;
         if (xml.Find("<domain>example.org</domain>") < 0)
            throw 0;
         if (xml.Find("<adkim>s</adkim>") < 0)
            throw 0;
         // aspf was not published; the default is reported explicitly.
         if (xml.Find("<aspf>r</aspf>") < 0)
            throw 0;
         if (xml.Find("<p>quarantine</p>") < 0)
            throw 0;
         // No sp tag published: the member is absent, not empty.
         if (xml.Find("<sp>") >= 0)
            throw 0;
         if (xml.Find("<pct>50</pct>") < 0)
            throw 0;
         if (xml.Find("<source_ip>192.0.2.7</source_ip>") < 0)
            throw 0;
         if (xml.Find("<count>3</count>") < 0)
            throw 0;
         if (xml.Find("<disposition>quarantine</disposition>") < 0)
            throw 0;
         if (xml.Find("<header_from>example.org</header_from>") < 0)
            throw 0;
         if (xml.Find("<dkim><domain>other.example</domain><result>pass</result></dkim>") < 0)
            throw 0;
         // The raw SPF result, distinct from the aligned fail above it.
         if (xml.Find("<spf><domain>bounce.example.org</domain><result>pass</result></spf>") < 0)
            throw 0;
      }

      OutputDebugString(_T("hMailServer: Testing AccountLockout\n"));
      {
         // Config travels as parameters (the /Test ini has no lockout keys),
         // clock as an explicit epoch - the TryConsumeAt pattern. Threshold 3,
         // ten-minute window, five-minute lockout.
         AccountLockout *lockout = AccountLockout::Instance();
         const time_t t0 = 1000000;

         // Two failures do not lock; the third does, and the lock expires on
         // schedule rather than early or never.
         lockout->RecordFailureAt(_T("selftest-a@x.test"), t0, 3, 600, 300);
         lockout->RecordFailureAt(_T("selftest-a@x.test"), t0 + 1, 3, 600, 300);
         if (lockout->IsLockedOutAt(_T("selftest-a@x.test"), t0 + 2, 300))
            throw 0;
         lockout->RecordFailureAt(_T("selftest-a@x.test"), t0 + 2, 3, 600, 300);
         if (!lockout->IsLockedOutAt(_T("selftest-a@x.test"), t0 + 3, 300))
            throw 0;
         if (!lockout->IsLockedOutAt(_T("SELFTEST-A@X.TEST"), t0 + 3, 300))
            throw 0; // the name is one identity whatever its case
         if (lockout->IsLockedOutAt(_T("selftest-a@x.test"), t0 + 2 + 300, 300))
            throw 0;

         // Failures outside the window have aged out: two stale plus one
         // fresh is not three.
         lockout->RecordFailureAt(_T("selftest-b@x.test"), t0, 3, 600, 300);
         lockout->RecordFailureAt(_T("selftest-b@x.test"), t0 + 1, 3, 600, 300);
         lockout->RecordFailureAt(_T("selftest-b@x.test"), t0 + 700, 3, 600, 300);
         if (lockout->IsLockedOutAt(_T("selftest-b@x.test"), t0 + 701, 300))
            throw 0;

         // A successful logon clears the groundwork an attacker laid.
         lockout->RecordFailureAt(_T("selftest-c@x.test"), t0, 3, 600, 300);
         lockout->RecordFailureAt(_T("selftest-c@x.test"), t0 + 1, 3, 600, 300);
         lockout->RecordSuccess(_T("selftest-c@x.test"));
         lockout->RecordFailureAt(_T("selftest-c@x.test"), t0 + 2, 3, 600, 300);
         if (lockout->IsLockedOutAt(_T("selftest-c@x.test"), t0 + 3, 300))
            throw 0;

         // A clock that stepped backwards after the lock was set must not
         // extend it past its nominal duration.
         lockout->RecordFailureAt(_T("selftest-d@x.test"), t0, 1, 600, 300);
         if (!lockout->IsLockedOutAt(_T("selftest-d@x.test"), t0 - 10000, 300))
            throw 0;
         if (lockout->IsLockedOutAt(_T("selftest-d@x.test"), t0 - 10000 + 301, 300))
            throw 0;

         // Attempts made WHILE locked are not counted, and the window restarts
         // when the lock expires - so a re-lock costs a fresh threshold rather
         // than one guess. Without both halves an attacker holding a trickle of
         // guesses on a name kept it locked for ever, which is a denial of
         // service against the mailbox the feature exists to protect.
         // Threshold 3, window 600, lock 300.
         lockout->RecordFailureAt(_T("selftest-e@x.test"), t0, 3, 600, 300);
         lockout->RecordFailureAt(_T("selftest-e@x.test"), t0 + 1, 3, 600, 300);
         lockout->RecordFailureAt(_T("selftest-e@x.test"), t0 + 2, 3, 600, 300);
         if (!lockout->IsLockedOutAt(_T("selftest-e@x.test"), t0 + 3, 300))
            throw 0;

         // Twenty attempts across the whole lock, all refused without the password
         // being checked and therefore all uncounted.
         for (int i = 0; i < 20; i++)
            lockout->RecordFailureAt(_T("selftest-e@x.test"), t0 + 10 + (i * 10), 3, 600, 300);

         // The lock still ends when it was always going to end...
         if (!lockout->IsLockedOutAt(_T("selftest-e@x.test"), t0 + 2 + 299, 300))
            throw 0;
         if (lockout->IsLockedOutAt(_T("selftest-e@x.test"), t0 + 2 + 300, 300))
            throw 0;

         // ...and the first two failures afterwards do not re-lock it, which is
         // the assertion that fails if either half of the fix is removed.
         lockout->RecordFailureAt(_T("selftest-e@x.test"), t0 + 400, 3, 600, 300);
         if (lockout->IsLockedOutAt(_T("selftest-e@x.test"), t0 + 401, 300))
            throw 0;
         lockout->RecordFailureAt(_T("selftest-e@x.test"), t0 + 402, 3, 600, 300);
         if (lockout->IsLockedOutAt(_T("selftest-e@x.test"), t0 + 403, 300))
            throw 0;

         // The third does.
         lockout->RecordFailureAt(_T("selftest-e@x.test"), t0 + 404, 3, 600, 300);
         if (!lockout->IsLockedOutAt(_T("selftest-e@x.test"), t0 + 405, 300))
            throw 0;

         lockout->RecordSuccess(_T("selftest-e@x.test"));

         lockout->RecordSuccess(_T("selftest-a@x.test"));
         lockout->RecordSuccess(_T("selftest-b@x.test"));
         lockout->RecordSuccess(_T("selftest-c@x.test"));
         lockout->RecordSuccess(_T("selftest-d@x.test"));
      }

      OutputDebugString(_T("hMailServer: Testing Base64\n"));
      ModifiedUTF7Tester modifiedUTF7Tester;
      modifiedUTF7Tester.Test();

      OutputDebugString(_T("hMailServer: Testing AddresslistParser\n"));
      AddresslistParserTester *pTest2 = new AddresslistParserTester();
      pTest2->Test();
      delete pTest2;

      OutputDebugString(_T("hMailServer: Testing charset\n"));
      CharsetTester *pCharsetTester = new CharsetTester;
      pCharsetTester->Test();
      delete pCharsetTester;


      OutputDebugString(_T("hMailServer: Testing LocalIPAddresses\n"));
      LocalIPAddressesTester *pTest4 = new LocalIPAddressesTester();
      pTest4->Test();
      delete pTest4;

      OutputDebugString(_T("hMailServer: Testing MessageData\n"));
      MessageDataTester *pMsgData = new MessageDataTester();
      pMsgData->Test();
      delete pMsgData;

      OutputDebugString(_T("hMailServer: Testing Time\n"));
      TimeTester *pTimeT = new TimeTester();
      pTimeT->Test();
      delete pTimeT;




      OutputDebugString(_T("hMailServer: Testing BlowFishEncryptorTester\n"));
      BlowFishEncryptorTester *pTest3 = new BlowFishEncryptorTester();
      pTest3->Test();
      delete pTest3;

      OutputDebugString(_T("hMailServer: Testing IMAPSimpleCommandParserTester\n"));
      IMAPSimpleCommandParserTester *pTest = new IMAPSimpleCommandParserTester();
      pTest->Test();
      delete pTest;

   }

   void 
   ClassTester::LoadSettings_()
   {
      String sAppPath = Utilities::GetBinDirectory();
      if (sAppPath.Right(1) != _T("\\"))
         sAppPath += _T("\\");

      String sConfigFile = sAppPath + "test_config.xml";
      String sTestSpec = FileUtilities::ReadCompleteTextFile(sConfigFile);

      if (sTestSpec.IsEmpty())
         return;

      XDoc oDoc;
      oDoc.Load(sTestSpec);

      XNode *pBackupNode = oDoc.GetChild(_T("Config"));

      if (!pBackupNode)
         throw;

      mime_data_path_ = pBackupNode->GetChildValue(_T("MimeDataPath"));
   }

   void 
   ClassTester::TestBackup_()
   {
      std::shared_ptr<BackupManager> pBackupManager = Application::Instance()->GetBackupManager();
      std::shared_ptr<Backup> pBackup = pBackupManager->LoadBackup("C:\\Temp\\Backup\\HMBackup 2006-12-10 091555.zip");
      pBackup->SetRestoreOptions(1 | 2 | 4 | 8 | 16 | 32);
      pBackupManager->StartRestore(pBackup);

      Sleep(1000000);
   }
}
