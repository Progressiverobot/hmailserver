// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later


#pragma once

namespace HM
{

   class DistributionLists;
   class Aliases;
   class Accounts;
   class DomainAliases;

   class Domain : public BusinessObject<Domain>
   {
   public:
	   Domain();
	   virtual ~Domain();

      enum AntiSpamOptions
      {
         ASUseGreylisting = 1,
         
         ASDKIMSign = 2,
         ASDKIMSimpleHeader = 4,
         ASDKIMSimpleBody = 8,
         ASDKIMSHA1 = 16,
         ASDKIMSignAliases = 32
      };

      enum DomainSignatureMethod
      {
         SMUnknown = 0,
         SMSetIfNotSpecifiedInAccount = 1,
         SMOverwriteAccountSignature = 2,
         SMAppendToAccountSignature = 3
      };
      
      enum DomainLimitations
      {
         MaxNoOfAccounts = 1,
         MaxNoOfAliases = 2,
         MaxNoOfDistributionLists = 4
      };

      String GetName() const { return name_; } 
      void SetName(const String & newVal) { name_ = newVal; };

      String GetPostmaster() const { return postmaster_; } 
      void SetPostmaster(const String & newVal) { postmaster_ = newVal; };

      String GetADDomainName() const { return addomain_name_; } 
      void SetADDomainName(const String & newVal) { addomain_name_ = newVal; };

      bool GetIsActive() const { return active_ ? true : false; } 
      void SetIsActive(bool newVal) { active_ = newVal ? 1 : 0; }
      
      int GetMaxMessageSize() const {return max_message_size_;}
      void SetMaxMessageSize(int iNewVal) {max_message_size_ = iNewVal; }
      
      bool GetUsePlusAddressing() const {return use_plus_addressing_; }
      void SetUsePlusAddressing(bool bNewVal) {use_plus_addressing_ = bNewVal; }

      String GetPlusAddressingChar() const {return plus_addressing_char_; }
      void SetPlusAddressingChar(const String &sNewVal) {plus_addressing_char_ = sNewVal; }

      int GetAntiSpamOptions() const {return anti_spam_options_;}
      void SetAntiSpamOptions(int iNewVal) {anti_spam_options_ = iNewVal; }

      bool GetASUseGreyListing() const;
      void SetASUseGreyListing(bool bNewVal);

      int GetMaxSizeMB() const;
      void SetMaxSizeMB(int iNewVal);

      bool GetEnableSignature() const;
      void SetEnableSignature(bool bNewVal);

      DomainSignatureMethod GetSignatureMethod() const;
      void SetSignatureMethod(DomainSignatureMethod sm);

      String GetSignaturePlainText() const;
      void SetSignaturePlainText(const String &sNewVal);

      String GetSignatureHTML() const;
      void SetSignatureHTML(const String &sNewVal);

      bool GetAddSignaturesToReplies() const;
      void SetAddSignaturesToReplies(bool bNewVal);

      bool GetAddSignaturesToLocalMail() const;
      void SetAddSignaturesToLocalMail(bool bNewVal);

      int GetMaxNoOfAccounts() const;
      void SetMaxNoOfAccounts(int iNewVal);
      int GetMaxNoOfAliases() const;
      void SetMaxNoOfAliases(int iNewVal);
      int GetMaxNoOfDistributionLists() const;
      void SetMaxNoOfDistributionLists(int iNewVal);

      int GetLimitationsEnabled() const;
      void SetLimitationsEnabled(int iNewVal);

      bool GetMaxNoOfAccountsEnabled() const;
      void SetMaxNoOfAccountsEnabled(bool iNewVal);
      bool GetMaxNoOfAliasesEnabled() const;
      void SetMaxNoOfAliasesEnabled(bool iNewVal);
      bool GetMaxNoOfDistributionListsEnabled() const;
      void SetMaxNoOfDistributionListsEnabled(bool iNewVal);

      int GetMaxAccountSize() const;
      void SetMaxAccountSize(int iNewVal);
      
