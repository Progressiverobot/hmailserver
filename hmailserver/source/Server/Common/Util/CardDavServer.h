// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

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
// A card is kept as the client sent it (hm_contacts.contactvcard, schema
// 6040), under the resource name the client chose (contacturi) and with its
// own UID (contactuid), and is served back byte for byte - so the ETag a PUT
// answers is the ETag of what a GET returns, and a phone that stored TEL and
// PHOTO reads them back. The row's name and address are taken from the card
// (FN, N, the preferred EMAIL) for the webmail's address book, and a change
// the webmail makes to them is written back into the card. What the store
// cannot hold is refused, never silently dropped: a card with no EMAIL is
// answered 403, since a contact here has one address, and a card whose address
// another contact of the book already has is answered 409 naming that contact.
// A contact the webmail made has no name of its own and is served as <id>.vcf,
// with a UID derived from the row (StableUid_).

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
