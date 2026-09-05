// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class MessageData;
   class MessageRecipients;

   class Message : public BusinessObject<Message>
   {
   public:

      enum State
      {
         Created = 0,
         Delivering = 1,
         Delivered = 2,
      };

      enum Flags
      {
         FlagSeen = 1,
         FlagDeleted = 2,
         FlagFlagged = 4,
         FlagAnswered = 8,
         FlagDraft = 16,
         FlagRecent = 32,
         FlagVirusScan = 64,
         FlagSpam = 128,

         // RFC 3030 BINARYMIME. The ninth bit, which is why hm_messages.
         // messageflags had to be widened from tinyint to smallint in schema
         // 6025 - FlagSeen..FlagSpam had taken every bit a tinyint could hold.
         FlagBinaryMime = 256
      };

      Message(bool generateFileName);
      Message(const Message& other);
      Message();
      virtual ~Message();

      void Initialize(bool generateFileName);

      String GetName() {return filename_; }

      static String GenerateFileName();

      String GetPartialFileName() const;
      void SetPartialFileName(const String &FileName);

      String GetFromAddress() const { return from_address_; }
      void SetFromAddress(const String &FromAddress) { from_address_ = FromAddress; }

      unsigned int GetUID() const { return uid_; }
      void SetUID(unsigned int  uid) { uid_ = uid; }

      // RFC 7162 (CONDSTORE/QRESYNC): per-mailbox mod-sequence value, bumped on
      // arrival and on every metadata (flag) change.
      __int64 GetModSeq() const { return message_modseq_; }
      void SetModSeq(__int64 modSeq) { message_modseq_ = modSeq; }

      __int64 GetAccountID() const { return message_account_id_; }
      void SetAccountID(__int64 MsgAccountID) { message_account_id_ = (int) MsgAccountID; }
   
      State GetState() const { return (State) message_state_; }
      void SetState(State MessageState) { message_state_ = MessageState; }

      int GetSize() const { return message_size_; }
      void SetSize(int MessageSize) { message_size_ = MessageSize; }

      __int64 GetFolderID() const { return message_folder_id_; }
      void SetFolderID(__int64 iFolderID) { message_folder_id_ = (int) iFolderID; }

      short GetFlags() {return flags_; }
      void SetFlags(short iNewVal) {flags_ = iNewVal; }

      bool GetFlagSeen() const;
      void SetFlagSeen(bool bNewVal);
      bool GetFlagDeleted() const;
      void SetFlagDeleted(bool bNewVal);
      bool GetFlagDraft() const;
      void SetFlagDraft(bool bNewVal);
      bool GetFlagAnswered() const;
      void SetFlagAnswered(bool bNewVal);
      bool GetFlagFlagged() const;
      void SetFlagFlagged(bool bNewVal);
      bool GetFlagRecent() const;
      void SetFlagRecent(bool bNewVal);
      bool GetFlagVirusScan() const;
      void SetFlagVirusScan(bool bNewVal);
      bool GetFlagSpam() const;
      void SetFlagSpam(bool bNewVal);

      // RFC 3030: this message's content is BINARYMIME and must never be
      // transmitted down a line-oriented DATA path. SMTPClientConnection refuses
      // to relay a message carrying this mark (554 5.6.3).
      //
      // PERSISTED, as bit 256 of flags_. It was in-memory only when it shipped,
      // because messageflags was a tinyint with all eight bits taken - and that
      // gap was real: a restart with a binary message still queued for an
      // external recipient, or a forwarded copy delivered from a cold cache, read
      // the mark back as false and relayed the content over DATA, corrupting it
      // at the first NUL and smuggling bare LFs past the check that exists to
      // stop them. Schema 6025 widened the column for this, so the mark now
      // survives the database round-trip and the refusal holds on every path.
      bool GetBinaryMime() const { return (flags_ & FlagBinaryMime) != 0; }
      void SetBinaryMime(bool bNewVal)
      {
         if (bNewVal)
            flags_ |= FlagBinaryMime;
         else
            flags_ &= ~FlagBinaryMime;
      }

      void SetNoOfRetries(unsigned short lNewVal) {no_of_retries_ = lNewVal; }
      unsigned short GetNoOfRetries() const { return no_of_retries_;}


      void SetCreateTime(const String &sCreateTime) {create_time_ = sCreateTime; }
      String GetCreateTime() const {return create_time_; }

      // RFC 8514 (SAVEDATE): when the message was saved into its current
      // mailbox. Distinct from the create time, which IMAP COPY must preserve
      // as INTERNALDATE; a copy gets a fresh save date.
      void SetSaveDate(const String &sSaveDate) {save_date_ = sSaveDate; }
      String GetSaveDate() const {return save_date_; }

      // RFC 8474 (OBJECTID): the stable email id. Stamped at first save and -
      // the opposite of the save date - deliberately carried by copies: the
      // same message content keeps the same EMAILID wherever it goes.
      void SetEmailId(const String &sEmailId) {email_id_ = sEmailId; }
      String GetEmailId() const {return email_id_; }

      std::shared_ptr<MessageRecipients> GetRecipients();

      bool XMLStore(XNode *pParentNode, int iOptions);
      bool XMLLoad(XNode *pNode, int iOptions);
      bool XMLLoadSubItems(XNode *pNode, int iOptions) {return true;}

   protected:

      int message_size_;

      AnsiString create_time_;
      AnsiString save_date_;
      AnsiString email_id_;
      AnsiString filename_;
      AnsiString from_address_;
      
      int message_account_id_;
      int message_folder_id_;

      short message_state_;
      short no_of_retries_;
      short flags_;

      // See GetBinaryMime above: in-memory only until messageflags is widened.

      unsigned int uid_;

      __int64 message_modseq_;
      
   private:

      bool GetFlag_(int iFlag) const;
      void SetFlag_(int iFlag, bool bSet);

      std::shared_ptr<MessageRecipients> recipients_;

   };
}
