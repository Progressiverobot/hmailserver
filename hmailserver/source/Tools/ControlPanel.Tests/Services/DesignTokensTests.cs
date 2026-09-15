// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The design tokens: the numbers in DesignTokens, the resources in
   /// Views/Scaffold/Tokens.xaml and the names in docs/ControlPanelDesign.md
   /// are three copies of one table, and the page waves code against the doc.
   /// These hold the copies equal and the rules the numbers obey - the scale is
   /// on a four-pixel grid, High Contrast invents no colour, the information
   /// colour stays apart from the three status colours for colour-blind eyes.
   /// </summary>
   public class DesignTokensTests
   {
      [Fact]
      public void TheSpacingScaleAscendsOnAFourPixelGrid()
      {
         IReadOnlyList<double> scale = DesignTokens.Space.All;

         Assert.Equal(new[] { 4.0, 8, 12, 16, 24, 32 }, scale);

         for (int i = 1; i < scale.Count; i++)
            Assert.True(scale[i] > scale[i - 1], "The scale must ascend.");

         foreach (double step in scale)
            Assert.Equal(0, step % 4);
      }

      [Fact]
      public void TheRadiiAndTheShellDimensionsAreWhatTheDocSays()
      {
         Assert.Equal(4, DesignTokens.Radius.Control);
         Assert.Equal(8, DesignTokens.Radius.Card);
         Assert.Equal(48, DesignTokens.Shell.CommandBarHeight);
         Assert.Equal(48, DesignTokens.Shell.RailWidth);
         Assert.Equal(34, DesignTokens.Shell.NavRowHeight);

         // The rail must fit inside the window's minimum width beside a usable
         // content area, and the threshold must be wider than the window minimum
         // or the rail could never appear on its own.
         Assert.True(DesignTokens.Shell.CollapseSidebarBelow > 760);
         Assert.True(DesignTokens.Shell.SidebarMinWidth < DesignTokens.Shell.SidebarMaxWidth);
         Assert.True(DesignTokens.Dialog.MinWidth < DesignTokens.Dialog.DefaultWidth);
         Assert.True(DesignTokens.Dialog.DefaultWidth < DesignTokens.Dialog.MaxWidth);
         Assert.InRange(DesignTokens.Dialog.MaxHeightFraction, 0.5, 1.0);
      }

      /// <summary>
      /// Under High Contrast every colour is one the user chose in Windows: the
      /// three status colours are the window text (their shape and word tell
      /// them apart), brand and information are the highlight, neutral is the
      /// disabled-text grey, a card is the window colour with a window-text
      /// outline. Nothing from the light or dark palettes may leak through.
      /// </summary>
      [Fact]
      public void HighContrastDerivesEveryColourFromTheSystemPaletteAndInventsNone()
      {
         var system = new ChartSystemColors(0xFF112233, 0xFFEEDDCC, 0xFF445566, 0xFF778899, 0xFFAABBCC);

         DesignTokens.StatusArgb status = DesignTokens.Status(ChartTheme.HighContrast, system);
         Assert.Equal(system.WindowText, status.Success);
         Assert.Equal(system.WindowText, status.Warning);
         Assert.Equal(system.WindowText, status.Danger);
         Assert.Equal(system.Highlight, status.Brand);
         Assert.Equal(system.Highlight, status.Info);
         Assert.Equal(system.GrayText, status.Neutral);

         DesignTokens.SurfaceArgb surfaces = DesignTokens.Surfaces(ChartTheme.HighContrast, system);
         Assert.Equal(system.Window, surfaces.CardBackground);
         Assert.Equal(system.WindowText, surfaces.CardBorder);
         Assert.Equal(system.WindowText, surfaces.Divider);

         Assert.Equal(0, DesignTokens.NoticeTintOpacityFor(ChartTheme.HighContrast));
         Assert.True(DesignTokens.NoticeTintOpacityFor(ChartTheme.Dark) > 0);
         Assert.True(DesignTokens.NoticeTintOpacityFor(ChartTheme.Light) > 0);
      }

      [Theory]
      [InlineData(ChartTheme.Light)]
      [InlineData(ChartTheme.Dark)]
      public void TheLightAndDarkStatusColoursAreTheStatusPalettes(ChartTheme theme)
      {
         (uint success, uint warning, uint danger) = StatusPalette.Argb(theme == ChartTheme.Light);
         DesignTokens.StatusArgb status = DesignTokens.Status(theme, ChartSystemColors.Fallback);

         Assert.Equal(success, status.Success);
         Assert.Equal(warning, status.Warning);
         Assert.Equal(danger, status.Danger);

         // Opaque, every one: a status colour with alpha would read differently
         // on a card and on the page behind it.
         foreach (uint colour in new[] { status.Brand, status.Success, status.Warning, status.Danger, status.Info, status.Neutral })
            Assert.Equal(0xFFu, colour >> 24);
      }

      /// <summary>
      /// The information colour is the fourth status colour, and it must be
      /// tellable from the other three by the same eyes ColourVisionTests holds
      /// the three to - a purple that reads as the danger red under protanopia
      /// would make an information notice look like a failure.
      /// </summary>
      [Theory]
      [InlineData(ChartTheme.Light)]
      [InlineData(ChartTheme.Dark)]
      public void InformationStaysApartFromTheThreeStatusColoursUnderTheTwoDichromacies(ChartTheme theme)
      {
         DesignTokens.StatusArgb status = DesignTokens.Status(theme, ChartSystemColors.Fallback);

         foreach ((string name, uint other) in new[] { ("success", status.Success), ("warning", status.Warning), ("danger", status.Danger) })
         {
            foreach (ColourVision.Vision vision in new[] { ColourVision.Vision.Protanopia, ColourVision.Vision.Deuteranopia, ColourVision.Vision.Typical })
            {
               double deltaE = ColourVision.DeltaE(status.Info, other, vision);
               Assert.True(deltaE >= 8.0, $"{theme} info/{name} under {vision}: ΔE {deltaE:F1} is below the floor of 8.");
            }
         }
      }

      /// <summary>
      /// The card surfaces of the light and dark themes are translucent - they
      /// sit on Mica in the sidebar and on a solid page in the content area, and
      /// an opaque card would be a different colour in the two places.
      /// </summary>
      [Fact]
      public void TheLightAndDarkCardSurfacesAreTranslucent()
      {
         foreach (ChartTheme theme in new[] { ChartTheme.Light, ChartTheme.Dark })
         {
            DesignTokens.SurfaceArgb surfaces = DesignTokens.Surfaces(theme, ChartSystemColors.Fallback);
            Assert.True(surfaces.CardBackground >> 24 < 0xFF, theme + " card background is opaque.");
            Assert.True(surfaces.CardBorder >> 24 < 0xFF, theme + " card border is opaque.");
         }
      }

      /// <summary>
      /// Every resource key in Tokens.xaml is named in the design doc, and every
      /// token the doc names exists. The doc is what the page waves read; a
      /// token it does not name is one they will not use, and a token it names
      /// that does not exist is a XAML that fails at run time.
      /// </summary>
      [Fact]
      public void EveryTokenInTokensXamlIsInTheDesignDocAndTheOtherWayRound()
      {
         string tokensXaml = ReadControlPanelFile(Path.Join("Views", "Scaffold", "Tokens.xaml"));
         string doc = ReadDoc("ControlPanelDesign.md");
         if (tokensXaml == null || doc == null)
            return;   // sources not available (e.g. running from a packaged drop)

         var declared = Regex.Matches(tokensXaml, "x:Key=\"([A-Za-z0-9]+)\"").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

         // A token is documented by a table row that opens with its name in
         // backticks; prose may mention anything.
         var documented = Regex.Matches(doc, @"^\| `([A-Za-z0-9]+)` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value)
            .Where(name => name.StartsWith("App", StringComparison.Ordinal) || name.StartsWith("Text", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

         Assert.True(declared.Count >= 30, "Tokens.xaml holds fewer tokens than the design has.");

         List<string> undocumented = declared.Except(documented).OrderBy(k => k).ToList();
         Assert.True(undocumented.Count == 0, "Tokens.xaml declares tokens the design doc does not name: " + string.Join(", ", undocumented));

         List<string> missing = documented.Except(declared).OrderBy(k => k).ToList();
         Assert.True(missing.Count == 0, "The design doc names tokens Tokens.xaml does not declare: " + string.Join(", ", missing));
      }

      /// <summary>Every scaffold component has a section in the doc, by its class name.</summary>
      [Fact]
      public void EveryScaffoldComponentIsInTheDesignDoc()
      {
         string controlPanel = FindControlPanelDirectory();
         string doc = ReadDoc("ControlPanelDesign.md");
         if (controlPanel == null || doc == null)
            return;

         string scaffold = Path.Join(controlPanel, "Views", "Scaffold");
         List<string> components = Directory.GetFiles(scaffold, "*.cs")
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"public (?:abstract )?class (\w+)").Select(m => m.Groups[1].Value))
            .Where(name => !name.StartsWith("Scaffold", StringComparison.Ordinal))
            .OrderBy(name => name)
            .ToList();

         Assert.True(components.Count >= 10, "Fewer scaffold components than the design has: " + string.Join(", ", components));

         foreach (string component in components)
            Assert.True(doc.Contains("`" + component + "`", StringComparison.Ordinal), component + " has no entry in docs/ControlPanelDesign.md.");
      }

      /// <summary>
      /// The C# constants and the XAML resources say the same numbers. The
      /// pages read the XAML, the code reads the constants, and a drift between
      /// them is a card whose padding differs by which language placed it.
      /// </summary>
      [Fact]
      public void TokensXamlSaysTheSameNumbersAsDesignTokens()
      {
         string tokensXaml = ReadControlPanelFile(Path.Join("Views", "Scaffold", "Tokens.xaml"));
         if (tokensXaml == null)
            return;

         string Value(string key)
         {
            Match match = Regex.Match(tokensXaml, "x:Key=\"" + key + "\">([^<]+)<");
            Assert.True(match.Success, key + " is not in Tokens.xaml.");
            return match.Groups[1].Value;
         }

         Assert.Equal(DesignTokens.Space.Xs.ToString(), Value("AppSpaceXs"));
         Assert.Equal(DesignTokens.Space.Sm.ToString(), Value("AppSpaceSm"));
         Assert.Equal(DesignTokens.Space.Md.ToString(), Value("AppSpaceMd"));
         Assert.Equal(DesignTokens.Space.Lg.ToString(), Value("AppSpaceLg"));
         Assert.Equal(DesignTokens.Space.Xl.ToString(), Value("AppSpaceXl"));
         Assert.Equal(DesignTokens.Space.Xxl.ToString(), Value("AppSpaceXxl"));
         Assert.Equal(DesignTokens.Radius.Control.ToString(), Value("AppControlCornerRadius"));
         Assert.Equal(DesignTokens.Radius.Card.ToString(), Value("AppCardCornerRadius"));
         Assert.Equal(DesignTokens.Radius.Pill.ToString(), Value("AppPillCornerRadius"));
         Assert.Equal(DesignTokens.Shell.CommandBarHeight.ToString(), Value("AppCommandBarHeight"));
         Assert.Equal(DesignTokens.Shell.RailWidth.ToString(), Value("AppRailWidth"));
         Assert.Equal(DesignTokens.Shell.NavRowHeight.ToString(), Value("AppNavRowHeight"));
         Assert.Equal(Typography.Caption.ToString(), Value("AppFontSizeCaption"));
         Assert.Equal(Typography.Body.ToString(), Value("AppFontSizeBody"));
         Assert.Equal(Typography.DialogTitle.ToString(), Value("AppFontSizeSubtitle"));
         Assert.Equal(DesignTokens.NoticeTintOpacity.ToString(System.Globalization.CultureInfo.InvariantCulture), Value("AppNoticeTintOpacity"));

         // The brushes are declared with the dark values, which ThemeTokens
         // republishes for the theme in force; the declaration must be the dark
         // table, or the instant before the first Refresh shows another palette.
         DesignTokens.StatusArgb dark = DesignTokens.Status(ChartTheme.Dark, ChartSystemColors.Fallback);
         DesignTokens.SurfaceArgb surfaces = DesignTokens.Surfaces(ChartTheme.Dark, ChartSystemColors.Fallback);

         string Colour(string key)
         {
            Match match = Regex.Match(tokensXaml, "x:Key=\"" + key + "\" Color=\"#([0-9A-Fa-f]+)\"");
            Assert.True(match.Success, key + " is not a brush in Tokens.xaml.");
            string hex = match.Groups[1].Value.ToUpperInvariant();
            return hex.Length == 6 ? "FF" + hex : hex;
         }

         Assert.Equal(dark.Brand.ToString("X8"), Colour("AppBrandBrush"));
         Assert.Equal(dark.Success.ToString("X8"), Colour("AppSuccessBrush"));
         Assert.Equal(dark.Warning.ToString("X8"), Colour("AppWarningBrush"));
         Assert.Equal(dark.Danger.ToString("X8"), Colour("AppDangerBrush"));
         Assert.Equal(dark.Info.ToString("X8"), Colour("AppInfoBrush"));
         Assert.Equal(dark.Neutral.ToString("X8"), Colour("AppNeutralBrush"));
         Assert.Equal(surfaces.CardBackground.ToString("X8"), Colour("AppCardBackgroundBrush"));
         Assert.Equal(surfaces.CardBorder.ToString("X8"), Colour("AppCardBorderBrush"));
         Assert.Equal(surfaces.CardHover.ToString("X8"), Colour("AppCardHoverBrush"));
         Assert.Equal(surfaces.Divider.ToString("X8"), Colour("AppDividerBrush"));
      }

      private static string ReadControlPanelFile(string relativePath)
      {
         string controlPanel = FindControlPanelDirectory();
         if (controlPanel == null)
            return null;

         string path = Path.Join(controlPanel, relativePath);
         return File.Exists(path) ? File.ReadAllText(path) : null;
      }

      private static string ReadDoc(string name)
      {
         string controlPanel = FindControlPanelDirectory();
         if (controlPanel == null)
            return null;

         // source/Tools/ControlPanel -> hmailserver/docs
         string path = Path.Join(controlPanel, "..", "..", "..", "docs", name);
         return File.Exists(path) ? File.ReadAllText(path) : null;
      }

      /// <summary>Walks up from the test binaries to the Control Panel sources.</summary>
      private static string FindControlPanelDirectory()
      {
         for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
         {
            string candidate = Path.Join(directory.FullName, "ControlPanel", "MainWindow.xaml");
            if (File.Exists(candidate))
               return Path.Join(directory.FullName, "ControlPanel");
         }

         return null;
      }
   }
}
