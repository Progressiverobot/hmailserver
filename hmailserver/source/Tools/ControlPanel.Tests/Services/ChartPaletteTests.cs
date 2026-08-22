// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The theme half of the chart accessibility work.
   ///
   /// What these pin down, and why they fail against the unfixed Control Panel:
   /// before this change the dashboard charts held five hardcoded SKColor
   /// constants - 0x2F81F7, 0x3FB950, 0xA371F7, 0xD29922 and a grey axis - and used
   /// them in every theme. ThemeTokens already built a High Contrast palette out of
   /// SystemColors for the status and log colours, and no chart consulted it, so
   /// turning High Contrast on left the charts painted in exactly the colours the
   /// user had just told Windows they could not read. There was no type that could
   /// answer "what colour is the SMTP line in High Contrast", which is why the
   /// first assertion below could not be written at all, let alone pass.
   ///
   /// The palette takes the system colours as an argument rather than reading
   /// SystemColors itself, so these tests can hand it sentinel values and prove
   /// that nothing but a system colour comes back out - on a build agent that is
   /// not, and must not have to be, running a High Contrast theme.
   /// </summary>
   public class ChartPaletteTests
   {
      // Deliberately meaningless, distinct values: any of them appearing in the
      // output can only have come from the system palette.
      //
      // They are also deliberately far apart in luminance. The original set ran
      // 0x010101 to 0x050505 - five shades of near-black - which was fine while the
      // palette only asked "is this candidate the same number as the background",
      // and became wrong the moment it started asking "can this candidate be seen
      // against the background": every one of them would have been dropped, the
      // High Contrast palette would have fallen back to a single colour, and the
      // tests below would have been asserting against a degenerate case rather than
      // against a plausible theme. A sentinel has to be meaningless, not unusable.
      private static readonly ChartSystemColors Sentinels = new ChartSystemColors(
         windowText: 0xFF010101,
         window: 0xFFFEFEFE,
         highlight: 0xFF3C0078,
         hotTrack: 0xFF00007F,
         grayText: 0xFF5A5A5A);

      private static readonly string[] ThreeSeries = { "SMTP", "IMAP", "POP3" };

      // The exact constants DashboardView used to paint with regardless of theme.
      private static readonly uint[] OldHardcodedPalette =
      {
         0xFF2F81F7, 0xFF3FB950, 0xFFA371F7, 0xFFD29922, 0xFF8B949E
      };

      [Fact]
      public void HighContrast_PaintsEverySeriesInASystemColour()
      {
         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(ChartTheme.HighContrast, Sentinels, ThreeSeries);

         Assert.Equal(3, styles.Count);
         foreach (ChartSeriesStyle style in styles)
         {
            Assert.True(
               style.Argb == Sentinels.WindowText ||
               style.Argb == Sentinels.Highlight ||
               style.Argb == Sentinels.HotTrack,
               style.Name + " was painted 0x" + style.Argb.ToString("X8") + ", which is not a system colour.");
         }
      }

      [Fact]
      public void HighContrast_NeverUsesTheOldHardcodedChartPalette()
      {
         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(ChartTheme.HighContrast, Sentinels, ThreeSeries);
         ChartChrome chrome = ChartPalette.ResolveChrome(ChartTheme.HighContrast, Sentinels);

         foreach (uint hardcoded in OldHardcodedPalette)
         {
            Assert.DoesNotContain(hardcoded, styles.Select(s => s.Argb));
            Assert.NotEqual(hardcoded, chrome.AxisTextArgb);
            Assert.NotEqual(hardcoded, chrome.GridLineArgb);
            Assert.NotEqual(hardcoded, chrome.TooltipTextArgb);
         }
      }

      [Fact]
      public void HighContrast_NeverPaintsALineInTheBackgroundColour()
      {
         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(ChartTheme.HighContrast, Sentinels, ThreeSeries);

         Assert.DoesNotContain(Sentinels.Window, styles.Select(s => s.Argb));
      }

      [Fact]
      public void HighContrast_DropsASystemColourThatHasCollapsedOntoTheBackground()
      {
         // A High Contrast theme is free to define Highlight as the window colour.
         // An invisible line is a worse failure than two lines sharing a colour,
         // because the dash pattern still tells those two apart.
         var collapsed = new ChartSystemColors(
            windowText: 0xFFFFFFFF,
            window: 0xFF000000,
            highlight: 0xFF000000,
            hotTrack: 0xFF8080FF,
            grayText: 0xFF00FF00);

         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(ChartTheme.HighContrast, collapsed, ThreeSeries);

         Assert.DoesNotContain(collapsed.Window, styles.Select(s => s.Argb));
         Assert.All(styles, s => Assert.True(s.Argb == collapsed.WindowText || s.Argb == collapsed.HotTrack));
      }

      [Fact]
      public void HighContrast_DropsASystemColourThatIsMerelyInvisibleRatherThanIdentical()
      {
         // These are Windows' own shipped "High Contrast #1" colours: a #800000
         // Highlight on a #000000 window. That is 1.92:1 - a dark red line on black
         // that cannot be seen at any size or magnification - and it is NOT equal to
         // the background, so the previous guard, which tested only for equality,
         // drew it. This is the case that actually occurs on a real desktop, and it
         // was invisible to every existing test because every existing test used a
         // Highlight that was either usable or literally the window colour.
         var highContrast1 = new ChartSystemColors(
            windowText: 0xFFFFFFFF,
            window: 0xFF000000,
            highlight: 0xFF800000,
            hotTrack: 0xFF8080FF,
            grayText: 0xFF00FF00);

         Assert.True(ChartPalette.ContrastRatio(0xFF800000, 0xFF000000) < ChartPalette.MinimumSeriesContrast,
            "The premise of this test is that #800000 on #000000 is below the threshold.");

         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(ChartTheme.HighContrast, highContrast1, ThreeSeries);

         Assert.DoesNotContain(0xFF800000u, styles.Select(s => s.Argb));

         // And what is left is still enough to draw three distinguishable series,
         // because the dash pattern never depended on the colour count.
         Assert.Equal(3, styles.Select(s => s.Pattern).Distinct().Count());
      }

      [Fact]
      public void EverySeriesColourClearsTheContrastThresholdInEveryShippedHighContrastTheme()
      {
         // The four themes Windows ships, as (windowText, window, highlight,
         // hotTrack, grayText). Walked rather than spot-checked because the palette's
         // whole promise in High Contrast is "whatever the theme is, you can see the
         // lines", and that promise is only worth anything if it has been evaluated
         // against the themes people actually turn on.
         var themes = new[]
         {
            ("High Contrast White", new ChartSystemColors(0xFF000000, 0xFFFFFFFF, 0xFF37006E, 0xFF00009F, 0xFF600000)),
            ("High Contrast Black", new ChartSystemColors(0xFFFFFFFF, 0xFF000000, 0xFF1AEBFF, 0xFF8080FF, 0xFF3FF23F)),
            ("High Contrast #1", new ChartSystemColors(0xFFFFFFFF, 0xFF000000, 0xFF800000, 0xFF8080FF, 0xFF00FF00)),
            ("High Contrast #2", new ChartSystemColors(0xFFFFFFFF, 0xFF000000, 0xFF008080, 0xFF8080FF, 0xFF00FF00))
         };

         foreach ((string name, ChartSystemColors system) in themes)
         {
            IReadOnlyList<ChartSeriesStyle> styles =
               ChartPalette.ResolveSeries(ChartTheme.HighContrast, system, ThreeSeries);
            ChartChrome chrome = ChartPalette.ResolveChrome(ChartTheme.HighContrast, system);

            foreach (ChartSeriesStyle style in styles)
            {
               double ratio = ChartPalette.ContrastRatio(style.Argb, system.Window);
               Assert.True(ratio >= ChartPalette.MinimumSeriesContrast,
                  name + " draws " + style.Name + " at " + ratio.ToString("F2") + ":1 against the window colour.");
            }

            // Axis labels are text, so they answer to the 4.5:1 text threshold
            // rather than the 3:1 graphical one.
            Assert.True(ChartPalette.ContrastRatio(chrome.AxisTextArgb, system.Window) >= 4.5,
               name + " draws its axis labels below 4.5:1.");
            Assert.True(ChartPalette.ContrastRatio(chrome.TooltipTextArgb, chrome.TooltipBackgroundArgb) >= 4.5,
               name + " draws its tooltip text below 4.5:1.");
         }
      }

      [Fact]
      public void ContrastRatio_MatchesTheWcagReferenceValues()
      {
         // Black on white is the definition of 21:1 and a colour against itself is
         // 1:1. Without these two the threshold above could be enforcing anything.
         Assert.Equal(21.0, ChartPalette.ContrastRatio(0xFF000000, 0xFFFFFFFF), 2);
         Assert.Equal(1.0, ChartPalette.ContrastRatio(0xFF2F6FE0, 0xFF2F6FE0), 6);

         // Symmetric, and blind to alpha - the palette only ever produces opaque
         // colours, and a translucent one has no contrast until you know what is
         // behind it.
         Assert.Equal(ChartPalette.ContrastRatio(0xFF000000, 0xFFFFFFFF),
                      ChartPalette.ContrastRatio(0xFFFFFFFF, 0xFF000000), 6);
         Assert.Equal(ChartPalette.ContrastRatio(0xFF1A7F37, 0xFFFFFFFF),
                      ChartPalette.ContrastRatio(0x001A7F37, 0x00FFFFFF), 6);
      }

      [Theory]
      [InlineData(ChartTheme.Light, 0xFFFFFFFFu)]
      [InlineData(ChartTheme.Dark, 0xFF1B1B1Bu)]
      public void TheOrdinaryPalettesClearTheTextThresholdOnTheSurfaceTheyClaimToBeTunedFor(
         ChartTheme theme, uint surface)
      {
         // ChartPalette's own comment claims "each of these clears 4.5:1 against a
         // white surface; each of the dark set clears it against #1B1B1B", and
         // nothing checked it. A claim in a comment is worth exactly as much as the
         // test behind it: this is what stops the next person who nudges a hue for
         // aesthetic reasons from quietly dropping a series below the line.
         //
         // 4.5:1 rather than the 3:1 graphical minimum on purpose. These same five
         // hues are the status token palette in ThemeTokens, where they are used for
         // small text on badges and log lines, so the stricter of the two thresholds
         // is the one that has to hold.
         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(theme, Sentinels, new[] { "A", "B", "C", "D", "E" });
         ChartChrome chrome = ChartPalette.ResolveChrome(theme, Sentinels);

         foreach (ChartSeriesStyle style in styles)
         {
            double ratio = ChartPalette.ContrastRatio(style.Argb, surface);
            Assert.True(ratio >= 4.5,
               theme + " series " + style.Name + " is " + ratio.ToString("F2") + ":1 on 0x" + surface.ToString("X8"));
         }

         Assert.True(ChartPalette.ContrastRatio(chrome.AxisTextArgb, surface) >= 4.5,
            theme + " axis labels are below 4.5:1 - this is the defect the roadmap recorded as "
            + "\"the axis grey failed the contrast ratio on the light theme\".");
         Assert.True(ChartPalette.ContrastRatio(chrome.PlaceholderTextArgb, surface) >= 4.5,
            theme + " empty-state text is below 4.5:1.");
         Assert.True(ChartPalette.ContrastRatio(chrome.TooltipTextArgb, chrome.TooltipBackgroundArgb) >= 4.5,
            theme + " tooltip text is below 4.5:1.");
      }

      [Fact]
      public void HighContrast_HasNoTranslucentFillAndNoInventedCurve()
      {
         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(ChartTheme.HighContrast, Sentinels, ThreeSeries);
         ChartChrome chrome = ChartPalette.ResolveChrome(ChartTheme.HighContrast, Sentinels);

         Assert.All(styles, s => Assert.Equal(0, (int) s.AreaAlpha));
         Assert.Equal(0d, chrome.LineSmoothness);
      }

      [Theory]
      [InlineData(ChartTheme.Light)]
      [InlineData(ChartTheme.Dark)]
      [InlineData(ChartTheme.HighContrast)]
      public void NoThemeSplinesMonitoringData(ChartTheme theme)
      {
         // Asserted for every theme deliberately. The only existing assertion was on
         // High Contrast, and Light and Dark were both still 0.8 - so the one theme
         // nobody runs was correct and the two everybody runs invented values between
         // samples and rounded the peaks off, which on a queue-depth or session-count
         // chart removes exactly the events being watched for.
         Assert.Equal(0d, ChartPalette.ResolveChrome(theme, Sentinels).LineSmoothness);
      }

      [Theory]
      [InlineData(ChartTheme.Light)]
      [InlineData(ChartTheme.Dark)]
      public void OrdinaryPalette_FillsOnlyASingleSeriesChart(ChartTheme theme)
      {
         // The area fill is there to make a one-line sparkline readable. Keyed on the
         // first series instead of on the series count, it put a translucent wash under
         // SMTP on the three-series Active sessions chart, across the IMAP and POP3
         // lines, on a default install in both ordinary themes - which is why this is
         // asserted for Light and Dark rather than only for High Contrast.
         IReadOnlyList<ChartSeriesStyle> many = ChartPalette.ResolveSeries(theme, Sentinels, ThreeSeries);

         Assert.All(many, s => Assert.Equal(0, (int) s.AreaAlpha));

         IReadOnlyList<ChartSeriesStyle> one =
            ChartPalette.ResolveSeries(theme, Sentinels, new[] {"Messages"});

         Assert.Single(one);
         Assert.True(one[0].AreaAlpha > 0,
            "A single-series chart is the one case the fill is for, so it must keep it.");
      }

      [Fact]
      public void HighContrast_TakesTheSurfaceWithTheForeground()
      {
         // Taking the system foreground colours without the system background is
         // how a chart ends up black on black: WPF-UI keeps painting its own dark
         // card underneath unless it is told otherwise.
         ChartChrome chrome = ChartPalette.ResolveChrome(ChartTheme.HighContrast, Sentinels);

         Assert.Equal(Sentinels.Window, chrome.SurfaceArgb);
         Assert.Equal(Sentinels.WindowText, chrome.SurfaceTextArgb);
         Assert.NotEqual(chrome.SurfaceArgb, chrome.SurfaceTextArgb);
      }

      [Theory]
      [InlineData(ChartTheme.Light)]
      [InlineData(ChartTheme.Dark)]
      public void OrdinaryThemes_DoNotOwnTheSurfaceOrUseSystemColours(ChartTheme theme)
      {
         IReadOnlyList<ChartSeriesStyle> styles = ChartPalette.ResolveSeries(theme, Sentinels, ThreeSeries);
         ChartChrome chrome = ChartPalette.ResolveChrome(theme, Sentinels);

         Assert.Null(chrome.SurfaceArgb);
         Assert.Null(chrome.SurfaceTextArgb);
         Assert.Null(chrome.SurfaceBorderArgb);

         uint[] system =
         {
            Sentinels.WindowText, Sentinels.Window, Sentinels.Highlight,
            Sentinels.HotTrack, Sentinels.GrayText
         };
         foreach (uint color in system)
            Assert.DoesNotContain(color, styles.Select(s => s.Argb));
      }

      [Theory]
      [InlineData(ChartTheme.Light)]
      [InlineData(ChartTheme.Dark)]
      [InlineData(ChartTheme.HighContrast)]
      public void EverySeriesIsIdentifiableWithoutItsColour(ChartTheme theme)
      {
         // Five series is more than any chart in the Control Panel has, and it is
         // the point at which the pattern and marker lists have been used up.
         string[] names = { "A", "B", "C", "D", "E" };

         IReadOnlyList<ChartSeriesStyle> styles = ChartPalette.ResolveSeries(theme, Sentinels, names);

         Assert.Equal(names.Length, styles.Select(s => s.Pattern).Distinct().Count());
         Assert.Equal(names.Length, styles.Select(s => s.Marker).Distinct().Count());
      }

      [Fact]
      public void HighContrast_KeepsSeriesApartAfterTheThreeColoursRepeat()
      {
         // Three system colours against five dash patterns: the pair stays unique
         // well past the point where the colours alone have started to repeat.
         // Deriving the pattern from the colour index instead would give the first
         // three series a solid line each and leave them indistinguishable.
         string[] names = { "A", "B", "C", "D", "E", "F", "G", "H" };

         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(ChartTheme.HighContrast, Sentinels, names);

         var pairs = styles.Select(s => s.Argb + "/" + s.Pattern).ToList();
         Assert.Equal(names.Length, pairs.Distinct().Count());
      }

      [Fact]
      public void LightAndDarkAreDifferentPalettes()
      {
         IReadOnlyList<ChartSeriesStyle> light = ChartPalette.ResolveSeries(ChartTheme.Light, Sentinels, ThreeSeries);
         IReadOnlyList<ChartSeriesStyle> dark = ChartPalette.ResolveSeries(ChartTheme.Dark, Sentinels, ThreeSeries);

         for (int i = 0; i < ThreeSeries.Length; i++)
            Assert.NotEqual(light[i].Argb, dark[i].Argb);
      }

      [Fact]
      public void ResolveSeries_ToleratesNoSeriesAndAnUnnamedSeries()
      {
         Assert.Empty(ChartPalette.ResolveSeries(ChartTheme.Dark, Sentinels, null));
         Assert.Empty(ChartPalette.ResolveSeries(ChartTheme.Dark, Sentinels, new string[0]));

         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(ChartTheme.Dark, Sentinels, new string[] { null });
         Assert.Equal("", styles[0].Name);
      }

      [Fact]
      public void DashIntervals_AreFreshArraysAndSolidHasNone()
      {
         // Skia's dash effect and WPF's StrokeDashArray both take the array by
         // reference and one of them rescales it, so a shared static array would
         // let the legend swatch corrupt the chart's own pattern.
         Assert.Null(ChartPalette.DashIntervalsFor(ChartLinePattern.Solid));

         float[] first = ChartPalette.DashIntervalsFor(ChartLinePattern.Dash);
         float[] second = ChartPalette.DashIntervalsFor(ChartLinePattern.Dash);

         Assert.NotNull(first);
         Assert.Equal(first, second);
         Assert.NotSame(first, second);
      }

      [Fact]
      public void EveryPatternAndMarkerCanBeSaidInWords()
      {
         // The legend's accessible name is built from these. An added enum value
         // with no description would leave a screen reader saying "SMTP,  line
         // with a  marker".
         foreach (ChartLinePattern pattern in Enum.GetValues<ChartLinePattern>())
            Assert.False(string.IsNullOrWhiteSpace(ChartPalette.DescribePattern(pattern)));

         foreach (ShapeMark mark in Enum.GetValues<ShapeMark>())
            Assert.False(string.IsNullOrWhiteSpace(ChartPalette.DescribeMarker(mark)));

         Assert.Equal(Enum.GetValues<ChartLinePattern>().Length,
            Enum.GetValues<ChartLinePattern>().Select(ChartPalette.DescribePattern).Distinct().Count());
         Assert.Equal(Enum.GetValues<ShapeMark>().Length,
            Enum.GetValues<ShapeMark>().Select(ChartPalette.DescribeMarker).Distinct().Count());
      }
      // ---- Flatten: the bug that shipped ------------------------------------

      /// <summary>
      /// The chart cards rendered PURE WHITE in the dark theme, and this is the
      /// arithmetic that stops it happening again.
      ///
      /// A Fluent control fill is an overlay, not a tinted colour. Dark theme's
      /// ControlFillColorDefaultBrush is #0FFFFFFF - white at six percent - so its
      /// entire dark appearance lives in the alpha channel. The old code needed an
      /// opaque colour and took the RGB, which is white. Two large cards on the
      /// busiest page in the application went from near-black to white, keeping
      /// axis labels painted for a dark surface.
      ///
      /// The numbers below are measured from the real window, not invented: the page
      /// backdrop is 32,32,32 and a healthy card - one whose alpha nothing touched -
      /// is 45,45,45. Compositing has to reproduce that exactly, or the fixed card
      /// still will not match the ones beside it.
      /// </summary>
      [Fact]
      public void Flatten_TurnsTheDarkThemeCardFillIntoTheColourItLooksLike()
      {
         const uint controlFillDark = 0x0FFFFFFF;   // white at 6%
         const uint pageBackdrop = 0xFF202020;      // 32,32,32

         uint result = ChartPalette.Flatten(controlFillDark, pageBackdrop);

         Assert.Equal(0xFFu, (result >> 24) & 0xFF);

         // 45,45,45 - the same colour as every other card on that page.
         Assert.Equal(45u, (result >> 16) & 0xFF);
         Assert.Equal(45u, (result >> 8) & 0xFF);
         Assert.Equal(45u, result & 0xFF);
      }

      /// <summary>
      /// The negative control. This is the exact assertion the shipped code failed:
      /// whatever else Flatten does, a nearly-transparent white must never come out
      /// anywhere near white when the backdrop is dark.
      /// </summary>
      [Fact]
      public void Flatten_NeverInvertsATranslucentOverlayOnADarkBackdrop()
      {
         uint result = ChartPalette.Flatten(0x0FFFFFFF, 0xFF202020);

         uint red = (result >> 16) & 0xFF;

         Assert.True(red < 96,
            "a six-percent white over a dark backdrop must stay dark, but came out at " + red);
      }

      [Fact]
      public void Flatten_LeavesAnOpaqueOverlayAlone()
      {
         const uint opaque = 0xFF123456;

         Assert.Equal(opaque, ChartPalette.Flatten(opaque, 0xFF202020));
      }

      [Fact]
      public void Flatten_OnAFullyTransparentOverlayIsTheBackdrop()
      {
         uint result = ChartPalette.Flatten(0x00FFFFFF, 0xFF202020);

         Assert.Equal(0xFF202020u, result);
      }

      /// <summary>
      /// The light theme has the same shape with the sign reversed - a translucent
      /// BLACK overlay on a near-white page - and dropping the alpha there would
      /// have produced pure black. Nobody hit it because the app was being run in
      /// dark mode, which is exactly why it is pinned here.
      /// </summary>
      [Fact]
      public void Flatten_HandlesTheLightThemeShapeToo()
      {
         uint result = ChartPalette.Flatten(0x0F000000, 0xFFFAFAFA);

         uint red = (result >> 16) & 0xFF;

         Assert.True(red > 200,
            "a six-percent black over a near-white backdrop must stay light, but came out at " + red);
      }
   }
}
