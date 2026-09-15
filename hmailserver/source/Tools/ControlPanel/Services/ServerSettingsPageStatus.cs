// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>The state of the server settings page, as the pill in its header shows it.
/// Distinct from <see cref="SettingsPageState"/>, which is the lifecycle of an
/// hMailServer.INI page: that one is read, written and then waits on a service
/// restart, and this one is read and written over COM and takes effect as it is
/// saved. Two pages, two lifecycles, two types.</summary>
   public enum ServerSettingsPageState
   {
      /// <summary>What is on screen is what the server holds. Nothing to say, so no pill.</summary>
      Clean,

      /// <summary>An editor has been changed since the page was read or last saved.</summary>
      Unsaved,

      /// <summary>The writes are in progress.</summary>
      Saving,

      /// <summary>Every write succeeded.</summary>
      Saved,

      /// <summary>At least one write failed; the sentence beside the pill says how many.</summary>
      PartlySaved
   }

   /// <summary>
   /// What the status pill and the notice on a settings page say for each state:
   /// a status level (a colour and a shape through <see cref="StatusSemantics"/>)
   /// and a word for the pill, and the sentences the notice carries after a read
   /// or a save. WPF-free so a test can hold every state that shows a pill to a
   /// distinct word and the right severity - the pill is the one thing on the
   /// page that says whether what is on screen has reached the server, and a
   /// state drawn in another state's colour would be a lie told permanently.
   /// </summary>
   public static class ServerSettingsPageStatus
   {
      /// <summary>Every state, in the order a page moves through them. Only used by the tests today.</summary>
      public static IReadOnlyList<ServerSettingsPageState> AllStates { get; } = new[]
      {
         ServerSettingsPageState.Clean,
         ServerSettingsPageState.Unsaved,
         ServerSettingsPageState.Saving,
         ServerSettingsPageState.Saved,
         ServerSettingsPageState.PartlySaved
      };

      /// <summary>
      /// Unsaved is a warning because leaving the page loses the change; Saving
      /// is information because there is nothing to do but wait; a save with a
      /// failure is critical because what is on screen is not what the server
      /// holds and the reader has to find out which rows.
      /// </summary>
      public static StatusLevel LevelFor(ServerSettingsPageState state)
      {
         switch (state)
         {
            case ServerSettingsPageState.Unsaved:
               return StatusLevel.Warning;
            case ServerSettingsPageState.Saving:
               return StatusLevel.Information;
            case ServerSettingsPageState.Saved:
               return StatusLevel.Good;
            case ServerSettingsPageState.PartlySaved:
               return StatusLevel.Critical;
            default:
               return StatusLevel.Normal;
         }
      }

      /// <summary>The word on the pill; empty for the state that shows none.</summary>
      public static string WordFor(ServerSettingsPageState state)
      {
         switch (state)
         {
            case ServerSettingsPageState.Unsaved:
               return L("Unsaved changes");
            case ServerSettingsPageState.Saving:
               return L("Savingâ€¦");
            case ServerSettingsPageState.Saved:
               return L("Saved");
            case ServerSettingsPageState.PartlySaved:
               return L("Partly saved");
            default:
               return "";
         }
      }

      /// <summary>Whether the pill is shown at all. A clean page has nothing to say.</summary>
      public static bool ShowsPill(ServerSettingsPageState state) => state != ServerSettingsPageState.Clean;

      /// <summary>
      /// The sentence after a read: null when every value was read, which is the
      /// ordinary case and not worth a notice; otherwise how many were not and
      /// the first reason.
      /// </summary>
      public static string ReadSentence(int failedReads, string diagnosis)
      {
         if (failedReads <= 0)
            return null;

         return F("{0} setting(s) could not be read â€” {1}", failedReads, diagnosis ?? "");
      }

      /// <summary>
      /// The sentence after a save. hMailServer.ini is read when the service
      /// starts, so an INI-backed row does not take effect until it is restarted -
      /// the sentence says so rather than claiming everything applied.
      /// </summary>
      public static string SaveSentence(int saved, int failed, string timeText, bool iniWritten)
      {
         if (failed > 0)
            return F("Saved {0} settings, {1} could not be written.", saved, failed);

         string appliedNote = iniWritten
            ? L(" - server settings applied immediately; hMailServer.ini settings apply after a service restart.")
            : L(" - applied immediately.");

         return F("Saved {0} settings at {1}", saved, timeText ?? "") + appliedNote;
      }
   }
}
