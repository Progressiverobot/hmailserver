// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The Control Panel's translation lookup. The English text is the key: a
   /// caption is written once, in English, where it is used - <c>L("_Save changes")</c>
   /// in code, <c>{loc:L '_Save changes'}</c> in XAML - and the catalogue for the
   /// current language (Resources/Strings.&lt;culture&gt;.resx) maps that text to
   /// its translation. Anything the catalogue does not hold comes back in English,
   /// so a partly translated language degrades to English words rather than to
   /// blank controls or resource-key names, and the English catalogue itself
   /// (Resources/Strings.resx) is generated from the source rather than written by
   /// hand: build/check-localisation.py keeps it equal to the set of marked texts
   /// and fails the build when it is not.
   ///
   /// Three markers, so that a scan of the source finds every translatable text:
   /// <see cref="L"/> translates now; <see cref="F"/> translates and formats;
   /// <see cref="N"/> only marks, for a table whose English text has to survive
   /// (the navigation map is compared against the documentation by its English
   /// titles) and is translated where it is shown.
   ///
   /// Nothing here touches WPF or the registry. The application chooses the
   /// culture and the catalogue at start-up (<see cref="Use"/>); the tests hand
   /// in a dictionary.
   /// </summary>
   public static class Loc
   {
      /// <summary>A language the Control Panel can show, as offered to the user.</summary>
      public sealed class Language
      {
         internal Language(string tag, string nativeName)
         {
            Tag = tag;
            NativeName = nativeName;
         }

         /// <summary>BCP 47 tag, or empty for "whatever Windows displays in".</summary>
         public string Tag { get; }

         /// <summary>The language's name in itself, which is how a language picker is readable to the person who needs it.</summary>
         public string NativeName { get; }
      }

      /// <summary>
      /// The languages a catalogue exists for, plus the empty tag for following
      /// Windows. Adding a language is a new Strings.&lt;tag&gt;.resx and a row here.
      /// </summary>
      public static readonly IReadOnlyList<Language> Languages = new[]
      {
         new Language("", N("Windows display language")),
         new Language("en", "English"),   // no-loc: a language is named in itself
         new Language("de", "Deutsch"),   // no-loc
         new Language("es", "Español"),   // no-loc
         new Language("fr", "Français"),   // no-loc
         new Language("it", "Italiano"),   // no-loc
         new Language("nl", "Nederlands"),   // no-loc
         new Language("pl", "Polski"),   // no-loc
         new Language("pt-BR", "Português (Brasil)"),   // no-loc
         new Language("ru", "Русский"),   // no-loc
         new Language("sv", "Svenska"),   // no-loc
      };

      private static Func<string, CultureInfo, string> lookup_;
      private static CultureInfo culture_;
      private static bool english_ = true;

      /// <summary>The UI culture in force, or null when nothing has been chosen (English).</summary>
      public static CultureInfo Culture => culture_;

      /// <summary>
      /// Installs the catalogue and the culture. <paramref name="lookup"/> takes
      /// the English text and the culture and returns the translation, or null
      /// or empty when it has none; the application passes ResourceManager's
      /// GetString. A null lookup, or an English culture, makes every call an
      /// identity - the common case costs nothing.
      /// </summary>
      public static void Use(Func<string, CultureInfo, string> lookup, CultureInfo culture)
      {
         lookup_ = lookup;
         culture_ = culture;
         english_ = lookup == null || culture == null || IsEnglish(culture);
      }

      /// <summary>Back to untranslated English; the tests use it between cases.</summary>
      public static void Reset() => Use(null, null);

      /// <summary>
      /// The culture a stored language choice resolves to: the tag itself when
      /// one is stored, the Windows display language when the choice is empty,
      /// and English when the tag is not one Windows knows.
      /// </summary>
      public static CultureInfo Resolve(string tag)
      {
         if (string.IsNullOrWhiteSpace(tag))
            return CultureInfo.InstalledUICulture;

         try
         {
            return CultureInfo.GetCultureInfo(tag.Trim());
         }
         catch (CultureNotFoundException)
         {
            return CultureInfo.GetCultureInfo("en");
         }
      }

      /// <summary>True for English of any region and for the invariant culture.</summary>
      public static bool IsEnglish(CultureInfo culture)
         => culture == null
            || culture.Equals(CultureInfo.InvariantCulture)
            || string.Equals(culture.TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase);

      /// <summary>The translation of <paramref name="english"/>, or the English itself when there is none.</summary>
      public static string L(string english)
      {
         if (english_ || string.IsNullOrEmpty(english))
            return english;

         string translated;
         try
         {
            translated = lookup_(english, culture_);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // A catalogue that cannot be read (a satellite assembly missing from
            // an installation, a corrupt resource) is worth an English caption,
            // not an exception in the middle of building a page - and not one
            // exception per caption, so stop asking.
            english_ = true;
            return english;
         }

         return string.IsNullOrEmpty(translated) ? english : translated;
      }

      /// <summary>
      /// Translates and then formats, with the user's own number and date
      /// conventions. The placeholders must be the same set in every
      /// translation; the checker enforces that, because a missing {0} is a
      /// sentence with no subject and an extra one is FormatException.
      /// </summary>
      public static string F(string english, params object[] args)
         => string.Format(CultureInfo.CurrentCulture, L(english), args);

      /// <summary>
      /// Marks a text for the catalogue without translating it here: the value
      /// stays English for the table it sits in and is passed through
      /// <see cref="L"/> at the point it is displayed.
      /// </summary>
      public static string N(string english) => english;
   }
}