      /*
         DKIM START

      */
      bool GetDKIMEnabled() const;
      void SetDKIMEnabled(bool newVal);

      bool GetDKIMAliasesEnabled() const;
      void SetDKIMAliasesEnabled(bool newVal);

      AnsiString GetDKIMSelector() const;
      void SetDKIMSelector(const String &newValue);

      String GetDKIMPrivateKeyFile() const;
      void SetDKIMPrivateKeyFile(const String &newValue);

      // The secondary selector/key pair stages a key rotation. It never signs
      // anything: it is configuration parked next to the primary so that the
      // new public key can be published in DNS and confirmed to resolve while
      // the primary key continues to sign every message. Without it, rotating
      // a key means editing the one selector in place, and every message
      // signed between that edit and DNS propagation fails verification.
      AnsiString GetDKIMSecondarySelector() const;
      void SetDKIMSecondarySelector(const String &newValue);

      String GetDKIMSecondaryPrivateKeyFile() const;
      void SetDKIMSecondaryPrivateKeyFile(const String &newValue);

      // The cutover: the secondary becomes the primary and the secondary slot
      // is cleared. Fails, with a reason in errorMessage, rather than promote
      // a half-configured or unusable pair - a bad promote would stop DKIM
      // signing for the domain, which is the outage this scheme exists to
      // prevent. The caller is responsible for persisting the domain afterwards.
      bool PromoteDKIMSecondary(String &errorMessage);

      // An outbound relay chosen by the SENDING domain, which is a different
      // question from the one routes answer. A route says "mail addressed to this
      // domain goes to that host"; this says "mail FROM this domain leaves through
      // that host", which is what a server hosting several independent domains
      // needs when each of them has bought its own delivery provider. Empty host
      // means the domain has no opinion and the global relayer applies, so an
      // upgraded installation behaves exactly as before until somebody fills one in.
      String GetRelayHost() const { return relay_host_; }
      void SetRelayHost(const String &newValue) { relay_host_ = newValue; }

      // 0 means "unset", and the resolver reads it as 25 - the same rule the global
      // relayer already uses, so the two cannot disagree about what a blank port means.
      long GetRelayPort() const { return relay_port_; }
      void SetRelayPort(long newValue) { relay_port_ = newValue; }

      bool GetRelayRequiresAuth() const { return relay_requires_auth_; }
      void SetRelayRequiresAuth(bool newValue) { relay_requires_auth_ = newValue; }

      String GetRelayUsername() const { return relay_username_; }
      void SetRelayUsername(const String &newValue) { relay_username_ = newValue; }

      String GetRelayPassword() const { return relay_password_; }
      void SetRelayPassword(const String &newValue) { relay_password_ = newValue; }

      ConnectionSecurity GetRelayConnectionSecurity() const { return relay_connection_security_; }
      void SetRelayConnectionSecurity(ConnectionSecurity newValue) { relay_connection_security_ = newValue; }

      // The domain-wide out-of-office reply. It answers mail to accounts that have
      // no active vacation message of their own (a closed office, a decommissioned
      // department), and it can say different things to different senders:
      //
      //   * GetVacationSubject/GetVacationMessage is the EXTERNAL text - what a
      //     sender from outside the server's own domains receives. It is also the
      //     fallback for internal senders when no internal text is configured, so
      //     the safe, generic wording belongs here.
      //   * GetVacationInternalSubject/GetVacationInternalMessage is the optional
      //     INTERNAL text - what a sender who resolves to an account or alias
      //     hosted on this server receives. Empty means "internal senders get the
      //     external text too".
      //
      // "Internal" is decided by SMTPVacationMessageCreator::IsInternalSender, and
      // its guarantee is deliberately weak: the envelope sender NAMES a local
      // mailbox, not that the message came from that mailbox's owner. The envelope
      // is forgeable wherever the installation's IP ranges accept a local MAIL FROM
      // without authentication, so the internal text must never carry anything that
      // cannot be shown to a stranger.
      //
      // Precedence against an account's own vacation message: the account wins, the
      // domain answers only for accounts with nothing of their own, and exactly one
      // reply is ever produced per delivered message. Two replies would double the
      // loop pressure and can contradict each other ("back on Monday" beside "this
      // office has closed").
      bool GetVacationMessageIsOn() const { return vacation_message_on_; }
      void SetVacationMessageIsOn(bool bNewVal);

