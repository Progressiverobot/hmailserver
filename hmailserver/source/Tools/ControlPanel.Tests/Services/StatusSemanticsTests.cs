// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The "no information conveyed by colour alone" half of the accessibility work.
   ///
   /// Why these fail against the unfixed Control Panel: the dashboard's only piece
   /// of judgement - "this queue is not draining" - was expressed by setting the
   /// queue counter's foreground to the warning brush and nothing else. There was no
   /// shape, no word, and nothing in the accessible name, so the entire message was
   /// the colour. Neither the threshold nor the mapping from severity to appearance
   /// existed anywhere a test could reach: the comparison was a bare "&gt; 100"
   /// inside a refresh method and the colour was a resource key beside it.
   /// </summary>
   public class StatusSemanticsTests
   {
      [Fact]
      public void EveryLevelCarriesAShapeAWordAndABrush()
      {
         foreach (StatusLevel level in StatusSemantics.AllLevels)
         {
            StatusPresentation status = StatusSemantics.For(level);

            Assert.Equal(level, status.Level);
            Assert.False(string.IsNullOrWhiteSpace(status.SeverityWord), level + " has no severity word.");
            Assert.False(string.IsNullOrWhiteSpace(status.BrushKey), level + " has no brush key.");
            Assert.True(Enum.IsDefined(status.Shape), level + " has no shape.");
         }
      }

      [Fact]
      public void NoTwoLevelsShareAShapeOrAWord()
      {
         // This is the whole point of the shape: if two levels drew the same one,
         // the colour would be back to being the only thing that told them apart.
         Assert.Equal(StatusSemantics.AllLevels.Count,
            StatusSemantics.AllLevels.Select(l => StatusSemantics.For(l).Shape).Distinct().Count());

         Assert.Equal(StatusSemantics.AllLevels.Count,
            StatusSemantics.AllLevels.Select(l => StatusSemantics.For(l).SeverityWord).Distinct().Count());
      }

      [Fact]
      public void EveryLevelIsCoveredByAllLevels()
      {
         // Adding a level without describing it would leave it silently rendering as
         // Normal, which is the failure mode this whole class exists to prevent.
         foreach (StatusLevel level in Enum.GetValues<StatusLevel>())
            Assert.Contains(level, StatusSemantics.AllLevels);
      }

      [Fact]
      public void NormalIsInvisible()
      {
         // A default install must look exactly as it did: ordinary text colour, no
         // badge. Only Normal is allowed to use the plain text brush.
         StatusPresentation normal = StatusSemantics.For(StatusLevel.Normal);

         Assert.Equal("TextFillColorPrimaryBrush", normal.BrushKey);
         Assert.DoesNotContain("TextFillColorPrimaryBrush",
            StatusSemantics.AllLevels
               .Where(l => l != StatusLevel.Normal)
               .Select(l => StatusSemantics.For(l).BrushKey));
      }

      [Fact]
      public void AnUndefinedLevelFallsBackToNormalRatherThanThrowing()
      {
         StatusPresentation status = StatusSemantics.For((StatusLevel) 99);

         Assert.Equal(StatusLevel.Normal, status.Level);
      }

      [Theory]
      [InlineData(0, StatusLevel.Normal)]
      [InlineData(1, StatusLevel.Normal)]
      [InlineData(100, StatusLevel.Normal)]
      [InlineData(101, StatusLevel.Warning)]
      [InlineData(10000, StatusLevel.Warning)]
      public void TheQueueBacklogThresholdIsExclusive(int queueLength, StatusLevel expected)
      {
         // Exactly the behaviour the dashboard has always had - a queue sitting on
         // a hundred messages is busy, not broken - now in one place where it can be
         // read and changed.
         Assert.Equal(expected, StatusSemantics.ForQueueLength(queueLength));
      }

      [Fact]
      public void ANegativeQueueLengthIsNotABacklog()
      {
         // The queue length is derived from splitting a string the server returns,
         // so it cannot go negative today; if it ever does, the answer is not to
         // report a backlog.
         Assert.Equal(StatusLevel.Normal, StatusSemantics.ForQueueLength(-1));
      }

      [Fact]
      public void TheThresholdCanBeRaisedForABusierServer()
      {
         Assert.Equal(StatusLevel.Normal, StatusSemantics.ForQueueLength(500, 1000));
         Assert.Equal(StatusLevel.Warning, StatusSemantics.ForQueueLength(1001, 1000));
      }

      // ---- Configuration warnings on the Server status page ------------------
      //
      // Why these could not be written before: the Server status page had its own
      // private severity scale ("Critical" / "High" / "Medium" / "Info") feeding a
      // private switch that returned a hardcoded SolidColorBrush, inside a private
      // method of a WPF view. There was no type anywhere that could answer "what
      // level is a High warning" or "what happens to a severity nobody recognises",
      // which is why the whole of that behaviour went unexamined - including the
      // two badges whose text failed WCAG AA.

      [Fact]
      public void EveryConfigurationWarningSeverityIsRecognised()
      {
         foreach (string severity in StatusSemantics.ConfigurationWarningSeverities)
         {
            StatusLevel level = StatusSemantics.ForConfigurationWarning(severity);

            Assert.NotEqual(StatusLevel.Normal, level);
            Assert.NotEqual(StatusLevel.Good, level);
         }
      }

      [Theory]
      [InlineData("Critical", StatusLevel.Critical)]
      [InlineData("High", StatusLevel.Warning)]
      [InlineData("Medium", StatusLevel.Information)]
      [InlineData("Info", StatusLevel.Information)]
      public void TheConfigurationWarningScaleMapsOntoTheOneStatusVocabulary(string severity, StatusLevel expected)
      {
         Assert.Equal(expected, StatusSemantics.ForConfigurationWarning(severity));
      }

      [Theory]
      [InlineData("critical")]
      [InlineData("CRITICAL")]
      [InlineData("  Critical  ")]
      public void ASeverityWordIsMatchedWithoutRegardToCaseOrSurroundingSpace(string severity)
      {
         Assert.Equal(StatusLevel.Critical, StatusSemantics.ForConfigurationWarning(severity));
      }

      [Theory]
      [InlineData(null)]
      [InlineData("")]
      [InlineData("   ")]
      [InlineData("Sever")]
      [InlineData("urgent")]
      public void AnUnrecognisedSeverityIsAWarningRatherThanTheCalmestBadgeOnThePage(string severity)
      {
         // The behaviour being replaced: the colour table's default arm returned the
         // steel-blue "Info" brush, so a severity word that matched nothing rendered
         // as the least alarming thing on the page and nothing said so. Normal is
         // worse still - it draws no badge at all, so the warning would be a
         // sentence of ordinary body text.
         StatusLevel level = StatusSemantics.ForConfigurationWarning(severity);

         Assert.Equal(StatusLevel.Warning, level);
         Assert.NotEqual("TextFillColorPrimaryBrush", StatusSemantics.For(level).BrushKey);
      }

      [Fact]
      public void EveryConfigurationWarningIsDrawnWithAShapeAsWellAsAColour()
      {
         // The badge used to be a filled pill whose entire non-textual content was
         // its colour. A shape is what survives High Contrast squashing four colours
         // onto two, and it is the channel the dashboard's backlog badge already
         // uses - so a reader who learns the cross means Critical there sees the
         // same cross here.
         foreach (string severity in StatusSemantics.ConfigurationWarningSeverities)
         {
            StatusPresentation status = StatusSemantics.For(StatusSemantics.ForConfigurationWarning(severity));

            Assert.True(Enum.IsDefined(status.Shape), severity + " has no shape.");
            Assert.StartsWith("App", status.BrushKey);
         }
      }
   }
}
