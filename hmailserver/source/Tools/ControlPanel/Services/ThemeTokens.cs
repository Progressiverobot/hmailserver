// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;          // SystemEvents, for the High Contrast switch
using Wpf.Ui.Appearance;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Central semantic colour tokens for the Control Panel.
   ///
   /// Each token is a single shared, mutable <see cref="SolidColorBrush"/> that is
   /// registered in the application resources and also exposed as a static field.
   /// When the theme changes we recompute the brush <em>colours in place</em>, so
   /// every consumer updates live — whether it referenced the brush through a
   /// <c>DynamicResource</c> in XAML or holds a direct reference (e.g. the log
   /// viewer assigns these instances to each line). This keeps all state colours
   /// (success / warning / danger / info, plus the log-severity palette) legible
   /// and on-brand across light, dark and high-contrast themes — instead of the
   /// previous hardcoded values that were tuned for dark and failed on light.
   /// </summary>
   public static class ThemeTokens
   {
      // Status / semantic accents.
      public static readonly SolidColorBrush Brand = new();
      public static readonly SolidColorBrush Success = new();
      public static readonly SolidColorBrush Warning = new();
      public static readonly SolidColorBrush Danger = new();
      public static readonly SolidColorBrush Info = new();

      // Log-severity palette.
      public static readonly SolidColorBrush LogDefault = new();
      public static readonly SolidColorBrush LogSmtp = new();
      public static readonly SolidColorBrush LogImap = new();
      public static readonly SolidColorBrush LogPop3 = new();
      public static readonly SolidColorBrush LogApp = new();
      public static readonly SolidColorBrush LogError = new();

      /// <summary>
      /// Raised after every recomputation, so consumers that cannot express
      /// themselves as a brush reference can rebuild.
      ///
      /// The charts are the reason this exists. A LiveCharts series holds Skia
      /// paints, not WPF brushes, so it can neither take a DynamicResource nor
      /// benefit from the in-place brush mutation the rest of the app relies on:
      /// the only way a chart follows a theme change is to be told about it and
      /// rebuild its paints. Subscribers should hook this on Loaded and unhook on
      /// Unloaded rather than in a constructor - the pages are cached and swapped
      /// in and out of the content host, and a static event is exactly the kind of
      /// root that keeps a dead page alive forever.
      /// </summary>
      public static event EventHandler Changed;

      /// <summary>
      /// Which palette the charts must use, recomputed with the tokens.
      ///
      /// High Contrast wins over the light/dark preference: the user asked the
      /// operating system for it, and the Control Panel's own theme toggle is not
      /// a licence to override that.
      /// </summary>
      public static ChartTheme CurrentChartTheme { get; private set; } = ChartTheme.Dark;

      /// <summary>
      /// The system colours the High Contrast palette is built from, snapshotted
      /// on each refresh so the WPF-free palette code never has to touch
      /// SystemColors itself.
      /// </summary>
      public static ChartSystemColors CurrentSystemColors { get; private set; } = ChartSystemColors.Fallback;

      private static bool initialised_;

      /// <summary>
      /// Computes the initial colours and subscribes to theme changes.
      /// Safe to call more than once.
      /// </summary>
      public static void Initialize()
      {
         if (initialised_)
            return;
         initialised_ = true;

         // A focus ring that is visible on every theme is part of the same job as
         // the colour tokens, and it is registered here rather than in App.xaml so
         // that the whole of it lives in one file.
         AccessibleFocus.Register();

         Refresh();

         try
         {
            ApplicationThemeManager.Changed += (_, __) => Refresh();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // If the theme manager doesn't raise changes we still refresh
            // explicitly from the theme toggle (see MainWindow).
         }

         // Windows' own accessibility switches, which the WPF-UI theme manager knows
         // nothing about. Without this the High Contrast palette was only ever picked
         // up at startup or when the user pressed the light/dark toggle - and pressing
         // that toggle calls SystemThemeWatcher.UnWatch, so for anybody who had ever
         // used it, turning High Contrast on in Windows did nothing at all until the
         // application was restarted. That is the headline requirement of the
         // accessibility work, and it was the one thing not wired up.
         //
         // UserPreferenceChanged rather than SystemParameters.StaticPropertyChanged:
         // it is raised for the Accessibility, Color, VisualStyle and General
         // categories, which between them cover turning High Contrast on and switching
         // between High Contrast themes. It arrives on a background thread, so the
         // refresh is marshalled to the dispatcher - the tokens are read by the UI and
         // Changed subscribers touch WPF objects.
         try
         {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged_;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // A missing SystemEvents pump is not a reason to fail startup; the theme
            // toggle still refreshes explicitly.
         }
      }

      private static void OnUserPreferenceChanged_(object sender, UserPreferenceChangedEventArgs e)
      {
         if (e.Category != UserPreferenceCategory.Accessibility &&
             e.Category != UserPreferenceCategory.Color &&
             e.Category != UserPreferenceCategory.VisualStyle &&
             e.Category != UserPreferenceCategory.General)
            return;

         try
         {
            System.Windows.Threading.Dispatcher dispatcher = Application.Current?.Dispatcher;

            if (dispatcher != null && !dispatcher.CheckAccess())
               dispatcher.BeginInvoke(new Action(Refresh));
            else
               Refresh();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Never let a system notification take the application down.
         }
      }

      /// <summary>
      /// The three status colours for the light or the dark theme, from
      /// <see cref="StatusPalette"/> - the one place they live, where a test holds
      /// every pair apart for colour-blind eyes.
      /// </summary>
      public static (Color success, Color warning, Color danger) StatusColours(bool light)
      {
         (uint success, uint warning, uint danger) = StatusPalette.Argb(light);
         return (FromArgb(success), FromArgb(warning), FromArgb(danger));
      }

      private static Color FromArgb(uint argb)
      {
         return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
      }

      /// <summary>
      /// Whether the application is in High Contrast: Windows says so, or
      /// HMAILCP_HIGH_CONTRAST=1 in the environment says to behave as if it did.
      /// The variable exists for one reason - the High Contrast theme changes
      /// every control in the application, and a change like that has to be looked
      /// at, so build/capture-cp.ps1 -HighContrast launches the Control Panel with
      /// it set and captures the result on an ordinary desktop. It is read once.
      /// </summary>
      public static bool IsHighContrast => SystemParameters.HighContrast || ForcedHighContrast;

      private static readonly bool ForcedHighContrast =
         string.Equals(Environment.GetEnvironmentVariable("HMAILCP_HIGH_CONTRAST"), "1", StringComparison.Ordinal);

      /// <summary>Recomputes every token colour for the current theme.</summary>
      public static void Refresh()
      {
         Color brand, success, warning, danger, info;
         Color logDefault, logSmtp, logImap, logPop3, logApp, logError;

         // Snapshot what the charts need before we start branching, so there is
         // exactly one decision about which theme is in force.
         CurrentSystemColors = ReadSystemColors();
         CurrentChartTheme = IsHighContrast
            ? ChartTheme.HighContrast
            : IsLight() ? ChartTheme.Light : ChartTheme.Dark;

         if (IsHighContrast)
         {
            brand = info = logApp = SystemColors.HighlightColor;
            success = warning = danger = SystemColors.WindowTextColor;
            logDefault = SystemColors.GrayTextColor;
            logSmtp = logImap = logPop3 = SystemColors.WindowTextColor;
            logError = SystemColors.HotTrackColor;
         }
         else if (IsLight())
         {
            // Darker, saturated values: each clears 4.5:1 on a white surface.
            brand = Hex("#2F6FE0");
            (success, warning, danger) = StatusColours(light: true);
            info = Hex("#6639BA");
            logDefault = Hex("#57606A");
            logSmtp = Hex("#1A7F37");
            logImap = Hex("#6639BA");
            logPop3 = Hex("#9A6700");
            logApp = Hex("#2F6FE0");
            logError = Hex("#CF222E");
         }
         else
         {
            // Lighter, brighter values for dark surfaces.
            brand = Hex("#4C8DFF");
            (success, warning, danger) = StatusColours(light: false);
            info = Hex("#A371F7");
            logDefault = Hex("#9DA7B0");
            logSmtp = Hex("#3FB950");
            logImap = Hex("#A371F7");
            logPop3 = Hex("#D29922");
            logApp = Hex("#4C8DFF");
            logError = Hex("#F85149");
         }

         // Channel 1: mutate the shared, never-frozen instances in place.
         Set(Brand, brand);
         Set(Success, success);
         Set(Warning, warning);
         Set(Danger, danger);
         Set(Info, info);
         Set(LogDefault, logDefault);
         Set(LogSmtp, logSmtp);
         Set(LogImap, logImap);
         Set(LogPop3, logPop3);
         Set(LogApp, logApp);
         Set(LogError, logError);

         // App.xaml declares "BrandBrush" as a fixed dark-theme blue (#2F81F7), and
         // every StaticResource reference to it - the sidebar pill, the tab
         // underline, the per-page icons - kept that blue on a light surface, where
         // it reads washed-out. The declared instance is mutated IN PLACE rather
         // than replaced, and the difference is load order: a StaticResource
         // resolves to an instance once, when its view loads, so replacing the
         // dictionary entry only reaches views loaded afterwards - and the App
         // constructor (where Initialize runs) executes before App.xaml's
         // resources even exist. Mutating the declared brush updates every
         // consumer that ever resolved it, whenever the theme changes. XAML
         // resource brushes are not frozen unless marked so; the guard keeps a
         // future Freeze from becoming a crash here.
         if (Application.Current?.Resources["BrandBrush"] is SolidColorBrush declaredBrand && !declaredBrand.IsFrozen)
            declaredBrand.Color = brand;

         // Channel 2: publish fresh brushes for XAML DynamicResource consumers.
         Publish("AppBrandBrush", brand);
         Publish("AppSuccessBrush", success);
         Publish("AppWarningBrush", warning);
         Publish("AppDangerBrush", danger);
         Publish("AppInfoBrush", info);
         Publish("LogDefaultBrush", logDefault);
         Publish("LogSmtpBrush", logSmtp);
         Publish("LogImapBrush", logImap);
         Publish("LogPop3Brush", logPop3);
         Publish("LogAppBrush", logApp);
         Publish("LogErrorBrush", logError);

         // Channel 3: tell the consumers that cannot be expressed as a brush.
         //
         // Invoked one handler at a time, deliberately. A single try/catch around
         // Changed?.Invoke looks equivalent and is not: an exception from the first
         // subscriber abandons the rest of the invocation list, so with two chart cards
         // subscribed the second silently keeps the previous theme's paints - and in
         // High Contrast that means a card still painted in colours the user has just
         // told Windows they cannot see. Wrapping each handler means one broken
         // consumer cannot cost the others their restyle.
         //
         // Still swallowed rather than propagated, for the original reason: a chart
         // that failed to restyle is not worth taking the window down for. But it is
         // reported now instead of vanishing.
         EventHandler handlers = Changed;

         if (handlers == null)
            return;

         foreach (Delegate handler in handlers.GetInvocationList())
         {
            try
            {
               ((EventHandler)handler)(null, EventArgs.Empty);
            }
            catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
            {
               App.LogException(ex);
            }
         }
      }

      /// <summary>
      /// Snapshots the system colours the High Contrast chart palette is built
      /// from. SystemColors is safe to read at any time, but it is only meaningful
      /// once a WPF application exists, so fall back to the classic High Contrast
      /// White values rather than to zeroes (which would be transparent black -
      /// invisible on every surface).
      /// </summary>
      private static ChartSystemColors ReadSystemColors()
      {
         try
         {
            return new ChartSystemColors(
               ToArgb(SystemColors.WindowTextColor),
               ToArgb(SystemColors.WindowColor),
               ToArgb(SystemColors.HighlightColor),
               ToArgb(SystemColors.HotTrackColor),
               ToArgb(SystemColors.GrayTextColor));
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return ChartSystemColors.Fallback;
         }
      }

      private static uint ToArgb(Color color)
      {
         return ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
      }

      private static bool IsLight()
      {
         try
         {
            return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return false;
         }
      }

      private static void Set(SolidColorBrush brush, Color color)
      {
         if (!brush.IsFrozen)
            brush.Color = color;
      }

      private static void Publish(string key, Color color)
      {
         if (Application.Current == null)
            return;
         Application.Current.Resources[key] = new SolidColorBrush(color);
      }

      private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
   }
}
