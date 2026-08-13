using System.Collections.Generic;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>Which of the three palettes a chart must paint itself with.</summary>
   public enum ChartTheme
   {
      Light,
      Dark,
      HighContrast
   }

   /// <summary>
   /// The stroke pattern that identifies a series <em>without using colour</em>.
   ///
   /// This is not decoration. Windows High Contrast gives us three usable
   /// foreground colours at most (window text, highlight, hot-track), so a
   /// three-series chart in High Contrast can easily end up with two lines the
   /// same colour - and a colour-blind reader on the ordinary palette has the
   /// same problem with the green/amber pair. The dash pattern is the channel
   /// that always survives: it is present in the chart, repeated in the legend
   /// swatch, and spelled out in words in the legend's accessible name.
   /// </summary>
   public enum ChartLinePattern
   {
      Solid,
      Dash,
      Dot,
      DashDot,
      LongDash
   }

   /// <summary>
   /// A shape vocabulary shared by the chart legend swatches and the KPI status
   /// badges. Both need "the same information as the colour, but as a shape", and
   /// having one list means a reader who learns that a triangle is a warning on
   /// the dashboard sees the same triangle in a chart legend.
   /// </summary>
   public enum ShapeMark
   {
      Circle,
      Square,
      Triangle,
      Diamond,
      Cross
   }

   /// <summary>
   /// The handful of Windows system colours a High Contrast palette is built
   /// from, as 0xAARRGGBB.
   ///
   /// The WPF layer reads these from <c>System.Windows.SystemColors</c> and hands
   /// them in. Keeping them as plain numbers is what lets the palette - the part
   /// with the actual decisions in it - be unit tested without a WPF dispatcher,
   /// and it is why the tests can feed in sentinel values and prove that nothing
   /// but a system colour comes back out.
   /// </summary>
   public readonly struct ChartSystemColors
   {
      public ChartSystemColors(uint windowText, uint window, uint highlight, uint hotTrack, uint grayText)
      {
         WindowText = windowText;
         Window = window;
         Highlight = highlight;
         HotTrack = hotTrack;
         GrayText = grayText;
      }

      /// <summary>The theme's foreground; always readable on <see cref="Window"/>.</summary>
      public uint WindowText { get; }

      /// <summary>The theme's surface colour. Never valid as a line colour.</summary>
      public uint Window { get; }

      public uint Highlight { get; }

      public uint HotTrack { get; }

      /// <summary>Disabled/secondary text. Used for grid lines only.</summary>
      public uint GrayText { get; }

      /// <summary>
      /// The classic Windows "High Contrast White" values, used when there is no
      /// live WPF application to read <c>SystemColors</c> from (unit tests, and
      /// the theoretical case of a palette resolved before startup). Black on
      /// white with a blue highlight is safe in the sense that matters: every
      /// entry contrasts strongly with <see cref="Window"/>.
      /// </summary>
      public static ChartSystemColors Fallback { get; } =
         new ChartSystemColors(0xFF000000, 0xFFFFFFFF, 0xFF0078D7, 0xFF00009F, 0xFF6D6D6D);
   }

   /// <summary>How one series is drawn, in every channel that carries meaning.</summary>
   public sealed class ChartSeriesStyle
   {
      internal ChartSeriesStyle(string name, uint argb, ChartLinePattern pattern, ShapeMark marker,
                                float strokeThickness, byte areaAlpha)
      {
         Name = name;
         Argb = argb;
         Pattern = pattern;
         Marker = marker;
         StrokeThickness = strokeThickness;
         AreaAlpha = areaAlpha;
      }

      public string Name { get; }

      /// <summary>Line colour as 0xAARRGGBB.</summary>
      public uint Argb { get; }

      public ChartLinePattern Pattern { get; }

      public ShapeMark Marker { get; }

      public float StrokeThickness { get; }

      /// <summary>
      /// Alpha of the gradient area fill under the line, or 0 for no fill. High
      /// Contrast returns 0: a translucent wash over a High Contrast surface is
      /// exactly the "nearly the right colour" rendering the user turned High
      /// Contrast on to get rid of, and it drags the line's own contrast down
      /// where the two overlap.
      /// </summary>
      public byte AreaAlpha { get; }

      /// <summary>
      /// A fresh dash-interval array for <see cref="Pattern"/>, or null when the
      /// line is solid. Fresh because Skia's dash effect and WPF's
      /// <c>StrokeDashArray</c> both take ownership-ish references and one of
      /// them scales the values; sharing a static array between the chart and the
      /// legend swatch would let one of them mutate the other's pattern.
      /// </summary>
      public float[] DashIntervals => ChartPalette.DashIntervalsFor(Pattern);

      /// <summary>"solid", "dashed", ... for use in an accessible name.</summary>
      public string PatternText => ChartPalette.DescribePattern(Pattern);

      /// <summary>"circle", "square", ... for use in an accessible name.</summary>
      public string MarkerText => ChartPalette.DescribeMarker(Marker);
   }

   /// <summary>
   /// Everything on a chart that is not a series: axis labels, grid lines, the
   /// tooltip, the empty-state placeholder, and - in High Contrast only - the
   /// surface the chart sits on.
   /// </summary>
   public sealed class ChartChrome
   {
      public uint AxisTextArgb { get; init; }

      public uint GridLineArgb { get; init; }

      public uint TooltipBackgroundArgb { get; init; }

      public uint TooltipTextArgb { get; init; }

      public uint PlaceholderTextArgb { get; init; }

      /// <summary>
      /// Line curvature, 0 = straight segments between samples.
      ///
      /// Curved lines interpolate points that were never measured. That is an
      /// acceptable cosmetic lie on the ordinary palette, but a High Contrast
      /// reader is usually working at a magnification where the invented curve is
      /// the most visible thing on the chart, so High Contrast gets 0.
      /// </summary>
      public double LineSmoothness { get; init; }

      /// <summary>
      /// Surface / text / border the chart area must paint itself with, or null
      /// to leave the app's own theme brushes alone.
      ///
      /// This is non-null in High Contrast for a specific reason: the palette
      /// guarantees contrast against the <em>system window colour</em>, and
      /// WPF-UI keeps painting its own dark or light card underneath unless it is
      /// told otherwise. Handing back black-on-black is worse than doing nothing,
      /// so when we take the system foreground colours we take the system
      /// background with them.
      /// </summary>
      public uint? SurfaceArgb { get; init; }

      public uint? SurfaceTextArgb { get; init; }

      public uint? SurfaceBorderArgb { get; init; }
   }

   /// <summary>
   /// Resolves the colours, dash patterns and marker shapes a chart draws with,
   /// for one of the three themes.
   ///
   /// Before this existed the dashboard hardcoded five SKColor constants
   /// (0x2F81F7 blue, 0x3FB950 green, 0xA371F7 purple, 0xD29922 amber, and a grey
   /// for the axes) and used them in every theme. ThemeTokens had already grown a
   /// High Contrast branch built from SystemColors for the status and log
   /// palettes, but no chart consulted it, so with High Contrast on the charts
   /// carried on rendering in the four colours the user had just told Windows
   /// they cannot see - and the axis grey failed the contrast ratio on the light
   /// theme too.
   ///
   /// Everything here is deliberately free of WPF types. The theme decisions are
   /// the part worth testing, and a static class that only touches numbers can be
   /// tested in the ordinary xUnit run with no dispatcher, no Application and no
   /// live High Contrast setting on the build agent.
   /// </summary>
   public static class ChartPalette
   {
      /// <summary>
      /// Assigned to series in order and cycled. Five patterns against three High
      /// Contrast colours means the (colour, pattern) pair stays unique for
      /// fifteen series even though the colours repeat every third one - see
      /// ResolveSeries for why the two cycles must have different lengths.
      /// </summary>
      private static readonly ChartLinePattern[] Patterns =
      {
         ChartLinePattern.Solid,
         ChartLinePattern.Dash,
         ChartLinePattern.Dot,
         ChartLinePattern.DashDot,
         ChartLinePattern.LongDash
      };

      private static readonly ShapeMark[] Markers =
      {
         ShapeMark.Circle,
         ShapeMark.Square,
         ShapeMark.Triangle,
         ShapeMark.Diamond,
         ShapeMark.Cross
      };

      // Same hues as the ThemeTokens status palette, so a green line and a green
      // "success" label are the same green. Each of these clears 4.5:1 against a
      // white surface; each of the dark set clears it against #1B1B1B.
      private static readonly uint[] LightSeriesColors =
      {
         0xFF2F6FE0, 0xFF1A7F37, 0xFF6639BA, 0xFF9A6700, 0xFFCF222E
      };

      private static readonly uint[] DarkSeriesColors =
      {
         0xFF4C8DFF, 0xFF3FB950, 0xFFA371F7, 0xFFD29922, 0xFFF85149
      };

      /// <summary>
      /// Picks a colour, a dash pattern and a marker shape for each named series.
      /// Never returns null and never returns fewer entries than
      /// <paramref name="seriesNames"/> has.
      /// </summary>
      public static IReadOnlyList<ChartSeriesStyle> ResolveSeries(
         ChartTheme theme, ChartSystemColors system, IReadOnlyList<string> seriesNames)
      {
         var result = new List<ChartSeriesStyle>();
         if (seriesNames == null)
            return result;

         uint[] colors = SeriesColors_(theme, system);
         bool highContrast = theme == ChartTheme.HighContrast;

         // Colour cycles over `colors`, pattern and marker over five entries. The
         // two cycle lengths are coprime in the case that matters (three system
         // colours against five patterns), so the pair keeps identifying the
         // series long after the colours alone have started to repeat. Deriving
         // the pattern from the colour index instead - the obvious alternative -
         // would have given every one of the first five series a solid line and
         // left High Contrast with three indistinguishable pairs.
         for (int i = 0; i < seriesNames.Count; i++)
         {
            result.Add(new ChartSeriesStyle(
               seriesNames[i] ?? "",
               colors[i % colors.Length],
               Patterns[i % Patterns.Length],
               Markers[i % Markers.Length],
               highContrast ? 3f : 2.5f,
               // The area fill exists to make a SINGLE-series sparkline readable, so
               // it is keyed on the chart having exactly one series - not on being the
               // first series of any chart. Keying it on i == 0 put a translucent wash
               // under SMTP on the three-series Active sessions chart, over the top of
               // the IMAP and POP3 lines, on a default install. Never in High Contrast,
               // where a wash fights the system palette rather than helping.
               highContrast || seriesNames.Count > 1 ? (byte) 0 : (byte) 70));
         }

         return result;
      }

      /// <summary>Axis, grid, tooltip and surface colours for one theme.</summary>
      public static ChartChrome ResolveChrome(ChartTheme theme, ChartSystemColors system)
      {
         if (theme == ChartTheme.HighContrast)
         {
            // Grid lines use GrayText because that is the one system colour that
            // is defined to be visible but subordinate; using WindowText would
            // give a grid as loud as the data.
            return new ChartChrome
            {
               AxisTextArgb = system.WindowText,
               GridLineArgb = system.GrayText,
               TooltipBackgroundArgb = system.Window,
               TooltipTextArgb = system.WindowText,
               PlaceholderTextArgb = system.WindowText,
               LineSmoothness = 0,
               SurfaceArgb = system.Window,
               SurfaceTextArgb = system.WindowText,
               SurfaceBorderArgb = system.WindowText
            };
         }

         if (theme == ChartTheme.Light)
         {
            return new ChartChrome
            {
               AxisTextArgb = 0xFF57606A,
               GridLineArgb = 0x3357606A,
               TooltipBackgroundArgb = 0xFFFFFFFF,
               TooltipTextArgb = 0xFF1F2328,
               PlaceholderTextArgb = 0xFF57606A,

               // Zero in every theme, not just High Contrast. Spline interpolation
               // invents values between samples and rounds the peaks off - and the
               // peaks are the entire reason somebody is looking at a queue-depth or
               // session-count chart. A curve through monitoring data is not a
               // prettier truth, it is a different one.
               //
               // This was 0.8 here and in the dark theme while being 0 on the High
               // Contrast branch, which meant the accessibility work fixed the
               // rendering for the theme almost nobody runs and left it wrong in the
               // two everybody does.
               LineSmoothness = 0
            };
         }

         return new ChartChrome
         {
            AxisTextArgb = 0xFF9DA7B0,
            GridLineArgb = 0x338B949E,
            TooltipBackgroundArgb = 0xFF2B2B2B,
            TooltipTextArgb = 0xFFF0F0F0,
            PlaceholderTextArgb = 0xFF9DA7B0,

            // Zero, for the reason given on the light branch above: monitoring data
            // must not be splined.
            LineSmoothness = 0
         };
      }

      /// <summary>
      /// Dash intervals in device pixels for one pattern, or null for a solid
      /// line. A fresh array every call - see ChartSeriesStyle.DashIntervals.
      /// </summary>
      public static float[] DashIntervalsFor(ChartLinePattern pattern)
      {
         switch (pattern)
         {
            case ChartLinePattern.Dash:
               return new[] { 8f, 4f };
            case ChartLinePattern.Dot:
               return new[] { 2f, 3f };
            case ChartLinePattern.DashDot:
               return new[] { 9f, 3f, 2f, 3f };
            case ChartLinePattern.LongDash:
               return new[] { 16f, 5f };
            default:
               return null;
         }
      }

      /// <summary>The dash pattern in words, for an accessible name.</summary>
      public static string DescribePattern(ChartLinePattern pattern)
      {
         switch (pattern)
         {
            case ChartLinePattern.Dash:
               return "dashed";
            case ChartLinePattern.Dot:
               return "dotted";
            case ChartLinePattern.DashDot:
               return "dash-dotted";
            case ChartLinePattern.LongDash:
               return "long-dashed";
            default:
               return "solid";
         }
      }

      /// <summary>The marker shape in words, for an accessible name.</summary>
      public static string DescribeMarker(ShapeMark marker)
      {
         switch (marker)
         {
            case ShapeMark.Square:
               return "square";
            case ShapeMark.Triangle:
               return "triangle";
            case ShapeMark.Diamond:
               return "diamond";
            case ShapeMark.Cross:
               return "cross";
            default:
               return "circle";
         }
      }

      private static uint[] SeriesColors_(ChartTheme theme, ChartSystemColors system)
      {
         if (theme == ChartTheme.Light)
            return LightSeriesColors;
         if (theme == ChartTheme.Dark)
            return DarkSeriesColors;

         // High Contrast: the only colours we are allowed to use are the ones the
         // theme itself defines. Any candidate that has collapsed onto the
         // background colour is dropped rather than drawn - a High Contrast theme
         // is free to define Highlight as the window colour, and an invisible
         // line is a worse failure than two lines sharing a colour, because the
         // dash pattern can still tell those two apart.
         var colors = new List<uint>(3);
         AddIfVisible_(colors, system.WindowText, system.Window);
         AddIfVisible_(colors, system.Highlight, system.Window);
         AddIfVisible_(colors, system.HotTrack, system.Window);
         if (colors.Count == 0)
            colors.Add(system.WindowText);
         return colors.ToArray();
      }

      private static void AddIfVisible_(List<uint> colors, uint candidate, uint background)
      {
         if (candidate != background && !colors.Contains(candidate))
            colors.Add(candidate);
      }
   }
}
