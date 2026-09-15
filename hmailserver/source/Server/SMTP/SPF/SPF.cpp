// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include <ctime>

#include "SPF.h"

#include "SPFAddress.h"
#include "SPFDnsResolver.h"
#include "SPFEvaluator.h"
#include "SPFEvaluatorTester.h"
#include "SPFMacroExpanderTester.h"
#include "SPFRecordTester.h"
#include "Conformance/SPFConformanceTester.h"

#include "../../Common/Application/Configuration.h"
#include "../../Common/Application/ErrorManager.h"
#include "../../Common/Application/IniFileSettings.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SPF::SPF(void)
   {

   }

   SPF::~SPF(void)
   {

   }

   String
   SPF::GetCheckedDomain(const String &senderEmail, const String &heloHost)
   {
      String domain = StringParser::ExtractDomain(senderEmail);

      if (domain.IsEmpty())
         return heloHost;

      return domain;
   }

   SPF::Result
   SPF::Test(const String &sSenderIP, const String &sSenderEmail, const String &sHeloHost, String &sExplanation)
   {
      sExplanation = "";

      SPFAddress clientAddress;

      if (!SPFAddress::TryParse(AnsiString(sSenderIP), clientAddress))
      {
         // Section 4.1 makes the client address an input, so without one there is no check
         // to make. A "none" rather than an error of either kind: the domain has not been
         // asked anything. A scoped link-local address is the way to get here.
         return SPFResult::None;
      }

      auto lookup = std::make_shared<SPFDnsResolver>();

      SPFEvaluator evaluator(lookup);

      // The r and t macros of section 7.2, which only the text an exp modifier
      // points at may use. The host name is the one the Authentication-Results
      // header identifies this server by.
      evaluator.SetReceivingHost(AnsiString(Configuration::Instance()->GetHostName()));
      evaluator.SetTimestamp((__int64) ::time(0));

      // Section 4.6.4 fixes the void-term limit at two, and two is what the evaluator
      // uses unless this server has been told otherwise. SpfVoidLookupLimit has been
      // an operator-facing setting here since 20 August 2026 - raise it for a sender
      // whose policy names hosts that were decommissioned, or set it to 0 to switch
      // the limit off - and it keeps meaning what it has always meant.
      evaluator.SetVoidTermLimit(IniFileSettings::Instance()->GetSpfVoidLookupLimit());

      AnsiString explanation;

      SPFResult result = evaluator.Check(clientAddress,
                                         AnsiString(GetCheckedDomain(sSenderEmail, sHeloHost)),
                                         AnsiString(sSenderEmail),
                                         AnsiString(sHeloHost),
                                         explanation);

      sExplanation = String(explanation);

      return result;
   }

   void
   SPFTester::Test()
   {
      SPFRecordTester recordTester;
      Report_("the grammar of RFC 7208 section 12", recordTester.Run());

      SPFMacroExpanderTester macroTester;
      Report_("macro expansion, RFC 7208 section 7", macroTester.Run());

      SPFEvaluatorTester evaluatorTester;
      Report_("check_host(), RFC 7208 section 4", evaluatorTester.Run());

      SPFConformance::SPFConformanceTester conformanceTester;
      Report_("the openspf.org conformance suite for RFC 7208", conformanceTester.Run());

      // The count, and not only the failures. A case that stopped being recognised
      // would otherwise be a suite that got quieter rather than one that got smaller,
      // and the whole value of a vendored corpus is that it is the same corpus every
      // implementation runs.
      if (conformanceTester.GetCasesRun() < SPFConformance::SPFConformanceTester::GetCaseCount())
      {
         String message;
         message.Format(_T("SPF: the conformance suite decided %d of its %d cases."),
                        conformanceTester.GetCasesRun(),
                        SPFConformance::SPFConformanceTester::GetCaseCount());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 4404, "SPFTester::Test", message);

         throw 0;
      }
   }

   void
   SPFTester::Report_(const AnsiString &what, const std::vector<AnsiString> &failures)
   {
      if (failures.empty())
         return;

      // Every line, then the failure. A bare throw here would report only that "the
      // class tests failed", and the point of a corpus is to say which cases are
      // wrong and how - one broken mechanism usually breaks several of them, and the
      // set is what says which mechanism it is.
      for (size_t i = 0; i < failures.size(); i++)
      {
         String message;
         message.Format(_T("SPF - %s: %s"), String(what).c_str(), String(failures[i]).c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 4404, "SPFTester::Test", message);
      }

      throw 0;
   }
}
