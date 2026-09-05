// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// One starting point on the Welcome page: what somebody came here to do, in
   /// their words, and the page where it is done.
   /// </summary>
   public sealed class WelcomeIntent
   {
      public WelcomeIntent(string heading, string blurb, string page)
      {
         Heading = heading;
         Blurb = blurb;
         Page = page;
      }

      /// <summary>The task, phrased as the administrator would - never a page name.</summary>
      public string Heading { get; }

      /// <summary>What they will find, so the choice is informed before they click.</summary>
      public string Blurb { get; }

      /// <summary>The navigation key of the page.</summary>
      public string Page { get; }
   }

   /// <summary>
   /// The Welcome page's "What do you want to do?" list.
   ///
   /// A starting page rather than a directory: a dozen tasks, ordered by how often
   /// they are the reason the application was opened, with the one that used to be
   /// findable nowhere - mail that has stalled - first. The palette (Ctrl+K) knows
   /// hundreds of phrasings through <see cref="IntentIndex"/>; this is the short
   /// list somebody who has not learned the product yet can scan in a moment.
   /// </summary>
   public static class WelcomeIntents
   {
      public static readonly IReadOnlyList<WelcomeIntent> Entries = new List<WelcomeIntent>
      {
         new WelcomeIntent("Mail is stuck or slow",
            "Work out which half has stalled - accepting or delivering - turn on the logging that names the cause, and read the lines that do.",
            "stalledmail"),
         new WelcomeIntent("See what is waiting to go out",
            "Every message still in the delivery queue, the last error the server got for it, and when it is retried.",
            "queue"),
         new WelcomeIntent("Watch the server work",
            "The log as it is written, filtered by protocol - the fastest way to see what happened to one message.",
            "logs"),
         new WelcomeIntent("Add a domain or a mailbox",
            "Domains, accounts, aliases and distribution lists, and each account's passwords, forwarding and folders.",
            "domains"),
         new WelcomeIntent("Stop spam",
            "What every spam check is doing right now, in the order the server runs them, and where each is switched on.",
            "spamoverview"),
         new WelcomeIntent("Someone is guessing passwords",
            "Auto-ban by address, the per-name lockout, and the logon tarpit that makes each guess cost seconds.",
            "autoban"),
         new WelcomeIntent("Let a device or printer send mail",
            "IP ranges: which addresses may relay, which must authenticate, and which are exempt from spam checks.",
            "ipranges"),
         new WelcomeIntent("Renew or install a certificate",
            "The certificates the listeners present, and automatic renewal through ACME.",
            "certs"),
         new WelcomeIntent("Send all mail through my provider",
            "The smart host, its port and credentials, and the retry schedule for delivery.",
            "delivery"),
         new WelcomeIntent("Check the server is healthy",
            "The built-in connectivity and configuration checks, and the last message-store consistency scan.",
            "diagnostics"),
         new WelcomeIntent("Back up the configuration and the mail",
            "What a backup holds, the schedule, verification of every archive, and restore.",
            "backup"),
         new WelcomeIntent("Protect the administrator credential",
            "The administrator password, and a second factor the server itself enforces on every client.",
            "adminaccess"),
      };
   }
}
