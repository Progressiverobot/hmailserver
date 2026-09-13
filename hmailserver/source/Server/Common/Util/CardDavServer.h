// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// CardDAV (RFC 6352) over the account's address book, served by the web
// services listener under /dav/ so that a phone or a desktop client syncs the
// contacts the webmail keeps. One address book per account, "Contacts", whose
// members are the rows of hm_contacts (ContactStore) as vCard 3.0.
//
// The tree a client walks, from the RFC 6764 well-known redirect:
//
//    /dav/                                     the context path: PROPFIND here
//                                              answers current-user-principal
//    /dav/principals/<address>/                the principal: addressbook-home-set
//    /dav/addressbooks/<address>/              the home: one child collection
//    /dav/addressbooks/<address>/contacts/     the address book: getctag,
//                                              sync-token, the reports
//    /dav/addressbooks/<address>/contacts/<id>.vcf   one contact
//
// Every request under /dav/ is HTTP Basic as the account - the address and the
// password, or an app password - through AccountLogon, the same check as an
// IMAP logon with the same lockout and auto-ban. It is served over HTTPS only:
// over plain HTTP, with no proxy saying X-Forwarded-Proto: https, the answer is
// 403 with the reason, because Basic puts the password on the wire.
//
// What the store cannot hold is refused, never silently dropped: a card with no
// EMAIL is answered 403, since a contact here is a name and one address; the
// other properties a client sends (TEL, ADR, ORG, NOTE, PHOTO) are not kept,
// and the card the server returns says so by not carrying them. A client that
// creates a contact under a name of its own choosing gets the contact back
// under the server's name, <id>.vcf, in the Location header of the 201 - the
// store has no column for a client's resource name or UID, so both are derived
// from the row: see StableUid_ and CardDavServer.cpp.

#pragma once

#include "HttpServer.h"

namespace HM
{
   class CardDavServer
   {
   public:
      // The context path RFC 6764 section 6 has the well-known URI redirect
      // to. With a trailing slash, as a collection.
      static const char *ContextPath;

      // Whether a request-target is under ContextPath (or is it, without the
      // slash).
      static bool IsDavTarget(const AnsiString &target);

      // Answers one request under ContextPath. over_https says whether the
      // request reached the listener over TLS, or a proxy that terminated TLS
      // says it did.
      static HttpResponse Handle(const HttpRequest &request, bool over_https);

      // Whether a request under ContextPath may be as large as the listener's
      // large cap: a PUT (a card can carry a photo of some hundred kilobytes,
      // which is read and not kept) and a REPORT (a multiget names many
      // hrefs).
      static bool IsLargeRequest(const AnsiString &method, const AnsiString &target);
   };
}
