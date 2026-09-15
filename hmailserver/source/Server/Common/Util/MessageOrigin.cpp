// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "MessageOrigin.h"

#include "../Application/Configuration.h"
#include "../Application/ObjectCache.h"
#include "../BO/Alias.h"
#include "../BO/Domain.h"
#include "../BO/DomainAliases.h"
#include "../BO/IncomingRelays.h"
#include "../BO/Message.h"
#include "../Cache/CacheContainer.h"
#include "../TCPIP/IPAddress.h"
#include "../Util/Utilities.h"
#include "../AntiSpam/DMARC/DMARC.h"
#include "../../SMTP/SMTPConfiguration.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Header field names are case-insensitive (RFC 5322 3.6.8), and there is
      // no case-insensitive find on AnsiString, so this is the one place that
      // spells the comparison out.
      bool StartsWithFieldName(const AnsiString &line, const AnsiString &fieldName)
      {
         if (line.GetLength() <= fieldName.GetLength())
            return false;

         for (int i = 0; i < fieldName.GetLength(); i++)
         {
            if (::tolower((unsigned char) line.GetAt(i)) != ::tolower((unsigned char) fieldName.GetAt(i)))
               return false;
         }

         // White space between the name and the colon is obsolete syntax that
         // RFC 5322 4.5.8 still requires a parser to accept, and real mail
         // carries it.
         int index = fieldName.GetLength();
         while (index < line.GetLength() && (line.GetAt(index) == ' ' || line.GetAt(index) == '\t'))
            index++;

         return index < line.GetLength() && line.GetAt(index) == ':';
      }

      // Every run of white space, folding included, becomes one space. The
      // Received header this server writes is folded across four lines, and
      // every comparison below would otherwise have to know where.
      AnsiString CollapseWhiteSpace(const AnsiString &value)
      {
         AnsiString result;
         bool inWhiteSpace = false;

         for (int i = 0; i < value.GetLength(); i++)
         {
            char c = value.GetAt(i);

            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
               inWhiteSpace = true;
               continue;
            }

            if (inWhiteSpace && !result.IsEmpty())
               result += " ";

            inWhiteSpace = false;
            result += c;
         }

         return result;
      }

      AnsiString ToLowerCopy(const AnsiString &value)
      {
         AnsiString result = value;
         result.ToLower();
         return result;
      }
   }

   AnsiString
   MessageOrigin::FirstFieldValue(const AnsiString &header, const AnsiString &fieldName)
   {
      int position = 0;

      while (position < header.GetLength())
      {
         int lineEnd = header.Find("\n", position);
         if (lineEnd < 0)
            lineEnd = header.GetLength();

         AnsiString line = header.Mid(position, lineEnd - position);

         // The blank line that ends the header block. Anything below it is body,
         // and a body may hold a line that looks exactly like a field - which is
         // how a sender would otherwise supply a second From for this to read.
         AnsiString trimmed = line;
         trimmed.TrimRight();
         if (trimmed.IsEmpty())
            return "";

         if (StartsWithFieldName(line, fieldName))
         {
            int colon = line.Find(":");
            AnsiString value = line.Mid(colon + 1);

            // Every folded continuation line - one that begins with space or
            // tab (RFC 5322 2.2.3).
            int next = lineEnd + 1;
            while (next < header.GetLength())
            {
               char first = header.GetAt(next);
               if (first != ' ' && first != '\t')
                  break;

               int continuationEnd = header.Find("\n", next);
               if (continuationEnd < 0)
                  continuationEnd = header.GetLength();

               value += " " + header.Mid(next, continuationEnd - next);
               next = continuationEnd + 1;
            }

            return CollapseWhiteSpace(value);
         }

         position = lineEnd + 1;
      }

      return "";
   }

   bool
   MessageOrigin::ReceivedSaysAuthenticated(const AnsiString &header, const String &thisServerName)
   {
      if (thisServerName.IsEmpty())
         return false;

      AnsiString received = ToLowerCopy(FirstFieldValue(header, "Received"));
      if (received.IsEmpty())
         return false;

      // "by <this server> with ESMTP<flags>". Both halves are required. The
      // "by" clause is what makes the line ours rather than a sender's, and
      // without it any relayed message carrying an ESMTPA hop of its own -
      // which is every message somebody submitted anywhere - would read as
      // authenticated here.
      AnsiString marker = "by " + ToLowerCopy(AnsiString(thisServerName)) + " with esmtp";

      int markerPosition = received.Find(marker);
      if (markerPosition < 0)
         return false;

      for (int i = markerPosition + marker.GetLength(); i < received.GetLength(); i++)
      {
         char c = received.GetAt(i);

         if (c == ' ' || c == '\t')
            break;

         // RFC 3848: S for a TLS session, A for an authenticated one. Read as a
         // set rather than by position, so a letter appearing between them one
         // day does not silently turn the answer to no.
         if (c == 'a')
            return true;
      }

      return false;
   }

   bool
   MessageOrigin::IsHostedAddress(const String &address)
   {
      if (address.IsEmpty())
         return false;

      if (!StringParser::IsValidEmailAddress(address))
         return false;

      if (CacheContainer::Instance()->GetAccount(address))
         return true;

      std::shared_ptr<const Alias> alias = CacheContainer::Instance()->GetAlias(address);
      if (alias && alias->GetIsActive())
         return true;

      String domainName = StringParser::ExtractDomain(address);
      if (domainName.IsEmpty())
         return false;

      if (CacheContainer::Instance()->GetDomain(domainName))
         return true;

      // A domain alias is a name this server hosts too. DKIMSigner resolves one
      // the same way, for the same reason.
      std::shared_ptr<DomainAliases> domainAliases = ObjectCache::Instance()->GetDomainAliases();
      if (domainAliases)
      {
         String resolvedDomain = StringParser::ExtractDomain(domainAliases->ApplyAliasesOnAddress(address));

         if (!resolvedDomain.IsEmpty() &&
             resolvedDomain.CompareNoCase(domainName) != 0 &&
             CacheContainer::Instance()->GetDomain(resolvedDomain))
            return true;
      }

      return false;
   }

   MessageOrigin::Facts
   MessageOrigin::ReadFacts(std::shared_ptr<Message> message, const AnsiString &header, const String &sendersIP)
   {
      Facts facts;

      if (message)
         facts.envelope_sender = message->GetFromAddress();

      // The tree's RFC 5322 address parser, through the function DMARC already
      // uses on this same header for this same purpose - so the two agree on
      // what the From address is, which matters more than either answer alone.
      facts.header_from = DMARC::ExtractAddressFromHeaderValue(String(FirstFieldValue(header, "From")));

      facts.authenticated_submission = ReceivedSaysAuthenticated(header, Utilities::ComputerName());

      if (!sendersIP.IsEmpty())
      {
         IPAddress address;

         // Quietly: an address this could not parse is simply not a relay, and
         // reporting it would put a line in the error log for every message
         // whose Received header some other server wrote in a shape ours does
         // not read.
         if (address.TryParse(sendersIP, false))
         {
            std::shared_ptr<IncomingRelays> relays = Configuration::Instance()->GetSMTPConfiguration()->GetIncomingRelays();

            if (relays)
               facts.from_trusted_relay = relays->IsIncomingRelay(address);
         }
      }

      return facts;
   }

   bool
   MessageOrigin::IsExternalSender(const Facts &facts, const HostedAddressTest &isHosted, String &reason)
   {
      if (facts.authenticated_submission)
      {
         reason = _T("the session authenticated");
         return false;
      }

      if (facts.from_trusted_relay)
      {
         // The administrator has named this address as a machine that hands
         // mail to this server on its behalf, which is a statement that the hop
         // is part of this installation. Mail arriving through a filtering
         // gateway listed here is therefore never tagged - which is the one
         // sharp edge of this feature, and hmailserver/docs/DomainTransforms.md
         // says so plainly rather than leaving it to be discovered.
         reason = _T("a trusted incoming relay handed it to us");
         return false;
      }

      // The visible sender first: it is what the reader is being warned about,
      // and a message whose From is outside is outside whatever its envelope
      // claims.
      if (!facts.header_from.IsEmpty() && !isHosted(facts.header_from))
      {
         reason = _T("the From address is not at a domain this server hosts");
         return true;
      }

      if (!facts.envelope_sender.IsEmpty() && !isHosted(facts.envelope_sender))
      {
         reason = _T("the envelope sender is not at a domain this server hosts");
         return true;
      }

      if (facts.header_from.IsEmpty() && facts.envelope_sender.IsEmpty())
      {
         // No From and a null return path, from an unauthenticated session off
         // no trusted relay. Nothing here belongs to this installation, so the
         // honest answer is outside.
         reason = _T("the message names no sender at all");
         return true;
      }

      reason = _T("every address the message names is at a domain this server hosts");
      return false;
   }

   bool
   MessageOrigin::IsExternalSender(const Facts &facts, String &reason)
   {
      return IsExternalSender(facts, [](const String &address) { return IsHostedAddress(address); }, reason);
   }

   void
   MessageOriginTester::Test()
   {
      TestFieldReading_();
      TestReceivedParsing_();
      TestTheRule_();
   }

   void
   MessageOriginTester::TestFieldReading_()
   {
      AnsiString header =
         "Return-Path: <bounce@example.test>\r\n"
         "Received: from client.example.test (client.example.test [192.0.2.9])\r\n"
         "\tby MAILBOX with ESMTPSA id 41\r\n"
         "\t; Mon, 15 Sep 2026 09:00:00 +0000\r\n"
         "From: \"Ada Lovelace\" <ada@example.test>\r\n"
         "Subject: Hello\r\n"
         "\r\n"
         "From: not-a-header@example.invalid\r\n";

      if (MessageOrigin::FirstFieldValue(header, "From") != "\"Ada Lovelace\" <ada@example.test>")
         throw std::logic_error("MessageOrigin::FirstFieldValue did not read the From field.");

      if (MessageOrigin::FirstFieldValue(header, "Subject") != "Hello")
         throw std::logic_error("MessageOrigin::FirstFieldValue did not read the Subject field.");

      // The folded Received header comes back as one line.
      if (MessageOrigin::FirstFieldValue(header, "Received").Find("by MAILBOX with ESMTPSA id 41") < 0)
         throw std::logic_error("MessageOrigin::FirstFieldValue did not unfold the Received field.");

      if (!MessageOrigin::FirstFieldValue(header, "X-Absent").IsEmpty())
         throw std::logic_error("MessageOrigin::FirstFieldValue invented a field that is not there.");

      // A line below the blank line is body, not header. Reading it would let a
      // sender supply a second Received for the rule below to believe.
      AnsiString bodyOnly = "Subject: Hello\r\n\r\nReceived: from evil by MAILBOX with ESMTPA id 1\r\n";
      if (!MessageOrigin::FirstFieldValue(bodyOnly, "Received").IsEmpty())
         throw std::logic_error("MessageOrigin::FirstFieldValue read a field out of the body.");
   }

   void
   MessageOriginTester::TestReceivedParsing_()
   {
      AnsiString authenticated =
         "Received: from client (client [192.0.2.9])\r\n"
         "\tby MAILBOX with ESMTPSA id 41\r\n"
         "\t; Mon, 15 Sep 2026 09:00:00 +0000\r\n";

      if (!MessageOrigin::ReceivedSaysAuthenticated(authenticated, _T("MAILBOX")))
         throw std::logic_error("An ESMTPSA Received header written by this server did not read as authenticated.");

      // Case-insensitive on both halves: Windows reports the computer name in
      // upper case and the header carries it as configured.
      if (!MessageOrigin::ReceivedSaysAuthenticated(authenticated, _T("mailbox")))
         throw std::logic_error("The server name comparison is case-sensitive; it must not be.");

      AnsiString plain =
         "Received: from client (client [192.0.2.9])\r\n"
         "\tby MAILBOX with ESMTP id 41\r\n";

      if (MessageOrigin::ReceivedSaysAuthenticated(plain, _T("MAILBOX")))
         throw std::logic_error("An ESMTP Received header with no A read as authenticated.");

      AnsiString tlsOnly =
         "Received: from client (client [192.0.2.9])\r\n"
         "\tby MAILBOX with ESMTPS id 41\r\n";

      if (MessageOrigin::ReceivedSaysAuthenticated(tlsOnly, _T("MAILBOX")))
         throw std::logic_error("An ESMTPS Received header - TLS, not authentication - read as authenticated.");

      // The line a sender can forge: it does not name this server in its by
      // clause, and it is not the topmost one either.
      AnsiString forged =
         "Received: from relay.example.test (relay.example.test [192.0.2.9])\r\n"
         "\tby MAILBOX with ESMTP id 41\r\n"
         "Received: from somewhere by somebodyelse with ESMTPA id 9\r\n";

      if (MessageOrigin::ReceivedSaysAuthenticated(forged, _T("MAILBOX")))
         throw std::logic_error("A Received header the sender supplied was allowed to claim authentication.");

      if (MessageOrigin::ReceivedSaysAuthenticated(authenticated, _T("")))
         throw std::logic_error("With no server name to compare against, the answer must be no.");
   }

   void
   MessageOriginTester::TestTheRule_()
   {
      // The installation, for this test: two hosted addresses and nothing else.
      MessageOrigin::HostedAddressTest hosted = [](const String &address)
      {
         return address.CompareNoCase(_T("ada@example.test")) == 0 ||
                address.CompareNoCase(_T("postmaster@example.test")) == 0;
      };

      String reason;

      MessageOrigin::Facts outside;
      outside.envelope_sender = _T("stranger@elsewhere.invalid");
      outside.header_from = _T("stranger@elsewhere.invalid");
      if (!MessageOrigin::IsExternalSender(outside, hosted, reason))
         throw std::logic_error("A sender at no hosted domain was judged internal.");

      MessageOrigin::Facts inside;
      inside.envelope_sender = _T("ada@example.test");
      inside.header_from = _T("ada@example.test");
      if (MessageOrigin::IsExternalSender(inside, hosted, reason))
         throw std::logic_error("A sender at a hosted domain was judged external.");

      // Authenticated beats everything: a signed-in account sending as somebody
      // else is still this installation's own traffic.
      MessageOrigin::Facts authenticated = outside;
      authenticated.authenticated_submission = true;
      if (MessageOrigin::IsExternalSender(authenticated, hosted, reason))
         throw std::logic_error("An authenticated submission was judged external.");

      MessageOrigin::Facts relayed = outside;
      relayed.from_trusted_relay = true;
      if (MessageOrigin::IsExternalSender(relayed, hosted, reason))
         throw std::logic_error("A message from a trusted incoming relay was judged external.");

      // The two disagreements, both resolved in the direction that warns.
      MessageOrigin::Facts forgedEnvelope;
      forgedEnvelope.envelope_sender = _T("ada@example.test");
      forgedEnvelope.header_from = _T("stranger@elsewhere.invalid");
      if (!MessageOrigin::IsExternalSender(forgedEnvelope, hosted, reason))
         throw std::logic_error("A message with an outside From and a hosted envelope was judged internal.");

      MessageOrigin::Facts forgedFrom;
      forgedFrom.envelope_sender = _T("stranger@elsewhere.invalid");
      forgedFrom.header_from = _T("ada@example.test");
      if (!MessageOrigin::IsExternalSender(forgedFrom, hosted, reason))
         throw std::logic_error("A message with an outside envelope and a hosted From was judged internal.");

      // A bounce this server generated: null return path, a hosted From.
      MessageOrigin::Facts localBounce;
      localBounce.header_from = _T("postmaster@example.test");
      if (MessageOrigin::IsExternalSender(localBounce, hosted, reason))
         throw std::logic_error("A bounce this server generated was judged external.");

      // A bounce from elsewhere: null return path, a From that is not ours.
      MessageOrigin::Facts remoteBounce;
      remoteBounce.header_from = _T("MAILER-DAEMON@elsewhere.invalid");
      if (!MessageOrigin::IsExternalSender(remoteBounce, hosted, reason))
         throw std::logic_error("A bounce from another server was judged internal.");

      MessageOrigin::Facts nameless;
      if (!MessageOrigin::IsExternalSender(nameless, hosted, reason))
         throw std::logic_error("A message naming no sender was judged internal.");

      if (reason.IsEmpty())
         throw std::logic_error("The rule gave no reason for its answer.");
   }
}
