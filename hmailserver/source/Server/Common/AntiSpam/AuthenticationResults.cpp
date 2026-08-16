// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"

#include "AuthenticationResults.h"

#include "../Util/Utilities.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // RFC 5322 caps a header line at 998 octets. Several dkim= clauses with long d=
      // values pass that easily, and an over-long line is something a downstream MTA
      // may refuse, so each method starts a new folded line once the current one has
      // grown past a comfortable width.
      const int MaxLineLength = 76;

      // A ceiling on any single value taken from the message. Nothing legitimate needs
      // more, and it bounds what one sender can add to every header we write.
      const int MaxValueLength = 255;
   }

   AuthenticationResults::AuthenticationResults() :
      spf_result_(ResultNotEvaluated),
      dmarc_result_(ResultNotEvaluated)
   {

   }

   void
   AuthenticationResults::SetSpf(MethodResult result, const AnsiString &identityType, const AnsiString &identityValue,
                                 const AnsiString &clientIp, const AnsiString &heloHost)
   {
      spf_result_ = result;
      spf_identity_type_ = identityType;
      spf_identity_value_ = identityValue;
      spf_client_ip_ = clientIp;
      spf_helo_host_ = heloHost;
   }

   void
   AuthenticationResults::AddDkim(MethodResult result, const AnsiString &signingDomain)
   {
      DkimEntry entry;
      entry.result = result;
      entry.domain = signingDomain;

      dkim_entries_.push_back(entry);
   }

   void
   AuthenticationResults::SetDmarc(MethodResult result, const AnsiString &headerFromDomain, const AnsiString &policyText)
   {
      dmarc_result_ = result;
      dmarc_from_domain_ = headerFromDomain;
      dmarc_policy_ = policyText;
   }

   bool
   AuthenticationResults::IsEmpty() const
   {
      return spf_result_ == ResultNotEvaluated &&
             dkim_entries_.empty() &&
             dmarc_result_ == ResultNotEvaluated;
   }

   bool
   AuthenticationResults::HasSpfResult() const
   {
      return spf_result_ != ResultNotEvaluated;
   }

   AnsiString
   AuthenticationResults::ResultKeyword_(MethodResult result)
   {
      switch (result)
      {
      case ResultNone:      return "none";
      case ResultPass:      return "pass";
      case ResultFail:      return "fail";
      case ResultSoftFail:  return "softfail";
      case ResultNeutral:   return "neutral";
      case ResultTempError: return "temperror";
      case ResultPermError: return "permerror";
      default:              return "none";
      }
   }

   AnsiString
   AuthenticationResults::SanitizeValue_(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength());

      for (char c : value)
      {
         const bool alphanumeric = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

         // Dropping rather than escaping, because there is no escaping that is safe in
         // every position this text can land in: inside a comment, beside a tag, at the
         // start of a folded line. Everything not listed here goes - which removes CR,
         // LF, HTAB, space, ';', '(', ')', '"', '\' and ',' - so no value can end a
         // clause, close a comment or start a header field of its own.
         if (alphanumeric ||
             c == '@' || c == '.' || c == '-' || c == '_' ||
             c == '+' || c == '=' || c == '/' || c == ':' ||
             c == '[' || c == ']')
         {
            result += c;
         }

         if (result.GetLength() >= MaxValueLength)
            break;
      }

      return result;
   }

   AnsiString
   AuthenticationResults::GetAuthservId()
   {
      String configured = IniFileSettings::Instance()->GetAuthenticationResultsIdentity();

      AnsiString identity = configured.IsEmpty() ? AnsiString(Utilities::ComputerName()) : AnsiString(configured);
      identity.MakeLower();

      // This becomes the first token of the field and is what the strip below matches
      // on, so it has to be a single safe token or the whole scheme is unparseable -
      // and, worse, unstrippable, which would let a forged field survive.
      identity = SanitizeValue_(identity);

      if (identity.IsEmpty())
         identity = "localhost";

      return identity;
   }

   void
   AuthenticationResults::AppendMethod_(AnsiString &target, const AnsiString &clause)
   {
      int lastBreak = target.ReverseFind('\n');
      int currentLineLength = target.GetLength() - (lastBreak + 1);

      // RFC 8601's ABNF puts the ';' BEFORE each methodspec, and there is no trailing
      // one after the last.
      target += ";";

      if (currentLineLength + clause.GetLength() + 2 > MaxLineLength)
         target += "\r\n\t";
      else
         target += " ";

      target += clause;
   }

   AnsiString
   AuthenticationResults::BuildAuthenticationResultsValue() const
   {
      if (IsEmpty())
         return "";

      AnsiString value = GetAuthservId();

      if (spf_result_ != ResultNotEvaluated)
      {
         AnsiString clause = Formatter::FormatAsAnsi("spf={0}", ResultKeyword_(spf_result_));

         AnsiString identity = SanitizeValue_(spf_identity_value_);
         if (!identity.IsEmpty())
            clause += Formatter::FormatAsAnsi(" {0}={1}", spf_identity_type_, identity);

         AppendMethod_(value, clause);
      }

      for (const DkimEntry &entry : dkim_entries_)
      {
         AnsiString clause = Formatter::FormatAsAnsi("dkim={0}", ResultKeyword_(entry.result));

         AnsiString domain = SanitizeValue_(entry.domain);
         if (!domain.IsEmpty())
            clause += Formatter::FormatAsAnsi(" header.d={0}", domain);

         AppendMethod_(value, clause);
      }

      if (dmarc_result_ != ResultNotEvaluated)
      {
         AnsiString clause = Formatter::FormatAsAnsi("dmarc={0}", ResultKeyword_(dmarc_result_));

         // The published policy as a reason comment. It is what tells a downstream
         // filter whether a dmarc=fail was p=none advisory or a p=reject the domain
         // owner meant, which the keyword alone cannot say.
         AnsiString policy = SanitizeValue_(dmarc_policy_);
         if (!policy.IsEmpty())
            clause += Formatter::FormatAsAnsi(" (p={0})", policy);

         AnsiString fromDomain = SanitizeValue_(dmarc_from_domain_);
         if (!fromDomain.IsEmpty())
            clause += Formatter::FormatAsAnsi(" header.from={0}", fromDomain);

         AppendMethod_(value, clause);
      }

      return value;
   }

   AnsiString
   AuthenticationResults::BuildReceivedSpfValue() const
   {
      if (spf_result_ == ResultNotEvaluated)
         return "";

      const AnsiString authservId = GetAuthservId();
      const AnsiString identity = SanitizeValue_(spf_identity_value_);
      const AnsiString clientIp = SanitizeValue_(spf_client_ip_);
      const AnsiString helo = SanitizeValue_(spf_helo_host_);

      // The comment is written here rather than taken from the policy's exp= string.
      // exp= is a TXT record the SENDING domain controls, so quoting it would let a
      // stranger choose the bytes of a header field this server vouches for.
      AnsiString explanation;

      switch (spf_result_)
      {
      case ResultPass:
         explanation = Formatter::FormatAsAnsi("{0}: domain of {1} designates {2} as permitted sender", authservId, identity, clientIp);
         break;
      case ResultFail:
      case ResultSoftFail:
         explanation = Formatter::FormatAsAnsi("{0}: domain of {1} does not designate {2} as permitted sender", authservId, identity, clientIp);
         break;
      case ResultNone:
         explanation = Formatter::FormatAsAnsi("{0}: {1} does not publish an SPF policy", authservId, identity);
         break;
      default:
         explanation = Formatter::FormatAsAnsi("{0}: the SPF policy of {1} could not be evaluated", authservId, identity);
         break;
      }

      AnsiString value = Formatter::FormatAsAnsi("{0} ({1})", ResultKeyword_(spf_result_), explanation);

      value += "\r\n\t";
      value += Formatter::FormatAsAnsi("client-ip={0}; envelope-from=<{1}>;", clientIp, identity);

      if (!helo.IsEmpty())
         value += Formatter::FormatAsAnsi(" helo={0};", helo);

      value += Formatter::FormatAsAnsi(" receiver={0}; identity={1}",
         authservId, spf_identity_type_ == "smtp.helo" ? AnsiString("helo") : AnsiString("mailfrom"));

      return value;
   }

   bool
   AuthenticationResults::FieldCarriesAuthservId_(const AnsiString &field, const AnsiString &wantedLowerCase)
   {
      int colonPosition = field.Find(":");

      if (colonPosition <= 0)
         return false;

      AnsiString name = field.Mid(0, colonPosition);
      name.Trim();

      if (name.CompareNoCase("Authentication-Results") != 0)
         return false;

      AnsiString value = field.Mid(colonPosition + 1);

      // UNFOLDED before anything is read out of it, which is what the previous version
      // failed to do. It looked at the first physical line only, so
      //
      //    Authentication-Results:\r\n
      //    \tmail; spf=pass smtp.mailfrom=attacker.example\r\n
      //
      // presented an empty value on line one, matched nothing, and the forged field
      // survived - while every RFC 8601 reader unfolds it and sees our own identity
      // vouching for the sender's own verdict. Folding is legal RFC 5322 and needs no
      // special configuration to produce, so this was a bypass of the whole point of
      // the strip.
      value.Replace("\r", "");
      value.Replace("\n", " ");

      // The authserv-id is the first token, ahead of the optional version number and
      // the first ';'.
      int semicolonPosition = value.Find(";");
      if (semicolonPosition >= 0)
         value = value.Mid(0, semicolonPosition);

      value.Trim();

      // RFC 8601 permits the authserv-id as a quoted string, and the quotes are not
      // part of the identity. Comparing with them attached was the second bypass:
      // Authentication-Results: "mail"; dkim=pass was not recognised as ours, and a
      // downstream reader unquotes it and trusts it.
      if (value.GetLength() >= 2 && value[0] == '"')
      {
         int closingQuote = value.Find("\"", 1);

         if (closingQuote > 0)
            value = value.Mid(1, closingQuote - 1);
         else
            value = value.Mid(1);
      }
      else
      {
         int spacePosition = value.Find(" ");
         if (spacePosition > 0)
            value = value.Mid(0, spacePosition);
      }

      value.Trim();
      value.MakeLower();

      return value == wantedLowerCase;
   }

   bool
   AuthenticationResults::ContainsResultsForAuthservId(const AnsiString &headerBlock, const AnsiString &authservId)
   {
      int removedCount = 0;
      StripResultsForAuthservId(headerBlock, authservId, removedCount);

      return removedCount > 0;
   }

   AnsiString
   AuthenticationResults::StripResultsForAuthservId(const AnsiString &headerBlock,
                                                    const AnsiString &authservId,
                                                    int &removedCount)
   {
      removedCount = 0;

      AnsiString wanted = authservId;
      wanted.MakeLower();

      AnsiString result;
      result.reserve(headerBlock.GetLength());

      // Split on "\n" and put it back, rather than parsing with MimeHeader and
      // re-serialising: every field this does NOT drop has to survive byte for byte,
      // because the message may carry a DKIM signature over those exact bytes.
      std::vector<AnsiString> lines = StringParser::SplitString(headerBlock, "\n");

      // Whether the block itself ended with a newline has to be remembered separately,
      // because SplitString DISCARDS the trailing empty substring: splitting
      // "A: 1\r\nB: 2\r\n" gives two elements, not three, so "is this the last element"
      // cannot tell a block that ended with a newline from one that did not.
      //
      // Getting this wrong silently corrupts the message. LoadHeader returns the header
      // up to and including the CRLF that closes its last field, and the blank line
      // separating header from body follows it - so dropping that final newline would
      // splice the last header line onto the separator and leave "\r\r\n" where the
      // header/body boundary should be.
      const bool inputEndsWithNewline = !headerBlock.IsEmpty() &&
                                        headerBlock[headerBlock.GetLength() - 1] == '\n';

      // A whole field at a time, with its continuation lines, and the decision taken on
      // the complete unfolded value.
      //
      // Deciding line by line was what let a folded field through: the first line of
      // "Authentication-Results:\r\n\tmail; ..." carries no authserv-id at all, so the
      // field was kept and its continuation - which is where the identity actually is -
      // was kept with it. Buffering the field means the same bytes are emitted or
      // dropped as a unit, and the test sees what a reader would see.
      AnsiString pendingField;

      for (size_t i = 0; i <= lines.size(); i++)
      {
         const bool isEnd = (i == lines.size());

         // A continuation line begins with WSP; anything else starts a new field and
         // therefore settles the previous one.
         const bool isContinuation = !isEnd && !lines[i].IsEmpty() &&
                                     (lines[i][0] == ' ' || lines[i][0] == '\t');

         if (!isContinuation && !pendingField.IsEmpty())
         {
            if (FieldCarriesAuthservId_(pendingField, wanted))
               removedCount++;
            else
               result += pendingField;

            pendingField.Empty();
         }

         if (isEnd)
            break;

         // SplitString removed the "\n"; the "\r" is still on the line. Rebuilt exactly
         // as found, so a kept field is byte-for-byte what arrived - which matters
         // because a DKIM signature may cover those exact bytes.
         pendingField += lines[i];

         if (((i + 1) < lines.size()) || inputEndsWithNewline)
            pendingField += "\n";
      }

      return result;
   }
}
