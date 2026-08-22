// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Message;
   class MessageData;

   class SpamTestRunner;
   class SpamTestResult;
   class AuthenticationResults;

   class SpamProtection : public Singleton<SpamProtection>
   {
   public:
      SpamProtection(void);
      ~SpamProtection(void);

      void Load();

      // authenticationResults is where the tests record what they concluded, for the
      // RFC 8601 header. It is defaulted and null-checked everywhere, so with the
      // feature off nothing is allocated and no test does anything extra.
      //
      // It spans both phases deliberately: SPF is a pre-transmission test while DKIM
      // and DMARC are post-transmission, so a carrier scoped to one phase could only
      // ever describe part of the answer. The connection owns it for the length of the
      // message.
      std::set<std::shared_ptr<SpamTestResult> > RunPreTransmissionTests(const String &sFromAddress, const IPAddress & iOriginatingIP, const IPAddress &iConnectingIP, const String &sHeloHost, std::shared_ptr<AuthenticationResults> authenticationResults = nullptr);
      std::set<std::shared_ptr<SpamTestResult> > RunPostTransmissionTests(const String &sFromAddress, const IPAddress & iOriginatingIP, const IPAddress &iConnectingIP, std::shared_ptr<Message> pMessage, std::shared_ptr<AuthenticationResults> authenticationResults = nullptr);

      static std::shared_ptr<MessageData> AddSpamScoreHeaders(std::shared_ptr<Message> pMessage, std::set<std::shared_ptr<SpamTestResult> > setResult, bool classifiedAsSpam);
      static bool GreyListingAllowSend(const String &sSenderAddress, const String &sRecipientAddress, const IPAddress & iRemoteIP);

      static int CalculateTotalSpamScore(std::set<std::shared_ptr<SpamTestResult> > result);

      static bool IsWhiteListed(const String &sFromAddress, const IPAddress & iIPAddress);

      bool PerformGreyListing(std::shared_ptr<Message> message, const std::set<std::shared_ptr<SpamTestResult> > &spamTestResults, const String &toAddress, const IPAddress &ipaddress);
   private:
      
      
      std::shared_ptr<SpamTestRunner> spam_test_runner_;
      
   };

}