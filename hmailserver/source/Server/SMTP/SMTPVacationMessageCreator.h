// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd


namespace HM
{
   class Message;
   class MimeHeader;

   class SMTPVacationMessageCreator : public Singleton<SMTPVacationMessageCreator>
   {
   public:

      SMTPVacationMessageCreator();
	   virtual ~SMTPVacationMessageCreator();

      // Composes and queues one auto-reply to sToAddress on behalf of
      // recipientAccount, carrying the given subject and body. Both the account's
      // own vacation message and the domain-wide out-of-office reply arrive here -
      // the caller chooses whose text is sent, this class decides whether ANY
      // automatic response may answer pOriginalMessage at all (RFC 3834), and one
      // sender is answered once per recipient however many messages they send.
      void CreateVacationMessage(std::shared_ptr<const Account> recipientAccount,
                                  const String &sToAddress,
                                  const String &sVacationSubject,
                                  const String &sVacationMessage,
                                  const std::shared_ptr<Message> pOriginalMessage);

      // Whether senderAddress belongs to this installation: it resolves to an
      // account hosted here, or to an active alias. Used to choose between the
      // internal and external out-of-office texts.
      //
      // What this does and does not guarantee, stated bluntly: it guarantees the
      // envelope sender NAMES a local mailbox. It does not guarantee the message
      // came from that mailbox's owner, because the envelope sender is whatever the
      // submitting client claimed - the queued message does not record whether the
      // session authenticated, so authentication cannot be consulted here. An
      // installation whose IP ranges require SMTP authentication for local sender
      // addresses (the default) closes that gap at the door; one that accepts a
      // local MAIL FROM from anywhere lets any outsider elicit the internal text.
      // Either way the internal text is a courtesy gradient, not access control,
      // and must never say anything that cannot be shown to a stranger.
      //
      // Deliberately errs towards "external": an address on a hosted domain that
      // resolves to nothing (a forged colleague, a plus-tagged sender, an address
      // at a domain alias) gets the generic external text, which is always safe.
      static bool IsInternalSender(const String &senderAddress);

      void VacationMessageTurnedOff(const String &sUserAddress);

      // The domain-wide equivalent of VacationMessageTurnedOff: forgets every
      // already-answered sender for every account address at sDomainName. Called
      // when a domain's out-of-office reply is switched off, for the same reason
      // the account path clears on its toggle - the memory exists to stop repeats
      // of the SAME absence notice, not to silence the next one.
      void DomainVacationMessageTurnedOff(const String &sDomainName);

   private:

      bool CanSendVacationMessage_(const String &sFrom, const String &sTo);

      // The RFC 3834 gate, applied to the message being replied to before anything
      // is composed and before the once-per-sender slot is spent: no reply to mail
      // that is itself automatic (Auto-Submitted other than "no"), to bulk/list/junk
      // Precedence, to mailing-list traffic (any RFC 2369/2919 List-* header), to a
      // sender that asked not to be answered (X-Auto-Response-Suppress), or to the
      // two robot mailboxes RFC 3834 2 names itself. Returns true with the reason
      // filled in when the reply must not be sent.
      static bool ShouldSuppressAutoReply_(const MimeHeader &originalHeader,
                                           const String &senderAddress,
                                           String &reason);

      std::multimap<String, String> mapVacationMessageRecipients;
      boost::recursive_mutex mutex_;
   };
}