      String GetVacationSubject() const { return vacation_subject_; }
      void SetVacationSubject(const String &sNewVal) { vacation_subject_ = sNewVal; }

      String GetVacationMessage() const { return vacation_message_; }
      void SetVacationMessage(const String &sNewVal) { vacation_message_ = sNewVal; }

      String GetVacationInternalSubject() const { return vacation_internal_subject_; }
      void SetVacationInternalSubject(const String &sNewVal) { vacation_internal_subject_ = sNewVal; }

      String GetVacationInternalMessage() const { return vacation_internal_message_; }
      void SetVacationInternalMessage(const String &sNewVal) { vacation_internal_message_ = sNewVal; }

      // The organisation-wide privacy policy for accounts that DO have their own
      // vacation message: when this is on and the domain's external text is
      // configured, an external sender receives the domain's external text in place
      // of the account's personal one, while internal senders still receive what
      // the account wrote. Off (the default) preserves the pre-existing behaviour -
      // the account's single message goes to everyone - so an upgraded installation
      // changes nothing until an administrator decides otherwise.
      bool GetVacationExternalOverride() const { return vacation_external_override_; }
      void SetVacationExternalOverride(bool bNewVal) { vacation_external_override_ = bNewVal; }

      int GetDKIMHeaderCanonicalizationMethod() const;
      void SetDKIMHeaderCanonicalizationMethod(int newValue);

      int GetDKIMBodyCanonicalizationMethod() const;
      void SetDKIMBodyCanonicalizationMethod(int newValue);

      int GetDKIMSigningAlgorithm() const;
      void SetDKIMSigningAlgorithm(int newValue);
      /*
         DKIM END
      */

      std::shared_ptr<Accounts> GetAccounts();
      std::shared_ptr<Accounts> GetAccounts(__int64 iAccountID);
      std::shared_ptr<Aliases> GetAliases();
      std::shared_ptr<DistributionLists> GetDistributionLists();
      std::shared_ptr<DomainAliases> GetDomainAliases();

      bool XMLStore(XNode *pParentNode, int iBackupOptions);
      bool XMLLoad(XNode *pDomainNode, int iRestoreOptions);
      bool XMLLoadSubItems(XNode *pDomainNode, int iRestoreOptions);

      size_t GetEstimatedCachingSize();
   protected:

      int anti_spam_options_;
      
      String name_;
      String postmaster_;
      String addomain_name_;

      bool use_plus_addressing_;
      String plus_addressing_char_;

      bool enable_signature_;
      DomainSignatureMethod signature_method_;
      String signature_plain_text_;
      String signature_html_;
      bool add_signatures_to_local_mail_;
      bool add_signatures_to_replies_;
      int active_;
      unsigned int max_message_size_;
      int max_size_mb_;
      int max_no_of_accounts_;
      int max_no_of_aliases_;
      int max_no_of_distribution_lists_;
      int limitations_enabled_;
      int max_account_size_;

      String dkim_selector_;
      String dkim_private_key_file_;

      String dkim_secondary_selector_;
      String dkim_secondary_private_key_file_;

      String relay_host_;
      long relay_port_;
      bool relay_requires_auth_;
      String relay_username_;
      String relay_password_;
      ConnectionSecurity relay_connection_security_;

      bool vacation_message_on_;
      String vacation_subject_;
      String vacation_message_;
      String vacation_internal_subject_;
      String vacation_internal_message_;
      bool vacation_external_override_;

      std::shared_ptr<Accounts> accounts_;
      std::shared_ptr<Aliases> aliases_;
      std::shared_ptr<DistributionLists> distribution_lists_;
      std::shared_ptr<DomainAliases> domain_aliases_;
   };
}