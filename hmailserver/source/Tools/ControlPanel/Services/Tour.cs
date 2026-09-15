// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>What a step does when the control it points at is not on the screen.</summary>
   public enum WhenMissing
   {
      /// <summary>
      /// Move on to the next step. The default, and what nearly every step
      /// wants: a control that is hidden because the server has not got that
      /// far, or because a licence or a platform does not have it, is not a
      /// reason to stop teaching the rest of the page.
      /// </summary>
      Skip,

      /// <summary>
      /// End the tour, saying which step could not be shown. For a step the
      /// rest of the tour is meaningless without - there is no point walking
      /// somebody through an account when the domain list will not open.
      /// </summary>
      EndTour
   }

   /// <summary>
   /// One stop on a tour: a control to point at, one sentence about it, and
   /// what to do when it is not there.
   ///
   /// A step never *acts*. It does not press the button it points at, it does
   /// not fill the field, and it does not navigate anywhere except to the page
   /// the control lives on - a tour that does the work teaches nothing, and a
   /// tour that does the work wrongly on somebody's live server is worse than
   /// no tour at all.
   /// </summary>
   public sealed class TourStep
   {
      public TourStep(string page, string target, string sentence,
         string until = null, WhenMissing whenMissing = WhenMissing.Skip)
      {
         Page = page;
         Target = target;
         Sentence = sentence;
         Until = until;
         WhenMissing = whenMissing;
      }

      /// <summary>
      /// The navigation key of the page the control is on. The runner opens it
      /// before looking for the control. Null means "wherever the reader
      /// already is", which is what a step about the shell itself wants.
      /// </summary>
      public string Page { get; }

      /// <summary>
      /// The control, by its automation id - the same id the Ctrl+K palette,
      /// <see cref="SettingsSearchIndex"/> and build/capture-cp.ps1 route by.
      /// An id, not a position: a step pinned to "the third button" survives
      /// nothing.
      /// </summary>
      public string Target { get; }

      /// <summary>
      /// The one sentence shown beside the control. One, not a paragraph: this
      /// is read standing up, over the thing it describes, and the wiki is
      /// where the paragraph belongs.
      /// </summary>
      public string Sentence { get; }

      /// <summary>
      /// The name of the condition that ends this step on its own - the thing
      /// the step is waiting for the reader to do, such as <c>domain-exists</c>.
      /// The runner asks the surface whether it has happened; null is a step
      /// that ends only when the reader presses Next. A condition never blocks:
      /// Next still moves on whether it is satisfied or not.
      /// </summary>
      public string Until { get; }

      /// <summary>What happens when <see cref="Target"/> is not on the screen.</summary>
      public WhenMissing WhenMissing { get; }
   }

   /// <summary>An ordered list of steps with a name, a version and somewhere to start.</summary>
   public sealed class Tour
   {
      public Tour(string id, string name, string blurb, int version, IReadOnlyList<TourStep> steps)
      {
         Id = id;
         Name = name;
         Blurb = blurb;
         Version = version;
         Steps = steps;
      }

      /// <summary>
      /// A short stable key. It is what the finished record is kept under, so
      /// renaming one abandons every reader's record of having done it; the
      /// name is what changes when the words change.
      /// </summary>
      public string Id { get; }

      /// <summary>The tour's name, as the "show me" entry points offer it.</summary>
      public string Name { get; }

      /// <summary>One line saying where the tour ends, so the choice to start it is informed.</summary>
      public string Blurb { get; }

      /// <summary>
      /// Raised when the steps change enough that somebody who finished the old
      /// one has not seen the new one. A finished record at a lower version is
      /// a tour worth offering again; a resume point from a lower version is
      /// meaningless and is dropped.
      /// </summary>
      public int Version { get; }

      public IReadOnlyList<TourStep> Steps { get; }

      /// <summary>The index of the first step on a page, or -1 when the tour never visits it.</summary>
      public int FirstStepOn(string page)
      {
         if (string.IsNullOrEmpty(page))
            return -1;

         for (int index = 0; index < Steps.Count; index++)
         {
            if (string.Equals(Steps[index].Page, page, StringComparison.OrdinalIgnoreCase))
               return index;
         }

         return -1;
      }
   }

   /// <summary>
   /// The Control Panel's tours.
   ///
   /// One today, deliberately: the first-run walk, which is the hour this
   /// product spends worst. It ends where a new administrator wanted to be
   /// rather than where it started - a domain, an account, a listener offering
   /// TLS, and the dashboard's own verdict on the result - and every stop is a
   /// control that is already on a page, pointed at rather than reimplemented.
   ///
   /// The list is here rather than in the shell so that a test can hold every
   /// step to a page that exists and a sentence that is marked for
   /// translation. Nothing in this file knows about WPF, a window, or a
   /// running server.
   /// </summary>
   public static class TourCatalog
   {
      /// <summary>The first-run walk's id. Named in the shell and in the palette.</summary>
      public const string FirstRun = "firstrun";

      public static readonly IReadOnlyList<Tour> All = new List<Tour>
      {
         new Tour(FirstRun,
            N("Show me around"),
            N("A domain, a mailbox, a listener that offers TLS, and what the dashboard makes of it. Four minutes; leave whenever you like."),
            1,
            new List<TourStep>
            {
               new TourStep("domains", "domains-add",
                  N("Start with a domain: the part after the @ that this server accepts mail for. Add yours here."),
                  "domain-exists"),
               new TourStep("domains", "domains-grid",
                  N("Your domains live in this list. Opening one gives you its accounts, aliases and distribution lists.")),
               new TourStep("domains", "domains-accounts",
                  N("Now a mailbox. An account is an address somebody signs in to and receives mail at."),
                  "account-exists"),
               new TourStep("ports", "ports-grid",
                  N("These are the ports the server listens on. The submission port, 587, is the one your own people send through."),
                  whenMissing: WhenMissing.EndTour),
               new TourStep("ports", "ports-add",
                  N("A listener wants a certificate and STARTTLS, so that no password crosses the network in the clear."),
                  "tls-listener"),
               new TourStep("certs", "certs-grid",
                  N("Certificates a listener can present are kept here, and ACME can renew them for you."),
                  "certificate-exists"),
               new TourStep("dashboard", "dashboard-verdict",
                  N("And this is the server's own verdict on what you have built. It is the page to come back to.")),
            }),
      };

      /// <summary>The tour with this id, or null.</summary>
      public static Tour Find(string id)
         => string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(tour => string.Equals(tour.Id, id, StringComparison.OrdinalIgnoreCase));

      /// <summary>
      /// The tour a page's help button offers, and the step it would start at:
      /// the first tour that visits the page, starting at its first step there.
      /// A page no tour visits gets null, and its help button is not drawn -
      /// a button that opens an apology is worse than no button.
      /// </summary>
      public static Tour ForPage(string page, out int startAt)
      {
         startAt = 0;
         if (string.IsNullOrEmpty(page))
            return null;

         foreach (Tour tour in All)
         {
            int at = tour.FirstStepOn(page);
            if (at >= 0)
            {
               startAt = at;
               return tour;
            }
         }

         return null;
      }

      /// <summary>The same question without the index, for a caller that only asks whether there is one.</summary>
      public static Tour ForPage(string page) => ForPage(page, out _);
   }
}
