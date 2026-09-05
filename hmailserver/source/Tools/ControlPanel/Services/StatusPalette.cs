// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The three status colours, per theme, as ARGB - the one place they live.
   /// <see cref="ThemeTokens"/> turns them into brushes and <see cref="ChartPalette"/>
   /// paints series with them, so a green line and a green "success" label are
   /// the same green; and being plain integers they can be measured by a test
   /// that has no WPF (<see cref="ColourVision"/>).
   ///
   /// Every pair - success/warning, success/danger, warning/danger - is held at
   /// least ΔE 8 apart under simulated protanopia and deuteranopia as well as
   /// typical vision. The status shape and word carry the meaning regardless
   /// (<see cref="StatusSemantics"/>), but the colour is what a glance reads.
   /// The light set clears 4.5:1 against a white surface; the dark set against
   /// #1B1B1B.
   /// </summary>
   public static class StatusPalette
   {
      public const uint LightSuccess = 0xFF1A7F37;
      public const uint LightWarning = 0xFF9A6700;
      public const uint LightDanger = 0xFFCF222E;

      public const uint DarkSuccess = 0xFF3FB950;
      public const uint DarkWarning = 0xFFD29922;
      public const uint DarkDanger = 0xFFF85149;

      /// <summary>The (success, warning, danger) triple for the light or the dark theme.</summary>
      public static (uint success, uint warning, uint danger) Argb(bool light)
      {
         return light
            ? (LightSuccess, LightWarning, LightDanger)
            : (DarkSuccess, DarkWarning, DarkDanger);
      }
   }
}
