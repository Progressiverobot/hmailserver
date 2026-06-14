// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "IMAPCommand.h"
#include "IMAPSearchParser.h"


namespace HM
{
   class IMAPSearchParser;
   class IMAPConnection;
   class MessageData;
   class MimeHeader;

   class IMAPCommandSEARCH : public IMAPCommand
   {
   public:
	   IMAPCommandSEARCH(bool bIsSort);
	   virtual ~IMAPCommandSEARCH();

      void SetIsUID() { is_uid_ = true; }

      IMAPResult ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument);

   private:

      bool MatchesHeaderCriteria_(const String &fileName,std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesUIDCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesSequenceSetCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria, int index);
      bool MatchesTEXTCriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesBODYCriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesONCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesSENTONCriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesSENTBEFORECriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesSENTSINCECriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesSINCECriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      
      bool MatchesBEFORECriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesLARGERCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);
      bool MatchesSMALLERCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria);

      String GetHeaderValue_(const String &fileName, std::shared_ptr<Message> pMessage, const String &sHeaderField);
      
      
      bool DoesMessageMatch_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPSearchCriteria> pParentCriteria, const String &fileName, std::shared_ptr<Message> pMessage, int index);
      bool IsMessageRecent_(std::shared_ptr<IMAPConnection> pConnection, __int64 message_uid);

      std::shared_ptr<MessageData> message_data_;
      std::shared_ptr<MimeHeader> mime_header_;

      bool is_sort_;
      bool is_uid_;

      // RFC 4731 (ESEARCH) result-option state, parsed from a "RETURN (...)" clause.
      bool is_esearch_;
      bool esearch_min_;
      bool esearch_max_;
      bool esearch_all_;
      bool esearch_count_;

      // RFC 5182 (SEARCHRES): set when the "SAVE" result option is present, in which case
      // the matched UIDs are stored on the connection for later reference via "$".
      bool esearch_save_;

      // RFC 7162 (CONDSTORE): set when a MODSEQ search key is present, so the SEARCH/ESEARCH
      // response can report the highest mod-sequence among the matched messages.
      bool modseq_search_;
      __int64 highest_modseq_;
   };
}
