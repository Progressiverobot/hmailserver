// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class Message;

   class AWStats
   {
   public:
      AWStats(void);
      ~AWStats(void);

      static void LogDeliveryFailure(const String &senderIP, const String &sFromAddress, const String &sToAddress, int iErrorCode);
      static void LogDeliverySuccess(const String &senderIP, const String &recipientIP, std::shared_ptr<Message> pMessage, const String &sRecipient);

      static void SetEnabled(bool bNewVal);
      static bool GetEnabled();

   private:

      static void Log_(const String &senderIP, const String &recipientIP, const String &senderAddress, const String &recipientAddress, int iErrorCode, int iBytesReceived);

      // Strips anything from a value that could end the journal record early or move
      // a column: control characters (a CR or an LF would forge a second record),
      // spaces and angle brackets.
      static String SanitizeField_(const String &value);

      static bool enabled_;
   };
}