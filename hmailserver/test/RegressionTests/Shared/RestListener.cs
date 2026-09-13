// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The REST listener a fixture talks to, started and stopped in the one way
   ///    that is right for the bench the suite is on.
   ///
   ///    On the Windows bench the listener is off until a fixture wants it: the
   ///    fixture writes RestApiBindAddress and RestApiPort into hMailServer.ini,
   ///    reinitialises, and writes RestApiPort back to 0 in its teardown, on a
   ///    port of its own so that two fixtures never contend for one. Against the
   ///    Linux server the listener is already on - it is the one the whole fixture
   ///    layer talks to, on the port HMTEST_REST_PORT names - and a fixture that
   ///    moved it or turned it off would take every test after it down with it.
   ///    So: with HMTEST_REST_PORT in the environment, Start answers that port and
   ///    writes nothing, and Stop does nothing; without it, they do what the
   ///    fixtures always did. The fixture's Reinitialize call stays where it was,
   ///    because the other settings a fixture writes still need reading.
   /// </summary>
   public static class RestListener
   {
      /// <summary>The port the suite's own listener answers on, or 0 when a fixture starts its own.</summary>
      public static int Provided
      {
         get
         {
            string value = Environment.GetEnvironmentVariable("HMTEST_REST_PORT");
            int port;
            return !string.IsNullOrWhiteSpace(value) && int.TryParse(value.Trim(), out port) && port > 0 ? port : 0;
         }
      }

      /// <summary>
      ///    Arranges for the listener to be on after the caller's next Reinitialize,
      ///    and answers the port it will answer on: preferredPort on a bench where
      ///    the fixture starts it, the suite's own port where it is already on.
      /// </summary>
      public static int Start(int preferredPort)
      {
         int provided = Provided;
         if (provided > 0)
            return provided;

         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", preferredPort.ToString());
         return preferredPort;
      }

      /// <summary>
      ///    Arranges for the listener to be off after the caller's next Reinitialize
      ///    - or leaves the suite's own alone.
      /// </summary>
      public static void Stop()
      {
         if (Provided > 0)
            return;

         IniFileSetting.Write("RestApiPort", "0");
      }
   }
}
