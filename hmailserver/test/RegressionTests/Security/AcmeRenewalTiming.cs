// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    When an ACME certificate is renewed: two thirds of the way through whatever
   ///    lifetime it has, never later than a day before it expires, and from the
   ///    moment of issue when it does not even live a day. This is what makes Let's
   ///    Encrypt's 64-day default (10 February 2027), the CA/Browser Forum's 100-day
   ///    ceiling (15 March 2027) and its 47-day one (15 March 2029) non-events for this
   ///    server: nothing is fixed at thirty days. The arithmetic is asked of the server
   ///    through Diagnostics.AcmeRenewalTime, with the ACME suggested-renewal window
   ///    (ARI, RFC 9773) out of the picture - that is the CA's opinion, and it is
   ///    tested where the CA is faked.
   /// </summary>
   [TestFixture]
   public class AcmeRenewalTiming : TestFixtureBase
   {
      private const double Day = 86400;
      private static readonly double Issued = new DateTimeOffset(2027, 2, 10, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

      private double RenewAt(double lifetimeDays)
      {
         return _application.Diagnostics.AcmeRenewalTime(Issued, Issued + lifetimeDays * Day);
      }

      [Test]
      public void ANinetyDayCertificateRenewsAtSixtyDays()
      {
         Assert.AreEqual(Issued + 60 * Day, RenewAt(90), "Two thirds of ninety days.");
      }

      [Test]
      public void ASixtyFourDayCertificateRenewsTwoThirdsThrough()
      {
         // Let's Encrypt's default from 10 February 2027: two thirds of 64 days is
         // 42 days and 16 hours, and the remaining third - 21 days - is the margin.
         Assert.AreEqual(Issued + (64 * Day / 3) * 2, RenewAt(64));
         Assert.AreEqual(Issued + 42 * Day + 16 * 3600, RenewAt(64));
      }

      [Test]
      public void AFortySevenDayCertificateRenewsTwoThirdsThrough()
      {
         // The CA/Browser Forum's 47-day ceiling from 15 March 2029: renewal at day
         // 31 and a third, with over two weeks in hand.
         double lifetime = 47 * Day;
         Assert.AreEqual(Issued + Math.Floor(lifetime / 3) * 2, RenewAt(47));
         Assert.IsTrue(RenewAt(47) < Issued + 32 * Day && RenewAt(47) > Issued + 31 * Day);
      }

      [Test]
      public void NeverLaterThanADayBeforeExpiry()
      {
         // Two days: two thirds would leave sixteen hours, so the floor wins and the
         // renewal is due after one day, with a whole day for retries.
         Assert.AreEqual(Issued + 1 * Day, RenewAt(2));
      }

      [Test]
      public void ACertificateThatDoesNotLiveADayRenewsFromIssue()
      {
         // Twelve hours: there is no day to leave, so the only honest answer is now.
         Assert.AreEqual(Issued, RenewAt(0.5));
      }

      [Test]
      public void AnUnreadableIssueDateFallsBackToThirtyDaysBeforeExpiry()
      {
         // No notBefore: the ninety-day lifetime this server always assumed, and a
         // third of it before expiry - the old fixed thirty days, as the fallback.
         double expiry = Issued + 90 * Day;
         Assert.AreEqual(expiry - 30 * Day, _application.Diagnostics.AcmeRenewalTime(0, expiry));
      }
   }
}
