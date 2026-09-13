// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Xml;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   /// <summary>
   ///    CardDAV (RFC 6352) on the web services listener: the account's
   ///    address book under /dav/, the same hm_contacts rows the webmail's
   ///    /api/v1/me/contacts routes read and write. Both listeners are started
   ///    here, because the point of every write below is that the OTHER
   ///    surface then sees it: a PUT is checked through the REST contacts route,
   ///    a REST-created contact is fetched as a vCard, and nothing is asserted
   ///    from a response alone.
   ///
   ///    The listener is plain HTTP on this bench, and CardDAV refuses Basic
   ///    over plain HTTP, so the requests say what a TLS-terminating proxy in
   ///    front of the listener would say: X-Forwarded-Proto: https. One test
   ///    leaves it out, to pin the refusal.
   ///
   ///    What the store cannot hold is pinned as refused, not silently kept:
   ///    a card without an EMAIL is 403 and nothing is written. The store has
   ///    no column for a client's resource name or UID, so a contact created
   ///    under a client's name comes back under the server's, in the Location
   ///    of the 201 - pinned too, because a client that honours that header
   ///    keeps one copy and one that does not gets the server's copy at its
   ///    next listing.
   /// </summary>
   [TestFixture]
   public class CardDav : TestFixtureBase
   {
      private const int WebServicesPort = 9105;
      // The port the listener answers on: this one on the Windows bench, the
      // suite's own where RestListener finds one already on.
      private static int RestPort = 9535;
      private const string UserPassword = "Original-Passw0rd!";

      private const string NsDav = "DAV:";
      private const string NsCardDav = "urn:ietf:params:xml:ns:carddav";
      private const string NsCalendarServer = "http://calendarserver.org/ns/";

      private Account _account;

      private string Address
      {
         get { return "self@" + _domain.Name; }
      }

      private string Principal
      {
         get { return "/dav/principals/" + Address + "/"; }
      }

      private string Home
      {
         get { return "/dav/addressbooks/" + Address + "/"; }
      }

      private string Book
      {
         get { return Home + "contacts/"; }
      }

      [SetUp]
      public void StartListeners()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, Address, UserPassword);

         IniFileSetting.Write("CalDavRedirectUrl", "");
         IniFileSetting.Write("CardDavRedirectUrl", "");
         IniFileSetting.Write("WebServicesBindAddress", "127.0.0.1");
         IniFileSetting.Write("WebServicesHttpPort", WebServicesPort.ToString());
         IniFileSetting.Write("WebServicesHttpsPort", "0");

         RestPort = RestListener.Start(RestPort);
         IniFileSetting.Write("RestApiCertificateFile", "");
         IniFileSetting.Write("RestApiPrivateKeyFile", "");

         _application.Reinitialize();

         Response probe = Dav("OPTIONS", "/dav/", Auth(Address, UserPassword));
         Assert.AreEqual(200, probe.Status, "The CardDAV endpoint did not answer OPTIONS. Body: " + probe.Body);

         Response rest = Rest("GET", "/api/v1/me/contacts");
         Assert.AreEqual(200, rest.Status, "The REST listener did not answer. Body: " + rest.Body);
      }

      [TearDown]
      public void StopListeners()
      {
         IniFileSetting.Write("WebServicesHttpPort", "0");
         RestListener.Stop();
         _application.Reinitialize();
      }

      [Test]
      [Description("OPTIONS on the context path advertises DAV class 1, 3 and addressbook, and the methods the address book answers")]
      public void OptionsAdvertisesTheAddressbookClass()
      {
         Response options = Dav("OPTIONS", Book, Auth(Address, UserPassword));

         Assert.AreEqual(200, options.Status, "Body: " + options.Body);

         string dav = options.Header("DAV");
         Assert.IsNotNull(dav, "No DAV header. Headers: " + options.HeaderText);
         StringAssert.Contains("addressbook", dav);
         StringAssert.Contains("1", dav);

         string allow = options.Header("Allow");
         Assert.IsNotNull(allow, "No Allow header. Headers: " + options.HeaderText);
         StringAssert.Contains("PROPFIND", allow);
         StringAssert.Contains("REPORT", allow);
      }

      [Test]
      [Description("A client walks from the context path to the address book: current-user-principal, addressbook-home-set, then the collection with its CardDAV properties")]
      public void DiscoveryWalksFromTheContextPathToTheAddressBook()
      {
         // Step 1, at the URL the well-known redirect gave: who am I.
         Response root = Propfind("/dav/", "0", "<D:current-user-principal/>");
         Assert.AreEqual(207, root.Status, "Body: " + root.Body);
         Assert.AreEqual(Principal, PropText(root, "/dav/", "D:current-user-principal/D:href"), "Body: " + root.Body);

         // Step 2, at the principal: where are my address books.
         Response principal = Propfind(Principal, "0", "<C:addressbook-home-set/><D:resourcetype/>");
         Assert.AreEqual(207, principal.Status, "Body: " + principal.Body);
         Assert.AreEqual(Home, PropText(principal, Principal, "C:addressbook-home-set/D:href"), "Body: " + principal.Body);
         Assert.IsNotNull(Prop(principal, Principal, "D:resourcetype/D:principal"), "The principal must say so. Body: " + principal.Body);

         // Step 3, at the home, one level down: the address book, typed as one.
         Response home = Propfind(Home, "1", "<D:resourcetype/><D:displayname/>");
         Assert.AreEqual(207, home.Status, "Body: " + home.Body);
         CollectionAssert.Contains(Hrefs(home), Book, "The home must list the address book. Body: " + home.Body);
         Assert.IsNotNull(Prop(home, Book, "D:resourcetype/C:addressbook"), "The collection must be a CARDDAV:addressbook. Body: " + home.Body);
         Assert.AreEqual("Contacts", PropText(home, Book, "D:displayname"), "Body: " + home.Body);

         // Step 4, at the address book: what it supports and its state.
         Response book = Propfind(Book, "0",
            "<C:supported-address-data/><CS:getctag/><D:sync-token/><D:supported-report-set/><D:current-user-privilege-set/>");
         Assert.AreEqual(207, book.Status, "Body: " + book.Body);

         XmlNode addressData = Prop(book, Book, "C:supported-address-data/C:address-data-type");
         Assert.IsNotNull(addressData, "Body: " + book.Body);
         Assert.AreEqual("text/vcard", addressData.Attributes["content-type"].Value);
         Assert.AreEqual("3.0", addressData.Attributes["version"].Value);

         Assert.IsFalse(string.IsNullOrEmpty(PropText(book, Book, "CS:getctag")), "No getctag. Body: " + book.Body);
         Assert.IsFalse(string.IsNullOrEmpty(PropText(book, Book, "D:sync-token")), "No sync-token. Body: " + book.Body);
         Assert.IsNotNull(Prop(book, Book, "D:supported-report-set/D:supported-report/D:report/C:addressbook-multiget"), "Body: " + book.Body);
         Assert.IsNotNull(Prop(book, Book, "D:supported-report-set/D:supported-report/D:report/C:addressbook-query"), "Body: " + book.Body);
         Assert.IsNotNull(Prop(book, Book, "D:supported-report-set/D:supported-report/D:report/D:sync-collection"), "Body: " + book.Body);
         Assert.IsNotNull(Prop(book, Book, "D:current-user-privilege-set/D:privilege/D:write"), "Body: " + book.Body);

         // A property this server does not have is answered in the 404
         // propstat, not invented.
         Response unknown = Propfind(Book, "0", "<D:quota-available-bytes/>");
         Assert.AreEqual(207, unknown.Status, "Body: " + unknown.Body);
         Assert.IsNull(Prop(unknown, Book, "D:quota-available-bytes"), "Body: " + unknown.Body);
         StringAssert.Contains("404 Not Found", unknown.Body);
      }

      [Test]
      [Description("PUT with If-None-Match: * creates a contact the REST contacts route then shows; GET under the client's own name returns the card as sent, byte for byte, with the same strong ETag, and the listing carries it")]
      public void PutCreatesAContactTheRestRouteShows()
      {
         string clientName = Book + "6f2c1e2a-3b0c-4d1e-9f7a-0123456789ab.vcf";
         string sent = Card("Alice Example", "Example;Alice;;;", "Alice@Example.com");
         Response created = Put(clientName, sent, "If-None-Match: *\r\n");

         Assert.AreEqual(201, created.Status, "Body: " + created.Body);

         string etag = created.Header("ETag");
         Assert.IsNotNull(etag, "A PUT must answer with the new ETag. Headers: " + created.HeaderText);
         Assert.IsTrue(etag.StartsWith("\"") && etag.EndsWith("\"") && etag.Length > 8, "A strong, quoted ETag: " + etag);

         // The contact lives where the client put it: the Location is the
         // request's own URL.
         string location = created.Header("Location");
         Assert.AreEqual(clientName, location, "Headers: " + created.HeaderText);

         // The other surface sees it: the webmail's contacts route.
         Response rest = Rest("GET", "/api/v1/me/contacts");
         Assert.AreEqual(200, rest.Status, "Body: " + rest.Body);
         StringAssert.Contains("\"address\":\"alice@example.com\"", rest.Body);
         StringAssert.Contains("\"name\":\"Alice Example\"", rest.Body);
         StringAssert.Contains("\"source\":\"manual\"", rest.Body);
         StringAssert.Contains("\"count\":1", rest.Body);

         // The card back, with the ETag the PUT answered.
         Response card = Dav("GET", location, Auth(Address, UserPassword));
         Assert.AreEqual(200, card.Status, "Body: " + card.Body);
         Assert.AreEqual(etag, card.Header("ETag"), "Headers: " + card.HeaderText);
         StringAssert.StartsWith("text/vcard", card.Header("Content-Type"));

         // Byte for byte what was sent: the client's PRODID, its UID, its
         // casing of the address - so the ETag the PUT answered is the ETag
         // of what a GET returns (RFC 9110 section 9.3.4).
         Assert.AreEqual(sent, card.Body);

         // A conditional GET on the ETag is 304.
         Response unchanged = Dav("GET", location, Auth(Address, UserPassword), null, "If-None-Match: " + etag + "\r\n");
         Assert.AreEqual(304, unchanged.Status, "Body: " + unchanged.Body);

         // The listing carries the member with that ETag.
         Response listing = Propfind(Book, "1", "<D:getetag/><D:getcontenttype/>");
         Assert.AreEqual(207, listing.Status, "Body: " + listing.Body);
         CollectionAssert.Contains(Hrefs(listing), location, "Body: " + listing.Body);
         Assert.AreEqual(etag, PropText(listing, location, "D:getetag"), "Body: " + listing.Body);
         StringAssert.StartsWith("text/vcard", PropText(listing, location, "D:getcontenttype"));

         // The UID is the card's own, on every read.
         Response again = Dav("GET", location, Auth(Address, UserPassword));
         Assert.AreEqual(Between(sent, "\r\nUID:", "\r\n"), Between(again.Body, "\r\nUID:", "\r\n"));
      }

      [Test]
      [Description("PUT with a stale If-Match is 412 and changes nothing; with the current ETag it updates, and the REST route shows the new name; If-None-Match: * on an existing contact is 412")]
      public void PutWithAStaleIfMatchIsRefusedAndACurrentOneUpdates()
      {
         long id = RestCreate("Bob Builder", "bob@example.com");
         string href = Book + id + ".vcf";

         Response before = Dav("GET", href, Auth(Address, UserPassword));
         Assert.AreEqual(200, before.Status, "Body: " + before.Body);
         string etag = before.Header("ETag");
         StringAssert.Contains("\r\nFN:Bob Builder\r\n", before.Body);

         Response stale = Put(href, Card("Robert Builder", "Builder;Robert;;;", "bob@example.com"), "If-Match: \"not-the-etag\"\r\n");
         Assert.AreEqual(412, stale.Status, "Body: " + stale.Body);

         Response unchanged = Rest("GET", "/api/v1/me/contacts");
         StringAssert.Contains("\"name\":\"Bob Builder\"", unchanged.Body);

         Response updated = Put(href, Card("Robert Builder", "Builder;Robert;;;", "bob@example.com"), "If-Match: " + etag + "\r\n");
         Assert.AreEqual(204, updated.Status, "Body: " + updated.Body);
         string newEtag = updated.Header("ETag");
         Assert.IsNotNull(newEtag, "Headers: " + updated.HeaderText);
         Assert.AreNotEqual(etag, newEtag, "A write that changed the card must change the ETag.");

         Response after = Rest("GET", "/api/v1/me/contacts");
         StringAssert.Contains("\"name\":\"Robert Builder\"", after.Body);
         StringAssert.Contains("\"count\":1", after.Body);

         Response read = Dav("GET", href, Auth(Address, UserPassword));
         Assert.AreEqual(newEtag, read.Header("ETag"));
         StringAssert.Contains("\r\nFN:Robert Builder\r\n", read.Body);

         Response create = Put(href, Card("Robert Builder", "Builder;Robert;;;", "bob@example.com"), "If-None-Match: *\r\n");
         Assert.AreEqual(412, create.Status, "If-None-Match: * on an existing resource must be 412. Body: " + create.Body);

         // Moving this contact onto another contact's address would be two
         // rows for one address: 409, and the other contact is named.
         long other = RestCreate("Carol", "carol@somewhere.test");
         Response collision = Put(href, Card("Robert Builder", "Builder;Robert;;;", "carol@somewhere.test"));
         Assert.AreEqual(409, collision.Status, "Body: " + collision.Body);
         StringAssert.Contains(Book + other + ".vcf", collision.Body);
      }

      [Test]
      [Description("DELETE removes the contact: GET is then 404 and the REST route no longer lists it")]
      public void DeleteRemovesTheContact()
      {
         long id = RestCreate("Carol", "carol@somewhere.test");
         string href = Book + id + ".vcf";

         Response deleted = Dav("DELETE", href, Auth(Address, UserPassword));
         Assert.AreEqual(204, deleted.Status, "Body: " + deleted.Body);

         Response gone = Dav("GET", href, Auth(Address, UserPassword));
         Assert.AreEqual(404, gone.Status, "Body: " + gone.Body);

         Response rest = Rest("GET", "/api/v1/me/contacts");
         Assert.IsFalse(rest.Body.Contains("carol@somewhere.test"), "Body: " + rest.Body);
         StringAssert.Contains("\"contacts\":[]", rest.Body);

         Response again = Dav("DELETE", href, Auth(Address, UserPassword));
         Assert.AreEqual(404, again.Status, "Body: " + again.Body);

         // The collections are not deletable.
         Response book = Dav("DELETE", Book, Auth(Address, UserPassword));
         Assert.AreEqual(403, book.Status, "Body: " + book.Body);
      }

      [Test]
      [Description("addressbook-multiget answers each href, 404 for one that is not there; addressbook-query filters on EMAIL and FN text-matches and honours a limit")]
      public void MultigetAndQueryReportsAnswerFromTheStore()
      {
         long alice = RestCreate("Alice Example", "alice@example.com");
         long bob = RestCreate("Bob Builder", "bob@example.com");
         long carol = RestCreate("Carol", "carol@somewhere.test");

         string aliceHref = Book + alice + ".vcf";
         string bogusHref = Book + "999999999.vcf";

         Response multiget = Report(Book,
            "<C:addressbook-multiget xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\">" +
            "<D:prop><D:getetag/><C:address-data/></D:prop>" +
            "<D:href>" + aliceHref + "</D:href><D:href>" + bogusHref + "</D:href>" +
            "</C:addressbook-multiget>");
         Assert.AreEqual(207, multiget.Status, "Body: " + multiget.Body);

         List<string> hrefs = Hrefs(multiget);
         CollectionAssert.Contains(hrefs, aliceHref, "Body: " + multiget.Body);
         CollectionAssert.Contains(hrefs, bogusHref, "The missing href is answered, with a status. Body: " + multiget.Body);

         string card = PropText(multiget, aliceHref, "C:address-data");
         StringAssert.Contains("EMAIL;TYPE=INTERNET,PREF:alice@example.com", card);
         StringAssert.Contains("FN:Alice Example", card);
         Assert.IsFalse(string.IsNullOrEmpty(PropText(multiget, aliceHref, "D:getetag")), "Body: " + multiget.Body);
         StringAssert.Contains("404 Not Found", ResponseStatus(multiget, bogusHref));

         // A text-match on EMAIL: the two at example.com, not Carol.
         Response byDomain = Report(Book, Query("<C:prop-filter name=\"EMAIL\"><C:text-match collation=\"i;unicode-casemap\" match-type=\"contains\">EXAMPLE.COM</C:text-match></C:prop-filter>", null));
         Assert.AreEqual(207, byDomain.Status, "Body: " + byDomain.Body);
         CollectionAssert.AreEquivalent(new[] { aliceHref, Book + bob + ".vcf" }, Hrefs(byDomain), "Body: " + byDomain.Body);

         // A text-match on FN, starts-with, case-insensitively: Alice only.
         Response byName = Report(Book, Query("<C:prop-filter name=\"FN\"><C:text-match match-type=\"starts-with\">ali</C:text-match></C:prop-filter>", null));
         Assert.AreEqual(207, byName.Status, "Body: " + byName.Body);
         CollectionAssert.AreEquivalent(new[] { aliceHref }, Hrefs(byName), "Body: " + byName.Body);

         // negate-condition: everyone but Alice.
         Response notAlice = Report(Book, Query("<C:prop-filter name=\"FN\"><C:text-match negate-condition=\"yes\">alice</C:text-match></C:prop-filter>", null));
         Assert.AreEqual(207, notAlice.Status, "Body: " + notAlice.Body);
         CollectionAssert.AreEquivalent(new[] { Book + bob + ".vcf", Book + carol + ".vcf" }, Hrefs(notAlice), "Body: " + notAlice.Body);

         // No filter, a limit of two: two of the three.
         Response limited = Report(Book, Query("", "<C:limit><C:nresults>2</C:nresults></C:limit>"));
         Assert.AreEqual(207, limited.Status, "Body: " + limited.Body);
         Assert.AreEqual(2, Hrefs(limited).Count, "Body: " + limited.Body);

         // A report this server does not have is refused as such.
         Response unsupported = Report(Book, "<C:free-busy-query xmlns:C=\"urn:ietf:params:xml:ns:caldav\"/>");
         Assert.AreEqual(403, unsupported.Status, "Body: " + unsupported.Body);
         StringAssert.Contains("supported-report", unsupported.Body);
      }

      [Test]
      [Description("sync-collection lists the whole book on an empty token, answers no changes on the current one, refuses a stale one with DAV:valid-sync-token, and getctag changes with every write")]
      public void SyncCollectionAnswersTheWholeBookOnceAndTheTokenAfter()
      {
         long alice = RestCreate("Alice Example", "alice@example.com");

         Response initial = Report(Book, Sync(""));
         Assert.AreEqual(207, initial.Status, "Body: " + initial.Body);
         CollectionAssert.AreEquivalent(new[] { Book + alice + ".vcf" }, Hrefs(initial), "Body: " + initial.Body);
         string token = SyncToken(initial);
         Assert.IsFalse(string.IsNullOrEmpty(token), "No sync-token. Body: " + initial.Body);

         Response nothingNew = Report(Book, Sync(token));
         Assert.AreEqual(207, nothingNew.Status, "Body: " + nothingNew.Body);
         Assert.AreEqual(0, Hrefs(nothingNew).Count, "The current token means no changes. Body: " + nothingNew.Body);
         Assert.AreEqual(token, SyncToken(nothingNew));

         string ctagBefore = PropText(Propfind(Book, "0", "<CS:getctag/>"), Book, "CS:getctag");

         long bob = RestCreate("Bob Builder", "bob@example.com");

         string ctagAfter = PropText(Propfind(Book, "0", "<CS:getctag/>"), Book, "CS:getctag");
         Assert.AreNotEqual(ctagBefore, ctagAfter, "A write through the other surface must change the ctag.");

         // The store keeps no change log: a token that is not the present
         // state is refused with the precondition, and the client lists again.
         Response stale = Report(Book, Sync(token));
         Assert.AreEqual(403, stale.Status, "Body: " + stale.Body);
         StringAssert.Contains("valid-sync-token", stale.Body);

         Response relisted = Report(Book, Sync(""));
         Assert.AreEqual(207, relisted.Status, "Body: " + relisted.Body);
         CollectionAssert.AreEquivalent(new[] { Book + alice + ".vcf", Book + bob + ".vcf" }, Hrefs(relisted), "Body: " + relisted.Body);
         Assert.AreNotEqual(token, SyncToken(relisted));
      }

      [Test]
      [Description("A wrong password and no credential are 401 with a Basic challenge; another account's address book is 404, not 403")]
      public void TheWrongPasswordAndAnotherAccountsBookAreRefused()
      {
         Response wrong = Propfind("/dav/", "0", "<D:current-user-principal/>", Auth(Address, "not-the-password"));
         Assert.AreEqual(401, wrong.Status, "Body: " + wrong.Body);
         StringAssert.Contains("Basic", wrong.Header("WWW-Authenticate") ?? "", "Headers: " + wrong.HeaderText);

         Response none = Dav("PROPFIND", "/dav/", null, "<D:propfind xmlns:D=\"DAV:\"><D:prop><D:current-user-principal/></D:prop></D:propfind>", "Depth: 0\r\n");
         Assert.AreEqual(401, none.Status, "Body: " + none.Body);

         string other = "other@" + _domain.Name;
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, other, UserPassword);

         RestCreate("Alice Example", "alice@example.com");

         Response theirs = Propfind(Book, "1", "<D:getetag/>", Auth(other, UserPassword));
         Assert.AreEqual(404, theirs.Status, "Another account's address book is not there for them. Body: " + theirs.Body);

         Response theirPrincipal = Propfind(Principal, "0", "<D:displayname/>", Auth(other, UserPassword));
         Assert.AreEqual(404, theirPrincipal.Status, "Body: " + theirPrincipal.Body);

         Response theirOwn = Propfind("/dav/principals/" + other + "/", "0", "<D:displayname/>", Auth(other, UserPassword));
         Assert.AreEqual(207, theirOwn.Status, "Body: " + theirOwn.Body);
         Assert.AreEqual(other, PropText(theirOwn, "/dav/principals/" + other + "/", "D:displayname"));
      }

      [Test]
      [Description("Over plain HTTP, with no proxy saying otherwise, /dav/ is refused with the reason before any credential is looked at")]
      public void PlainHttpIsRefusedWithTheReason()
      {
         Response plain = Raw(WebServicesPort, "PROPFIND", "/dav/", Auth(Address, UserPassword),
            "<D:propfind xmlns:D=\"DAV:\"><D:prop><D:current-user-principal/></D:prop></D:propfind>", "application/xml", "Depth: 0\r\n");

         Assert.AreEqual(403, plain.Status, "Body: " + plain.Body);
         StringAssert.Contains("HTTPS", plain.Body);
         StringAssert.Contains("X-Forwarded-Proto", plain.Body);
         Assert.IsFalse(plain.Body.Contains("multistatus"), "No answer at all over plain HTTP. Body: " + plain.Body);
      }

      [Test]
      [Description("A card without an EMAIL is refused with CARDDAV:valid-address-data and the reason, and nothing is stored; a card that is not a vCard likewise")]
      public void ACardWithoutAnEmailAddressIsRefusedAndNothingIsStored()
      {
         string phoneOnly = string.Join("\r\n", new[]
         {
            "BEGIN:VCARD",
            "VERSION:3.0",
            "FN:Dave Phone",
            "N:Phone;Dave;;;",
            "TEL;TYPE=CELL:+44 7700 900123",
            "UID:2c1e0a5b-1111-4222-8333-444455556666",
            "END:VCARD",
            ""
         });

         Response refused = Put(Book + "2c1e0a5b-1111-4222-8333-444455556666.vcf", phoneOnly, "If-None-Match: *\r\n");
         Assert.AreEqual(403, refused.Status, "Body: " + refused.Body);
         StringAssert.Contains("valid-address-data", refused.Body);
         StringAssert.Contains("EMAIL", refused.Body);

         Response notACard = Put(Book + "x.vcf", "this is not a card", "If-None-Match: *\r\n");
         Assert.AreEqual(403, notACard.Status, "Body: " + notACard.Body);
         StringAssert.Contains("valid-address-data", notACard.Body);

         Response wrongType = Raw(WebServicesPort, "PUT", Book + "y.vcf", Auth(Address, UserPassword),
            "{\"name\":\"Not a card\"}", "application/json", "X-Forwarded-Proto: https\r\n");
         Assert.AreEqual(415, wrongType.Status, "Body: " + wrongType.Body);

         Response rest = Rest("GET", "/api/v1/me/contacts");
         StringAssert.Contains("\"contacts\":[]", rest.Body);
      }

      [Test]
      [Description("A create for an address the book already has is 409 naming that contact, and changes nothing: the client asked to create, and a contact is one address here")]
      public void ACreateForAnAddressTheBookAlreadyHasIsRefusedNamingTheContact()
      {
         long id = RestCreate("dave", "dave@example.com");

         Response refused = Put(Book + "phone-chosen-name.vcf", Card("David Example", "Example;David;;;", "DAVE@example.com"), "If-None-Match: *\r\n");
         Assert.AreEqual(409, refused.Status, "Body: " + refused.Body);
         StringAssert.Contains(Book + id + ".vcf", refused.Body);

         // Nothing changed: the one contact keeps its name, and the client's
         // name is not a resource.
         Response rest = Rest("GET", "/api/v1/me/contacts");
         StringAssert.Contains("\"name\":\"dave\"", rest.Body);
         StringAssert.Contains("\"count\":1", rest.Body);
         Response clientName = Dav("GET", Book + "phone-chosen-name.vcf", Auth(Address, UserPassword));
         Assert.AreEqual(404, clientName.Status, "Body: " + clientName.Body);

         // The existing contact can be given the card, under its own name.
         Response updated = Put(Book + id + ".vcf", Card("David Example", "Example;David;;;", "dave@example.com"));
         Assert.AreEqual(204, updated.Status, "Body: " + updated.Body);
         StringAssert.Contains("\"name\":\"David Example\"", Rest("GET", "/api/v1/me/contacts").Body);
      }

      [Test]
      [Description("A card is kept as sent: a phone's TEL, ADR and NOTE survive the round trip under the client's name, a second PUT with the ETag updates it in place, and DELETE under that name removes it")]
      public void ACardIsKeptAsSentUnderTheClientsName()
      {
         string name = Book + "5d2a0f6e-7b1c-4e3d-8a9f-abcdef012345.vcf";
         string first = string.Join("\r\n", new[]
         {
            "BEGIN:VCARD",
            "VERSION:3.0",
            "PRODID:-//A phone//EN",
            "UID:5d2a0f6e-7b1c-4e3d-8a9f-abcdef012345",
            "FN:Frank Example",
            "N:Example;Frank;;;",
            "EMAIL;TYPE=INTERNET,PREF:frank@example.com",
            "TEL;TYPE=CELL:+44 7700 900123",
            "ADR;TYPE=HOME:;;1 Example Street;Exampletown;;EX1 2MP;GB",
            "NOTE:Met at the conference.",
            "END:VCARD",
            ""
         });

         Response created = Put(name, first, "If-None-Match: *\r\n");
         Assert.AreEqual(201, created.Status, "Body: " + created.Body);
         Assert.AreEqual(name, created.Header("Location"), "Headers: " + created.HeaderText);
         string etag = created.Header("ETag");

         Response read = Dav("GET", name, Auth(Address, UserPassword));
         Assert.AreEqual(200, read.Status, "Body: " + read.Body);
         Assert.AreEqual(first, read.Body, "The card comes back as it was sent, TEL, ADR and NOTE included.");
         Assert.AreEqual(etag, read.Header("ETag"));

         // The webmail sees the name and address the card carries.
         Response rest = Rest("GET", "/api/v1/me/contacts");
         StringAssert.Contains("\"name\":\"Frank Example\"", rest.Body);
         StringAssert.Contains("\"address\":\"frank@example.com\"", rest.Body);

         // A second PUT with the ETag replaces the card in place: the same name,
         // a new ETag, the new TEL.
         string second = first.Replace("+44 7700 900123", "+44 7700 900999");
         Response updated = Put(name, second, "If-Match: " + etag + "\r\n");
         Assert.AreEqual(204, updated.Status, "Body: " + updated.Body);
         Assert.AreNotEqual(etag, updated.Header("ETag"));
         Response reread = Dav("GET", name, Auth(Address, UserPassword));
         Assert.AreEqual(second, reread.Body);
         Assert.AreEqual(updated.Header("ETag"), reread.Header("ETag"));

         // The listing names it by the client's name, and the multiget answers it there.
         Response listing = Propfind(Book, "1", "<D:getetag/>");
         CollectionAssert.Contains(Hrefs(listing), name, "Body: " + listing.Body);
         Response multiget = Report(Book, "<C:addressbook-multiget xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\"><D:prop><D:getetag/><C:address-data/></D:prop><D:href>" + name + "</D:href></C:addressbook-multiget>");
         Assert.AreEqual(207, multiget.Status, "Body: " + multiget.Body);
         StringAssert.Contains("TEL;TYPE=CELL:+44 7700 900999", PropText(multiget, name, "C:address-data"));

         Response deleted = Dav("DELETE", name, Auth(Address, UserPassword));
         Assert.AreEqual(204, deleted.Status, "Body: " + deleted.Body);
         Assert.AreEqual(404, Dav("GET", name, Auth(Address, UserPassword)).Status);
         StringAssert.Contains("\"contacts\":[]", Rest("GET", "/api/v1/me/contacts").Body);
      }

      [Test]
      [Description("A change made in the webmail is written into the stored card: the FN and EMAIL follow, the TEL and UID stay, and the ETag changes")]
      public void AWebmailEditIsWrittenIntoTheStoredCard()
      {
         string name = Book + "grace.vcf";
         string sent = string.Join("\r\n", new[]
         {
            "BEGIN:VCARD",
            "VERSION:3.0",
            "UID:grace-0001",
            "FN:Grace Example",
            "N:Example;Grace;;;",
            "EMAIL;TYPE=INTERNET,PREF:grace@example.com",
            "TEL;TYPE=CELL:+44 7700 900456",
            "END:VCARD",
            ""
         });
         Response created = Put(name, sent, "If-None-Match: *\r\n");
         Assert.AreEqual(201, created.Status, "Body: " + created.Body);

         Response listing = Rest("GET", "/api/v1/me/contacts");
         long id = long.Parse(Between(listing.Body, "\"id\":", ","));

         Response renamed = Rest("PUT", "/api/v1/me/contacts/" + id, "{\"name\":\"Grace Renamed\",\"address\":\"grace.renamed@example.com\"}");
         Assert.AreEqual(200, renamed.Status, "Body: " + renamed.Body);

         Response read = Dav("GET", name, Auth(Address, UserPassword));
         Assert.AreEqual(200, read.Status, "Body: " + read.Body);
         Assert.AreNotEqual(created.Header("ETag"), read.Header("ETag"), "A card that changed has a new ETag.");
         StringAssert.Contains("\r\nFN:Grace Renamed\r\n", read.Body);
         StringAssert.Contains("\r\nN:Renamed;Grace;;;\r\n", read.Body);
         StringAssert.Contains("\r\nEMAIL;TYPE=INTERNET,PREF:grace.renamed@example.com\r\n", read.Body);
         StringAssert.Contains("\r\nTEL;TYPE=CELL:+44 7700 900456\r\n", read.Body);
         StringAssert.Contains("\r\nUID:grace-0001\r\n", read.Body);
         StringAssert.StartsWith("BEGIN:VCARD\r\nVERSION:3.0\r\n", read.Body);
      }

      [Test]
      [Description("A vCard 2.1 encoding is refused with CARDDAV:valid-address-data naming it, rather than stored as it would be read; a numeric character reference to a surrogate in a report is not reflected into the 207")]
      public void LegacyEncodingsAreRefusedAndBadReferencesAreNotReflected()
      {
         string legacy = string.Join("\r\n", new[]
         {
            "BEGIN:VCARD",
            "VERSION:2.1",
            "FN;ENCODING=QUOTED-PRINTABLE:Ren=C3=A9",
            "EMAIL;INTERNET:rene@example.com",
            "END:VCARD",
            ""
         });
         Response refused = Put(Book + "rene.vcf", legacy, "If-None-Match: *\r\n");
         Assert.AreEqual(403, refused.Status, "Body: " + refused.Body);
         StringAssert.Contains("valid-address-data", refused.Body);
         StringAssert.Contains("QUOTED-PRINTABLE", refused.Body);
         StringAssert.Contains("\"contacts\":[]", Rest("GET", "/api/v1/me/contacts").Body);

         RestCreate("Alice Example", "alice@example.com");
         Response multiget = Report(Book, "<C:addressbook-multiget xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\"><D:prop><D:getetag/></D:prop><D:href>" + Book + "&#xD800;&#xFFFE;no-such.vcf</D:href></C:addressbook-multiget>");
         Assert.AreEqual(207, multiget.Status, "Body: " + multiget.Body);
         Assert.DoesNotThrow(() => Parse(multiget), "The 207 must stay well-formed XML whatever the client put in an href.");
      }

      [Test]
      [Description("A vCard 4.0 card is accepted: FN, the preferred EMAIL by PREF, and a mailto: value")]
      public void AVCard40CardIsImported()
      {
         string card = string.Join("\r\n", new[]
         {
            "BEGIN:VCARD",
            "VERSION:4.0",
            "FN:Erin Example",
            "N:Example;Erin;;;",
            "EMAIL;PREF=2:erin.second@example.com",
            "EMAIL;PREF=1:mailto:Erin@Example.com",
            "TEL;VALUE=uri:tel:+44-7700-900123",
            "END:VCARD",
            ""
         });

         Response created = Put(Book + "erin.vcf", card, "If-None-Match: *\r\n");
         Assert.AreEqual(201, created.Status, "Body: " + created.Body);

         Response rest = Rest("GET", "/api/v1/me/contacts");
         StringAssert.Contains("\"address\":\"erin@example.com\"", rest.Body);
         StringAssert.Contains("\"name\":\"Erin Example\"", rest.Body);
         Assert.IsFalse(rest.Body.Contains("erin.second"), "The preferred address is the one kept. Body: " + rest.Body);

         // Served back as 3.0, which is what the collection advertises.
         Response read = Dav("GET", created.Header("Location"), Auth(Address, UserPassword));
         Assert.AreEqual(200, read.Status, "Body: " + read.Body);
         StringAssert.Contains("VERSION:3.0", read.Body);
         StringAssert.Contains("EMAIL;TYPE=INTERNET,PREF:erin@example.com", read.Body);
      }

      // ---------------------------------------------------------------- helpers

      private static string Card(string formattedName, string structuredName, string email)
      {
         return string.Join("\r\n", new[]
         {
            "BEGIN:VCARD",
            "VERSION:3.0",
            "PRODID:-//A phone//EN",
            "UID:" + Guid.NewGuid().ToString("D"),
            "FN:" + formattedName,
            "N:" + structuredName,
            "EMAIL;TYPE=INTERNET:" + email,
            "END:VCARD",
            ""
         });
      }

      private static string Query(string propFilters, string limit)
      {
         return "<C:addressbook-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\">" +
                "<D:prop><D:getetag/></D:prop>" +
                (propFilters.Length > 0 ? "<C:filter>" + propFilters + "</C:filter>" : "") +
                (limit ?? "") +
                "</C:addressbook-query>";
      }

      private static string Sync(string token)
      {
         return "<D:sync-collection xmlns:D=\"DAV:\"><D:sync-token>" + token + "</D:sync-token><D:sync-level>1</D:sync-level>" +
                "<D:prop><D:getetag/></D:prop></D:sync-collection>";
      }

      private long RestCreate(string name, string address)
      {
         Response created = Rest("POST", "/api/v1/me/contacts", "{\"name\":\"" + name + "\",\"address\":\"" + address + "\"}");
         Assert.AreEqual(201, created.Status, "Body: " + created.Body);
         return long.Parse(Between(created.Body, "\"id\":", ","));
      }

      private Response Propfind(string path, string depth, string props, string authorization = null)
      {
         return Dav("PROPFIND", path, authorization ?? Auth(Address, UserPassword),
            "<D:propfind xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\" xmlns:CS=\"http://calendarserver.org/ns/\"><D:prop>" + props + "</D:prop></D:propfind>",
            "Depth: " + depth + "\r\n");
      }

      private Response Report(string path, string body)
      {
         return Dav("REPORT", path, Auth(Address, UserPassword), body, "Depth: 1\r\n");
      }

      private Response Put(string path, string card, string extraHeaders = null)
      {
         return Raw(WebServicesPort, "PUT", path, Auth(Address, UserPassword), card, "text/vcard; charset=utf-8",
            "X-Forwarded-Proto: https\r\n" + (extraHeaders ?? ""));
      }

      // A request to the CardDAV listener, as a TLS-terminating proxy would
      // forward it.
      private static Response Dav(string method, string path, string authorization, string body = null, string extraHeaders = null)
      {
         return Raw(WebServicesPort, method, path, authorization, body, "application/xml; charset=utf-8",
            "X-Forwarded-Proto: https\r\n" + (extraHeaders ?? ""));
      }

      private Response Rest(string method, string path, string body = null)
      {
         return Raw(RestPort, method, path, Auth(Address, UserPassword), body, "application/json", null);
      }

      private static string Auth(string user, string password)
      {
         return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
      }

      private static string Between(string text, string start, string end)
      {
         int from = text.IndexOf(start, StringComparison.Ordinal);
         Assert.IsTrue(from >= 0, "Missing " + start + " in: " + text);
         from += start.Length;
         int to = text.IndexOf(end, from, StringComparison.Ordinal);
         Assert.IsTrue(to >= 0, "Missing " + end + " after " + start + " in: " + text);
         return text.Substring(from, to - from);
      }

      // ------------------------------------------------------- multistatus XML

      private static XmlDocument Parse(Response response)
      {
         var document = new XmlDocument { XmlResolver = null };
         try
         {
            document.LoadXml(response.Body);
         }
         catch (XmlException ex)
         {
            Assert.Fail("Not well-formed XML (" + ex.Message + "): " + response.Body);
         }
         return document;
      }

      private static XmlNamespaceManager Namespaces(XmlDocument document)
      {
         var namespaces = new XmlNamespaceManager(document.NameTable);
         namespaces.AddNamespace("D", NsDav);
         namespaces.AddNamespace("C", NsCardDav);
         namespaces.AddNamespace("CS", NsCalendarServer);
         return namespaces;
      }

      private static List<string> Hrefs(Response response)
      {
         XmlDocument document = Parse(response);
         return document.SelectNodes("/D:multistatus/D:response/D:href", Namespaces(document))
            .Cast<XmlNode>().Select(node => node.InnerText).ToList();
      }

      private static string SyncToken(Response response)
      {
         XmlDocument document = Parse(response);
         XmlNode token = document.SelectSingleNode("/D:multistatus/D:sync-token", Namespaces(document));
         return token == null ? null : token.InnerText;
      }

      // The property, from the 200 propstat of the response for the href.
      private static XmlNode Prop(Response response, string href, string propertyPath)
      {
         XmlDocument document = Parse(response);
         return document.SelectSingleNode(
            "/D:multistatus/D:response[D:href='" + href + "']/D:propstat[contains(D:status,'200')]/D:prop/" + propertyPath,
            Namespaces(document));
      }

      private static string PropText(Response response, string href, string propertyPath)
      {
         XmlNode node = Prop(response, href, propertyPath);
         Assert.IsNotNull(node, "No " + propertyPath + " for " + href + " in: " + response.Body);
         return node.InnerText;
      }

      private static string ResponseStatus(Response response, string href)
      {
         XmlDocument document = Parse(response);
         XmlNode status = document.SelectSingleNode("/D:multistatus/D:response[D:href='" + href + "']/D:status", Namespaces(document));
         Assert.IsNotNull(status, "No status for " + href + " in: " + response.Body);
         return status.InnerText;
      }

      // ------------------------------------------------------------- raw HTTP

      private sealed class Response
      {
         public int Status;
         public string Body = "";
         public string HeaderText = "";
         public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

         public string Header(string name)
         {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
         }
      }

      private static Response Raw(int port, string method, string path, string authorization, string body, string contentType, string extraHeaders)
      {
         using (var client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", port);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  Thread.Sleep(200);
               }
            }

            if (last != null)
               throw last;

            using (NetworkStream stream = client.GetStream())
            using (var memory = new MemoryStream())
            {
               var head = new StringBuilder();
               head.Append(method + " " + path + " HTTP/1.0\r\n");
               head.Append("Host: 127.0.0.1:" + port + "\r\n");
               if (authorization != null)
                  head.Append("Authorization: " + authorization + "\r\n");
               if (extraHeaders != null)
                  head.Append(extraHeaders);

               byte[] bodyBytes = body == null ? new byte[0] : Encoding.UTF8.GetBytes(body);
               if (body != null)
               {
                  head.Append("Content-Type: " + contentType + "\r\n");
                  head.Append("Content-Length: " + bodyBytes.Length + "\r\n");
               }

               head.Append("Connection: close\r\n\r\n");

               byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
               stream.Write(headBytes, 0, headBytes.Length);
               if (bodyBytes.Length > 0)
                  stream.Write(bodyBytes, 0, bodyBytes.Length);

               byte[] buffer = new byte[8192];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               var response = new Response();

               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               response.HeaderText = separator >= 0 ? raw.Substring(0, separator) : raw;
               response.Body = separator >= 0 ? raw.Substring(separator + 4) : "";

               string[] lines = response.HeaderText.Split(new[] { "\r\n" }, StringSplitOptions.None);
               if (lines.Length > 0)
               {
                  string[] parts = lines[0].Split(' ');
                  if (parts.Length >= 2)
                     int.TryParse(parts[1], out response.Status);
               }

               for (int i = 1; i < lines.Length; i++)
               {
                  int colon = lines[i].IndexOf(':');
                  if (colon > 0)
                     response.Headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
               }

               return response;
            }
         }
      }
   }
}
