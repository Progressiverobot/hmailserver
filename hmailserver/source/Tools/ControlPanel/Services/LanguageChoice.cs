// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Markup;
using Microsoft.Win32;
using hMailServer.ControlPanel.Views;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Which language the Control Panel shows, and how it is changed. The choice
   /// is per Windows user (the same registry key that keeps the theme and the
   /// palette history), empty means "whatever Windows displays in", and a change
   /// restarts the Control Panel: every page builds its captions once, from the
   /// culture in force when it loads, and rebuilding all of them in place would be
   /// a great deal of machinery for something chosen once.
   /// </summary>
   public static class LanguageChoice
   {
      private const string RegistryPath = @"Software\hMailServer\ControlPanel";
      private const string ValueName = "Language"; // no-loc

      // The catalogue: Resources/Strings.resx (English, generated) and one
      // Strings.<culture>.resx per language, compiled by the SDK into satellite
      // assemblies beside hMailCP.exe (sv\hMailCP.resources.dll).
      private static readonly ResourceManager Catalogue =
         new("hMailServer.ControlPanel.Resources.Strings", typeof(LanguageChoice).Assembly);

      /// <summary>The stored tag: empty for the Windows display language.</summary>
      public static string Stored { get; private set; } = "";

      /// <summary>
      /// Reads the stored choice and puts its culture in force for the process:
      /// the catalogue lookup, the UI culture of every thread, and the language
      /// WPF shapes text with. Called once, before the first window exists.
      /// </summary>
      public static void Apply()
      {
         try
         {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            Stored = (key?.GetValue(ValueName) as string ?? "").Trim();
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            Stored = "";
         }

         CultureInfo culture = Resolve(Stored);
         Use(Catalogue.GetString, culture);

         CultureInfo.DefaultThreadCurrentUICulture = culture;
         CultureInfo.CurrentUICulture = culture;

         try
         {
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
               new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Only typography (hyphenation, font fallback) depends on this; the
            // captions are already translated.
         }
      }

      /// <summary>
      /// Offers to switch to <paramref name="tag"/>. Nothing happens for the
      /// language already stored; otherwise the user is asked, because the
      /// switch restarts the Control Panel. Returns true when a restart is under
      /// way, so a caller can leave its own state alone.
      /// </summary>
      public static bool Offer(string tag)
      {
         tag = (tag ?? "").Trim();
         if (string.Equals(tag, Stored, StringComparison.OrdinalIgnoreCase))
            return false;

         string name = null;
         foreach (Language language in Languages)
         {
            if (string.Equals(language.Tag, tag, StringComparison.OrdinalIgnoreCase))
               name = L(language.NativeName);
         }

         if (name == null)
            return false;

         if (Dialogs.Show(F("Show the Control Panel in {0}? It restarts to change language.", name),
                L("Language"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
         {
            return false;
         }

         try
         {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue(ValueName, tag);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            Dialogs.Show(F("The language could not be saved: {0}", fatalCheck.Message), L("Language"),
               MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
         }

         Stored = tag;
         ((App)Application.Current).Restart();
         return true;
      }
   }
}
