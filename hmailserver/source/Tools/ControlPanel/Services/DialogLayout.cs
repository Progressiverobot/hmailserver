// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The arithmetic behind a tabbed dialog on the standard frame. The frame
   /// sizes a dialog to its content and caps it at a fraction of the work area;
   /// a tab control inside it is sized to its tallest tab so the window does not
   /// change height with every tab, and that height has a floor (a dialog whose
   /// tabs are all short still needs room to be a dialog) and a ceiling (the
   /// window's cap less what the frame draws around the tabs, past which the
   /// tallest tab scrolls inside its own viewer). Plain numbers, so a test can
   /// hold the three cases.
   /// </summary>
   public static class DialogLayout
   {
      /// <summary>
      /// What the frame draws around a tab control: the title bar, the frame's
      /// inset above and below, the heading and its gap, the footer and its gap.
      /// Generous rather than exact, because the ceiling only has to keep the
      /// footer on screen.
      /// </summary>
      public const double FrameChrome = 200;

      /// <summary>The least a tab control is given, strip included.</summary>
      public const double MinTabControlHeight = 240;

      /// <summary>The window height the frame allows on a work area of this height (<see cref="DesignTokens.Dialog.MaxHeightFraction"/>).</summary>
      public static double MaxWindowHeight(double workAreaHeight)
         => Math.Max(MinTabControlHeight, workAreaHeight * DesignTokens.Dialog.MaxHeightFraction);

      /// <summary>The height a tab control takes: its tallest tab, no less than the floor, no more than the window allows.</summary>
      public static double TabControlHeight(double tallestTab, double maxWindowHeight)
      {
         double ceiling = Math.Max(MinTabControlHeight, maxWindowHeight - FrameChrome);
         return Math.Min(Math.Max(tallestTab, MinTabControlHeight), ceiling);
      }
   }
}
