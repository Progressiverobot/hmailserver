// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// vCard, as far as this server's address book needs it: a reader for the
// content lines of a vCard 3.0 (RFC 2426) or 4.0 (RFC 6350) card, the escaping
// and folding rules the two share, and the two operations CardDAV performs on
// a contact - taking a name and one e-mail address out of a card a client
// sends, and writing a card for a contact the store holds. Bytes in, bytes
// out, UTF-8 throughout: nothing here is a String.

#pragma once

#include <utility>
#include <vector>

namespace HM
{
   struct VCardProperty
   {
      AnsiString group;    // "item1" of item1.EMAIL, or empty
      AnsiString name;     // upper-cased: "EMAIL", "FN", "N"
      AnsiString value;    // as written, escapes intact; see VCard::Unescape

      // Parameter names upper-cased; values as written, quotes removed. A
      // vCard 2.1 bare parameter (EMAIL;INTERNET;PREF:) is kept as TYPE.
      std::vector<std::pair<AnsiString, AnsiString>> parameters;

      // Whether a parameter carries the value - case-insensitively, and inside
      // a comma-separated list, so TYPE=INTERNET,PREF has PREF.
      bool HasParameter(const AnsiString &parameterName, const AnsiString &parameterValue) const;

      // The first value of the parameter, or "" when absent.
      AnsiString Parameter(const AnsiString &parameterName) const;
   };

   class VCard
   {
   public:
      // Unfolds and splits one card. False, with the reason, when the text is
      // not one: it must open with BEGIN:VCARD and close with END:VCARD, and
      // every line between must have a name and a colon. Line breaks may be
      // CRLF or bare LF; a line beginning with a space or a tab continues the
      // one before it.
      static bool Parse(const AnsiString &text, std::vector<VCardProperty> &properties, AnsiString &problem);

      // The text-value escapes: \\ \n \, \; (RFC 2426 section 2.4.2, RFC 6350
      // section 3.4).
      static AnsiString Unescape(const AnsiString &value);
      static AnsiString Escape(const AnsiString &value);

      // The fields of a structured value such as N or ADR, split on the
      // semicolons that are not escaped.
      static std::vector<AnsiString> Components(const AnsiString &value);

      // One content line folded at 75 octets with CRLF and a space, never
      // inside a UTF-8 sequence (RFC 2426 section 2.6).
      static AnsiString Fold(const AnsiString &line);

      // What this store keeps of a card. The name is FN, or assembled from N
      // when there is no FN; the address is the EMAIL marked preferred (TYPE
      // PREF in 3.0, the lowest PREF in 4.0), else the first one, lower-cased
      // and without a mailto: prefix. False when the card has no EMAIL at all,
      // which this address book cannot hold.
      static bool ExtractNameAndAddress(const std::vector<VCardProperty> &properties, AnsiString &name, AnsiString &address);

      // A vCard 3.0 for one contact: UID, FN, N derived from the name, and
      // one EMAIL. CRLF line breaks, folded.
      static AnsiString Generate(const AnsiString &uid, const AnsiString &name, const AnsiString &address);

      // The N derived from a display name: "Family, Given" splits on the comma,
      // "Given Family" on the last space, a single word is a given name.
      static void SplitName(const AnsiString &name, AnsiString &family, AnsiString &given);
   };
}
