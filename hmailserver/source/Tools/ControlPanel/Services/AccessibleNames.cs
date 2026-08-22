// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// One editor on a settings page, as far as naming it is concerned: the words
   /// printed next to it, the card it sits in, and the setting it writes.
   /// </summary>
   public sealed class LabelledEditor
   {
      public LabelledEditor(string label, string group, string key)
      {
         Label = label ?? "";
         Group = group ?? "";
         Key = key ?? "";
      }

      /// <summary>The caption shown beside or above the control.</summary>
      public string Label { get; }

      /// <summary>The title of the card the control sits in. May be empty.</summary>
      public string Group { get; }

      /// <summary>COM path or INI key. Only used to tell two rows apart.</summary>
      public string Key { get; }
   }

   /// <summary>
   /// Works out the accessible name for each editor on a generated settings page.
   ///
   /// WHY THIS EXISTS: on the two table-driven settings pages and in the generated
   /// property dialog, a setting's wording is a <c>TextBlock</c> placed above the
   /// control and nothing connects the two. WPF does not infer that relationship,
   /// so UI Automation sees an unnamed edit box: a screen reader announces "edit"
   /// for essentially every server setting in the product. The controls did carry
   /// an <c>AutomationId</c> - which is for test automation and is not spoken -
   /// so the code looked as though it had been thought about. Checkboxes were the
   /// exception all along, because their label is their <c>Content</c> and a
   /// content control names itself.
   ///
   /// WHY IT TAKES THE WHOLE PAGE AT ONCE rather than naming one control at a
   /// time: a label is not always unique on its page, and the shipped case is the
   /// Protocols page, where "Max simultaneous connections (0 = unlimited)" and
   /// "Welcome banner (empty = default)" each appear three times - once on the
   /// SMTP card, once on IMAP, once on POP3. Announcing the bare label there would
   /// trade "unnamed" for "named the same as two other things", which is no
   /// better: a listener cannot tell which protocol they are setting. Those six
   /// controls were counted, not guessed - the duplicates on these pages are
   /// exactly those two labels, so this pass qualifies six rows and leaves the
   /// other 238 alone.
   ///
   /// WHY NOT QUALIFY EVERYTHING: a screen-reader user pays for every word, and
   /// most of these labels are already specific ("Spam mark threshold (score)").
   /// Prefixing all 244 of them with their card title would make the page slower
   /// to work through, which is a cost paid by the people this is for. Duplication
   /// is the condition that actually causes confusion, so duplication is the
   /// trigger.
   ///
   /// A label repeated on DIFFERENT pages - "Host" and "Port" on the SpamAssassin
   /// card of Anti-spam and the ClamAV card of Anti-virus - is deliberately not
   /// qualified: a reader is on one page at a time, and the palette, which is the
   /// one place those two do collide, already shows each result's navigation path.
   ///
   /// Deliberately free of WPF types, like ChartPalette and
   /// <see cref="NavigationMap"/>: the decision about what a control is called is
   /// the part worth testing, and it must be testable without a dispatcher.
   /// </summary>
   public static class AccessibleNames
   {
      /// <summary>
      /// Separates the qualifying card title from the label. A colon rather than
      /// a dash because screen readers pause on it, so "SpamAssassin: Host" is
      /// heard as two pieces of information rather than as one odd phrase.
      /// </summary>
      public const string Separator = ": ";

      /// <summary>
      /// The accessible name for each entry, in the order they were given. Never
      /// null, always the same length as <paramref name="editors"/>.
      ///
      /// An empty string means "leave this control alone": it is what an editor
      /// with no label of its own gets, and the caller must not set an empty
      /// accessible name - a control whose content already names it (a button, a
      /// checkbox) is better served by its content than by a blank override.
      /// </summary>
      public static IReadOnlyList<string> Resolve(IReadOnlyList<LabelledEditor> editors)
      {
         if (editors == null)
            return Array.Empty<string>();

         // How many rows share each label, counted over the whole page rather than
         // per card. Per card would find nothing: the duplicates that actually
         // confuse a listener are the Protocols page's connection limit and welcome
         // banner, which sit on three different cards in three different tabs.
         var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
         foreach (string label in editors.Select(e => Clean(e?.Label)).Where(l => l.Length != 0))
         {
            counts.TryGetValue(label, out int count);
            counts[label] = count + 1;
         }

         var names = new string[editors.Count];
         for (int i = 0; i < editors.Count; i++)
         {
            LabelledEditor editor = editors[i];
            string label = Clean(editor?.Label);

            if (label.Length == 0)
            {
               names[i] = "";
               continue;
            }

            names[i] = counts[label] > 1 ? Qualify(label, editor?.Group) : label;
         }

         return names;
      }

      /// <summary>
      /// "SpamAssassin: Host". Returns the label unchanged when there is no
      /// usable group, or when the label already carries the group's name - a
      /// checkbox reading "Use SpamAssassin" on the SpamAssassin card does not
      /// become clearer as "SpamAssassin: Use SpamAssassin".
      /// </summary>
      public static string Qualify(string label, string group)
      {
         string cleaned = Clean(label);
         string prefix = GroupPrefix(group);

         if (cleaned.Length == 0 || prefix.Length == 0)
            return cleaned;

         if (cleaned.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) >= 0)
            return cleaned;

         return prefix + Separator + cleaned;
      }

      /// <summary>
      /// The part of a card title worth saying out loud. Card titles carry a
      /// parenthetical gloss for the reader's benefit - "ClamAV (network daemon)",
      /// "ClamWin (local executable)", "ManageSieve (RFC 5804)" - and repeating
      /// that on every control inside the card is exactly the padding this class
      /// is trying not to add. The name in front of the bracket is the part that
      /// distinguishes one card from another.
      /// </summary>
      public static string GroupPrefix(string group)
      {
         string cleaned = Clean(group);
         if (cleaned.Length == 0)
            return "";

         int bracket = cleaned.IndexOf('(');
         if (bracket > 0)
            cleaned = cleaned.Substring(0, bracket).Trim();

         return cleaned;
      }

      /// <summary>
      /// Trims, and drops a trailing colon. Some labels are written with one
      /// because they read as a caption on screen; spoken, "Host:" is "Host
      /// colon".
      /// </summary>
      private static string Clean(string text)
      {
         if (string.IsNullOrEmpty(text))
            return "";

         string trimmed = text.Trim();
         while (trimmed.EndsWith(":", StringComparison.Ordinal))
            trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();

         return trimmed;
      }
   }
}
