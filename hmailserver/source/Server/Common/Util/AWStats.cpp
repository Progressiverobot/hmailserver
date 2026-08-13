// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include ".\awstats.h"
#include "Time.h"
#include "../BO/Message.h"
#include "../BO/MessageRecipients.h"
#include "../BO/MessageRecipient.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool AWStats::enabled_ = false;

   AWStats::AWStats(void)
   {
   }

   AWStats::~AWStats(void)
   {
   }

   void
   AWStats::LogDeliveryFailure(const String &senderIP, const String &sFromAddress, const String &sToAddress, int iErrorCode)
   {
      if (!enabled_)
         return;

      LOG_DEBUG(_T("AWStats::LogDeliveryFailure"));

      // Since we were unable to deliver the message, we log that the recipient IP address was 127.0.0.1
      // Not really clear what the 'correct' thing to log here is.
      Log_(senderIP, "127.0.0.1", sFromAddress, sToAddress, iErrorCode, 0);
   }


   void
   AWStats::LogDeliverySuccess(const String &senderIP, const String &recipientIP, std::shared_ptr<Message> pMessage, const String &sRecipient)
   {
      if (!enabled_)
         return;

      LOG_DEBUG(_T("AWStats::LogDeliverySuccess"));

      if (!pMessage)
      {
         // A journal writer must not be the thing that takes the server down. Two of
         // the three call sites hand over a message pointer they have just used, but
         // the third passes one that has been through a delivery pass, and an
         // unconditional dereference here would be an access violation raised from
         // inside logging - a fault caused by the diagnostic rather than reported by
         // it. Logged rather than reported: on a healthy server this cannot happen,
         // and if it does, the delivery itself already succeeded.
         LOG_APPLICATION(_T("AWStats::LogDeliverySuccess was called without a message. The delivery has not been written to the AWStats journal."));
         return;
      }

      Log_(senderIP, recipientIP, pMessage->GetFromAddress(), sRecipient, 250, pMessage->GetSize());
   }

   String
   AWStats::SanitizeField_(const String &value)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Makes one value safe to place in a tab-separated journal record.
   //
   // The journal is a machine-read stream: an AWStats installation splits each line
   // on tabs and counts fields. So a field carrying a tab silently shifts every
   // field after it, and a field carrying a CR or an LF ends the record early and
   // starts a forged one - a complete, plausible delivery record that nobody sent.
   //
   // Only the two address fields were cleaned before this, and only of angle
   // brackets, spaces and tabs. The field that most needs it was untouched: the
   // %host_r column is filled from the MX target hostname on the outbound path
   // (ExternalDelivery), which is a value taken from a DNS answer served by whoever
   // runs the recipient's domain. Nothing between the resolver and this line
   // guarantees a DNS label contains no control bytes, and a hostile authoritative
   // server is a supported deployment reality rather than a hypothetical - it is
   // simply the MX of the domain you are sending to.
   //---------------------------------------------------------------------------()
   {
      String result;
      result.reserve(value.GetLength());

      const int length = value.GetLength();

      for (int i = 0; i < length; i++)
      {
         wchar_t character = value[i];

         // Dropped rather than substituted, to match what this file already did with
         // the characters it did clean: a stripped character cannot be mistaken for
         // part of an address or a hostname, whereas a substituted one can.
         if (character < 0x20 || character == 0x7F)
            continue;

         if (character == '<' || character == '>' || character == ' ')
            continue;

         result += character;
      }

      return result;
   }

   void
   AWStats::Log_(const String &senderIP, const String &recipientIP, const String &senderAddress, const String &recipientAddress, int iErrorCode, int iBytesReceived)
   {
      if (!enabled_)
         return;

      // Following format is used:
      // %time2 %email %email_r %host %host_r %method %url %code %bytesd"

      String sTime = Time::GetCurrentDateTime();

      // Every field, not only the two addresses. One record, one line, always - see
      // SanitizeField_.
      String sModifiedSender = SanitizeField_(senderAddress);
      String sModifiedRecipient = SanitizeField_(recipientAddress);
      String sModifiedSenderIP = SanitizeField_(senderIP);
      String sModifiedRecipientIP = SanitizeField_(recipientIP);

      String sLogLine;
      sLogLine.Format(_T("%s\t%s\t%s\t%s\t%s\tSMTP\t?\t%d\t%d\r\n"), 
         sTime.c_str(), sModifiedSender.c_str(), sModifiedRecipient.c_str(), sModifiedSenderIP.c_str(), sModifiedRecipientIP.c_str(), iErrorCode, iBytesReceived);

      Logger::Instance()->LogAWStats(sLogLine);
   }

   void
   AWStats::SetEnabled(bool bNewVal)
   {
      enabled_ = bNewVal;
   }

   bool 
   AWStats::GetEnabled()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Returns true if the awstats log is enabled. false otherwise.
   //---------------------------------------------------------------------------()
   {
      return enabled_;
   }
}