// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class MessageRecipients;
   class MessageRecipient;
   class DistributionList;
   class Account;

   class RecipientParser  
   {
   public:
	   RecipientParser();
	   virtual ~RecipientParser();

      enum DeliveryPossibility
      {
         DP_Possible = 0,
         DP_RecipientUnknown = 1,
         DP_PermissionDenied = 2,

         // The recipient exists and is willing, but its mailbox is already at or
         // over quota. Separate from the two refusals above because it is
         // TEMPORARY - the mailbox will very likely be emptied - and answering it
         // as a permanent failure would throw away mail a retry would deliver.
         DP_MailboxFull = 3,
      };

      // checkRecipientQuota asks the one extra question RCPT TO needs and nothing
      // else does: is this mailbox already full? It is opt-in rather than always on
      // because the forwarding callers treat every answer other than DP_Possible as
      // "abandon the forward" and log it - so a full forwarding target would become
      // a dropped message instead of a deferred one, which is the opposite of what
      // this check is for.
      DeliveryPossibility CheckDeliveryPossibility(bool bSenderIsAuthed, String sSender, const String &sOriginalRecipient, String &sErrMsg, bool &bTreatSecurityAsLocal, int iRecursionLevel, bool checkRecipientQuota = false);
      void CreateMessageRecipientList(const String &sRecipientAddress, std::shared_ptr<MessageRecipients> pRecipients, bool &recipientOK);
      
   private:

      void CreateMessageRecipientList_(const String &recipientAddress, const String &sOriginalAddress, long lRecurse, std::shared_ptr<MessageRecipients> pRecipients, bool &recipientOK);
      void AddRecipient_(std::shared_ptr<MessageRecipients> pRecipients, std::shared_ptr<MessageRecipient> pRecipient);
      bool IsMailboxFull_(std::shared_ptr<const Account> account, String &sErrMsg);
      DeliveryPossibility UserCanSendToList_(const String &sSender, bool bSenderIsAuthenticated, std::shared_ptr<const DistributionList> pList, String &sErrMsg, int iRecursionLevel);

  };
}