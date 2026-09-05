// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The status colours as seen with the two common dichromacies.
   ///
   /// A status is never carried by colour alone (StatusSemantics gives every
   /// level a shape and a word), but colour is what a glance reads, and the
   /// roadmap once measured Success and Warning at ΔE 5.1 under protanopia
   /// against a target of 8. The palette has moved since; this pins where it is,
   /// so it cannot drift back without a test saying so. StatusPalette is the one
   /// place the colours live; ThemeTokens and ChartPalette both read it.
   /// </summary>
   public class ColourVisionTests
   {
      /// <summary>The floor every pair is held to, in both themes, under both simulations and typical vision.</summary>
      private const double Floor = 8.0;

      public static IEnumerable<object[]> StatusPairs()
      {
         foreach (bool light in new[] { true, false })
         {
            (uint success, uint warning, uint danger) = StatusPalette.Argb(light);
            string theme = light ? "light" : "dark";
            yield return new object[] { theme + " success/warning", success, warning };
            yield return new object[] { theme + " success/danger", success, danger };
            yield return new object[] { theme + " warning/danger", warning, danger };
         }
      }

      [Theory]
      [MemberData(nameof(StatusPairs))]
      public void EveryStatusPairStaysApartUnderProtanopiaDeuteranopiaAndTypicalVision(string pair, uint a, uint b)
      {
         foreach (ColourVision.Vision vision in new[] { ColourVision.Vision.Protanopia, ColourVision.Vision.Deuteranopia, ColourVision.Vision.Typical })
         {
            double deltaE = ColourVision.DeltaE(a, b, vision);
            Assert.True(deltaE >= Floor,
               $"{pair} under {vision}: ΔE {deltaE:F1} is below the floor of {Floor}. " +
               "Pick a hue pair a red-green dichromat can still tell apart; the shape and word carry the status meanwhile.");
         }
      }

      [Fact]
      public void IdenticalColoursAreZeroApartAndTheSimulationIsAlphaPreserving()
      {
         Assert.Equal(0.0, ColourVision.DeltaE(0xFF1A7F37, 0xFF1A7F37, ColourVision.Vision.Protanopia), 6);
         Assert.Equal(0x80u, ColourVision.Simulate(0x80FF0000, ColourVision.Vision.Deuteranopia) >> 24);
      }

      [Fact]
      public void ProtanopiaCollapsesRedTowardsTheGreenAxisAsTheModelPredicts()
      {
         // A protanope sees pure red as a dim yellowish-brown: the simulated red
         // channel must fall well below the original and the green channel rise.
         uint red = ColourVision.Simulate(0xFFFF0000, ColourVision.Vision.Protanopia);
         uint r = (red >> 16) & 0xFF, g = (red >> 8) & 0xFF;
         Assert.True(r < 0xA0, "red channel should fall: " + r);
         Assert.True(g > 0x40, "green channel should rise: " + g);

         // And the distance between pure red and pure green shrinks dramatically
         // for a protanope - the whole reason this class exists.
         double typical = ColourVision.DeltaE(0xFFFF0000, 0xFF00FF00, ColourVision.Vision.Typical);
         double protan = ColourVision.DeltaE(0xFFFF0000, 0xFF00FF00, ColourVision.Vision.Protanopia);
         Assert.True(protan < typical / 2, $"typical {typical:F1}, protanopia {protan:F1}");
      }
   }
}
