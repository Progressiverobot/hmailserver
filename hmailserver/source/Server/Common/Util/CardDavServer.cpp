// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// See CardDavServer.h for the shape of the tree. This file is the request
// side of it: a small namespace-aware XML reader for the PROPFIND and REPORT
// bodies (XMLite is neither namespace-aware nor meant for input off the wire),
// the DAV: and CARDDAV: property set of each resource kind, the three reports,
// and PUT/GET/DELETE of one contact as a vCard, on top of ContactStore and
// VCard.
//
// The bits of RFC 4918, 6352 and 6578 that shape the answers:
//
//   - PROPFIND (4918 section 9.1) answers 207 with one D:response per resource
//     in scope, and inside it one D:propstat for the properties found and one,
//     status 404, for those asked for and not there. Depth: 0 is the resource,
//     1 adds its members; infinity is treated as 1, as sabre/dav does, rather
//     than refused, since nothing here is deeper than one level anyway.
//   - The reports (6352 section 8, 6578 section 3): addressbook-multiget takes
//     hrefs and answers each, 404 for one that is not in this address book;
//     addressbook-query takes a filter of prop-filters with text-matches and
//     answers the contacts that match; sync-collection takes a token. The
//     store keeps no change log, so a token is the state of the whole
//     collection: an empty token lists everything, the current token answers
//     "no changes", and any other is refused with DAV:valid-sync-token so the
//     client lists again. That is a valid answer under 6578 section 3.2 and it
//     keeps the cheap poll cheap; it is not an incremental sync.
//   - ETags (6352 section 6.3.2.3) are strong and are the SHA-256 of the card
//     the server serves, so they change exactly when the card does. getctag
//     and the sync-token are the SHA-256 of every member's id and ETag.
//   - PUT with If-None-Match: * creates and is 412 on an existing resource;
//     If-Match must equal the current ETag or the answer is 412 (7232). A card
//     with no EMAIL is 403 with CARDDAV:valid-address-data and a sentence
//     saying why: this address book keeps a name and one e-mail address per
//     contact, and a card without one has nothing to be stored as.

#include "StdAfx.h"

#include <algorithm>
#include <openssl/sha.h>

