// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class MessageRecipient
   {
   public:
      MessageRecipient();
      ~MessageRecipient(void);

      enum DeliveryResult
      {
         ResultUndefined = 0,
         ResultOK = 1,
         ResultNonFatalError = 2,
         ResultFatalError =3,
         ResultOptionalHandshakeFailed = 4
      };

      void CopyFrom(std::shared_ptr<MessageRecipient> pRecip);

      void SetAddress(const String & sNewVal) {address_ = sNewVal; }
      String GetAddress() const {return address_;}

      void SetOriginalAddress(const String & sNewVal) {original_address_ = sNewVal; }
      String GetOriginalAddress() const {return original_address_;}

      void SetLocalAccountID(__int64 lNewVal) {local_account_id_ = lNewVal;}
      __int64 GetLocalAccountID() const{return local_account_id_;}

      void SetMessageID(__int64 lNewVal) {message_id_ = lNewVal;}
      __int64 GetMessageID() const {return message_id_;}

      bool GetRequireAuth() const {return requires_authentication_;}
      void SetRequiresAuth(bool bNewVal) {requires_authentication_ = bNewVal; }

      String GetRequiredSender() const {return required_sender_; }
      void SetRequiredSender(const String &sNewVal) {required_sender_ = sNewVal; }

      bool GetIsLocalName() const {return is_local_name_; }
      void SetIsLocalName(bool isLocalName) {is_local_name_ = isLocalName; }

      // True when this recipient is a distribution-list MODERATOR receiving a
      // posting for approval rather than a member receiving a distribution.
      // Set by RecipientParser when it redirects a moderated posting, and read by
      // DistributionListSender while the message is still being accepted, to stamp
      // the forward with its X-hMailServer-Moderation header instead of the List-*
      // set. In-memory only, deliberately: the header is written before the
      // message is queued, so nothing after that point needs the flag and it is
      // not persisted to hm_messagerecipients.
      bool GetIsModerationForward() const {return moderation_forward_; }
      void SetIsModerationForward(bool newVal) {moderation_forward_ = newVal; }

      // DSN NOTIFY (RFC 3461) bitmask requested by the client for this recipient.
      // 0 = default behaviour, NEVER=1, SUCCESS=2, FAILURE=4, DELAY=8.
      enum DSNNotify
      {
         DSNNotifyDefault = 0,
         DSNNotifyNever = 1,
         DSNNotifySuccess = 2,
         DSNNotifyFailure = 4,
         DSNNotifyDelay = 8
      };

      int GetDSNNotify() const {return dsn_notify_; }
      void SetDSNNotify(int newVal) {dsn_notify_ = newVal; }

      bool IsEmpty() {return address_.IsEmpty(); }

      // -- BEGIN REMOTE DELIVERY
      DeliveryResult GetDeliveryResult() const {return  result_; }
      void SetDeliveryResult(DeliveryResult newVal) {result_ = newVal; }

      String GetErrorMessage() const {return error_message_;}

      // The RFC 3463 enhanced status code that goes with error_message_.
      //
      // Empty only for a recipient that has not failed. There is deliberately no
      // separate setter for the prose: the delivery-status notification is built
      // from BOTH, and a failure site that could record one without the other is
      // how every recipient ends up reported as "5.0.0". Setting them together is
      // the whole point of this function existing.
      String GetEnhancedStatusCode() const {return enhanced_status_code_;}
      void SetDeliveryError(const String &errorMessage, const String &enhancedStatusCode)
      {
         error_message_ = errorMessage;
         enhanced_status_code_ = enhancedStatusCode;

         // A new failure discards the previous one's remote attribution.
         //
         // Without this the fields are sticky, and one recipient can be tried
         // against several MX hosts in one pass: a 4xx from the first host
         // followed by a connection timeout on the second would leave the first
         // host's name and reply attached to a failure it had nothing to do with,
         // and the report would quote one server's words as another's. The caller
         // sets them again immediately when this failure really does have them.
         remote_smtp_reply_.Empty();
         remote_mta_.Empty();
      }

      // What a remote server actually replied, verbatim and including its
      // three-digit code, and the host that replied it. Both stay empty unless
      // this server really did have an SMTP conversation - our own reasons for
      // abandoning a delivery must not be reported as the remote's words, and
      // RFC 3464 2.3 would rather have the field omitted than invented.
      String GetRemoteSmtpReply() const {return remote_smtp_reply_;}
      void SetRemoteSmtpReply(const String &sNewVal) {remote_smtp_reply_ = sNewVal; }

      String GetRemoteMta() const {return remote_mta_;}
      void SetRemoteMta(const String &sNewVal) {remote_mta_ = sNewVal; }
      // -- END REMOTE DELIVERY

   protected:

      String address_;
      __int64 local_account_id_;
      __int64 message_id_;

      bool is_local_name_;
      bool moderation_forward_;

      bool requires_authentication_;
      String required_sender_;
      String original_address_;

      int dsn_notify_;
      
      // -- BEGIN REMOTE DELIVERY
      DeliveryResult result_;
      String error_message_;
      String enhanced_status_code_;
      String remote_smtp_reply_;
      String remote_mta_;
      // -- END REMOTE DELIVERY
   };
}
