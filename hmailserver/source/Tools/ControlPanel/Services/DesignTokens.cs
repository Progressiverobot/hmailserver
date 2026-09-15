// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The Control Panel's design tokens as numbers: the spacing scale, the
   /// corner radii, the shell's fixed dimensions, and the colours that are not
   /// the three status colours (<see cref="StatusPalette"/> keeps those, with the
   /// test that holds them apart for colour-blind eyes).
   ///
   /// This is the one place the values live. Views/Scaffold/Tokens.xaml declares
   /// the same values as resources for XAML, and <see cref="ThemeTokens"/> turns
   /// the colours into brushes on every theme change; the doc that names every
   /// token is docs/ControlPanelDesign.md. Being plain numbers, the values can be
   /// held by a test without WPF - that the scale is monotonic and on a
   /// four-pixel grid, that High Contrast derives every colour from the system
   /// palette and invents none, that the information colour stays apart from the
   /// three status colours under the two common dichromacies.
   /// </summary>
   public static class DesignTokens
   {
      /// <summary>
      /// The spacing scale, in device-independent pixels. Every margin, padding
      /// and gap in the scaffold is one of these six; a page that needs a seventh
      /// is a page whose layout has gone wrong.
      /// </summary>
      public static class Space
      {
         public const double Xs = 4;
         public const double Sm = 8;
         public const double Md = 12;
         public const double Lg = 16;
         public const double Xl = 24;
         public const double Xxl = 32;

         /// <summary>The whole scale, ascending. Only used by the tests today.</summary>
         public static IReadOnlyList<double> All { get; } = new[] { Xs, Sm, Md, Lg, Xl, Xxl };
      }

      /// <summary>Corner radii. Controls and rows take the small one, cards and dialogs the large one, a pill is a capsule.</summary>
      public static class Radius
      {
         public const double Control = 4;
         public const double Card = 8;
         public const double Pill = 999;
      }

      /// <summary>The shell's fixed dimensions.</summary>
      public static class Shell
      {
         /// <summary>The command bar under the title bar.</summary>
         public const double CommandBarHeight = 48;

         /// <summary>The sidebar when it is collapsed to icons: one group glyph per row.</summary>
         public const double RailWidth = 48;

         /// <summary>Below this window width the sidebar collapses to the rail on its own; above it the user's choice stands.</summary>
         public const double CollapseSidebarBelow = 1000;

         /// <summary>The sidebar's expanded width limits - sized by the longest group name, not by taste (see MainWindow.xaml).</summary>
         public const double SidebarMinWidth = 200;
         public const double SidebarMaxWidth = 320;

         /// <summary>One navigation row, expanded or in the rail.</summary>
         public const double NavRowHeight = 34;
      }

      /// <summary>
      /// Dialog sizing. A dialog is sized to its content within these bounds
      /// instead of to a number typed into each one; the height is capped at a
      /// fraction of the screen's work area and the body scrolls past it.
      /// </summary>
      public static class Dialog
      {
         public const double MinWidth = 360;
         public const double DefaultWidth = 520;
         public const double MaxWidth = 900;
         public const double MaxHeightFraction = 0.9;
      }

      /// <summary>
      /// The tint behind an inline notice: the notice's status colour at this
      /// opacity. Zero under High Contrast, where a tinted surface is a colour
      /// the user asked Windows not to show them and the border carries the
      /// notice instead.
      /// </summary>
      public const double NoticeTintOpacity = 0.12;

      public static double NoticeTintOpacityFor(ChartTheme theme)
         => theme == ChartTheme.HighContrast ? 0 : NoticeTintOpacity;

      /// <summary>The status palette for one theme, as 0xAARRGGBB.</summary>
      public readonly struct StatusArgb
      {
         public StatusArgb(uint brand, uint success, uint warning, uint danger, uint info, uint neutral)
         {
            Brand = brand;
            Success = success;
            Warning = warning;
            Danger = danger;
            Info = info;
            Neutral = neutral;
         }

         public uint Brand { get; }
         public uint Success { get; }
         public uint Warning { get; }
         public uint Danger { get; }
         public uint Info { get; }

         /// <summary>A status that says nothing - "not connected", a count of zero. Secondary text, never a colour of its own.</summary>
         public uint Neutral { get; }
      }

      /// <summary>The surfaces for one theme, as 0xAARRGGBB. The translucent ones sit on Mica and on a solid page alike.</summary>
      public readonly struct SurfaceArgb
      {
         public SurfaceArgb(uint cardBackground, uint cardBorder, uint cardHover, uint divider)
         {
            CardBackground = cardBackground;
            CardBorder = cardBorder;
            CardHover = cardHover;
            Divider = divider;
         }

         public uint CardBackground { get; }
         public uint CardBorder { get; }

         /// <summary>A card that can be pointed at - a tile, a rail item - under the pointer.</summary>
         public uint CardHover { get; }

         /// <summary>The hairline between a card's content and its footer, and between the shell's regions.</summary>
         public uint Divider { get; }
      }

      // The light values are darker and saturated so that each clears 4.5:1 on a
      // white surface; the dark values are lighter and brighter for dark surfaces.
      // The three status colours come from StatusPalette; these are the rest.
      private const uint LightBrand = 0xFF2F6FE0;
      private const uint LightInfo = 0xFF6639BA;
      private const uint LightNeutral = 0xFF57606A;
      private const uint DarkBrand = 0xFF4C8DFF;
      private const uint DarkInfo = 0xFFA371F7;
      private const uint DarkNeutral = 0xFF9DA7B0;

      /// <summary>
      /// The status palette for a theme. High Contrast takes every colour from
      /// the system palette and invents none: the brand and information colours
      /// are the highlight, the three status colours are the window text (their
      /// shape and word carry the difference - <see cref="StatusSemantics"/>), and
      /// neutral is the disabled-text grey.
      /// </summary>
      public static StatusArgb Status(ChartTheme theme, ChartSystemColors system)
      {
         switch (theme)
         {
            case ChartTheme.HighContrast:
               return new StatusArgb(system.Highlight, system.WindowText, system.WindowText, system.WindowText,
                  system.Highlight, system.GrayText);

            case ChartTheme.Light:
               {
                  (uint success, uint warning, uint danger) = StatusPalette.Argb(light: true);
                  return new StatusArgb(LightBrand, success, warning, danger, LightInfo, LightNeutral);
               }

            default:
               {
                  (uint success, uint warning, uint danger) = StatusPalette.Argb(light: false);
                  return new StatusArgb(DarkBrand, success, warning, danger, DarkInfo, DarkNeutral);
               }
         }
      }

      /// <summary>
      /// The surfaces for a theme. The light and dark values are the ones the
      /// Fluent control-fill and stroke layers use, so a card matches the
      /// controls on it; High Contrast paints the window colour with a window-text
      /// outline, which is the only card that theme allows.
      /// </summary>
      public static SurfaceArgb Surfaces(ChartTheme theme, ChartSystemColors system)
      {
         switch (theme)
         {
            case ChartTheme.HighContrast:
               return new SurfaceArgb(system.Window, system.WindowText, system.Window, system.WindowText);

            case ChartTheme.Light:
               return new SurfaceArgb(0xB3FFFFFF, 0x0F000000, 0x80F9F9F9, 0x0F000000);

            default:
               return new SurfaceArgb(0x0FFFFFFF, 0x14FFFFFF, 0x15FFFFFF, 0x14FFFFFF);
         }
      }
   }
}