#include "CardDavServer.h"
#include "DavSupport.h"
#include "CalDavServer.h"
#include "ContactStore.h"
#include "VCard.h"
#include "../BO/Account.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const char *CardDavServer::ContextPath = "/dav/";

   using namespace Dav;

   namespace
   {
      const char *NsDav = "DAV:";
      const char *NsCardDav = "urn:ietf:params:xml:ns:carddav";
      const char *NsCalendarServer = "http://calendarserver.org/ns/";
      const char *NsCalDav = "urn:ietf:params:xml:ns:caldav";

      // How many hrefs one multiget may name; the XML ceilings are DavSupport's.
      const size_t MaxMultigetHrefs = 5000;

      // What CARDDAV:max-resource-size advertises, and the cap PUT enforces
      // on a card: the listener's large-request cap, less nothing - a card
      // that arrives has fitted already.
      const int MaxCardBytes = 1024 * 1024;

      const char *AllowContact = "Allow: OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, REPORT\r\n";
      const char *AllowCollection = "Allow: OPTIONS, PROPFIND, PROPPATCH, REPORT\r\n";

      //------------------------------------------------------------------------
      // The resources
      //------------------------------------------------------------------------

      enum ResourceKind
      {
         KindRoot,                // /dav/
         KindPrincipalCollection, // /dav/principals/
         KindPrincipal,           // /dav/principals/<address>/
         KindHomeCollection,      // /dav/addressbooks/
         KindCalendarsCollection, // /dav/calendars/ - CalDavServer's tree, listed here so the root is complete
         KindHome,                // /dav/addressbooks/<address>/
         KindAddressBook,         // /dav/addressbooks/<address>/contacts/
         KindContact,             // .../contacts/<id>.vcf, and the contact exists
         KindContactSlot          // .../contacts/<name>, nothing there yet: PUT may create
      };

      struct Resource
      {
         Resource() : kind(KindRoot) { }

         ResourceKind kind;
         AnsiString href;        // the canonical href, percent-encoded
         ContactRecord contact;  // KindContact only
         AnsiString slotName;    // KindContactSlot only: the name, decoded
      };

      // A member of the address book as the responses see it: the row, the
      // card the server serves for it and that card's ETag.
      struct Member
      {
         ContactRecord record;
         AnsiString card;
         AnsiString etag;
         AnsiString href;
         AnsiString uid;
      };

      // Everything one request needs to know, computed once.
      struct Context
      {
         Context(const HttpRequest &httpRequest) : request(httpRequest), listed(false), listFailed(false) { }

         const HttpRequest &request;
         std::shared_ptr<const Account> account;

         String addressWide;         // the account's address, lower-cased
         AnsiString addressUtf8;
         AnsiString principalHref;
         AnsiString homeHref;
         AnsiString bookHref;

         // The address book's members, loaded on first need.
         bool listed;
         bool listFailed;
         std::vector<Member> members;
      };

      // A name-based UUID of a seed, marked as version 8 (RFC 9562 section 5.8)
      // because its bits come from SHA-256: the same seed gives the same UID.
      AnsiString StableUidFromSeed_(const AnsiString &seed);

      // The UID of a contact the webmail made, which has no card and so no UID
      // of its own: derived from the account's address and the row id, so the
      // same row gives the same UID for as long as the account keeps its
      // address. A card a client sent carries its own UID, kept in the row.
      AnsiString StableUid_(const AnsiString &addressUtf8, __int64 id)
      {
         return StableUidFromSeed_("hMailServer.carddav.contact:" + addressUtf8 + ":" + Int64Text(id));
      }

      AnsiString StableUidFromSeed_(const AnsiString &seed)
      {

         unsigned char digest[SHA256_DIGEST_LENGTH] = {};
         SHA256(reinterpret_cast<const unsigned char *>(seed.c_str()), static_cast<size_t>(seed.GetLength()), digest);
         digest[6] = static_cast<unsigned char>((digest[6] & 0x0f) | 0x80);
         digest[8] = static_cast<unsigned char>((digest[8] & 0x3f) | 0x80);

         static const char *hex = "0123456789abcdef";
         AnsiString result;
         for (int i = 0; i < 16; i++)
         {
            if (i == 4 || i == 6 || i == 8 || i == 10)
               result += "-";
            result += hex[digest[i] >> 4];
            result += hex[digest[i] & 0x0f];
         }
         return result;
      }

      // The resource name a client created the contact under, or <id>.vcf for
      // a contact the webmail made.
      AnsiString ContactHref(const Context &context, const ContactRecord &record)
      {
         if (record.uri.IsEmpty())
            return context.bookHref + Int64Text(record.id) + ".vcf";
         return context.bookHref + PercentEncodeSegment(Utf8(record.uri));
      }

      void FillMember(const Context &context, const ContactRecord &record, Member &member)
      {
         member.record = record;
         member.uid = record.uid.IsEmpty() ? StableUid_(context.addressUtf8, record.id) : Utf8(record.uid);

         // A card a client sent is served as it came, byte for byte, so the
         // ETag the client was given for it is the ETag of what it reads back
         // (RFC 9110 section 8.8.3); a row that never came from a client - the
         // webmail's - is served as a card made from its name and address.
         member.card = record.vcard.IsEmpty()
            ? VCard::Generate(member.uid, Utf8(record.name), Utf8(record.address))
            : Utf8(record.vcard);
         member.etag = EtagOf(member.card);
         member.href = ContactHref(context, record);
      }

      bool EnsureListed(Context &context)
      {
         if (context.listed)
            return !context.listFailed;

         context.listed = true;
         std::vector<ContactRecord> records;
         if (!ContactStore::List(context.account->GetID(), records))
         {
            context.listFailed = true;
            return false;
         }

         for (size_t i = 0; i < records.size(); i++)
         {
            Member member;
            FillMember(context, records[i], member);
            context.members.push_back(member);
         }

         return true;
      }

      // The state of the whole collection, from which getctag and the
      // sync-token are made: every member's id and ETag, in id order.
      bool CollectionTag(Context &context, AnsiString &tag)
      {
         if (!EnsureListed(context))
            return false;

         std::vector<AnsiString> lines;
         for (size_t i = 0; i < context.members.size(); i++)
            lines.push_back(Int64Text(context.members[i].record.id) + ":" + context.members[i].etag);
         std::sort(lines.begin(), lines.end());

         AnsiString all;
         for (size_t i = 0; i < lines.size(); i++)
            all += lines[i] + "\n";

         tag = Sha256Hex(all).Left(32);
         return true;
      }

      AnsiString SyncTokenFor(const AnsiString &tag)
      {
         return "urn:x-hmailserver:carddav-sync:" + tag;
      }

      bool SameAddress(const Context &context, const AnsiString &segment)
      {
         String wide = Wide(segment);
         wide.ToLower();
         return wide == context.addressWide;
      }

      // The resource a path names, or false: a path outside the tree, or into
      // another account's part of it, is simply not found. Another account's
      // principal is 404 rather than 403 so that the tree of one account says
      // nothing about the existence of another.
      bool Resolve(Context &context, const AnsiString &target, Resource &resource)
      {
         std::vector<AnsiString> segments = PathSegments(target);

         // "/dav" or "/dav/" -> ["dav"] or ["dav", ""]
         if (segments.empty() || segments[0] != "dav")
            return false;

         // Drop the trailing empty segment a trailing slash leaves.
         bool trailingSlash = segments.size() > 1 && segments.back().IsEmpty();
         if (trailingSlash)
            segments.pop_back();

         if (segments.size() == 1)
         {
            resource.kind = KindRoot;
            resource.href = CardDavServer::ContextPath;
            return true;
         }

         if (segments[1] == "principals")
         {
            if (segments.size() == 2)
            {
               resource.kind = KindPrincipalCollection;
               resource.href = AnsiString(CardDavServer::ContextPath) + "principals/";
               return true;
            }
            if (segments.size() == 3 && SameAddress(context, segments[2]))
            {
               resource.kind = KindPrincipal;
               resource.href = context.principalHref;
               return true;
            }
            return false;
         }

         if (segments[1] == "addressbooks")
         {
            if (segments.size() == 2)
            {
               resource.kind = KindHomeCollection;
               resource.href = AnsiString(CardDavServer::ContextPath) + "addressbooks/";
               return true;
            }
            if (!SameAddress(context, segments[2]))
               return false;
            if (segments.size() == 3)
            {
               resource.kind = KindHome;
               resource.href = context.homeHref;
               return true;
            }
            if (segments[3] != "contacts")
               return false;
            if (segments.size() == 4)
            {
               resource.kind = KindAddressBook;
               resource.href = context.bookHref;
               return true;
            }
            if (segments.size() != 5 || trailingSlash || segments[4].IsEmpty())
               return false;

            // The name a client created a contact under names that contact;
            // <id>.vcf names a contact the webmail made, which has no name of
            // its own; any other name is a slot a PUT may fill.
            AnsiString name = segments[4];
            ContactRecord named;
            if (ContactStore::FindByUri(context.account->GetID(), Wide(name), named))
            {
               resource.kind = KindContact;
               resource.contact = named;
               resource.href = ContactHref(context, named);
               return true;
            }

            if (name.EndsWith(".vcf"))
            {
               AnsiString idText = name.Mid(0, name.GetLength() - 4);
               bool numeric = !idText.IsEmpty() && idText.GetLength() < 19;
               for (int i = 0; numeric && i < idText.GetLength(); i++)
                  numeric = idText[i] >= '0' && idText[i] <= '9';

               if (numeric)
               {
                  __int64 id = 0;
                  for (int i = 0; i < idText.GetLength(); i++)
                     id = id * 10 + (idText[i] - '0');

                  ContactRecord record;
                  if (id > 0 && ContactStore::Get(context.account->GetID(), id, record) && record.uri.IsEmpty())
                  {
                     resource.kind = KindContact;
                     resource.contact = record;
                     resource.href = ContactHref(context, record);
                     return true;
                  }
               }
            }

            resource.kind = KindContactSlot;
            resource.href = context.bookHref + PercentEncodeSegment(name);
            resource.slotName = name;
            return true;
         }

         return false;
      }

      //------------------------------------------------------------------------
      // Responses
      //------------------------------------------------------------------------

      // A DAV:error body (RFC 4918 section 16) naming the precondition that
      // failed, with a sentence for the person reading the client's log.
      HttpResponse DavError(int status, const AnsiString &preconditionXml, const AnsiString &description)
      {
         AnsiString xml = "<D:error xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\">" + preconditionXml +
            "<D:responsedescription>" + XmlEscape(description) + "</D:responsedescription></D:error>\r\n";
         return Xml(status, xml);
      }

      HttpResponse MethodNotAllowed(const Resource &resource)
      {
         bool contact = resource.kind == KindContact || resource.kind == KindContactSlot;
         return Text(405, "method not allowed", contact ? AllowContact : AllowCollection);
      }

      AnsiString MultistatusOpen()
      {
         return "<D:multistatus xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\" xmlns:CAL=\"urn:ietf:params:xml:ns:caldav\" xmlns:CS=\"http://calendarserver.org/ns/\">\r\n";
      }

      //------------------------------------------------------------------------
      // Properties
      //------------------------------------------------------------------------

      struct PropertyName
      {
         PropertyName() { }
         PropertyName(const char *propertyNs, const char *propertyName) : ns(propertyNs), name(propertyName) { }

         AnsiString ns;
         AnsiString name;
      };

      // How the properties are asked for.
      enum PropertyMode
      {
         ModeProp,      // the ones named
         ModeAllProp,   // every one the resource has (but not address-data)
         ModePropName   // the names of every one, no values
      };

      struct PropertyRequest
      {
         PropertyRequest() : mode(ModeAllProp) { }

         PropertyMode mode;
         std::vector<PropertyName> names;

         // address-data's partial-retrieval element, if the request named
         // properties inside it: the card is then reduced to those.
         std::vector<AnsiString> addressDataProperties;
      };

      // The element for a property in a response: prefixed for the three
      // namespaces the document declares, a default-namespace element for any
      // other. Empty inner XML makes an empty element.
      AnsiString PropertyElement(const PropertyName &property, const AnsiString &innerXml)
      {
         AnsiString prefix;
         if (property.ns == NsDav) prefix = "D:";
         else if (property.ns == NsCardDav) prefix = "C:";
         else if (property.ns == NsCalDav) prefix = "CAL:";
         else if (property.ns == NsCalendarServer) prefix = "CS:";

         AnsiString open = "<" + prefix + property.name;
         if (prefix.IsEmpty())
            open += " xmlns=\"" + XmlEscape(property.ns) + "\"";

         if (innerXml.IsEmpty())
            return open + "/>";
         return open + ">" + innerXml + "</" + prefix + property.name + ">";
      }

      AnsiString Href(const AnsiString &href)
      {
         return "<D:href>" + XmlEscape(href) + "</D:href>";
      }

      // The card reduced to the properties a partial address-data asked for.
      AnsiString ReduceCard(const AnsiString &card, const std::vector<AnsiString> &wanted)
      {
         if (wanted.empty())
            return card;

         AnsiString result;
         std::vector<AnsiString> lines = StringParser::SplitString(card, "\r\n");
         for (size_t i = 0; i < lines.size(); i++)
         {
            const AnsiString &line = lines[i];
            if (line.IsEmpty())
               continue;

            int colon = line.Find(":");
            int semicolon = line.Find(";");
            int end = colon;
            if (semicolon >= 0 && semicolon < end)
               end = semicolon;
            AnsiString name = end > 0 ? line.Mid(0, end) : line;
            name.ToUpper();

            bool keep = name == "BEGIN" || name == "END" || name == "VERSION";
            for (size_t j = 0; !keep && j < wanted.size(); j++)
               keep = wanted[j].CompareNoCase(name) == 0;

            if (keep)
               result += line + "\r\n";
         }
         return result;
      }

      // The value of one live property of a resource, or false when the
      // resource has no such property.
      bool PropertyValue(Context &context, const Resource &resource, const Member *member, const PropertyRequest &request,
                         const PropertyName &property, AnsiString &innerXml)
      {
         ResourceKind kind = resource.kind;
         bool isContact = kind == KindContact;
         bool isCollection = !isContact && kind != KindContactSlot;

         if (property.ns == NsDav)
         {
            if (property.name == "resourcetype")
            {
               innerXml = "";
               if (isCollection)
                  innerXml += "<D:collection/>";
               if (kind == KindPrincipal)
                  innerXml += "<D:principal/>";
               if (kind == KindAddressBook)
                  innerXml += "<C:addressbook/>";
               return true;
            }

            if (property.name == "displayname")
            {
               switch (kind)
               {
               case KindRoot: innerXml = "hMailServer"; break;
               case KindPrincipalCollection: innerXml = "Principals"; break;
               case KindPrincipal: innerXml = XmlEscape(context.addressUtf8); break;
               case KindHomeCollection: innerXml = "Address books"; break;
               case KindCalendarsCollection: innerXml = "Calendars"; break;
               case KindHome: innerXml = XmlEscape("Address books of " + context.addressUtf8); break;
               case KindAddressBook: innerXml = "Contacts"; break;
               case KindContact:
                  innerXml = XmlEscape(resource.contact.name.IsEmpty() ? Utf8(resource.contact.address) : Utf8(resource.contact.name));
                  break;
               default: return false;
               }
               return true;
            }

            if (property.name == "current-user-principal")
            {
               innerXml = Href(context.principalHref);
               return true;
            }

            if (property.name == "principal-collection-set")
            {
               innerXml = Href(AnsiString(CardDavServer::ContextPath) + "principals/");
               return true;
            }

            if (property.name == "principal-URL" && kind == KindPrincipal)
            {
               innerXml = Href(context.principalHref);
               return true;
            }

            if (property.name == "owner" && (kind == KindHome || kind == KindAddressBook || isContact))
            {
               innerXml = Href(context.principalHref);
               return true;
            }

            if (property.name == "current-user-privilege-set" && (kind == KindPrincipal || kind == KindHome || kind == KindAddressBook || isContact))
            {
               innerXml = "<D:privilege><D:read/></D:privilege><D:privilege><D:write/></D:privilege>"
                          "<D:privilege><D:write-content/></D:privilege><D:privilege><D:bind/></D:privilege>"
                          "<D:privilege><D:unbind/></D:privilege><D:privilege><D:read-current-user-privilege-set/></D:privilege>";
               return true;
            }

            if (property.name == "supported-report-set" && kind == KindAddressBook)
            {
               innerXml = "<D:supported-report><D:report><C:addressbook-multiget/></D:report></D:supported-report>"
                          "<D:supported-report><D:report><C:addressbook-query/></D:report></D:supported-report>"
                          "<D:supported-report><D:report><D:sync-collection/></D:report></D:supported-report>";
               return true;
            }

            if (property.name == "sync-token" && kind == KindAddressBook)
            {
               AnsiString tag;
               if (!CollectionTag(context, tag))
                  return false;
               innerXml = XmlEscape(SyncTokenFor(tag));
               return true;
            }

            if (property.name == "getetag" && isContact && member)
            {
               innerXml = XmlEscape(member->etag);
               return true;
            }

            if (property.name == "getcontenttype" && isContact)
            {
               innerXml = "text/vcard; charset=utf-8";
               return true;
            }

            if (property.name == "getcontentlength" && isContact && member)
            {
               innerXml = IntText(member->card.GetLength());
               return true;
            }

            return false;
         }

         if (property.ns == NsCardDav)
         {
            if (property.name == "addressbook-home-set" && (kind == KindRoot || kind == KindPrincipal))
            {
               innerXml = Href(context.homeHref);
               return true;
            }

            if (kind == KindAddressBook)
            {
               if (property.name == "supported-address-data")
               {
                  // Both versions are accepted on PUT and a card is served back as it
                  // was sent, so both are advertised - 3.0 first, being what a client
                  // sends unless told otherwise.
                  innerXml = "<C:address-data-type content-type=\"text/vcard\" version=\"3.0\"/>"
                             "<C:address-data-type content-type=\"text/vcard\" version=\"4.0\"/>";
                  return true;
               }
               if (property.name == "addressbook-description")
               {
                  innerXml = XmlEscape("The contacts of " + context.addressUtf8);
                  return true;
               }
               if (property.name == "max-resource-size")
               {
                  innerXml = IntText(MaxCardBytes);
                  return true;
               }
               if (property.name == "supported-collation-set")
               {
                  innerXml = "<C:supported-collation>i;ascii-casemap</C:supported-collation>"
                             "<C:supported-collation>i;octet</C:supported-collation>"
                             "<C:supported-collation>i;unicode-casemap</C:supported-collation>";
                  return true;
               }
            }

            if (property.name == "address-data" && isContact && member)
            {
               innerXml = XmlEscape(ReduceCard(member->card, request.addressDataProperties));
               return true;
            }

            return false;
         }

         if (property.ns == NsCalDav)
         {
            // The principal is one for both protocols: a calendar client
            // that walks from /dav/ finds its calendar home here, served by
            // CalDavServer.
            if (property.name == "calendar-home-set" && (kind == KindRoot || kind == KindPrincipal))
            {
               innerXml = Href(CalDavServer::HomeHref(context.addressUtf8));
               return true;
            }
            if (property.name == "calendar-user-address-set" && kind == KindPrincipal)
            {
               innerXml = Href("mailto:" + context.addressUtf8);
               return true;
            }
            return false;
         }

         if (property.ns == NsCalendarServer)
         {
            if (property.name == "getctag" && kind == KindAddressBook)
            {
               AnsiString tag;
               if (!CollectionTag(context, tag))
                  return false;
               innerXml = XmlEscape(tag);
               return true;
            }
         }

         return false;
      }

      // Every property a resource kind has, for allprop and propname.
      void KnownProperties(ResourceKind kind, bool includeAddressData, std::vector<PropertyName> &names)
      {
         names.push_back(PropertyName(NsDav, "resourcetype"));
         names.push_back(PropertyName(NsDav, "displayname"));
         names.push_back(PropertyName(NsDav, "current-user-principal"));
         names.push_back(PropertyName(NsDav, "principal-collection-set"));

         switch (kind)
         {
         case KindRoot:
            names.push_back(PropertyName(NsCardDav, "addressbook-home-set"));
            names.push_back(PropertyName(NsCalDav, "calendar-home-set"));
            break;
         case KindPrincipal:
            names.push_back(PropertyName(NsDav, "principal-URL"));
            names.push_back(PropertyName(NsDav, "current-user-privilege-set"));
            names.push_back(PropertyName(NsCardDav, "addressbook-home-set"));
            names.push_back(PropertyName(NsCalDav, "calendar-home-set"));
            names.push_back(PropertyName(NsCalDav, "calendar-user-address-set"));
            break;
         case KindHome:
            names.push_back(PropertyName(NsDav, "owner"));
            names.push_back(PropertyName(NsDav, "current-user-privilege-set"));
            break;
         case KindAddressBook:
            names.push_back(PropertyName(NsDav, "owner"));
            names.push_back(PropertyName(NsDav, "current-user-privilege-set"));
            names.push_back(PropertyName(NsDav, "supported-report-set"));
            names.push_back(PropertyName(NsDav, "sync-token"));
            names.push_back(PropertyName(NsCardDav, "addressbook-description"));
            names.push_back(PropertyName(NsCardDav, "supported-address-data"));
            names.push_back(PropertyName(NsCardDav, "max-resource-size"));
            names.push_back(PropertyName(NsCardDav, "supported-collation-set"));
            names.push_back(PropertyName(NsCalendarServer, "getctag"));
            break;
         case KindContact:
            names.push_back(PropertyName(NsDav, "getetag"));
            names.push_back(PropertyName(NsDav, "getcontenttype"));
            names.push_back(PropertyName(NsDav, "getcontentlength"));
            names.push_back(PropertyName(NsDav, "owner"));
            names.push_back(PropertyName(NsDav, "current-user-privilege-set"));
            if (includeAddressData)
               names.push_back(PropertyName(NsCardDav, "address-data"));
            break;
         default:
            break;
         }
      }

      // One D:response for a resource: the properties found in a 200
      // propstat, the ones asked for and absent in a 404 propstat.
      AnsiString ResponseFor(Context &context, const Resource &resource, const Member *member, const PropertyRequest &request)
      {
         std::vector<PropertyName> names;
         if (request.mode == ModeProp)
            names = request.names;
         else
         {
            KnownProperties(resource.kind, request.mode == ModePropName, names);

            // allprop with D:include: the named ones as well, once each.
            for (size_t i = 0; i < request.names.size(); i++)
            {
               bool known = false;
               for (size_t j = 0; !known && j < names.size(); j++)
                  known = names[j].ns == request.names[i].ns && names[j].name == request.names[i].name;
               if (!known)
                  names.push_back(request.names[i]);
            }
         }

         AnsiString found;
         AnsiString missing;
         for (size_t i = 0; i < names.size(); i++)
         {
            if (request.mode == ModePropName)
            {
               found += PropertyElement(names[i], "");
               continue;
            }

            AnsiString innerXml;
            if (PropertyValue(context, resource, member, request, names[i], innerXml))
               found += PropertyElement(names[i], innerXml);
            else
               missing += PropertyElement(names[i], "");
         }

         AnsiString xml = " <D:response>\r\n  " + Href(resource.href) + "\r\n";
         if (!found.IsEmpty() || missing.IsEmpty())
            xml += "  <D:propstat><D:prop>" + found + "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>\r\n";
         if (!missing.IsEmpty())
            xml += "  <D:propstat><D:prop>" + missing + "</D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat>\r\n";
         xml += " </D:response>\r\n";
         return xml;
      }

      AnsiString ResponseForMember(Context &context, const Member &member, const PropertyRequest &request)
      {
         Resource resource;
         resource.kind = KindContact;
         resource.contact = member.record;
         resource.href = member.href;
         return ResponseFor(context, resource, &member, request);
      }

      // The D:prop element of a request, into a PropertyRequest.
      void ReadPropertyRequest(const XmlElement *prop, PropertyRequest &request)
      {
         request.mode = ModeProp;
         request.names.clear();
         if (!prop)
            return;

         for (size_t i = 0; i < prop->children.size(); i++)
         {
            const XmlElement &child = prop->children[i];
            PropertyName name;
            name.ns = child.ns;
            name.name = child.name;
            request.names.push_back(name);

            if (child.Is(NsCardDav, "address-data"))
            {
               for (size_t j = 0; j < child.children.size(); j++)
               {
                  if (child.children[j].Is(NsCardDav, "prop"))
                  {
                     AnsiString wanted = child.children[j].Attribute("name");
                     if (!wanted.IsEmpty())
                        request.addressDataProperties.push_back(wanted);
                  }
               }
            }
         }
      }

      //------------------------------------------------------------------------
      // The methods
      //------------------------------------------------------------------------

      bool ReadBody(const HttpRequest &request, XmlElement &root, AnsiString &problem)
      {
         XmlReader reader(request.body);
         return reader.Read(root, problem);
      }

      int DepthOf(const HttpRequest &request)
      {
         AnsiString depth = Lower(Trimmed(request.Header("depth")));
         if (depth == "0")
            return 0;
         // Absent, 1 and infinity: one level. See the file comment.
         return 1;
      }

      HttpResponse HandleOptions(const Resource &resource)
      {
         bool contact = resource.kind == KindContact || resource.kind == KindContactSlot;
         return Respond(200, "text/plain", "", contact ? AllowContact : AllowCollection);
      }

      HttpResponse HandlePropfind(Context &context, const Resource &resource)
      {
         if (resource.kind == KindContactSlot)
            return NotFound();

         PropertyRequest request;
         if (!Trimmed(context.request.body).IsEmpty())
         {
            XmlElement root;
            AnsiString problem;
            if (!ReadBody(context.request, root, problem))
               return Text(400, "the PROPFIND body is not well-formed XML: " + problem);
            if (!root.Is(NsDav, "propfind"))
               return Text(400, "the PROPFIND body must be a DAV:propfind element");

            if (root.Child(NsDav, "propname"))
               request.mode = ModePropName;
            else if (const XmlElement *prop = root.Child(NsDav, "prop"))
               ReadPropertyRequest(prop, request);
            else
            {
               request.mode = ModeAllProp;
               // D:include names properties to add to allprop; they are
               // answered by a second pass over the named ones.
               if (const XmlElement *include = root.Child(NsDav, "include"))
               {
                  PropertyRequest included;
                  ReadPropertyRequest(include, included);
                  request.names = included.names;
               }
            }
         }

         int depth = DepthOf(context.request);

         AnsiString xml = MultistatusOpen();

         // The resource itself, and for allprop with D:include the extra ones.
         const Member *self = nullptr;
         Member selfMember;
         if (resource.kind == KindContact)
         {
            FillMember(context, resource.contact, selfMember);
            self = &selfMember;
         }
         xml += ResponseFor(context, resource, self, request);

         if (depth == 1)
         {
            switch (resource.kind)
            {
            case KindRoot:
            {
               Resource principals;
               principals.kind = KindPrincipalCollection;
               principals.href = AnsiString(CardDavServer::ContextPath) + "principals/";
               xml += ResponseFor(context, principals, nullptr, request);

               Resource homes;
               homes.kind = KindHomeCollection;
               homes.href = AnsiString(CardDavServer::ContextPath) + "addressbooks/";
               xml += ResponseFor(context, homes, nullptr, request);

               Resource calendars;
               calendars.kind = KindCalendarsCollection;
               calendars.href = CalDavServer::CalendarsPath;
               xml += ResponseFor(context, calendars, nullptr, request);
               break;
            }
            case KindPrincipalCollection:
            {
               Resource principal;
               principal.kind = KindPrincipal;
               principal.href = context.principalHref;
               xml += ResponseFor(context, principal, nullptr, request);
               break;
            }
            case KindHomeCollection:
            {
               Resource home;
               home.kind = KindHome;
               home.href = context.homeHref;
               xml += ResponseFor(context, home, nullptr, request);
               break;
            }
            case KindHome:
            {
               Resource book;
               book.kind = KindAddressBook;
               book.href = context.bookHref;
               xml += ResponseFor(context, book, nullptr, request);
               break;
            }
            case KindAddressBook:
            {
               if (!EnsureListed(context))
                  return Text(500, "the contacts could not be read");
               for (size_t i = 0; i < context.members.size(); i++)
                  xml += ResponseForMember(context, context.members[i], request);
               break;
            }
            default:
               break;
            }
         }

         xml += "</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      // Nothing here is writable by PROPPATCH: the one address book has the
      // name it has. Each property asked for is answered 403 in the 207, which
      // is how RFC 4918 section 9.2 says to refuse some and not all - and a
      // client that tries to name its address book learns so per property
      // rather than from a bare failure.
      HttpResponse HandleProppatch(Context &context, const Resource &resource)
      {
         if (resource.kind == KindContactSlot)
            return NotFound();

         XmlElement root;
         AnsiString problem;
         if (!ReadBody(context.request, root, problem))
            return Text(400, "the PROPPATCH body is not well-formed XML: " + problem);
         if (!root.Is(NsDav, "propertyupdate"))
            return Text(400, "the PROPPATCH body must be a DAV:propertyupdate element");

         AnsiString refused;
         for (size_t i = 0; i < root.children.size(); i++)
         {
            const XmlElement &action = root.children[i];
            if (!action.Is(NsDav, "set") && !action.Is(NsDav, "remove"))
               continue;
            const XmlElement *prop = action.Child(NsDav, "prop");
            if (!prop)
               continue;
            for (size_t j = 0; j < prop->children.size(); j++)
            {
               PropertyName name;
               name.ns = prop->children[j].ns;
               name.name = prop->children[j].name;
               refused += PropertyElement(name, "");
            }
         }

         AnsiString xml = MultistatusOpen();
         xml += " <D:response>\r\n  " + Href(resource.href) + "\r\n";
         xml += "  <D:propstat><D:prop>" + refused + "</D:prop><D:status>HTTP/1.1 403 Forbidden</D:status>"
                "<D:responsedescription>The properties of this address book are fixed; none can be set or removed.</D:responsedescription></D:propstat>\r\n";
         xml += " </D:response>\r\n</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      HttpResponse HandleGet(Context &context, const Resource &resource, bool head)
      {
         if (resource.kind == KindRoot)
         {
            AnsiString text = "hMailServer CardDAV and CalDAV. Your principal is " + context.principalHref +
               ", your address book is " + context.bookHref + " and your calendar is " + CalDavServer::HomeHref(context.addressUtf8) + "calendar/.";
            return Respond(200, "text/plain; charset=utf-8", head ? AnsiString("") : text + "\r\n");
         }

         if (resource.kind == KindContactSlot)
            return NotFound();

         if (resource.kind != KindContact)
            return MethodNotAllowed(resource);

         Member member;
         FillMember(context, resource.contact, member);

         AnsiString headers = "ETag: " + member.etag + "\r\n";

         AnsiString ifNoneMatch = Trimmed(context.request.Header("if-none-match"));
         if (!ifNoneMatch.IsEmpty() && ifNoneMatch != "*")
         {
            std::vector<AnsiString> tags = StringParser::SplitString(ifNoneMatch, ",");
            for (size_t i = 0; i < tags.size(); i++)
            {
               AnsiString tag = Trimmed(tags[i]);
               if (tag.StartsWith("W/"))
                  tag = tag.Mid(2);
               if (tag == member.etag)
                  return Respond(304, "text/vcard; charset=utf-8", "", headers);
            }
         }

         return Respond(200, "text/vcard; charset=utf-8", head ? AnsiString("") : member.card, headers);
      }

      bool IsVCardContentType(const AnsiString &contentType)
      {
         AnsiString type = Lower(Trimmed(contentType));
         int semicolon = type.Find(";");
         if (semicolon >= 0)
            type = Trimmed(type.Mid(0, semicolon));
         return type.IsEmpty() || type == "text/vcard" || type == "text/x-vcard" || type == "text/directory";
      }

      HttpResponse HandlePut(Context &context, const Resource &resource)
      {
         if (resource.kind != KindContact && resource.kind != KindContactSlot)
            return MethodNotAllowed(resource);

         const HttpRequest &request = context.request;

         if (!IsVCardContentType(request.Header("content-type")))
            return DavError(415, "<C:supported-address-data/>", "The body must be a vCard: text/vcard.");

         if (request.body.GetLength() > MaxCardBytes)
            return DavError(403, "<C:max-resource-size/>", "The card is larger than " + IntText(MaxCardBytes) + " bytes.");

         std::vector<VCardProperty> properties;
         AnsiString problem;
         if (!VCard::Parse(request.body, properties, problem))
            return DavError(403, "<C:valid-address-data/>", "The body is not one vCard: " + problem + ".");

         AnsiString legacy;
         if (VCard::UsesLegacyEncoding(properties, legacy))
            return DavError(403, "<C:valid-address-data/>",
               "The card uses a vCard 2.1 encoding this server does not decode (" + legacy +
               "); send vCard 3.0 or 4.0, UTF-8 throughout.");

         AnsiString nameUtf8;
         AnsiString addressUtf8;
         if (!VCard::ExtractNameAndAddress(properties, nameUtf8, addressUtf8))
            return DavError(403, "<C:valid-address-data/>",
               "This address book keeps a name and one e-mail address per contact, and the card has no EMAIL; "
               "there is nothing to store it as. Add an e-mail address to the contact.");

         String name = Wide(nameUtf8);
         name.TrimLeft();
         name.TrimRight();
         if (name.GetLength() > ContactStore::MaximumNameLength)
            name = name.Mid(0, ContactStore::MaximumNameLength);

         String address = Wide(addressUtf8);
         if (!ContactStore::IsValidAddress(address))
            return DavError(403, "<C:valid-address-data/>", "The EMAIL is not one e-mail address: " + addressUtf8);

         __int64 accountId = context.account->GetID();

         // The card's own UID is kept; a card without one gets a stable one -
         // for an existing contact the one it had, for a new one derived from
         // the name it was created under - so the same card always carries the
         // same UID and a client never sees a contact change identity.
         AnsiString uid = VCard::UidOf(properties);
         if (uid.IsEmpty())
         {
            uid = resource.kind == KindContact
               ? (resource.contact.uid.IsEmpty() ? StableUid_(context.addressUtf8, resource.contact.id) : Utf8(resource.contact.uid))
               : StableUidFromSeed_("hMailServer.carddav.resource:" + context.addressUtf8 + ":" + resource.slotName);
         }

         // The card is kept as sent: what a GET returns is what the client
         // wrote, so the ETag answered here is the ETag of the stored
         // representation, as RFC 9110 section 9.3.4 requires of a PUT.
         const AnsiString &card = request.body;

         if (resource.kind == KindContact)
         {
            Member current;
            FillMember(context, resource.contact, current);
            if (!PreconditionsHold(request, current.etag))
               return Text(412, "precondition failed: the contact has changed since it was read, or If-None-Match: * named an existing resource",
                  "ETag: " + current.etag + "\r\n");

            // The address is the row's identity in this store: moving this
            // contact onto another contact's address would be two rows for
            // one address, which the table refuses.
            __int64 other = 0;
            if (ContactStore::FindByAddress(accountId, address, other) && other != resource.contact.id)
            {
               ContactRecord otherRecord;
               AnsiString otherHref = ContactStore::Get(accountId, other, otherRecord) ? ContactHref(context, otherRecord) : AnsiString("");
               return Text(409, "another contact of this address book already has the address " + addressUtf8 +
                  " (" + otherHref + "); a contact is one address here");
            }

            if (!ContactStore::UpdateCard(accountId, resource.contact.id, name, address, Wide(uid), Wide(card)))
               return Text(500, "the contact could not be saved");

            ContactRecord updated = resource.contact;
            updated.name = name;
            updated.address = address;
            updated.uid = Wide(uid);
            updated.vcard = Wide(card);
            Member written;
            FillMember(context, updated, written);
            LOG_DEBUG("CardDAV: " + String(context.addressUtf8) + " updated contact " + StringParser::IntToString(updated.id) + ".");
            return Respond(204, "text/plain", "", "ETag: " + written.etag + "\r\n");
         }

         // A new resource, under the client's name. Nothing is there, so an
         // If-Match cannot hold; If-None-Match: * does.
         if (!PreconditionsHold(request, ""))
            return Text(412, "precondition failed: nothing exists at this URL for If-Match to match");

         // A contact is one address here. A card for an address the book
         // already holds - a contact the webmail collected, or one a client
         // created before under another name - is not a second contact and is
         // not this one either: the client asked to create, and rewriting the
         // other contact under its name would leave the client with two copies
         // of one row. 409 names the contact that has the address.
         __int64 existing = 0;
         ContactRecord record;
         if (ContactStore::FindByAddress(accountId, address, existing))
         {
            ContactRecord existingRecord;
            AnsiString existingHref = ContactStore::Get(accountId, existing, existingRecord) ? ContactHref(context, existingRecord) : AnsiString("");
            return Text(409, "this address book already has a contact with the address " + addressUtf8 +
               " (" + existingHref + "); a contact is one address here - update that one, or delete it first");
         }

         if (!ContactStore::InsertCard(accountId, name, address, Wide(resource.slotName), Wide(uid), Wide(card), record))
         {
            // Two clients creating the same address at once: the second insert
            // is refused by the table's unique index, and the answer is the same
            // 409 as if the first had come a moment earlier.
            if (ContactStore::FindByAddress(accountId, address, existing))
            {
               ContactRecord existingRecord;
               AnsiString existingHref = ContactStore::Get(accountId, existing, existingRecord) ? ContactHref(context, existingRecord) : AnsiString("");
               return Text(409, "this address book already has a contact with the address " + addressUtf8 + " (" + existingHref + ")");
            }
            return Text(500, "the contact could not be saved");
         }

         Member created;
         FillMember(context, record, created);
         LOG_DEBUG("CardDAV: " + String(context.addressUtf8) + " created contact " + StringParser::IntToString(record.id) + " at " + String(created.href) + ".");

         // The contact lives where the client put it: the Location is the
         // request's own URL, canonically encoded.
         return Respond(201, "text/plain", "", "ETag: " + created.etag + "\r\nLocation: " + created.href + "\r\n");
      }

      HttpResponse HandleDelete(Context &context, const Resource &resource)
      {
         if (resource.kind == KindContactSlot)
            return NotFound();
         if (resource.kind != KindContact)
            return Text(403, "the address book and the collections above it cannot be deleted");

         Member current;
         FillMember(context, resource.contact, current);
         if (!PreconditionsHold(context.request, current.etag))
            return Text(412, "precondition failed: the contact has changed since it was read", "ETag: " + current.etag + "\r\n");

         if (!ContactStore::Delete(context.account->GetID(), resource.contact.id))
            return NotFound();

         LOG_DEBUG("CardDAV: " + String(context.addressUtf8) + " deleted contact " + StringParser::IntToString(resource.contact.id) + ".");
         return Respond(204, "text/plain", "");
      }

      //------------------------------------------------------------------------
      // The reports
      //------------------------------------------------------------------------

      HttpResponse HandleMultiget(Context &context, const XmlElement &report)
      {
         PropertyRequest request;
         if (const XmlElement *prop = report.Child(NsDav, "prop"))
            ReadPropertyRequest(prop, request);
         else
         {
            request.mode = ModeProp;
            request.names.push_back(PropertyName(NsDav, "getetag"));
            request.names.push_back(PropertyName(NsCardDav, "address-data"));
         }

         if (!EnsureListed(context))
            return Text(500, "the contacts could not be read");

         AnsiString xml = MultistatusOpen();
         size_t hrefs = 0;
         // An href asked for twice is answered once: five thousand copies of one
         // href, each answered with a megabyte card escaped for XML, was a
         // twenty-gigabyte response (the review of 14 September 2026). And the
         // whole multistatus is bounded, since even distinct cards add up.
         std::set<AnsiString> answered;
         const size_t MaxMultistatusBytes = 64 * 1024 * 1024;
         for (size_t i = 0; i < report.children.size(); i++)
         {
            if (!report.children[i].Is(NsDav, "href"))
               continue;
            if (++hrefs > MaxMultigetHrefs)
               return Text(400, "too many hrefs in one multiget");
            if (xml.GetLength() > MaxMultistatusBytes)
               return Text(507, "the response would be too large; ask for fewer cards in one multiget");

            // An href may be absolute; only its path is compared, decoded, so
            // that a client's own encoding of the address still matches.
            AnsiString href = Trimmed(report.children[i].text);
            int scheme = href.Find("://");
            if (scheme >= 0)
            {
               int slash = href.Find("/", scheme + 3);
               href = slash >= 0 ? href.Mid(slash) : AnsiString("/");
            }

            // Matched against the members already in memory, decoded on both
            // sides so a client's own encoding of the address still matches;
            // no lookup in the store per href.
            AnsiString wanted = PercentDecode(href);
            if (!answered.insert(wanted).second)
               continue;
            const Member *found = nullptr;
            for (size_t j = 0; !found && j < context.members.size(); j++)
            {
               if (PercentDecode(context.members[j].href) == wanted)
                  found = &context.members[j];
            }

            if (found)
               xml += ResponseForMember(context, *found, request);
            else
               xml += " <D:response>\r\n  " + Href(href) + "\r\n  <D:status>HTTP/1.1 404 Not Found</D:status>\r\n </D:response>\r\n";
         }

         xml += "</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      struct TextMatch
      {
         TextMatch() : negate(false) { }

         AnsiString text;        // UTF-8
         AnsiString collation;   // i;unicode-casemap by default
         AnsiString matchType;   // contains by default
         bool negate;
      };

      struct PropFilter
      {
         PropFilter() : isNotDefined(false), allOf(false) { }

         AnsiString name;        // upper-cased vCard property name
         bool isNotDefined;
         bool allOf;             // test="allof"; anyof by default
         std::vector<TextMatch> textMatches;
      };

      struct Filter
      {
         Filter() : allOf(false) { }

         bool allOf;
         std::vector<PropFilter> propFilters;
      };

      void ReadFilter(const XmlElement &filter, Filter &result)
      {
         result.allOf = Lower(filter.Attribute("test")) == "allof";

         for (size_t i = 0; i < filter.children.size(); i++)
         {
            const XmlElement &element = filter.children[i];
            if (!element.Is(NsCardDav, "prop-filter"))
               continue;

            PropFilter propFilter;
            propFilter.name = element.Attribute("name");
            propFilter.name.ToUpper();
            propFilter.allOf = Lower(element.Attribute("test")) == "allof";

            for (size_t j = 0; j < element.children.size(); j++)
            {
               const XmlElement &condition = element.children[j];
               if (condition.Is(NsCardDav, "is-not-defined"))
                  propFilter.isNotDefined = true;
               else if (condition.Is(NsCardDav, "text-match"))
               {
                  TextMatch match;
                  match.text = condition.text;
                  match.collation = Lower(condition.Attribute("collation"));
                  match.matchType = Lower(condition.Attribute("match-type"));
                  match.negate = Lower(condition.Attribute("negate-condition")) == "yes";
                  propFilter.textMatches.push_back(match);
               }
               // param-filter is not evaluated: the cards here carry no
               // parameter a client filters on, and a filter that cannot be
               // applied does not narrow the answer.
            }

            result.propFilters.push_back(propFilter);
         }
      }

      // The values a contact has for a vCard property, as the card would
      // carry them.
      void PropertyValues(const Member &member, const AnsiString &name, std::vector<AnsiString> &values)
      {
         AnsiString nameUtf8 = Utf8(member.record.name);
         AnsiString addressUtf8 = Utf8(member.record.address);

         if (name == "FN")
            values.push_back(nameUtf8.IsEmpty() ? addressUtf8 : nameUtf8);
         else if (name == "N")
         {
            AnsiString family;
            AnsiString given;
            VCard::SplitName(nameUtf8, family, given);
            if (!family.IsEmpty())
               values.push_back(family);
            if (!given.IsEmpty())
               values.push_back(given);
            if (values.empty())
               values.push_back("");
         }
         else if (name == "EMAIL")
            values.push_back(addressUtf8);
         else if (name == "UID")
            values.push_back(member.uid);
      }

      bool TextMatches(const TextMatch &match, const AnsiString &value)
      {
         bool caseSensitive = match.collation == "i;octet";
         AnsiString haystack = caseSensitive ? value : Lower(value);
         AnsiString needle = caseSensitive ? match.text : Lower(match.text);

         bool result;
         if (match.matchType == "equals")
            result = haystack == needle;
         else if (match.matchType == "starts-with")
            result = haystack.StartsWith(needle);
         else if (match.matchType == "ends-with")
            result = haystack.EndsWith(needle);
         else
            result = haystack.Find(needle) >= 0;

         return match.negate ? !result : result;
      }

      bool PropFilterMatches(const PropFilter &filter, const Member &member)
      {
         std::vector<AnsiString> values;
         PropertyValues(member, filter.name, values);

         if (filter.isNotDefined)
            return values.empty();

         if (filter.textMatches.empty())
            return !values.empty();

         if (values.empty())
            return false;

         bool anyMatched = false;
         bool allMatched = true;
         for (size_t i = 0; i < filter.textMatches.size(); i++)
         {
            bool matched = false;
            for (size_t j = 0; !matched && j < values.size(); j++)
               matched = TextMatches(filter.textMatches[i], values[j]);
            anyMatched = anyMatched || matched;
            allMatched = allMatched && matched;
         }

         return filter.allOf ? allMatched : anyMatched;
      }

      bool FilterMatches(const Filter &filter, const Member &member)
      {
         if (filter.propFilters.empty())
            return true;

         bool anyMatched = false;
         bool allMatched = true;
         for (size_t i = 0; i < filter.propFilters.size(); i++)
         {
            bool matched = PropFilterMatches(filter.propFilters[i], member);
            anyMatched = anyMatched || matched;
            allMatched = allMatched && matched;
         }

         return filter.allOf ? allMatched : anyMatched;
      }

      HttpResponse HandleQuery(Context &context, const XmlElement &report)
      {
         PropertyRequest request;
         if (const XmlElement *prop = report.Child(NsDav, "prop"))
            ReadPropertyRequest(prop, request);
         else
         {
            request.mode = ModeProp;
            request.names.push_back(PropertyName(NsDav, "getetag"));
            request.names.push_back(PropertyName(NsCardDav, "address-data"));
         }

         Filter filter;
         if (const XmlElement *filterElement = report.Child(NsCardDav, "filter"))
            ReadFilter(*filterElement, filter);

         int limit = -1;
         if (const XmlElement *limitElement = report.Child(NsCardDav, "limit"))
         {
            if (const XmlElement *results = limitElement->Child(NsCardDav, "nresults"))
            {
               limit = atoi(results->text.c_str());
               if (limit < 0)
                  limit = 0;
            }
         }

         if (!EnsureListed(context))
            return Text(500, "the contacts could not be read");

         AnsiString xml = MultistatusOpen();
         int answered = 0;
         for (size_t i = 0; i < context.members.size(); i++)
         {
            if (limit >= 0 && answered >= limit)
               break;
            if (!FilterMatches(filter, context.members[i]))
               continue;
            xml += ResponseForMember(context, context.members[i], request);
            answered++;
         }

         xml += "</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      HttpResponse HandleSyncCollection(Context &context, const XmlElement &report)
      {
         PropertyRequest request;
         if (const XmlElement *prop = report.Child(NsDav, "prop"))
            ReadPropertyRequest(prop, request);
         else
         {
            request.mode = ModeProp;
            request.names.push_back(PropertyName(NsDav, "getetag"));
         }

         AnsiString presented;
         if (const XmlElement *token = report.Child(NsDav, "sync-token"))
            presented = Trimmed(token->text);

         AnsiString tag;
         if (!CollectionTag(context, tag))
            return Text(500, "the contacts could not be read");
         AnsiString current = SyncTokenFor(tag);

         AnsiString xml = MultistatusOpen();

         if (presented.IsEmpty())
         {
            // The initial sync: everything.
            for (size_t i = 0; i < context.members.size(); i++)
               xml += ResponseForMember(context, context.members[i], request);
         }
         else if (presented != current)
         {
            // The store keeps no change log, so a token that is not the
            // present state cannot be turned into the changes since it. The
            // client is told so and lists again (RFC 6578 section 3.2).
            return DavError(403, "<D:valid-sync-token/>",
               "The sync token is not the address book's current state, and this server keeps no change log to answer "
               "what changed since; ask again without a token for the whole address book.");
         }

         xml += " <D:sync-token>" + XmlEscape(current) + "</D:sync-token>\r\n";
         xml += "</D:multistatus>\r\n";
         return Xml(207, xml);
      }

      HttpResponse HandleReport(Context &context, const Resource &resource)
      {
         if (resource.kind == KindContactSlot)
            return NotFound();

         XmlElement root;
         AnsiString problem;
         if (!ReadBody(context.request, root, problem))
            return Text(400, "the REPORT body is not well-formed XML: " + problem);

         bool multiget = root.Is(NsCardDav, "addressbook-multiget");
         bool query = root.Is(NsCardDav, "addressbook-query");
         bool sync = root.Is(NsDav, "sync-collection");

         if (!multiget && !query && !sync)
            return DavError(403, "<D:supported-report/>", "The reports here are addressbook-multiget, addressbook-query and sync-collection.");

         // multiget is allowed at the address book and at a contact; the
         // other two are questions about the collection.
         bool onBook = resource.kind == KindAddressBook;
         bool onContact = resource.kind == KindContact;
         if (!onBook && !(multiget && onContact))
            return DavError(403, "<D:supported-report/>", "This report applies to the address book, " + context.bookHref + ".");

         if (multiget)
            return HandleMultiget(context, root);
         if (query)
            return HandleQuery(context, root);
         return HandleSyncCollection(context, root);
      }
   }

   bool
   CardDavServer::IsDavTarget(const AnsiString &target)
   {
      AnsiString path = target;
      int query = path.Find("?");
      if (query >= 0)
         path = path.Mid(0, query);

      return path == "/dav" || path.StartsWith(ContextPath);
   }

   bool
   CardDavServer::IsLargeRequest(const AnsiString &method, const AnsiString &target)
   {
      if (!IsDavTarget(target))
         return false;

      return method == "PUT" || method == "REPORT";
   }

   HttpResponse
   CardDavServer::Handle(const HttpRequest &request, bool over_https)
   {
      // Basic puts the account's password on the wire, so this surface is
      // HTTPS only - the same rule as the Apple configuration profile on this
      // listener, for the same reason. Refused before the credential is
      // looked at, so a password sent in clear is at least not also checked.
      if (!over_https)
      {
         return Text(403,
            "CardDAV is served over HTTPS only: HTTP Basic authentication would send the account's password in clear. "
            "Use the https:// address of this server (WebServicesHttpsPort), or put a TLS-terminating proxy in front "
            "of this listener that sets X-Forwarded-Proto: https.");
      }

      const AnsiString &method = request.method;

      Context context(request);
      bool disconnect = false;
      if (!Authenticate(request, "CardDAV", context.account, disconnect))
      {
         HttpResponse refusal = Unauthorized();
         // The connection closes with the refusal, whatever the lockout said:
         // IMAP delays a wrong password before answering (the tarpit), and this
         // listener has no delay, so a guess costs a new connection instead of
         // running at wire speed down one kept-alive socket.
         refusal.close = true;
         return refusal;
      }

      context.addressWide = context.account->GetAddress();
      context.addressWide.ToLower();
      context.addressUtf8 = Utf8(context.addressWide);
      AnsiString segment = PercentEncodeSegment(context.addressUtf8);
      context.principalHref = AnsiString(ContextPath) + "principals/" + segment + "/";
      context.homeHref = AnsiString(ContextPath) + "addressbooks/" + segment + "/";
      context.bookHref = context.homeHref + "contacts/";

      Resource resource;
      if (!Resolve(context, request.target, resource))
         return NotFound();

      HttpResponse response;
      try
      {
         if (method == "OPTIONS")
            response = HandleOptions(resource);
         else if (method == "PROPFIND")
            response = HandlePropfind(context, resource);
         else if (method == "PROPPATCH")
            response = HandleProppatch(context, resource);
         else if (method == "REPORT")
            response = HandleReport(context, resource);
         else if (method == "GET")
            response = HandleGet(context, resource, false);
         else if (method == "HEAD")
            response = HandleGet(context, resource, true);
         else if (method == "PUT")
            response = HandlePut(context, resource);
         else if (method == "DELETE")
            response = HandleDelete(context, resource);
         else if (method == "MKCOL" || method == "MKCALENDAR")
            response = Text(403, "this server keeps one address book per account, Contacts, and one calendar, Calendar; no collection can be made");
         else
            response = MethodNotAllowed(resource);
      }
      catch (...)
      {
         response = Text(500, "internal error");
      }

      response.close = response.close || disconnect;

      LOG_DEBUG("CardDAV: " + String(context.addressUtf8) + " " + String(method) + " " + String(request.target) + " -> " +
         StringParser::IntToString(response.status) + ".");

      return response;
   }
}
