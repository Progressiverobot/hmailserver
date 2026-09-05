// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Message;
   class MimeBody;
   class CMimeMessage;
   
   
   class MessageAttachmentStripper  
   {
   public:
      MessageAttachmentStripper();
      virtual ~MessageAttachmentStripper();

      static void Strip(std::shared_ptr<Message> pMessage);
   private:
      
      static void WriteToDisk_(std::shared_ptr<Message> pMessage, MimeBody &oMainMessage, std::shared_ptr<MimeBody> pBody);
      static bool IsGoodTextPart_(std::shared_ptr<MimeBody> pBody);
   };

}