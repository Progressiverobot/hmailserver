// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com
//
// RFC 8601 Authentication-Results and RFC 7208 section 9.1 Received-SPF.
//
// Carries the authentication verdicts for one inbound message from the spam tests
// that produced them to the point where the trace headers are written. Nothing
// here touches the disk - see AuthenticationResultsWriter.

#pragma once

namespace HM
{
   class AuthenticationResults
   {
   public:

      // The RFC 8601 result keywords.
      //
      // NotEvaluated is not a result: it means the check did not run, and the method
      // is then omitted from the header entirely. It is deliberately distinct from
      // None, which means "we looked, and there was no policy to apply" - a receiver
      // acting on our header needs to tell those two apart.
      enum MethodResult
      {
         ResultNotEvaluated = 0,
         ResultNone = 1,
         ResultPass = 2,
         ResultFail = 3,
         ResultSoftFail = 4,   // spf only
         ResultNeutral = 5,
         ResultTempError = 6,
         ResultPermError = 7
      };

      AuthenticationResults();

      // identityType is "smtp.mailfrom" or "smtp.helo" (RFC 7208 2.4 / 2.3).
      void SetSpf(MethodResult result, const AnsiString &identityType, const AnsiString &identityValue,
                  const AnsiString &clientIp, const AnsiString &heloHost);

      // Called once per signature found on the message.
      void AddDkim(MethodResult result, const AnsiString &signingDomain);

      // policyText is the p= the domain published, for the reason comment; "" if unknown.
      void SetDmarc(MethodResult result, const AnsiString &headerFromDomain, const AnsiString &policyText);

      // There is deliberately no SetAuth here. RFC 8601 defines an auth= method for
      // SMTP AUTH, but this server writes these headers only for mail arriving from
      // outside - on a submission it authenticated itself there is no third party whose
      // claims need recording, and the account is already named by X-AuthUser. An auth=
      // slot would therefore be a method nothing could ever populate, which is the
      // "exists but is inert" shape this codebase has been bitten by before.

      // True when not one method was evaluated, i.e. there is nothing to say.
      bool IsEmpty() const;
      bool HasSpfResult() const;

      // The identity this server authenticates as. AuthenticationResultsIdentity if
      // set, otherwise the computer name - the same name the Received header says the
      // message was received "by". Always lower case, always a single safe token.
      static AnsiString GetAuthservId();

      // Empty when IsEmpty().
      AnsiString BuildAuthenticationResultsValue() const;

      // Empty when the SPF check did not run.
      AnsiString BuildReceivedSpfValue() const;

      // Removes every Authentication-Results field from headerBlock whose authserv-id
      // is ours, leaving every other field byte for byte as it was.
      //
      // RFC 8601 section 5: on mail whose source we have not authenticated, a field
      // bearing our own identity did not come from us - this runs before we add ours -
      // so it is a forgery, and a downstream filter that trusts our name would act on
      // a "dkim=pass" the sender wrote themselves.
      static AnsiString StripResultsForAuthservId(const AnsiString &headerBlock,
                                                  const AnsiString &authservId,
                                                  int &removedCount);

      // Cheap pre-check, so the common case (no forged field) does no rewriting at all.
      static bool ContainsResultsForAuthservId(const AnsiString &headerBlock, const AnsiString &authservId);

   private:

      struct DkimEntry
      {
         MethodResult result;
         AnsiString domain;
      };

      static AnsiString ResultKeyword_(MethodResult result);

      // Every value that reaches these headers is attacker-supplied: the envelope
      // sender, the HELO string, a signature's d=, the authenticated user name.
      // Anything outside a conservative set is DROPPED rather than escaped, and the
      // result is capped, so no input can inject a CR or LF, close a comment, or start
      // a tag of its own. Returns "" when nothing usable is left, and the property is
      // then omitted rather than emitted empty.
      static AnsiString SanitizeValue_(const AnsiString &value);

      static void AppendMethod_(AnsiString &target, const AnsiString &clause);

      // Whether one complete header field - first line plus every continuation line - is
      // an Authentication-Results field carrying this authserv-id. The value is unfolded
      // and an optionally quoted authserv-id is unquoted before comparison, because a
      // reader downstream will do both and a strip that does neither can be walked past.
      static bool FieldCarriesAuthservId_(const AnsiString &field, const AnsiString &wantedLowerCase);

      MethodResult spf_result_;
      AnsiString spf_identity_type_;
      AnsiString spf_identity_value_;
      AnsiString spf_client_ip_;
      AnsiString spf_helo_host_;

      std::vector<DkimEntry> dkim_entries_;

      MethodResult dmarc_result_;
      AnsiString dmarc_from_domain_;
      AnsiString dmarc_policy_;

   };
}
