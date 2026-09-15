// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// A tabbed dialog on the frame is sized to its tallest tab. The three cases
   /// the arithmetic has to get right: a tall tab is given its height, a set of
   /// short tabs is still given a dialog's worth, and no tab may push the footer
   /// off a small screen.
   /// </summary>
   public class DialogLayoutTests
   {
      [Fact]
      public void TheTallestTabDecidesTheHeightWhenItFits()
      {
         double maxWindow = DialogLayout.MaxWindowHeight(1040);   // 936
         Assert.Equal(500, DialogLayout.TabControlHeight(500, maxWindow));
      }

      [Fact]
      public void ShortTabsStillGetTheFloor()
      {
         double maxWindow = DialogLayout.MaxWindowHeight(1040);
         Assert.Equal(DialogLayout.MinTabControlHeight, DialogLayout.TabControlHeight(90, maxWindow));
      }

      [Fact]
      public void ATabTallerThanTheScreenIsCappedSoTheFooterStaysOnIt()
      {
         double maxWindow = DialogLayout.MaxWindowHeight(720);   // 648
         double height = DialogLayout.TabControlHeight(1200, maxWindow);
         Assert.Equal(maxWindow - DialogLayout.FrameChrome, height);
         Assert.True(height + DialogLayout.FrameChrome <= maxWindow);
      }

      [Fact]
      public void ATinyWorkAreaNeverProducesLessThanTheFloor()
      {
         double maxWindow = DialogLayout.MaxWindowHeight(200);
         Assert.Equal(DialogLayout.MinTabControlHeight, DialogLayout.TabControlHeight(1200, maxWindow));
      }

      [Fact]
      public void TheWindowCapIsTheDialogTokenFractionOfTheWorkArea()
      {
         Assert.Equal(1000 * DesignTokens.Dialog.MaxHeightFraction, DialogLayout.MaxWindowHeight(1000));
      }

      /// <summary>
      /// The two big editors do not measure their twelve tabs - every one holds a
      /// whole embedded view and the tallest exceeds the cap anyway - so they ask
      /// for the ceiling by naming a height nothing can reach. That has to come
      /// back as a real number, not an infinity the layout would choke on.
      /// </summary>
      [Fact]
      public void AskingForMoreThanAnyScreenGivesTheCeiling()
      {
         double maxWindow = DialogLayout.MaxWindowHeight(1040);
         double ceiling = DialogLayout.TabControlHeight(double.PositiveInfinity, maxWindow);

         Assert.Equal(maxWindow - DialogLayout.FrameChrome, ceiling);
         Assert.True(double.IsFinite(ceiling));
         Assert.Equal(ceiling, DialogLayout.TabControlHeight(1_000_000, maxWindow));
      }
   }
}
