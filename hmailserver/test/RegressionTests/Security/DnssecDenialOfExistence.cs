// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    The validating resolver against a chain of trust the test signs itself: a root
   ///    whose key is the server's trust anchor, a TLD "test" beneath it, and one child
   ///    zone per test beneath that - each with its own name, because the resolver caches
   ///    a zone's verdict for up to an hour and one test's zone must not answer for
   ///    another's. The question every test asks is the resolver's verdict on the child's
   ///    TXT record, through Diagnostics.DnssecChainStatus: 0 secure, 1 insecure, 2 bogus.
   ///    <para />
   ///    What is under test is denial of existence. A child zone without a DS record at
   ///    the parent is an unsigned delegation - the ordinary state of most of the
   ///    internet - but only when the parent proves the DS absent with an NSEC or NSEC3
   ///    record signed by its key. Before this fixture the resolver took "no DS" at its
   ///    word, which is exactly what an attacker who strips the DS from the answer shows
   ///    it: a signed zone quietly downgraded to unsigned, DANE and validated TXT gone
   ///    with it. Now a missing proof is Bogus.
   /// </summary>
   [TestFixture]
   public class DnssecDenialOfExistence : TestFixtureBase
   {
      private const int Secure = 0;
      private const int Insecure = 1;
      private const int Bogus = 2;
      private const uint Ttl = 60;

      private static DnssecZone _root;
      private static DnssecZone _tld;

      [OneTimeSetUp]
      public void SignTheRootAndTheTld()
      {
         _root = new DnssecZone("");
         _tld = new DnssecZone("test");

         // The root: its key, signed by itself, and the DS for "test" signed by it.
         var zone = SuiteDns.Zone;
         PublishKeys(zone, _root);
         zone.WithRaw(_tld.Name, DnsWire.TypeDs, _tld.DsRdata)
             .WithRrsig(_tld.Name, DnsWire.TypeDs, _root.Sign(_tld.Name, DnsWire.TypeDs, Ttl, new[] { _tld.DsRdata }));
         PublishKeys(zone, _tld);

         // The server reads its trust anchors once, at start.
         ServerIniFile.SetSetting("DnssecTrustAnchors", _root.TrustAnchor);
         RestartServerAndReacquireCom();
      }

      [OneTimeTearDown]
      public void BackToTheRealRoot()
      {
         ServerIniFile.SetSetting("DnssecTrustAnchors", null);
         RestartServerAndReacquireCom();
         _root.Dispose();
         _tld.Dispose();
      }

      [TearDown]
      public void ForgetTheZones()
      {
         // The child zones go; the root and the TLD stay for the next test, since
         // SuiteDns.Reset would take them too.
         SuiteDns.Reset();
         var zone = SuiteDns.Zone;
         PublishKeys(zone, _root);
         zone.WithRaw(_tld.Name, DnsWire.TypeDs, _tld.DsRdata)
             .WithRrsig(_tld.Name, DnsWire.TypeDs, _root.Sign(_tld.Name, DnsWire.TypeDs, Ttl, new[] { _tld.DsRdata }));
         PublishKeys(zone, _tld);
      }

      private static void PublishKeys(FakeDnsServer zone, DnssecZone signer)
      {
         zone.WithRaw(signer.Name, DnsWire.TypeDnskey, signer.DnskeyRdata)
             .WithRrsig(signer.Name, DnsWire.TypeDnskey, signer.Sign(signer.Name, DnsWire.TypeDnskey, Ttl, new[] { signer.DnskeyRdata }));
      }

      /// <summary>A child of "test" that signs its own keys and one TXT record. Whether the parent has a DS for it is the test's decision.</summary>
      private static DnssecZone Child(string label)
      {
         var child = new DnssecZone(label + ".test");
         var zone = SuiteDns.Zone;
         PublishKeys(zone, child);
         var txt = DnsWire.Txt("v=spf1 -all");
         zone.WithRaw(child.Name, DnsWire.TypeTxt, txt)
             .WithRrsig(child.Name, DnsWire.TypeTxt, child.Sign(child.Name, DnsWire.TypeTxt, Ttl, new[] { txt }));
         return child;
      }

      private static void PublishDs(DnssecZone child)
      {
         SuiteDns.Zone.WithRaw(child.Name, DnsWire.TypeDs, child.DsRdata)
                 .WithRrsig(child.Name, DnsWire.TypeDs, _tld.Sign(child.Name, DnsWire.TypeDs, Ttl, new[] { child.DsRdata }));
      }

      /// <summary>An NSEC at the child's name, signed by the TLD, with the types given - the proof the parent gives that no DS exists.</summary>
      private static void PublishNsecDenial(DnssecZone child, int[] types, DnssecZone signer = null, DateTime? expiration = null, string owner = null)
      {
         owner = owner ?? child.Name;
         var nsec = DnsWire.Nsec("zzz.test", types);
         var rrsig = (signer ?? _tld).Sign(owner, DnsWire.TypeNsec, Ttl, new[] { nsec }, expiration: expiration);
         SuiteDns.Zone.WithDenial(child.Name, DnsWire.TypeDs, owner, DnsWire.TypeNsec, nsec)
                 .WithDenial(child.Name, DnsWire.TypeDs, owner, DnsWire.TypeRrsig, rrsig);
      }

      /// <summary>An NSEC3 in the TLD whose hashed owner matches the child (or covers it), signed by the TLD.</summary>
      private static void PublishNsec3Denial(DnssecZone child, int[] types, bool matching, bool optOut)
      {
         var salt = new byte[] { 0xAB, 0xCD };
         const ushort iterations = 2;
         var hash = DnsWire.Nsec3Hash(child.Name, salt, iterations);
         byte[] ownerHash = matching ? hash : Adjacent(hash, -1);
         byte[] nextHash = Adjacent(hash, +1);
         var owner = DnsWire.Base32Hex(ownerHash) + ".test";
         var nsec3 = DnsWire.Nsec3(optOut, iterations, salt, nextHash, types);
         var rrsig = _tld.Sign(owner, DnsWire.TypeNsec3, Ttl, new[] { nsec3 });
         SuiteDns.Zone.WithDenial(child.Name, DnsWire.TypeDs, owner, DnsWire.TypeNsec3, nsec3)
                 .WithDenial(child.Name, DnsWire.TypeDs, owner, DnsWire.TypeRrsig, rrsig);
      }

      /// <summary>The hash plus or minus one, as a big-endian number - a name that sorts just before or just after.</summary>
      private static byte[] Adjacent(byte[] hash, int delta)
      {
         var result = (byte[]) hash.Clone();
         for (var i = result.Length - 1; i >= 0; i--)
         {
            var value = result[i] + delta;
            if (value >= 0 && value <= 255)
            {
               result[i] = (byte) value;
               return result;
            }
            result[i] = (byte) (delta > 0 ? 0 : 255);
         }
         return result;
      }

      private int StatusOf(string name)
      {
         return _application.Diagnostics.DnssecChainStatus(name, "TXT");
      }

      [Test]
      [Description("The chain the tests are built on: a child with a DS at the parent validates to the trust anchor.")]
      public void ASignedChainIsSecure()
      {
         using (var child = Child("signed"))
         {
            PublishDs(child);
            Assert.AreEqual(Secure, StatusOf(child.Name));
         }
      }

      [Test]
      [Description("A name with no signatures anywhere near it is insecure - the ordinary state of most names, unchanged by any of this.")]
      public void AnUnsignedNameIsInsecure()
      {
         SuiteDns.Zone.WithTxt("plain.test", "v=spf1 -all");
         Assert.AreEqual(Insecure, StatusOf("plain.test"));
      }

      [Test]
      [Description("The downgrade: a zone that signs its records, whose DS is missing from the parent's answer with no proof that it should be, is bogus - not quietly unsigned.")]
      public void AMissingDsWithoutProofIsBogus()
      {
         using (var child = Child("stripped"))
            Assert.AreEqual(Bogus, StatusOf(child.Name));
      }

      [Test]
      [Description("An NSEC at the name, signed by the parent, with NS and without DS proves an unsigned delegation.")]
      public void AnNsecProvingNoDsMakesTheDelegationInsecure()
      {
         using (var child = Child("nsec"))
         {
            PublishNsecDenial(child, new[] { DnsWire.TypeNs, DnsWire.TypeRrsig, DnsWire.TypeNsec });
            Assert.AreEqual(Insecure, StatusOf(child.Name));
         }
      }

      [Test]
      [Description("An NSEC whose bitmap says a DS exists contradicts the answer it came with and proves nothing.")]
      public void AnNsecClaimingADsIsNotAProof()
      {
         using (var child = Child("nsecds"))
         {
            PublishNsecDenial(child, new[] { DnsWire.TypeNs, DnsWire.TypeDs, DnsWire.TypeRrsig, DnsWire.TypeNsec });
            Assert.AreEqual(Bogus, StatusOf(child.Name));
         }
      }

      [Test]
      [Description("An NSEC for some other name that neither matches nor covers the child proves nothing about it.")]
      public void AnNsecForAnotherNameIsNotAProof()
      {
         using (var child = Child("aaa-elsewhere"))
         {
            // "other.test" sorts after "aaa-elsewhere.test", so the span other.test -> zzz.test does not cover it.
            PublishNsecDenial(child, new[] { DnsWire.TypeNs, DnsWire.TypeRrsig, DnsWire.TypeNsec }, owner: "other.test");
            Assert.AreEqual(Bogus, StatusOf(child.Name));
         }
      }

      [Test]
      [Description("An NSEC signed by the child's own key rather than the parent's is not the parent's word and proves nothing.")]
      public void AnNsecSignedByTheWrongZoneIsNotAProof()
      {
         using (var child = Child("selfsigned"))
         {
            PublishNsecDenial(child, new[] { DnsWire.TypeNs, DnsWire.TypeRrsig, DnsWire.TypeNsec }, signer: child);
            Assert.AreEqual(Bogus, StatusOf(child.Name));
         }
      }

      [Test]
      [Description("An NSEC whose signature has expired is no proof today.")]
      public void AnExpiredProofIsNotAProof()
      {
         using (var child = Child("expired"))
         {
            PublishNsecDenial(child, new[] { DnsWire.TypeNs, DnsWire.TypeRrsig, DnsWire.TypeNsec }, expiration: DateTime.UtcNow.AddMinutes(-5));
            Assert.AreEqual(Bogus, StatusOf(child.Name));
         }
      }

      [Test]
      [Description("An NSEC3 whose hashed owner is the child's, with NS and without DS, proves the unsigned delegation the way an NSEC does.")]
      public void AnNsec3MatchingTheNameProvesNoDs()
      {
         using (var child = Child("nsec3"))
         {
            PublishNsec3Denial(child, new[] { DnsWire.TypeNs, DnsWire.TypeRrsig }, matching: true, optOut: false);
            Assert.AreEqual(Insecure, StatusOf(child.Name));
         }
      }

      [Test]
      [Description("An NSEC3 with Opt-Out covering the hashed name says an unsigned delegation may sit in the span, which is a proof.")]
      public void AnOptOutNsec3CoveringTheNameProvesNoDs()
      {
         using (var child = Child("optout"))
         {
            PublishNsec3Denial(child, new[] { DnsWire.TypeNs, DnsWire.TypeRrsig }, matching: false, optOut: true);
            Assert.AreEqual(Insecure, StatusOf(child.Name));
         }
      }

      [Test]
      [Description("An NSEC3 covering the hashed name without Opt-Out says the name does not exist - and a zone that signs records under a name its parent says is not there is bogus.")]
      public void ACoveringNsec3WithoutOptOutIsNotAProofOfDelegation()
      {
         using (var child = Child("covered"))
         {
            PublishNsec3Denial(child, new[] { DnsWire.TypeNs, DnsWire.TypeRrsig }, matching: false, optOut: false);
            Assert.AreEqual(Bogus, StatusOf(child.Name));
         }
      }

      [Test]
      [Description("A proven-unsigned zone in between makes everything below it insecure, however the deeper zone signs itself and whatever its own DS answer lacks.")]
      public void AnUnsignedZoneAboveMakesTheDeeperZoneInsecure()
      {
         using (var middle = new DnssecZone("island.test"))
         using (var deep = new DnssecZone("deep.island.test"))
         {
            // island.test: no DS, and the TLD proves it - an unsigned delegation.
            PublishNsecDenial(middle, new[] { DnsWire.TypeNs, DnsWire.TypeRrsig, DnsWire.TypeNsec });

            // deep.island.test signs its own records; its DS answer carries no proof at all.
            var zone = SuiteDns.Zone;
            PublishKeys(zone, deep);
            var txt = DnsWire.Txt("v=spf1 -all");
            zone.WithRaw(deep.Name, DnsWire.TypeTxt, txt)
                .WithRrsig(deep.Name, DnsWire.TypeTxt, deep.Sign(deep.Name, DnsWire.TypeTxt, Ttl, new[] { txt }));

            Assert.AreEqual(Insecure, StatusOf(deep.Name));
         }
      }
   }
}
