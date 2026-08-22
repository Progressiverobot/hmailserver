// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "SpamTestDMARC.h"

#include "DMARC.h"

#include "../SpamTestData.h"
#include "../AuthenticationResults.h"
#include "../SpamTestResult.h"
#include "../AntiSpamConfiguration.h"
#include "../AntiSpamDiagnostics.h"
#include "../DKIM/DKIM.h"

#include "../../BO/MessageData.h"
#include "../../Persistence/PersistentMessage.h"
#include "../../Util/DmarcRptStore.h"
#include "../../Util/Parsing/StringParser.h"
#include "../../../SMTP/SPF/SPF.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String 
   SpamTestDMARC::GetName() const
   {
      return GetTestName();
   }

   String 
   SpamTestDMARC::GetTestName()
   {
      return "SpamTestDMARC";
   }

   bool 
   SpamTestDMARC::GetIsEnabled()
   {
      AntiSpamConfiguration &config = Configuration::Instance()->GetAntiSpamConfiguration();
      return config.GetDMARCEnabled();
   }

   std::set<std::shared_ptr<SpamTestResult> >
   SpamTestDMARC::RunTest(std::shared_ptr<SpamTestData> pTestData)
   {
      std::set<std::shared_ptr<SpamTestResult> > setSpamTestResults;

      std::shared_ptr<MessageData> pMessageData = pTestData->GetMessageData();
      if (!pMessageData)
         return setSpamTestResults;

      // Determine the RFC5322.From domain.
      String fromHeader = pMessageData->GetFrom();
      if (fromHeader.IsEmpty())
         return setSpamTestResults;

      String fromAddress = DMARC::ExtractAddressFromHeaderValue(fromHeader);
      String fromDomain = StringParser::ExtractDomain(fromAddress);
      if (fromDomain.IsEmpty())
         return setSpamTestResults;

      // RFC 5322 3.6 allows exactly one From, and RFC 7489 6.6.1 requires a message
      // carrying more than one to be refused or treated as failing DMARC. That is not
      // pedantry here - it is the difference between two identities:
      //
      //    From: attacker@evil.example
      //    From: ceo@bank.example
      //    DKIM-Signature: ... d=evil.example; h=from:subject; b=...
      //
      // DKIM signs and verifies over the LAST From (RFC 6376 5.4.2, implemented in
      // Canonicalization.cpp), while fromDomain above comes from GetFrom(), which is
      // MimeHeader::FindField and answers with the FIRST. So the attacker signs one
      // From with a domain they own, DKIM passes, DMARC finds that domain aligned with
      // the From it read - and the recipient's client displays the other one. The
      // message is delivered looking like it came from the bank, and if it is relayed
      // on, Arc::BuildAuthenticationResults_ stamps dkim=pass into a sealed
      // ARC-Authentication-Results that downstream ARC-trusting receivers accept.
      //
      // Checked before any alignment is computed, so no verdict is ever reached from an
      // identity the author did not assert.
      int fromFieldCount = pMessageData->GetFieldOccurrenceCount(_T("From"));
      if (fromFieldCount > 1)
      {
         AntiSpamConfiguration &antiSpamConfig = Configuration::Instance()->GetAntiSpamConfiguration();

         String sMessage;
         sMessage.Format(_T("Blocked by DMARC: the message carries %d From header fields; RFC 5322 permits one."), fromFieldCount);

         std::shared_ptr<SpamTestResult> pResult =
            std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Fail, antiSpamConfig.GetDMARCFailureScore(), sMessage));
         setSpamTestResults.insert(pResult);

         return setSpamTestResults;
      }

      // Evaluate SPF for the envelope sender.
      bool spfPassed = false;
      String envelopeFromDomain = StringParser::ExtractDomain(pTestData->GetEnvelopeFrom());

      const IPAddress &originatingAddress = pTestData->GetOriginatingIP();
      if (!originatingAddress.IsAny() && !pTestData->GetEnvelopeFrom().IsEmpty())
      {
         String sExplanation;
         SPF::Result spfResult = SPF::Instance()->Test(originatingAddress.ToString(), pTestData->GetEnvelopeFrom(), pTestData->GetHeloHost(), sExplanation);
         spfPassed = (spfResult == SPF::Pass);
      }

      // Evaluate DKIM and collect the signing domains that verified, and the
      // selector each of them used - the second is for the aggregate report rather
      // than for alignment, which only ever compares domains.
      std::vector<AnsiString> dkimPassingDomains;
      std::vector<AnsiString> dkimPassingSelectors;

      std::shared_ptr<Message> pMessage = pMessageData->GetMessage();
      if (pMessage)
      {
         const String fileName = PersistentMessage::GetFileName(pMessage);

         DKIM dkim;
         dkim.Verify(fileName, dkimPassingDomains, dkimPassingSelectors);
      }

      DMARC dmarc;
      DMARC::Evaluation evaluation;
      DMARC::Result result = dmarc.Verify(fromDomain, envelopeFromDomain, spfPassed, dkimPassingDomains, &evaluation);

      // Aggregate reporting (RFC 7489 section 7.2): every message evaluated
      // against a published policy is counted, passes included - a report that
      // only carried failures would tell a domain owner nothing about whether
      // their legitimate mail aligns. TempError and PermError are excluded:
      // section 7.2 reports applied policy, and neither reached a verdict.
      //
      // The disposition recorded is the POLICY-EVALUATED one - what the
      // published policy asked for - not this server's eventual action, which
      // is decided later by the score threshold across every spam test.
      if (evaluation.policy_found &&
          (result == DMARC::Pass || result == DMARC::FailNone ||
           result == DMARC::FailQuarantine || result == DMARC::FailReject))
      {
         AnsiString disposition;
         switch (result)
         {
         case DMARC::FailReject:
            disposition = "reject";
            break;
         case DMARC::FailQuarantine:
            disposition = "quarantine";
            break;
         default:
            // Pass, and FailNone: the policy requested no action.
            disposition = "none";
            break;
         }

         AnsiString sourceIp = originatingAddress.IsAny() ? AnsiString("0.0.0.0") : AnsiString(originatingAddress.ToString());

         // RFC 9990 3.1.1.5 names exactly two values for discovery_method, and
         // the mapping happens here rather than in the store so that the store
         // stays a plain container - and so that the enum, which only the
         // evaluator can produce, does not have to travel into Util.
         AnsiString discoveryMethod =
            evaluation.discovery_method == DMARC::DiscoveryTreeWalk ? "treewalk" : "psl";

         DmarcRptStore::Instance()->Record(
            AnsiString(evaluation.policy_domain),
            AnsiString(evaluation.adkim), AnsiString(evaluation.aspf),
            AnsiString(evaluation.p), AnsiString(evaluation.sp), AnsiString(evaluation.np),
            AnsiString(evaluation.t), evaluation.pct, discoveryMethod,
            sourceIp, disposition,
            evaluation.dkim_aligned, evaluation.spf_aligned,
            AnsiString(fromDomain),
            AnsiString(envelopeFromDomain), spfPassed, dkimPassingDomains, dkimPassingSelectors);
      }

      AntiSpamConfiguration &config = Configuration::Instance()->GetAntiSpamConfiguration();

      std::shared_ptr<AuthenticationResults> authenticationResults = pTestData->GetAuthenticationResults();
      if (authenticationResults)
      {
         AuthenticationResults::MethodResult methodResult = AuthenticationResults::ResultNone;
         AnsiString policyText;

         switch (result)
         {
         case DMARC::Pass:
            methodResult = AuthenticationResults::ResultPass;
            break;
         case DMARC::FailReject:
            methodResult = AuthenticationResults::ResultFail;
            policyText = "reject";
            break;
         case DMARC::FailQuarantine:
            methodResult = AuthenticationResults::ResultFail;
            policyText = "quarantine";
            break;
         case DMARC::FailNone:
            methodResult = AuthenticationResults::ResultFail;
            policyText = "none";
            break;
         case DMARC::TempError:
            methodResult = AuthenticationResults::ResultTempError;
            break;
         case DMARC::PermError:
            methodResult = AuthenticationResults::ResultPermError;
            break;
         default:
            // NoPolicy: we looked and the domain publishes none, which is "none" -
            // distinct from the check not having run at all.
            methodResult = AuthenticationResults::ResultNone;
            break;
         }

         // The policy travels with the verdict because the keyword cannot carry it. A
         // downstream filter treats "dmarc=fail" from a p=none domain, which is asking
         // to be told and nothing more, very differently from a p=reject the domain
         // owner meant to be enforced.
         authenticationResults->SetDmarc(methodResult, AnsiString(fromDomain), policyText);
      }

      switch (result)
      {
      case DMARC::Pass:
         {
            std::shared_ptr<SpamTestResult> pResult = std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Pass, 0, ""));
            setSpamTestResults.insert(pResult);
            break;
         }
      case DMARC::FailReject:
      case DMARC::FailQuarantine:
         {
            int iScore = config.GetDMARCFailureScore();

            String sMessage;
            sMessage.Format(_T("Blocked by DMARC policy of %s."), fromDomain.c_str());

            std::shared_ptr<SpamTestResult> pResult = std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Fail, iScore, sMessage));
            setSpamTestResults.insert(pResult);
            break;
         }
      case DMARC::FailNone:
         {
            // The domain owner requested no action (p=none); only log.
            LOG_DEBUG("DMARC: Message failed DMARC for domain " + AnsiString(fromDomain) + " but policy is none.");
            break;
         }
      case DMARC::TempError:
         {
            // The policy record could not be looked up, so a p=reject domain was
            // just treated exactly like a domain with no DMARC record at all: the
            // message is accepted and nothing is scored. Per message that is the
            // right call, but while the resolver is unavailable it is true of
            // every message, and a DMARC check that has quietly stopped checking
            // looks the same from the outside as one that is passing everything.
            AntiSpamDiagnostics::ReportCheckIncomplete(AntiSpamDiagnostics::CheckDmarc,
               Formatter::Format("DMARC: The policy lookup for {0} did not complete. Policies cannot be applied while that continues, and affected messages are accepted without a DMARC verdict.", fromDomain));
            break;
         }
      case DMARC::PermError:
         {
            LOG_DEBUG("DMARC: The policy record of " + AnsiString(fromDomain) + " could not be evaluated. No score applied.");
            break;
         }
      default:
         {
            // NoPolicy: the domain published no policy, so there is nothing to do.
            break;
         }
      }

      return setSpamTestResults;
   }

}
