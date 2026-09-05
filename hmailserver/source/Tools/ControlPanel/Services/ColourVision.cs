// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// How far apart two colours are to someone who cannot see them the way the
   /// designer did.
   ///
   /// The status colours (success, warning, danger) never carry a status alone -
   /// every level also has a shape and a word (see <see cref="StatusSemantics"/>)
   /// - but the colour is still the first thing a sighted person reads, and about
   /// one man in twelve reads red and green differently. This simulates the two
   /// common dichromacies with the Machado, Oliveira and Fernandes (2009)
   /// matrices at full severity, applied in linear light, and measures the
   /// distance between the results in CIELAB (ΔE*ab, CIE 1976). A ΔE of 8 is the
   /// floor this project holds its status pairs to; below about 5 two colours
   /// are near-indistinguishable in a glance, and the target leaves room for a
   /// cheap monitor.
   ///
   /// The maths is deliberately self-contained so a test can pin the palette
   /// without a colour library: sRGB decoding, the simulation matrix, sRGB to XYZ
   /// (D65) and XYZ to Lab. Precision is that of a display, not of a
   /// colorimeter, which is all a UI check needs.
   /// </summary>
   public static class ColourVision
   {
      /// <summary>The kinds of colour vision simulated.</summary>
      public enum Vision
      {
         Typical,
         Protanopia,
         Deuteranopia,
      }

      // Machado et al. 2009, severity 1.0, row-major, applied to linear RGB.
      private static readonly double[,] Protan =
      {
         { 0.152286, 1.052583, -0.204868 },
         { 0.114503, 0.786281, 0.099216 },
         { -0.003882, -0.048116, 1.051998 },
      };

      private static readonly double[,] Deutan =
      {
         { 0.367322, 0.860646, -0.227968 },
         { 0.280085, 0.672501, 0.047413 },
         { -0.011820, 0.042940, 0.968881 },
      };

      /// <summary>
      /// The CIELAB ΔE*ab between two ARGB colours as they would be seen with the
      /// given colour vision. 0 for identical colours; roughly 2.3 is the just-
      /// noticeable difference under ideal conditions.
      /// </summary>
      public static double DeltaE(uint argbA, uint argbB, Vision vision)
      {
         (double la, double aa, double ba) = ToLab(Simulate(ToLinear(argbA), vision));
         (double lb, double ab, double bb) = ToLab(Simulate(ToLinear(argbB), vision));
         return Math.Sqrt((la - lb) * (la - lb) + (aa - ab) * (aa - ab) + (ba - bb) * (ba - bb));
      }

      /// <summary>
      /// The colour as it would be seen with the given vision, re-encoded as ARGB
      /// (alpha preserved). Only used for display and diagnostics; the measurement
      /// above works in Lab without re-quantising.
      /// </summary>
      public static uint Simulate(uint argb, Vision vision)
      {
         (double r, double g, double b) = Simulate(ToLinear(argb), vision);
         return (argb & 0xFF000000u)
            | ((uint)Encode(r) << 16)
            | ((uint)Encode(g) << 8)
            | (uint)Encode(b);
      }

      private static (double r, double g, double b) Simulate((double r, double g, double b) lin, Vision vision)
      {
         if (vision == Vision.Typical)
            return lin;

         double[,] m = vision == Vision.Protanopia ? Protan : Deutan;
         return (
            m[0, 0] * lin.r + m[0, 1] * lin.g + m[0, 2] * lin.b,
            m[1, 0] * lin.r + m[1, 1] * lin.g + m[1, 2] * lin.b,
            m[2, 0] * lin.r + m[2, 1] * lin.g + m[2, 2] * lin.b);
      }

      private static (double r, double g, double b) ToLinear(uint argb)
      {
         return (
            Decode((argb >> 16) & 0xFF),
            Decode((argb >> 8) & 0xFF),
            Decode(argb & 0xFF));
      }

      private static double Decode(uint channel)
      {
         double c = channel / 255.0;
         return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
      }

      private static byte Encode(double linear)
      {
         double v = Math.Max(0.0, Math.Min(1.0, linear));
         double s = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055;
         return (byte)Math.Round(s * 255.0);
      }

      private static (double l, double a, double b) ToLab((double r, double g, double b) lin)
      {
         // Out-of-gamut results of the simulation are clipped the way a display
         // would clip them, so the measurement is of what would be shown.
         double r = Math.Max(0.0, Math.Min(1.0, lin.r));
         double g = Math.Max(0.0, Math.Min(1.0, lin.g));
         double b = Math.Max(0.0, Math.Min(1.0, lin.b));

         double x = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
         double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
         double z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b;

         double fx = F(x / 0.95047);
         double fy = F(y / 1.00000);
         double fz = F(z / 1.08883);

         return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
      }

      private static double F(double t)
      {
         return t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : 7.787 * t + 16.0 / 116.0;
      }
   }
}
