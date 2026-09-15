// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The tours themselves. A tour is a list of promises about the application
   /// - this page exists, this control is on it, this sentence is translated -
   /// and every one of them can be broken by a change somewhere else that has
   /// nothing to do with tours. These are what break instead.
   /// </summary>
   public class TourCatalogTests
   {
      /// <summary>The Control Panel's sources, for the automation-id check below.</summary>
      private static readonly string Root = FindControlPanel();

      [Fact]
      public void EveryTourHasAnIdANameAndSteps()
      {
         Assert.NotEmpty(TourCatalog.All);

         foreach (Tour tour in TourCatalog.All)
         {
            Assert.False(string.IsNullOrWhiteSpace(tour.Id), "a tour with no id cannot be recorded as finished");
            Assert.False(string.IsNullOrWhiteSpace(tour.Name), tour.Id + " has no name");
            Assert.False(string.IsNullOrWhiteSpace(tour.Blurb), tour.Id + " does not say where it ends");
            Assert.True(tour.Version > 0, tour.Id + " has no version; a version of zero can never be superseded");
            Assert.NotEmpty(tour.Steps);
            Assert.Equal(tour.Id, tour.Id.Trim());
         }

         Assert.Equal(TourCatalog.All.Count,
            TourCatalog.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
      }

      [Fact]
      public void EveryStepNamesAPageThatExistsAndAControlToPointAt()
      {
         foreach (Tour tour in TourCatalog.All)
         {
            foreach (TourStep step in tour.Steps)
            {
               Assert.False(string.IsNullOrWhiteSpace(step.Target), tour.Id + " has a step pointing at nothing");
               Assert.False(string.IsNullOrWhiteSpace(step.Sentence), tour.Id + ": " + step.Target + " says nothing");

               if (step.Page != null)
               {
                  Assert.True(NavigationMap.Find(step.Page) != null,
                     tour.Id + " points at the page \"" + step.Page + "\", which the navigation does not have");
               }
            }
         }
      }

      /// <summary>
      /// The step points at an automation id, and the id has to be somewhere in
      /// the sources for the ring to have anything to draw around. Reading the
      /// files is the only way to check this without a window; a step whose id
      /// was renamed out from under it would otherwise be found by a person
      /// walking the tour and by nobody else.
      /// </summary>
      [Fact]
      public void EveryStepsAutomationIdIsSetSomewhereInTheControlPanel()
      {
         var ids = new HashSet<string>(StringComparer.Ordinal);
         foreach (string file in Directory.EnumerateFiles(Root, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
         {
            string text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match hit in
                     System.Text.RegularExpressions.Regex.Matches(text, "AutomationId(?:\\s*=\\s*|\\([^,]+,\\s*)\"([^\"]+)\""))
            {
               ids.Add(hit.Groups[1].Value);
            }
         }

         foreach (Tour tour in TourCatalog.All)
         {
            foreach (TourStep step in tour.Steps)
            {
               Assert.True(ids.Contains(step.Target),
                  tour.Id + " points at the automation id \"" + step.Target + "\", which nothing in the Control Panel sets");
            }
         }
      }

      /// <summary>
      /// A step that ends the tour when its control is missing stops everything
      /// after it, so it is worth being sure the catalogue does not use one
      /// where Skip would do: at most one per tour, and never the last step,
      /// where ending the tour and finishing it are the same thing anyway.
      /// </summary>
      [Fact]
      public void AStepThatEndsTheTourIsUsedSparinglyAndNeverLast()
      {
         foreach (Tour tour in TourCatalog.All)
         {
            Assert.True(tour.Steps.Count(s => s.WhenMissing == WhenMissing.EndTour) <= 1,
               tour.Id + " has more than one step that ends the tour when it is missing");
            Assert.NotEqual(WhenMissing.EndTour, tour.Steps[tour.Steps.Count - 1].WhenMissing);
         }
      }

      [Fact]
      public void APagesHelpButtonOffersTheTourThatVisitsItAtItsFirstStopThere()
      {
         Tour walk = TourCatalog.Find(TourCatalog.FirstRun);
         Assert.NotNull(walk);

         // The pages the walk visits: each offers the walk, starting at the
         // first stop on that page rather than back at the beginning.
         foreach (string page in walk.Steps.Select(s => s.Page).Where(p => p != null).Distinct())
         {
            Tour found = TourCatalog.ForPage(page, out int startAt);
            Assert.Same(walk, found);
            Assert.Equal(walk.FirstStepOn(page), startAt);
            Assert.Equal(page, walk.Steps[startAt].Page);
         }

         // A page no tour visits has no help button, and an unknown key is not
         // an exception.
         Assert.Null(TourCatalog.ForPage("logs"));
         Assert.Null(TourCatalog.ForPage("no-such-page"));
         Assert.Null(TourCatalog.ForPage(null));
         Assert.Null(TourCatalog.Find("no-such-tour"));
         Assert.Null(TourCatalog.Find(null));
      }

      [Fact]
      public void TheFirstRunWalkEndsWhereTheAdministratorWantedToBe()
      {
         Tour walk = TourCatalog.Find(TourCatalog.FirstRun);

         // The row this builds names four things in order; the walk must still
         // cover them, and must still finish on the dashboard rather than back
         // where it started.
         var pages = walk.Steps.Select(s => s.Page).ToList();
         Assert.Contains("domains", pages);
         Assert.Contains("ports", pages);
         Assert.Contains("certs", pages);
         Assert.Equal("dashboard", pages[pages.Count - 1]);
         Assert.True(pages.IndexOf("domains") < pages.IndexOf("ports"));
      }

      private static string FindControlPanel()
      {
         for (var at = new DirectoryInfo(AppContext.BaseDirectory); at != null; at = at.Parent)
         {
            string candidate = Path.Combine(at.FullName, "hmailserver", "source", "Tools", "ControlPanel");
            if (Directory.Exists(candidate))
               return candidate;
         }

         throw new DirectoryNotFoundException("the ControlPanel sources are not above " + AppContext.BaseDirectory);
      }
   }
}
