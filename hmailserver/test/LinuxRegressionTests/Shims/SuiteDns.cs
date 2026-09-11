// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The suite-wide fake DNS zone, as the fixtures address it. On Windows a
   ///    [SetUpFixture] binds 127.0.0.1:53, points the service's DNSServer at it for
   ///    the run, and fixtures add the records they need. The server here is on
   ///    another machine with a resolver of its own, and nothing in this environment
   ///    serves the zone to it; a fixture that adds a record is therefore ignored at
   ///    that point, with the reason, which for the two that do it is their
   ///    OneTimeSetUp and so the whole fixture.
   /// </summary>
   public static class SuiteDns
   {
      public const string Resolver = "127.0.0.1";

      public static Zone_ Zone { get; } = new Zone_();

      public static void Reset()
      {
      }

      public sealed class Zone_
      {
         public Zone_ WithMx(string name, int preference, string exchange)
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSuiteDns);
         }

         public Zone_ WithA(string name, string address)
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSuiteDns);
         }

         public Zone_ WithCname(string name, string target)
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSuiteDns);
         }

         public Zone_ WithTxt(string name, string text)
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSuiteDns);
         }

         public Zone_ ClearQueries()
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSuiteDns);
         }

         public Zone_ WithNxDomain(string name)
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSuiteDns);
         }

         public Zone_ WithPtr(string address, string name)
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSuiteDns);
         }

         public Zone_ WithAaaa(string name, string address)
         {
            throw NotOnThisServer.Skipped(NotOnThisServer.NoSuiteDns);
         }
      }
   }
}
