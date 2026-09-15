// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>Where an hMailServer.INI settings page stands, as its header pill shows it.</summary>
   public enum SettingsPageState
   {
      /// <summary>The file is open and the editors show what is in it.</summary>
      Editing,

      /// <summary>hMailServer.INI is not on this machine; the page can show nothing and save nothing.</summary>
      Unavailable,

      /// <summary>The file has been written; the service has not been restarted.</summary>
      Saved,

      /// <summary>The service is being stopped and started.</summary>
      Restarting,

      /// <summary>The restart failed; the file holds the new values, the running service the old.</summary>
      RestartFailed,

      /// <summary>The service came back but the Control Panel could not reattach to it.</summary>
      NotConnected,

      /// <summary>The service restarted with the saved values.</summary>
      Applied
   }

   /// <summary>
   /// The level and the word of the pill on a feature-settings page, per state.
   /// WPF-free so a test can hold every state to a distinct word and the right
   /// severity - the pill is what says whether what is on screen is what the
   /// server runs, and a state drawn in another state's colour would say the
   /// wrong thing permanently.
   /// </summary>
   public static class SettingsPageStatus
   {
      /// <summary>Every state, in the order a save moves through them. Only used by the tests today.</summary>
      public static IReadOnlyList<SettingsPageState> AllStates { get; } = new[]
      {
         SettingsPageState.Editing,
         SettingsPageState.Unavailable,
         SettingsPageState.Saved,
         SettingsPageState.Restarting,
         SettingsPageState.RestartFailed,
         SettingsPageState.NotConnected,
         SettingsPageState.Applied
      };

      /// <summary>
      /// Editing is Normal: the ordinary state of the page, not a fault. Saved
      /// and Applied are Good. Restarting is a warning because the values on
      /// screen are not yet the server's; a failed restart is critical because
      /// the file and the service now disagree; a lost reattach is a warning
      /// because the settings are live but the page cannot see the server.
      /// </summary>
      public static StatusLevel LevelFor(SettingsPageState state)
      {
         switch (state)
         {
            case SettingsPageState.Saved:
            case SettingsPageState.Applied:
               return StatusLevel.Good;
            case SettingsPageState.Restarting:
            case SettingsPageState.NotConnected:
               return StatusLevel.Warning;
            case SettingsPageState.Unavailable:
            case SettingsPageState.RestartFailed:
               return StatusLevel.Critical;
            default:
               return StatusLevel.Normal;
         }
      }

      /// <summary>The word on the pill.</summary>
      public static string WordFor(SettingsPageState state)
      {
         switch (state)
         {
            case SettingsPageState.Unavailable:
               return L("Unavailable");
            case SettingsPageState.Saved:
               return L("Saved");
            case SettingsPageState.Restarting:
               return L("Restartingâ€¦");
            case SettingsPageState.RestartFailed:
               return L("Not restarted");
            case SettingsPageState.NotConnected:
               return L("Not connected");
            case SettingsPageState.Applied:
               return L("Applied");
            default:
               return L("Editing");
         }
      }
   }
}
