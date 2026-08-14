// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "DKIMSigner.h"
#include "DKIM.h"
#include "Arc.h"

#include "Canonicalization.h"

#include "../../BO/Message.h"
#include "../../BO/MessageRecipient.h"
#include "../../BO/MessageRecipients.h"
#include "../../BO/Domain.h"
#include "../../BO/DomainAliases.h"
#include "../../Application/ObjectCache.h"
#include "../../Cache/CacheContainer.h"
#include "../../Util/Hashing/HashCreator.h"
#include "../../MIME/Mime.h"
#include "../../Persistence/PersistentMessage.h"
#include "../../Util/Parsing/AddresslistParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Appends a domain to the candidate list, lower-cased and without
      // duplicates. The order of the list is the order in which candidates are
      // tried, so it must be preserved.
      void AddCandidateDomain(std::vector<AnsiString> &candidates, const AnsiString &domainName)
      {
         if (domainName.IsEmpty())
            return;

         AnsiString candidate = domainName;
         candidate.MakeLower();

         for (const AnsiString &existing : candidates)
         {
            if (existing == candidate)
               return;
         }

         candidates.push_back(candidate);
      }

      // Appends the domain of an address. If the address is at a domain alias,
      // the domain the alias points at is appended as well - that is the domain
      // which actually owns the key.
      void AddCandidateAddress(std::vector<AnsiString> &candidates, const String &address)
      {
         if (address.IsEmpty())
            return;

         AnsiString domainName = StringParser::ExtractDomain(address);
         AddCandidateDomain(candidates, domainName);

         std::shared_ptr<DomainAliases> domainAliases = ObjectCache::Instance()->GetDomainAliases();
         if (domainAliases)
         {
            AnsiString aliasedDomainName = StringParser::ExtractDomain(domainAliases->ApplyAliasesOnAddress(address));
            AddCandidateDomain(candidates, aliasedDomainName);
         }
      }
   }

   DKIMSigner::DKIMSigner()
   {

   }

   void
   DKIMSigner::Sign(std::shared_ptr<Message> message)
   {
      // Load the message header once. It will be reused for both domain lookup
      // and signing, avoiding a second read from disk inside DKIM::Sign.
      const String fileName = PersistentMessage::GetFileName(message);
      AnsiString header = PersistentMessage::LoadHeader(fileName);

      MimeHeader mimeHeader;
      mimeHeader.Load(header.c_str(), header.GetLength(), false);

      AnsiString sealingDomain;
      AnsiString sealingSelector;
      String sealingPrivateKeyFile;

      bool signedAsAuthorDomain = SignAsAuthorDomain_(message, header, mimeHeader, sealingDomain, sealingSelector, sealingPrivateKeyFile);

      // ARC sealing (RFC 8617). ARC exists to carry the authentication results
      // we observed across a hop that breaks SPF and DKIM - a forward, an alias,
      // a distribution list - so it must not depend on whether we happened to
      // DKIM-sign the message as the author domain. It previously did, which
      // meant that relayed third-party mail, the only mail ARC is actually for,
      // was never sealed.
      //
      // When we did sign as the author domain we seal with exactly the same
      // identity as before, so that behaviour is unchanged. Otherwise we look
      // for the local domain that is handling the relay.
      if (!IniFileSettings::Instance()->GetArcSealingEnabled())
         return;

      if (!signedAsAuthorDomain)
      {
         if (!IsRelayedOnward_(message))
         {
            // Nothing leaves this server. A seal would be of no use to any
            // downstream receiver, and we would rather not add three headers to
            // every message that is only delivered into a local mailbox.
            return;
         }

         if (!ResolveArcSealingIdentity_(message, mimeHeader, sealingDomain, sealingSelector, sealingPrivateKeyFile))
         {
            LOG_DEBUG("ARC: No local domain with a usable DKIM key handles this relayed message. Not sealing.");
            return;
         }

         if (!FileUtilities::Exists(sealingPrivateKeyFile))
         {
            // A configuration problem rather than a per-message one, but it must
            // never stop the message: it is delivered without a seal.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5725, "DKIMSigner::Sign",
               "ARC sealing is enabled but the private key file of the sealing domain does not exist. The message is delivered without an ARC seal.");
            return;
         }
      }

      Arc arc;
      if (!arc.Seal(message, sealingDomain, sealingSelector, sealingPrivateKeyFile))
      {
         // Sealing is best effort. An unsealable message is still delivered -
         // the seal is extra information for the next hop, never a precondition
         // for delivery.
         LOG_DEBUG("ARC: The message could not be sealed. Delivering it unsealed.");
      }
   }

   bool
   DKIMSigner::SignAsAuthorDomain_(std::shared_ptr<Message> message,
                                   const AnsiString &header,
                                   MimeHeader &mimeHeader,
                                   AnsiString &signingDomain,
                                   AnsiString &signingSelector,
                                   String &signingPrivateKeyFile)
   {
      // Determine the signing domain from the RFC 5322 From: header, not the
      // envelope from (MAIL FROM). DMARC alignment requires d= to match the
      // From: header domain.
      AnsiString senderDomain;
      AnsiString senderAddress;
      MimeField *fromField = mimeHeader.GetField("From");
      if (fromField)
      {
         AddresslistParser parser;
         auto addresses = parser.ParseList(String(fromField->GetValue()));
         if (!addresses.empty())
         {
            senderAddress = addresses[0]->sMailboxName + "@" + addresses[0]->sDomainName;
            senderDomain = addresses[0]->sDomainName;
         }
      }

      if (senderDomain.IsEmpty())
      {
         senderAddress = message->GetFromAddress();
         senderDomain = StringParser::ExtractDomain(senderAddress);
      }

      std::shared_ptr<DomainAliases> pDA = ObjectCache::Instance()->GetDomainAliases();
      // try to get mailbox from the alias (if it is an alias actually)
      String sSender = pDA->ApplyAliasesOnAddress(senderAddress);
      AnsiString mbDomain = StringParser::ExtractDomain(sSender);

      // was the sender address from the main domain already?
      bool sameDomain = senderDomain.CompareNoCase(mbDomain) == 0;

      // Check if signing is enabled for this domain.
      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(mbDomain);

      if (!pDomain || !pDomain->GetDKIMEnabled())
         return false;
      // main domain signing enabled, do we have the sender address from the main domain or do we allow signing of the aliases?
      if (!(pDomain->GetDKIMAliasesEnabled() || sameDomain))
         return false;

      LOG_DEBUG("Signing message using DKIM...");

      // Only the primary selector/key ever signs. The domain may also hold a
      // secondary pair (GetDKIMSecondarySelector), but that pair exists purely
      // to stage a key rotation: it is configuration waiting for its DNS record
      // to propagate, and by definition that record may not be resolvable yet.
      // Signing with it as well would attach a signature that fails
      // verification at every receiver until propagation completes - and while
      // RFC 6376 section 6.1 tells verifiers to accept a message on any one
      // passing signature, real-world filters are known to score broken
      // signatures negatively, so a second signature can only lower
      // deliverability, never raise it. A failure to read the new key must
      // also never break signing with the old key that is still valid. The
      // rotation therefore cuts over atomically via Domain::PromoteDKIMSecondary
      // instead of ever signing with both.
      AnsiString selector = pDomain->GetDKIMSelector();
      // the senderDomain is either the main domain or it is allowed to sign using the key from the main domain
      AnsiString domain = senderDomain;
      AnsiString privateKeyFile = pDomain->GetDKIMPrivateKeyFile();

      if (selector.IsEmpty() || privateKeyFile.IsEmpty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5305, "DKIMSigner::Sign", "Either the selector or private key file was not specified.");
         return false;
      }

      Canonicalization::CanonicalizeMethod headerMethod = (Canonicalization::CanonicalizeMethod) pDomain->GetDKIMHeaderCanonicalizationMethod();
      Canonicalization::CanonicalizeMethod bodyMethod = (Canonicalization::CanonicalizeMethod) pDomain->GetDKIMBodyCanonicalizationMethod();
      HashCreator::HashType algorithm = (HashCreator::HashType) pDomain->GetDKIMSigningAlgorithm();

      DKIM dkim;
      if (!dkim.Sign(message, header, domain, selector, privateKeyFile, algorithm, headerMethod, bodyMethod))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5306, "DKIMSigner::Sign", "Message signing using DKIM failed.");
         return false;
      }

      signingDomain = domain;
      signingSelector = selector;
      signingPrivateKeyFile = privateKeyFile;

      return true;
   }

   bool
   DKIMSigner::IsRelayedOnward_(std::shared_ptr<Message> message)
   {
      std::shared_ptr<MessageRecipients> recipients = message->GetRecipients();
      if (!recipients)
         return false;

      for (std::shared_ptr<MessageRecipient> recipient : recipients->GetVector())
      {
         // A recipient without a local account is delivered by the external
         // delivery path, which is to say it leaves this server.
         if (recipient && recipient->GetLocalAccountID() == 0)
            return true;
      }

      return false;
   }

   bool
   DKIMSigner::ResolveArcSealingIdentity_(std::shared_ptr<Message> message,
                                          MimeHeader &mimeHeader,
                                          AnsiString &sealingDomain,
                                          AnsiString &sealingSelector,
                                          String &sealingPrivateKeyFile)
   {
      // An explicit ArcSealingDomain setting, if one is ever added to
      // IniFileSettings, belongs at the top of this list: an administrator who
      // names a sealing domain means that domain and nothing else. Until then
      // the sealing identity is derived from the message, which for every relay
      // shape hMailServer supports names the local domain doing the relaying.
      std::vector<AnsiString> candidates;

      // 1) The envelope sender. When a forward rewrites MAIL FROM - SRS, or
      //    RewriteEnvelopeFromWhenForwarding - this is the forwarding domain.
      //    It is also the submitting domain for an authenticated local user who
      //    sends with somebody else's address in From:.
      AddCandidateAddress(candidates, message->GetFromAddress());

      // 2) The address each recipient was originally addressed as. An alias or a
      //    distribution list at a local domain rewrites the recipient address to
      //    the external member, but the original address still names the local
      //    domain that is relaying the message.
      std::shared_ptr<MessageRecipients> recipients = message->GetRecipients();
      if (recipients)
      {
         for (std::shared_ptr<MessageRecipient> recipient : recipients->GetVector())
         {
            if (recipient)
               AddCandidateAddress(candidates, recipient->GetOriginalAddress());
         }
      }

      // 3) X-Original-Rcpt-To, written at reception when AddXOriginalRcptToHeader
      //    is enabled. On the queued copy of an account-level forward this is the
      //    only remaining reference to the local mailbox that forwarded, since
      //    the copy carries the external forward target as its recipient.
      MimeField *originalRecipientField = mimeHeader.GetField("X-Original-Rcpt-To");
      if (originalRecipientField)
      {
         AddresslistParser parser;
         auto addresses = parser.ParseList(String(originalRecipientField->GetValue()));

         for (std::shared_ptr<Address> address : addresses)
         {
            if (address)
               AddCandidateAddress(candidates, address->sMailboxName + "@" + address->sDomainName);
         }
      }

      for (const AnsiString &candidate : candidates)
      {
         if (GetHostedDomainIdentity_(candidate, sealingDomain, sealingSelector, sealingPrivateKeyFile))
            return true;
      }

      return false;
   }

   bool
   DKIMSigner::GetHostedDomainIdentity_(const AnsiString &domainName,
                                        AnsiString &signingDomain,
                                        AnsiString &signingSelector,
                                        String &signingPrivateKeyFile)
   {
      if (domainName.IsEmpty())
         return false;

      std::shared_ptr<const Domain> pDomain = CacheContainer::Instance()->GetDomain(domainName);

      if (!pDomain || !pDomain->GetIsActive() || !pDomain->GetDKIMEnabled())
         return false;

      // The primary pair only, for the same reason DKIM signing uses it: an
      // ARC seal has to verify at the very next hop, and the secondary pair is
      // by definition still waiting for its DNS record to propagate.
      AnsiString selector = pDomain->GetDKIMSelector();
      String privateKeyFile = pDomain->GetDKIMPrivateKeyFile();

      if (selector.IsEmpty() || privateKeyFile.IsEmpty())
         return false;

      // d= must name the domain which owns the key. Unlike DKIM there is no
      // alignment requirement on an ARC set, so a domain alias seals under the
      // name of the domain it points at rather than under the alias.
      signingDomain = pDomain->GetName();
      signingDomain.MakeLower();
      signingSelector = selector;
      signingPrivateKeyFile = privateKeyFile;

      return true;
   }
}
