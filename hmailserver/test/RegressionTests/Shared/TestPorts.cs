// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Net;
using System.Net.Sockets;
using System.Linq;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    Where the server under test listens: what the client simulators' parameterless
   ///    constructors connect to, and what the two fixtures that name a port themselves
   ///    use.
   ///
   ///    The Windows suite runs against a service on this machine on the standard ports,
   ///    and these defaults are exactly the literals the simulators carried until
   ///    September 2026, so it sees no change. The Linux suite (test/LinuxRegressionTests)
   ///    compiles the same fixture files against a server that is on other ports and may
   ///    be on another host, and sets these once, when its assembly loads, from the
   ///    HMTEST_* environment variables.
   ///
   ///    Settable rather than read from the environment here, so that this file has no
   ///    opinion about where the values come from: the Windows suite never sets them, and
   ///    nothing in it reads an environment variable by accident.
   /// </summary>
   public static class TestPorts
   {
      public static string Host { get; set; } = "127.0.0.1";
      public static int Smtp { get; set; } = 25;
      public static int Pop3 { get; set; } = 110;
      public static int Imap { get; set; } = 143;
      public static int Submission { get; set; } = 587;

      /// <summary>
      ///    The port a fixture means when it names one of the four standard numbers
      ///    itself - TcpConnection.Connect(143) and the like, of which the suite has
      ///    some sixty. On the Windows bench these ARE the server's ports and this is
      ///    the identity, so nothing there changes; against a server on other ports
      ///    they are the names of the four services, and this is what turns them into
      ///    that server's numbers.
      ///
      ///    It matters more than a tidy-up. WSL here runs with mirrored networking, so
      ///    127.0.0.1:143 inside it reaches the Windows machine's own hMailServer
      ///    service: without this, a fixture naming 143 quietly tested the wrong
      ///    server and failed on an account it had never heard of. On a CI runner with
      ///    no such service the same connection is simply refused.
      /// </summary>
      public static int Actual(int port)
      {
         if (port == 25)
            return Smtp;
         if (port == 110)
            return Pop3;
         if (port == 143)
            return Imap;
         if (port == 587)
            return Submission;

         return port;
      }

      /// <summary>
      ///    How long a client connection waits for the server to send anything before
      ///    the read fails, in milliseconds; 0 is the behaviour the suite has always
      ///    had, a read that waits for ever. The Windows bench never needs it: its
      ///    server always answers. The Linux suite sets it, because a server that
      ///    answers a command with an untagged BAD and nothing else otherwise stops
      ///    the run for good - the first Linux run stood seventeen minutes in one
      ///    test that way - and NUnit on .NET cannot abort a thread blocked in a read.
      ///    With it, such a test fails with an IOException that names the wait.
      /// </summary>
      public static int ReceiveTimeoutMilliseconds { get; set; } = 0;

      /// <summary>
      ///    Host as an address. A name is resolved to its first IPv4 address, because
      ///    the simulators open IPv4 sockets and the server's listeners are IPv4.
      /// </summary>
      public static IPAddress HostAddress
      {
         get
         {
            IPAddress parsed;
            if (IPAddress.TryParse(Host, out parsed))
               return parsed;

            return Dns.GetHostAddresses(Host).FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
               ?? throw new SocketException((int) SocketError.HostNotFound);
         }
      }
   }
}
