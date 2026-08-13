using System;
using System.Collections.Generic;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>What the server does with a value the Control Panel lets an administrator set.</summary>
   public enum ClaimKind
   {
      /// <summary>The server reads the value and acts on it, but the interface has
      /// been claiming more than it does and the note says where the line is.</summary>
      Honoured,

      /// <summary>
      /// The server reads the value only when something else is not set, so the
      /// interface must show which one wins. A precedence that exists only in the
      /// server source is indistinguishable, from the outside, from a setting that
      /// does not work.
      /// </summary>
      Conditional,

      /// <summary>
      /// The server stores the value and never reads it. The page must not offer
      /// an editor for one of these: a control that accepts a value nothing acts
      /// on is the failure this whole file exists to stop.
      /// </summary>
      Inert
   }

   /// <summary>One setting, and the honest sentence the page has to print beside it.</summary>
   public sealed class SettingClaim
   {
      internal SettingClaim(string key, ClaimKind kind, string note)
      {
         Key = key;
         Kind = kind;
         Note = note;
      }

      /// <summary>
      /// The COM property path or hMailServer.INI key, spelled exactly as the
      /// settings pages and <see cref="SettingsSearchIndex"/> spell it, so a claim
      /// can be matched to the row it belongs to.
      /// </summary>
      public string Key { get; }

      public ClaimKind Kind { get; }

      /// <summary>The note shown under the editor. Never empty.</summary>
      public string Note { get; }

      /// <summary>True when the server stores the value and never reads it.</summary>
      public bool IsInert => Kind == ClaimKind.Inert;
   }

   /// <summary>
   /// The settings whose interface wording has to be checked against what the
   /// server actually does with them - and the wording itself, so that it is one
   /// piece of data a test can assert on rather than a string literal buried in a
   /// view.
   ///
   /// WHY THIS EXISTS. Two settings shipped in this interface for years while the
   /// server ignored them completely: <c>Logging.Device</c> offered "SQL", and
   /// <c>Logging.LogFormat</c> offered an NCSA format. An administrator selected
   /// one, got no error, and lost the function - which is worse than the feature
   /// being absent, because nobody goes looking for the reason something they
   /// configured is not happening. Both server-side gaps are now closed
   /// (SqlLogDevice, NcsaLogFormatter), and reading those two files is what turned
   /// up the remaining wording problems recorded here: the interface was still
   /// naming a format the server does not emit, still crediting it to AWStats,
   /// which that setting deliberately does not touch, and still offering an
   /// editable box for a thread priority nothing reads.
   ///
   /// The rule this file encodes: the interface may only claim what the server
   /// does. Where the server does less than the obvious reading of a control, the
   /// note says so; where the server does nothing at all, the page shows the value
   /// and refuses to let it be edited.
   ///
   /// WHY THE OPTION LABELS LIVE HERE and not in the view: the wrong claim was an
   /// entry in a combo box, not a setting name, so it was invisible to the
   /// generated search index and to every test. Moving the option text next to the
   /// statement of what the server does with each value is what lets
   /// SettingClaimsTests assert that the interface is not offering a format the
   /// server cannot produce.
   ///
   /// No WPF here, for the same reason as <see cref="NavigationMap"/>: the claims
   /// are the part worth testing.
   /// </summary>
   public static class SettingClaims
   {
      // ---- Logging.LogFormat -------------------------------------------------

      /// <summary>eLogOutputFormat hLogFormatDefault.</summary>
      public const int LogFormatDefault = 1;

      /// <summary>eLogOutputFormat hLogFormatCSA.</summary>
      public const int LogFormatNcsa = 2;

      /// <summary>
      /// What the "Log line format" combo may offer.
      ///
      /// The second entry used to read "NCSA / combined (AWStats)", and it was
      /// wrong twice over. NcsaLogFormatter emits the NCSA *Common* Log Format and
      /// explicitly rejects the "combined" variant, because hMailServer has
      /// nothing to put in the referer and user-agent fields that variant adds.
      /// And the setting has nothing to do with AWStats: the AWStats journal is a
      /// separate file written under its own toggle on this same card, and the
      /// formatter deliberately leaves it alone so that an existing AWStats
      /// installation keeps parsing it. An administrator who picked this entry to
      /// make AWStats work therefore changed the format of the wrong file - and
      /// the one setting that would have worked was three rows above.
      /// </summary>
      public static readonly IReadOnlyList<(int Value, string Label)> LogFormatOptions =
         new (int, string)[]
         {
            (LogFormatDefault, "hMailServer (tab separated)"),
            (LogFormatNcsa, "NCSA Common Log Format")
         };

      // ---- Logging.Device ----------------------------------------------------

      /// <summary>eLogDevice hLogDeviceSQL.</summary>
      public const int LogDeviceSql = 1;

      /// <summary>eLogDevice hLogDeviceFile.</summary>
      public const int LogDeviceFile = 2;

      /// <summary>
      /// What the "Log destination" combo may offer. Files first: it is the
      /// default and the one almost every installation wants, and a combo whose
      /// first entry is the unusual choice invites a mis-click.
      /// </summary>
      public static readonly IReadOnlyList<(int Value, string Label)> LogDeviceOptions =
         new (int, string)[]
         {
            (LogDeviceFile, "Files on disk"),
            (LogDeviceSql, "Database (SQL)")
         };

      /// <summary>
      /// Shown on the JSON-lines checkbox while NCSA is selected. Logger::Render_
      /// checks the format setting first, so NCSA wins and the JSON switch has no
      /// effect - a precedence that was previously visible nowhere.
      /// </summary>
      public const string JsonOverriddenByNcsa =
         "The NCSA log line format above takes precedence, so this has no effect while it is selected. "
         + "The setting is remembered and applies again as soon as the format goes back to hMailServer's own.";

      private static readonly List<SettingClaim> entries_ = new List<SettingClaim>
      {
         // Verified against the server: Configuration::GetWorkerThreadPriority has
         // exactly one caller, the COM getter, and there is no SetThreadPriority
         // call anywhere in the tree. The value goes into the settings table and
         // is read back out again, and that is all that happens to it.
         new SettingClaim("WorkerThreadPriority", ClaimKind.Inert,
            "The server stores this value and never reads it - nothing sets a thread priority from it, so "
            + "changing it has no effect. It is shown rather than removed so that an administrator who set it "
            + "years ago can see it is still there and why nothing happened, and it is not editable because a "
            + "box that accepts a value nothing acts on is worse than no box at all. The thread counts above "
            + "are honoured."),

         // IniFileSettings reads [Settings] UseLanguage and hands it back over
         // COM; nothing else in the server consults it. The classic Administrator
         // was the tool that did, and it was removed in 6.2.10.
         new SettingClaim("UserInterfaceLanguage", ClaimKind.Conditional,
            "This is [Settings] UseLanguage in hMailServer.INI. The server only hands it back over the COM API - "
            + "it does not translate anything itself - and this Control Panel has no translations, so it affects "
            + "third-party administration tools only. Bounce and error wording is on the Server messages page."),

         new SettingClaim("Logging.LogFormat", ClaimKind.Honoured,
            "NCSA Common Log Format writes one line per entry as host, ident, session, [date], \"category and "
            + "message\", status, bytes - so a log analyser can count 5xx replies without knowing anything about "
            + "hMailServer. Ident and bytes are always \"-\" (the server never queries identd and the logger is "
            + "not told transfer sizes), and the AWStats journal is a separate file that this setting does not "
            + "change: its own switch is above."),

         new SettingClaim("Logging.Device", ClaimKind.Honoured,
            "The database destination creates its table on first use and inserts asynchronously, so a log write "
            + "never blocks a mail session. If the database is unreachable the entries go to the log files "
            + "instead and the server says so in the application log - nothing is discarded."),

         new SettingClaim("JsonLogging", ClaimKind.Conditional, JsonOverriddenByNcsa),

         new SettingClaim("OtelEndpoint", ClaimKind.Honoured,
            "Traces only. The exporter posts to /v1/traces; there is no metrics or logs exporter, so a collector "
            + "configured here receives spans and nothing else. Prometheus metrics are the port above.")
      };

      /// <summary>Every setting whose interface wording is pinned by a test.</summary>
      public static IReadOnlyList<SettingClaim> Entries => entries_;

      /// <summary>The claim for a setting, or null when it has none.</summary>
      public static SettingClaim For(string key)
      {
         if (string.IsNullOrEmpty(key))
            return null;

         foreach (SettingClaim claim in entries_)
         {
            if (string.Equals(claim.Key, key, StringComparison.OrdinalIgnoreCase))
               return claim;
         }

         return null;
      }

      /// <summary>The note for a setting, or an empty string when it has none.</summary>
      public static string NoteFor(string key) => For(key)?.Note ?? "";

      /// <summary>True when the server stores this setting and never reads it.</summary>
      public static bool IsInert(string key) => For(key)?.IsInert == true;
   }
}
