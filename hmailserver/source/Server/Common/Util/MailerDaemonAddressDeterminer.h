// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class Message;
   
   class MailerDaemonAddressDeterminer  
   {
   public:
	   MailerDaemonAddressDeterminer();
	   virtual ~MailerDaemonAddressDeterminer();

      static String GetMailerDaemonAddress(const std::shared_ptr<Message> pOrigMessage);
      static String GetMailerDaemonAddress(const String &sOrigSender, const String &sOrigReceiver);

      static bool IsMailerDaemonAddress(const String &sAddress);
   };

}
