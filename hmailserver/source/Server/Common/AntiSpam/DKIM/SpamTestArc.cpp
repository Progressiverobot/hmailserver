// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// ARC results used for inbound filtering (RFC 8617). See SpamTestArc.h for
// the trust model and the reasons behind each refusal below.
//
// Everything parsed in this file arrives in an unauthenticated message.

#include "StdAfx.h"

#include "SpamTestArc.h"

#include "Arc.h"
#include "DKIM.h"

#include "../SpamTestData.h"
#include "../SpamTestResult.h"
#include "../AntiSpamConfiguration.h"
#include "../DMARC/DMARC.h"

#include "../../BO/Message.h"
#include "../../BO/MessageData.h"
#include "../../Persistence/PersistentMessage.h"

#include "../../../SMTP/SPF/SPF.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // "example.org, example.net" / semicolons / whitespace - whatever an
      // administrator plausibly types - into lower-cased domain tokens.
      std::vector<AnsiString> ParseTrustedSealerList(const String &configuredList)
      {
         AnsiString flattened = AnsiString(configuredList);
         flattened.Replace(",", " ");
         flattened.Replace(";", " ");
         flattened.Replace("\t", " ");

         std::vector<AnsiString> result;

         for (AnsiString domain : StringParser::SplitString(flattened, " "))
         {
            domain.Trim();
            domain.MakeLower();

            if (!domain.IsEmpty())
               result.push_back(domain);
         }

         return result;
      }

      bool IsTrustedSealer(const std::vector<AnsiString> &trustedSealers, const AnsiString &sealerDomain)
      {
         // Exact match only, deliberately: the list names the precise d=
         // identities whose seals are honoured. A suffix match would make
         // "trusted.example" also honour "attacker-trusted.example" unless
         // written very carefully, and this list is a trust anchor.
         for (const AnsiString &trusted : trustedSealers)
         {
            if (trusted.Compare(sealerDomain) == 0)
               return true;
         }

         return false;
      }

      // Returns every result keyword recorded for the given method in an
      // RFC 8601 Authentication-Results value, lower-cased. "dkim=pass
      // header.d=x" and "dkim=fail" in the same value yield {"pass","fail"}.
      std::vector<AnsiString> ExtractMethodResults(const AnsiString &authenticationResults, const AnsiString &method)
      {
         AnsiString value = authenticationResults;
         value.Replace("\t", " ");

         // Strip RFC 8601 CFWS comments first, innermost pair at a time, so a
         // "dkim=pass" that appears only inside a comment is never read as a
         // verdict. The loop is bounded: comments can nest, but each pass
         // removes one pair and no sane value carries dozens.
         for (int guard = 0; guard < 64; guard++)
         {
            int openPosition = -1;
            bool removedOne = false;

            for (int i = 0; i < value.GetLength(); i++)
            {
               if (value[i] == '(')
               {
                  openPosition = i;
               }
               else if (value[i] == ')' && openPosition >= 0)
               {
                  value = value.Mid(0, openPosition) + " " + value.Mid(i + 1);
                  removedOne = true;
                  break;
               }
            }

            if (!removedOne)
               break;
         }

         std::vector<AnsiString> results;

         // Clauses are ';'-separated; the first token of a clause is
         // "method=result", anything after it is properties (header.d=...).
         // The leading "i=1" and authserv-id clauses fall through naturally:
         // "i" is not a method we ask for, and the authserv-id has no '='.
         for (AnsiString clause : StringParser::SplitString(value, ";"))
         {
            clause.Trim();

            AnsiString firstToken;
            for (AnsiString token : StringParser::SplitString(clause, " "))
            {
               token.Trim();
               if (!token.IsEmpty())
               {
                  firstToken = token;
                  break;
               }
            }

            if (firstToken.IsEmpty())
               continue;

            firstToken.MakeLower();

            int equalsPosition = firstToken.Find("=");
            if (equalsPosition <= 0)
               continue;

            AnsiString clauseMethod = firstToken.Mid(0, equalsPosition);
            if (clauseMethod.Compare(method) != 0)
               continue;

            AnsiString clauseResult = firstToken.Mid(equalsPosition + 1);
            if (!clauseResult.IsEmpty())
               results.push_back(clauseResult);
         }

         return results;
      }

      // Did the message authenticate where it first entered the (trusted)
      // chain? This is the result the offset "recovers".
      //
      // A recorded dmarc= verdict is authoritative when present: dmarc=pass
      // is exactly the thing forwarding destroyed, and any other dmarc value
      // means the failure PREDATES the forwarding - offsetting it would
      // forgive a failure the forwarder itself witnessed, so an explicit
      // non-pass refuses even if dkim/spf clauses look good. With no dmarc
      // clause (this server's own AAR writer, for one, records only dkim and
      // spf), a dkim=pass or spf=pass at the first hop is accepted as the
      // recovered evidence; the sealer wrote it, and the sealer is trusted -
      // that trust is the entire basis of this feature.
      bool OriginalAuthenticationPassed(const AnsiString &oldestAuthenticationResults, AnsiString &recoveredResult)
      {
         std::vector<AnsiString> dmarcResults = ExtractMethodResults(oldestAuthenticationResults, "dmarc");
         if (!dmarcResults.empty())
         {
            for (const AnsiString &result : dmarcResults)
            {
               if (result == "pass")
               {
                  recoveredResult = "dmarc=pass";
                  return true;
               }
            }

            return false;
         }

         for (const AnsiString &result : ExtractMethodResults(oldestAuthenticationResults, "dkim"))
         {
            if (result == "pass")
            {
               recoveredResult = "dkim=pass";
               return true;
            }
         }

         for (const AnsiString &result : ExtractMethodResults(oldestAuthenticationResults, "spf"))
         {
            if (result == "pass")
            {
               recoveredResult = "spf=pass";
               return true;
            }
         }

         return false;
      }
   }

   String
   SpamTestArc::GetName() const
   {
      return GetTestName();
   }

   String
   SpamTestArc::GetTestName()
   {
      return "SpamTestArc";
   }

   bool
   SpamTestArc::GetIsEnabled()
   {
      // Gated on the DMARC test rather than on this test's own settings: the
      // only thing this test ever does is offset a score SpamTestDMARC is
      // about to add. With DMARC scoring disabled there is no penalty to
      // offset, and a negative score emitted anyway would be an unmatched
      // credit any message with a chain could collect. The ARC-specific
      // settings are read in RunTest, after the cheap "does the message even
      // carry a chain" probe, so the overwhelming majority of messages never
      // touch them.
      AntiSpamConfiguration &config = Configuration::Instance()->GetAntiSpamConfiguration();
      return config.GetDMARCEnabled();
   }

   std::set<std::shared_ptr<SpamTestResult> >
   SpamTestArc::RunTest(std::shared_ptr<SpamTestData> pTestData)
   {
      // Every refusal below returns this set empty. Empty is the baseline: it
      // is what a message with no chain gets, so no failure mode - cv=fail,
      // invalid chain, DNS temperror, untrusted sealer, malformed chain - can
      // ever be more permissive than carrying no chain at all.
      std::set<std::shared_ptr<SpamTestResult> > setSpamTestResults;

      std::shared_ptr<MessageData> pMessageData = pTestData->GetMessageData();
      if (!pMessageData)
         return setSpamTestResults;

      std::shared_ptr<Message> pMessage = pMessageData->GetMessage();
      if (!pMessage)
         return setSpamTestResults;

      const String fileName = PersistentMessage::GetFileName(pMessage);

      AnsiString header = PersistentMessage::LoadHeader(fileName);
      if (header.IsEmpty())
         return setSpamTestResults;

      // Cheap presence probe before anything else, settings reads included:
      // almost no direct mail carries ARC headers, and reading a PropertySet
      // row that a not-yet-upgraded database does not have reports error 5015
      // - once per read, not once ever. Probing first confines that to the
      // rare ARC-bearing message until the settings rows exist, and to
      // nothing afterwards. A false positive here (a field NAME merely
      // starting with "arc-seal") just proceeds to the real parser.
      AnsiString lowerHeader = header;
      lowerHeader.MakeLower();
      if (!lowerHeader.StartsWith("arc-seal") && lowerHeader.Find("\narc-seal") < 0)
         return setSpamTestResults;

      AntiSpamConfiguration &arcConfig = Configuration::Instance()->GetAntiSpamConfiguration();

      if (!arcConfig.GetArcFilteringEnabled())
         return setSpamTestResults;

      // ARC IS ONLY WORTH ANYTHING FROM A SENDER YOU TRUST. An attacker can
      // fabricate an entire chain, seal every link with keys they publish in
      // their own DNS, and it will validate perfectly - RFC 8617 7.1 says in
      // as many words that a passing chain conveys no trust by itself. The
      // trusted-sealer list is therefore not an option of this feature; it IS
      // the feature. Without it this test MUST do nothing, and does.
      std::vector<AnsiString> trustedSealers = ParseTrustedSealerList(arcConfig.GetArcTrustedSealers());
      if (trustedSealers.empty())
         return setSpamTestResults;

      AntiSpamConfiguration &config = Configuration::Instance()->GetAntiSpamConfiguration();

      // The offset is defined as minus the DMARC failure score, so with that
      // score unset (or nonsensical) there is nothing this test could
      // meaningfully produce.
      int dmarcFailureScore = config.GetDMARCFailureScore();
      if (dmarcFailureScore <= 0)
         return setSpamTestResults;

      // SpamTestDMARC applies its score to a message carrying more than one
      // From header as a forgery defence (RFC 7489 6.6.1), not as an
      // authentication outcome - forwarding does not add From headers, so
      // that penalty is never forwarding damage and must never be offset.
      if (pMessageData->GetFieldOccurrenceCount(_T("From")) > 1)
         return setSpamTestResults;

      // Parse the chain - no DNS, no crypto - to learn who sealed it.
      Arc::ChainInfo chainInfo;
      if (Arc::ParseChain(header, chainInfo) != Arc::ChainParseStatus::Parsed)
      {
         // Malformed (or vanished between probe and parse): judged exactly as
         // if it carried no chain.
         return setSpamTestResults;
      }

      // The trust gate comes BEFORE validation on purpose. Validation costs
      // DNS lookups against domains the SENDER chose and signature checks on
      // the connection thread; refusing untrusted chains first means the only
      // mail that can make us do that work is mail whose every sealer the
      // administrator explicitly listed.
      for (const AnsiString &sealerDomain : chainInfo.sealer_domains)
      {
         if (!IsTrustedSealer(trustedSealers, sealerDomain))
         {
            LOG_DEBUG("ARC: Chain sealed by " + sealerDomain + ", which is not a trusted sealer. The chain is ignored.");
            return setSpamTestResults;
         }
      }

      // Recover the original authentication result from the instance-1
      // ARC-Authentication-Results. Still parse-only; if the first hop did
      // not record a pass there is nothing to recover and the expensive
      // validation below is never paid.
      AnsiString recoveredResult;
      if (!OriginalAuthenticationPassed(chainInfo.oldest_authentication_results, recoveredResult))
         return setSpamTestResults;

      // Full RFC 8617 5.2 validation: the cv= ladder, the newest
      // ARC-Message-Signature (which pins the CURRENT message content, From
      // and body included, to a trusted signer - this is what stops a
      // captured chain being transplanted onto other mail), and EVERY seal in
      // the chain, so the instance-1 results acted on above were provably
      // written by the trusted domain that claims them.
      Arc::ChainValidationStatus validation = Arc::ValidateChain(fileName, header);
      if (validation != Arc::ChainValidationStatus::Pass)
      {
         // Fail covers a cv=fail anywhere in the chain and any signature that
         // does not verify. TempError means a key lookup did not complete: we
         // proved nothing, so we act on nothing - failing closed costs one
         // forwarded message its offset, failing open would let DNS
         // unavailability hand out score credits.
         // Braced, because LOG_DEBUG expands to a bare `if (...) Log(...);` - an
         // unbraced else after it binds to the macro's own if, which is a compile error
         // here and would be a silent mis-binding somewhere less lucky.
         if (validation == Arc::ChainValidationStatus::TempError)
         {
            LOG_DEBUG("ARC: Chain validation did not complete (DNS). The chain is ignored.");
         }
         else
         {
            LOG_DEBUG("ARC: Chain did not validate. The chain is ignored.");
         }

         return setSpamTestResults;
      }

      // Establish that the DMARC penalty this offset exists to cancel is
      // actually coming, by evaluating DMARC exactly as SpamTestDMARC will -
      // same identities, same inputs, same calls. If DMARC will pass, or the
      // domain publishes no policy or p=none, or the lookup temp-fails,
      // SpamTestDMARC adds no score and an offset would be an unmatched
      // credit, so all of those refuse.
      //
      // (Two evaluations moments apart can in principle disagree if DNS
      // answers change between them; the pairing is by construction, not by a
      // shared result. The disagreement that would matter - we see FailReject,
      // SpamTestDMARC then sees anything else - needs a mid-message DNS flip
      // AND a fully validated trusted chain, and its worst case is a single
      // message credited one DMARC failure score. Accepted, and one reason
      // this test runs immediately before SpamTestDMARC.)
      String fromHeader = pMessageData->GetFrom();
      if (fromHeader.IsEmpty())
         return setSpamTestResults;

      String fromAddress = DMARC::ExtractAddressFromHeaderValue(fromHeader);
      String fromDomain = StringParser::ExtractDomain(fromAddress);
      if (fromDomain.IsEmpty())
         return setSpamTestResults;

      bool spfPassed = false;
      String envelopeFromDomain = StringParser::ExtractDomain(pTestData->GetEnvelopeFrom());

      const IPAddress &originatingAddress = pTestData->GetOriginatingIP();
      if (!originatingAddress.IsAny() && !pTestData->GetEnvelopeFrom().IsEmpty())
      {
         String sExplanation;
         SPF::Result spfResult = SPF::Instance()->Test(originatingAddress.ToString(), pTestData->GetEnvelopeFrom(), pTestData->GetHeloHost(), sExplanation);
         spfPassed = (spfResult == SPF::Pass);
      }

      std::vector<AnsiString> dkimPassingDomains;

      DKIM dkim;
      dkim.Verify(fileName, dkimPassingDomains);

      DMARC dmarc;
      DMARC::Result dmarcResult = dmarc.Verify(fromDomain, envelopeFromDomain, spfPassed, dkimPassingDomains);

      if (dmarcResult != DMARC::FailReject && dmarcResult != DMARC::FailQuarantine)
         return setSpamTestResults;

      // The offset: exactly minus the DMARC failure score, never more. The
      // net contribution of the DMARC-plus-ARC pair is therefore zero - the
      // same as a message that passed DMARC outright - and every other test's
      // score stands. A trusted valid chain repairs the forwarding damage; it
      // cannot push a message below the score its content earns, so it can
      // never turn "would be rejected" into "trusted" on its own.
      //
      // The result is typed Pass, not Fail, although only Fail-typed results
      // appear in the X-hMailServer-Reason-* headers: GetSpamTestResultMessage_
      // uses the FIRST Fail-typed result in an unordered set as the SMTP
      // rejection text, so a Fail-typed credit could become the stated reason
      // for rejecting a message that other tests condemned. The offset is
      // visible in the debug log and in the runner's per-test score line.
      String sMessage = Formatter::Format("ARC: Valid chain from trusted sealer(s); the first hop recorded {0}. The DMARC failure was caused in transit and its score is offset.", String(recoveredResult));

      LOG_DEBUG(sMessage);

      std::shared_ptr<SpamTestResult> pResult =
         std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Pass, -dmarcFailureScore, sMessage));
      setSpamTestResults.insert(pResult);

      return setSpamTestResults;
   }

}
