// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Security.Cryptography;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Generates strong random passwords using a cryptographic RNG. Ambiguous
   /// characters (0/O, 1/l/I) are omitted so the result is easy to read and type.
   /// </summary>
   public static class PasswordGenerator
   {
      private const string Lower = "abcdefghijkmnpqrstuvwxyz";
      private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
      private const string Digits = "23456789";
      private const string Symbols = "!@#$%^&*-_=+?";

      /// <summary>
      /// Returns a random password of the given length (minimum 8) that always
      /// contains at least one lower-case letter, upper-case letter, digit and symbol.
      /// </summary>
      public static string Generate(int length = 16)
      {
         if (length < 8)
            length = 8;

         string all = Lower + Upper + Digits + Symbols;
         var chars = new char[length];

         // Guarantee one of each class, then fill the rest from the full set.
         chars[0] = Pick(Lower);
         chars[1] = Pick(Upper);
         chars[2] = Pick(Digits);
         chars[3] = Pick(Symbols);
         for (int i = 4; i < length; i++)
            chars[i] = Pick(all);

         Shuffle(chars);
         return new string(chars);
      }

      private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

      private static void Shuffle(char[] chars)
      {
         for (int i = chars.Length - 1; i > 0; i--)
         {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
         }
      }
   }
}
