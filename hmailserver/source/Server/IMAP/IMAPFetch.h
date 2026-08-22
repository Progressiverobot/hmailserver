// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "IMAPCommandRangeAction.h"
#include "../Common/MIME/Mime.h"
#include "IMAPFetchParser.h"

namespace HM
{

   class IMAPConnection;
   class Message;
   class ByteBuffer; 
   class IMAPFetchParser;
   

   class IMAPFetch : public IMAPCommandRangeAction
   {
   public:
	   IMAPFetch();
	   virtual ~IMAPFetch();

      virtual IMAPResult DoAction(std::shared_ptr<IMAPConnection> pConnection, int messageIndex, std::shared_ptr<Message> pMessage, const std::shared_ptr<IMAPCommandArgument> pArgument);

      
   private:
      
      String CreateEnvelopeStructure_(MimeHeader& oHeader);
      String GetPartStructure_(std::shared_ptr<MimeBody> oPart, bool includeExtensionData, int iRecursion);
      String IteratePartRecursive_(std::shared_ptr<MimeBody> oPart, bool includeExtensionData, int iRecursion);
      String CreateEmailStructure_(const String &sField);
      std::shared_ptr<MimeBody>GetMessagePartByPartNo_(std::shared_ptr<MimeBody>pBody, long iPartNo);

      std::shared_ptr<ByteBuffer> GetByteBufferByBodyPart_(const String &messageFileName, std::shared_ptr<MimeBody> pBodyPart, IMAPFetchParser::BodyPart &oPart);
      std::shared_ptr<MimeBody> GetBodyPartByRecursiveIdentifier_(std::shared_ptr<MimeBody> pBody, const String &sName, bool loadEncapsulated = true);


      void GetBytesToSend_(int iBufferSize, IMAPFetchParser::BodyPart &oPart, int &iOutStart, int &iOutCount);

      void ReportCriticalError_(const String &messageFileName, const String &sMessage);

      // RFC 8970: the glance-sized body snippet the PREVIEW data item carries.
      String CreatePreviewText_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<Message> pMessage);

      void AppendOutput_(String &sOutput, const String &sAppend);
      void SendAndReset_(std::shared_ptr<IMAPConnection> pConnection, String &sOutput);

      std::shared_ptr<MimeBody> LoadMimeBody_(std::shared_ptr<IMAPFetchParser> pParser, const String &fileName);
      bool GetMessageBodyNeeded_(std::shared_ptr<IMAPFetchParser> pParser);

      bool append_space_;

      std::shared_ptr<IMAPFetchParser> parser_;

   };

}
