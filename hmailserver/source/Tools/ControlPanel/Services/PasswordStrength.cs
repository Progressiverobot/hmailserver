// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Lightweight password-strength heuristic for the account dialog: length plus
   /// character-class variety. Deliberately simple and offline (no dictionary), it
   /// is meant to steer admins away from obviously weak passwords, not to be a
   /// cryptographic strength oracle.
   /// </summary>
   public static class PasswordStrength
   {
      public enum Level
      {
         Empty,
         Weak,
         Fair,
         Strong
      }

      public static (Level Level, string Summary) Evaluate(string password)
      {
         if (string.IsNullOrEmpty(password))
            return (Level.Empty, "");

         int length = password.Length;
         bool lower = password.Any(char.IsLower);
         bool upper = password.Any(char.IsUpper);
         bool digit = password.Any(char.IsDigit);
         bool symbol = password.Any(c => !char.IsLetterOrDigit(c));
         int classes = (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0);

         var missing = new List<string>();
         if (length < 12) missing.Add(L("12+ characters"));
         if (!upper) missing.Add(L("an uppercase letter"));
         if (!lower) missing.Add(L("a lowercase letter"));
         if (!digit) missing.Add(L("a digit"));
         if (!symbol) missing.Add(L("a symbol"));

         Level level;
         if (length < 8 || classes <= 1)
            level = Level.Weak;
         else if (length >= 12 && classes >= 3)
            level = Level.Strong;
         else
            level = Level.Fair;

         string summary = level switch
         {
            Level.Strong => L("Strong password."),
            Level.Fair => missing.Count > 0
               ? F("Fair password \u2013 consider adding {0}.", string.Join(", ", missing))
               : L("Fair password."),
            _ => F("Weak password \u2013 add {0}.", string.Join(", ", missing)),
         };

         return (level, summary);
      }
   }
}
