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
   }
}
