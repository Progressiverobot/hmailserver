// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "MessageAttachmentStripper.h"
#include "../Mime/Mime.h"
#include "../BO/Message.h"
#include "../BO/ServerMessages.h"
#include "../Persistence/PersistentMessage.h"
#include "../Persistence/PersistentServerMessage.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   MessageAttachmentStripper::MessageAttachmentStripper()
   {

   }

   MessageAttachmentStripper::~MessageAttachmentStripper()
   {

   }

   void
   MessageAttachmentStripper::Strip(std::shared_ptr<Message> pMessage)
   {
      const String fileName = PersistentMessage::GetFileName(pMessage);

      // Read message, extract attachments.
      //
      // The result was discarded, and this function goes on to REWRITE the message from
      // what it read. A message it could not read parses as no parts, FindFirstPart
      // returns nothing and it returns below without touching the file - so the outcome
      // was accidentally safe rather than deliberately so, and it depended on
      // FindFirstPart's behaviour rather than on the load having worked. Say so instead.
      MimeBody oMimeMessage;

      if (oMimeMessage.LoadFromFile(fileName) != MimeLoadResult::Loaded)
      {
         LOG_APPLICATION(Formatter::Format("The message {0} could not be read, so its attachments have not been stripped. The message has been left exactly as it was.", fileName));
         return;
      }

      // Assume first part is the text to keep.
      std::shared_ptr<MimeBody> pBody = oMimeMessage.FindFirstPart();

      if (!pBody)
      {
         // Only one part in the message. So we got nothing to do...
         return;
      }

      while (pBody)
      {

         if (pBody->IsMultiPart())
         {
            std::shared_ptr<MimeBody> pSubBody = pBody->FindFirstPart();

           
            while (pSubBody)
            {
               if (IsGoodTextPart_(pSubBody))
               {
                  WriteToDisk_(pMessage, oMimeMessage, pSubBody);
                  return;
               }

               pSubBody = pBody->FindNextPart();
            }


         }
         
         if (IsGoodTextPart_(pBody))
         {
            WriteToDisk_(pMessage, oMimeMessage, pBody);
            return;
         }

         pBody = oMimeMessage.FindNextPart();
      }

      

   }

   void
   MessageAttachmentStripper::WriteToDisk_(std::shared_ptr<Message> pMessage, MimeBody &oMainMessage, std::shared_ptr<MimeBody> pBody)
   {
      if (!pBody)
         return;

      AnsiString sHeader;

      std::vector<MimeField> oFieldList = oMainMessage.Fields();

      auto iterField = oFieldList.begin();

      while (iterField != oFieldList.end())
      {
         MimeField oField = (*iterField);

         AnsiString sName = oField.GetName();
         if (sName.CompareNoCase("subject") == 0)
         {
            String sTextVirusFound = Configuration::Instance()->GetServerMessages()->GetMessage("VIRUS_FOUND");

            sHeader += sName;
            sHeader += ": " + sTextVirusFound +": ";
            sHeader += oField.GetValue();
            sHeader += "\r\n";
         }
         else
         if (sName.CompareNoCase("content-type") != 0)
         {
            sHeader += sName;
            sHeader += ": ";
            sHeader += oField.GetValue();
            sHeader += "\r\n";
         }

         iterField++;
      }

      String sBody = pBody->GetRawText();

      String sNotification = Configuration::Instance()->GetServerMessages()->GetMessage("VIRUS_ATTACHMENT_REMOVED");

      sBody = sNotification + sBody;

      String sData = sHeader;
      sData += "\r\n";
      sData += sBody + "\r\n";

      pMessage->SetSize(sData.GetLength());

      String fileName = PersistentMessage::GetFileName(pMessage);

      FileUtilities::WriteToFile(fileName, sData, false);
   }


   bool
   MessageAttachmentStripper::IsGoodTextPart_(std::shared_ptr<MimeBody> pBody)
   {
      if (!pBody)
         return false;

      String sContentType = pBody->GetContentType();

      if (sContentType.CompareNoCase(_T("text")))
         return true;

      return false;
   }
}
