using System;
using System.Windows;
using System.Windows.Media;
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

         Refresh();

         try
         {
            ApplicationThemeManager.Changed += (_, __) => Refresh();
         }
         catch (Exception)
         {
            // If the theme manager doesn't raise changes we still refresh
            // explicitly from the theme toggle (see MainWindow).
         }
      }

      /// <summary>Recomputes every token colour for the current theme.</summary>
      public static void Refresh()
      {
         Color brand, success, warning, danger, info;
         Color logDefault, logSmtp, logImap, logPop3, logApp, logError;

         if (SystemParameters.HighContrast)
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
            success = Hex("#1A7F37");
            warning = Hex("#9A6700");
            danger = Hex("#CF222E");
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
            success = Hex("#3FB950");
            warning = Hex("#D29922");
            danger = Hex("#F85149");
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
      }

      private static bool IsLight()
      {
         try
         {
            return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Light;
         }
         catch (Exception)
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

      private static Color Hex(string hex) => (Color) ColorConverter.ConvertFromString(hex);
   }
}
