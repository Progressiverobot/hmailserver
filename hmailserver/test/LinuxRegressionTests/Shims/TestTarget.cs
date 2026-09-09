// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Runtime.CompilerServices;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The server this run is aimed at, read once from the environment:
   ///
   ///       HMTEST_HOST            127.0.0.1
   ///       HMTEST_SMTP_PORT       2525
   ///       HMTEST_POP3_PORT       1110
   ///       HMTEST_IMAP_PORT       1143
   ///       HMTEST_REST_PORT       8045
   ///       HMTEST_ADMIN_PASSWORD  testar
   ///
   ///    The defaults are the PostgreSQL-backed test tree the Linux port was proved
   ///    against (docs/RegressionEnvironment.md, "On Linux"), and the same numbers
   ///    the CI job lays out, so a run with nothing set is a run against that.
   ///
   ///    The mail ports are handed to TestPorts when this assembly loads, before any
   ///    fixture is constructed, which is what makes the simulators' parameterless
   ///    constructors connect to the right place without the fixtures knowing.
   /// </summary>
   public static class TestTarget
   {
      public static readonly string Host = Read("HMTEST_HOST", "127.0.0.1");
      public static readonly int SmtpPort = Read("HMTEST_SMTP_PORT", 2525);
      public static readonly int Pop3Port = Read("HMTEST_POP3_PORT", 1110);
      public static readonly int ImapPort = Read("HMTEST_IMAP_PORT", 1143);
      public static readonly int RestPort = Read("HMTEST_REST_PORT", 8045);
      public static readonly string AdminPassword = Read("HMTEST_ADMIN_PASSWORD", "testar");

      public static string RestBaseUrl => "http://" + Host + ":" + RestPort;

      public static string Describe()
      {
         return Host + " (SMTP " + SmtpPort + ", POP3 " + Pop3Port + ", IMAP " + ImapPort + ", REST " + RestPort + ")";
      }

      /// <summary>
      ///    Two minutes: longer than any linked test waits for a reply it is going to
      ///    get, and the difference between a server that answers a command with an
      ///    untagged BAD and nothing else costing one failed test and costing the
      ///    whole run (TestPorts.ReceiveTimeoutMilliseconds says more).
      /// </summary>
      public const int ReceiveTimeoutMilliseconds = 120000;

      [ModuleInitializer]
      internal static void PointTheSimulatorsAtTheTarget()
      {
         TestPorts.Host = Host;
         TestPorts.Smtp = SmtpPort;
         TestPorts.Pop3 = Pop3Port;
         TestPorts.Imap = ImapPort;
         TestPorts.ReceiveTimeoutMilliseconds = ReceiveTimeoutMilliseconds;
      }

      private static string Read(string name, string fallback)
      {
         var value = Environment.GetEnvironmentVariable(name);
         return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
      }

      private static int Read(string name, int fallback)
      {
         var value = Environment.GetEnvironmentVariable(name);
         int parsed;
         if (string.IsNullOrWhiteSpace(value))
            return fallback;
         if (!int.TryParse(value.Trim(), out parsed) || parsed < 1 || parsed > 65535)
            throw new InvalidOperationException(name + " is set to '" + value + "', which is not a port number.");
         return parsed;
      }
   }
}
