// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// Ported from hMailServer upstream, https://github.com/hmailserver/hmailserver,
// commit 1beaeab9 ("Replace SPF evaluator", #645) of 13 September 2026, where
// this file carries the notice "Copyright (c) 2010 Martin Knafve /
// hMailServer.com". Both trees are AGPL-3.0-or-later, so the port is
// licence-clean and the attribution stands here.
//
// Taken unchanged.

#pragma once

namespace HM
{
   // The grammar of RFC 7208 a rule at a time: the corners the conformance suite
   // beside this does not visit, and a failure that names the rule rather than a
   // case whose result changed.
   class SPFRecordTester
   {
   public:

      std::vector<AnsiString> Run();

   private:

      void CheckMacroString_(const AnsiString &text, bool valid);
      void CheckExplanationText_(const AnsiString &text, bool valid);
      void CheckDomainSpec_(const AnsiString &text, bool valid);
      void CheckIP4_(const AnsiString &text, bool valid);
      void CheckIP6_(const AnsiString &text, bool valid);
      void CheckDomainName_(const AnsiString &text, bool valid);

      // Parses a record and reports it if the outcome is not the expected one.
      // Returns true when the record parsed, so that a caller can go on to look
      // at what came out.
      bool CheckRecord_(const AnsiString &record, bool valid);

      void Report_(const AnsiString &what, const AnsiString &subject, bool valid);

      void TestVersion_();
      void TestMacroStrings_();
      void TestDomainSpecs_();
      void TestAddressLiterals_();
      void TestDomainNames_();
      void TestMechanisms_();
      void TestPrefixLengths_();
      void TestModifiers_();
      void TestWholeRecords_();

      std::vector<AnsiString> failures_;
   };
}
