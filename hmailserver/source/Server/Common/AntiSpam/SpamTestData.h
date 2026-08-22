// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "../TCPIP/IPAddress.h"

namespace HM
{
   class MessageData;
   class IPAddress;
   class AuthenticationResults;

   class SpamTestData
   {
   public:
      
      SpamTestData();
      virtual ~SpamTestData();

      void SetEnvelopeFrom(const String &sEnvelopeFrom);
      String GetEnvelopeFrom() const;

      void SetHeloHost(const String &sHeloHost);
      String GetHeloHost() const;

      void SetOriginatingIP(const IPAddress &address);
      const IPAddress &GetOriginatingIP() const;

      void SetConnectingIP(const IPAddress &address);
      const IPAddress &GetConnectingIP() const;


      void SetMessageData(const std::shared_ptr<MessageData> pMessageData);
      std::shared_ptr<MessageData> GetMessageData() const;

      // Where the spam tests record what they concluded, for the RFC 8601 header.
      //
      // It has to be carried rather than recomputed: SpamTestResult says only which
      // test ran and what it scored, so the things the header needs - the signing
      // domain of each signature, the SPF identity, the DMARC policy that applied -
      // are known only inside the tests and were being dropped on the floor. This
      // object outlives the tests and is owned by the connection, so a verdict can
      // travel from the test that reached it to the point where headers are written.
      //
      // Null when the feature is off, and every writer null-checks: with the setting
      // at its default nothing is allocated and nothing extra is done per message.
      void SetAuthenticationResults(std::shared_ptr<AuthenticationResults> results);
      std::shared_ptr<AuthenticationResults> GetAuthenticationResults() const;

   private:

      String envelope_from_;

      IPAddress originatingAddress_;
      IPAddress connectingAddress_;
      String helo_host_;

      std::shared_ptr<MessageData> message_data_;

      std::shared_ptr<AuthenticationResults> authentication_results_;

   };

}