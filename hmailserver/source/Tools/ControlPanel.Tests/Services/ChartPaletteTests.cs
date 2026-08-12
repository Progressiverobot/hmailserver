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
      private static readonly ChartSystemColors Sentinels = new ChartSystemColors(
         windowText: 0xFF010101,
         window: 0xFF020202,
         highlight: 0xFF030303,
         hotTrack: 0xFF040404,
         grayText: 0xFF050505);

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
            windowText: 0xFF010101,
            window: 0xFF030303,
            highlight: 0xFF030303,
            hotTrack: 0xFF040404,
            grayText: 0xFF050505);

         IReadOnlyList<ChartSeriesStyle> styles =
            ChartPalette.ResolveSeries(ChartTheme.HighContrast, collapsed, ThreeSeries);

         Assert.DoesNotContain(collapsed.Window, styles.Select(s => s.Argb));
         Assert.All(styles, s => Assert.True(s.Argb == collapsed.WindowText || s.Argb == collapsed.HotTrack));
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
   }
}
