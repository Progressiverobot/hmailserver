// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The Control Panel type ramp - re-based 21 August 2026 onto the Windows 11
   /// (Fluent) ramp: Caption 12, Body 14, Body Strong 14 SemiBold, Subtitle 20,
   /// Title 28. The previous values were a hand-rolled 12 / 12.5 / 13 / 13.5 /
   /// 14 / 15 / 18 scale: five of seven constants were off the Windows ramp and
   /// four used half-pixel sizes, so even the sites that dutifully used the
   /// tokens rendered a non-native, sub-pixel scale. Because these are consts,
   /// re-basing them moves every consuming site at once.
   ///
   /// Page-level roles (page title/subtitle, KPI values, status labels) live as
   /// named styles in App.xaml and were re-based in the same change.
   /// </summary>
   public static class Typography
   {
      /// <summary>Small caption / hint text (e.g. card blurbs, status lines). Fluent Caption.</summary>
      public const double Caption = 12;

      /// <summary>Secondary text and field labels. Same size as Caption on the
      /// Fluent ramp - the old 12.5 sat between the two rungs and landed on
      /// neither.</summary>
      public const double Label = 12;

      /// <summary>Default body text and input-control text. Fluent Body.</summary>
      public const double Body = 14;

      /// <summary>Checkboxes and slightly emphasised body text. Fluent Body -
      /// emphasis on the ramp is carried by weight (SemiBold), not by a
      /// half-pixel size bump.</summary>
      public const double Control = 14;

      /// <summary>List/item titles. Fluent Body; pair with SemiBold.</summary>
      public const double ItemTitle = 14;

      /// <summary>Card / section headings inside a page. Fluent Body Strong is
      /// this size with SemiBold, which is how every consumer already uses it.</summary>
      public const double SectionHeading = 14;

      /// <summary>Modal dialog header. Fluent Subtitle.</summary>
      public const double DialogTitle = 20;

      /// <summary>The UI face. Segoe UI Variable is the Windows 11 face and the
      /// single cheapest "looks native" property in the application; classic
      /// Segoe UI is the Windows 10 fallback. Nothing set any FontFamily at all
      /// before this existed, which left classic Segoe UI everywhere.</summary>
      public const string UiFontFamily = "Segoe UI Variable Text, Segoe UI";

      /// <summary>The one monospace stack. There were four divergent ones
      /// (Consolas alone, two orderings of Cascadia/Consolas, and one with a
      /// css-ish "monospace" entry WPF ignores).</summary>
      public const string MonoFontFamily = "Cascadia Mono, Consolas";
   }
}
