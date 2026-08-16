// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include "SpamTestData.h"
#include "AuthenticationResults.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SpamTestData::SpamTestData(void)
   {

   }

   SpamTestData::~SpamTestData(void)
   {

   }

   void 
   SpamTestData::SetEnvelopeFrom(const String &sEnvelopeFrom)
   {
      envelope_from_ = sEnvelopeFrom;
   }

   String 
   SpamTestData::GetEnvelopeFrom() const
   {
      return envelope_from_;
   }

   void 
   SpamTestData::SetHeloHost(const String &sNewVal)
   {
      helo_host_ = sNewVal;
   }

   String 
   SpamTestData::GetHeloHost() const
   {
      return helo_host_;
   }

   void 
   SpamTestData::SetOriginatingIP(const IPAddress &address)
   {
      originatingAddress_ = address;
   }

   const IPAddress&
   SpamTestData::GetOriginatingIP() const
   {
      return originatingAddress_;
   }

   void 
   SpamTestData::SetConnectingIP(const IPAddress &iIPAddress)
   {
      connectingAddress_ = iIPAddress;
   }

   const IPAddress&
   SpamTestData::GetConnectingIP() const
   {
      return connectingAddress_;
   }

   void 
   SpamTestData::SetMessageData(std::shared_ptr<MessageData> pMessageData)
   {
      message_data_ = pMessageData;
   }

   std::shared_ptr<MessageData>
   SpamTestData::GetMessageData()  const
   {
      return message_data_;
   }

   void
   SpamTestData::SetAuthenticationResults(std::shared_ptr<AuthenticationResults> results)
   {
      authentication_results_ = results;
   }

   std::shared_ptr<AuthenticationResults>
   SpamTestData::GetAuthenticationResults() const
   {
      return authentication_results_;
   }

}