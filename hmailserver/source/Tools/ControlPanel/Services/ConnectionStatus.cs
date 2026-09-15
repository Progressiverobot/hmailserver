// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using static hMailServer.ControlPanel.Services.Loc;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>The state of the link between the Control Panel and the server, as the shell shows it.</summary>
   public enum LinkState
   {
      /// <summary>No session: the sign-in page is showing.</summary>
      NotConnected,

      /// <summary>A session, and its last probe answered.</summary>
      Connected,

      /// <summary>The link broke and the session is authenticating again.</summary>
      Reconnecting,

      /// <summary>The link broke and the reconnect failed, or was not attempted because the service is stopped.</summary>
      Lost
   }

   /// <summary>
   /// What the server status pill in the command bar says for each link state:
   /// a status level (which gives it a colour and a shape through
   /// <see cref="StatusSemantics"/>) and a word. WPF-free so a test can hold
   /// every state to a distinct word and the right severity - the pill is the
   /// only thing on the window that says whether the server is there, and a
   /// state that rendered as another state's colour would be a lie told
   /// permanently.
   /// </summary>
   public static class ConnectionStatus
   {
      /// <summary>Every state, in the order a session moves through them. Only used by the tests today.</summary>
      public static IReadOnlyList<LinkState> AllStates { get; } = new[]
      {
         LinkState.NotConnected,
         LinkState.Connected,
         LinkState.Reconnecting,
         LinkState.Lost
      };

      /// <summary>
      /// Not connected is Normal rather than a warning: the sign-in page is the
      /// ordinary first screen, not a fault. Reconnecting is a warning because the
      /// page on screen may be stale; Lost is critical because nothing on screen
      /// can be trusted or saved.
      /// </summary>
      public static StatusLevel LevelFor(LinkState state)
      {
         switch (state)
         {
            case LinkState.Connected:
               return StatusLevel.Good;
            case LinkState.Reconnecting:
               return StatusLevel.Warning;
            case LinkState.Lost:
               return StatusLevel.Critical;
            default:
               return StatusLevel.Normal;
         }
      }

      /// <summary>The word on the pill.</summary>
      public static string WordFor(LinkState state)
      {
         switch (state)
         {
            case LinkState.Connected:
               return L("Connected");
            case LinkState.Reconnecting:
               return L("Reconnecting…");
            case LinkState.Lost:
               return L("Connection lost");
            default:
               return L("Not connected");
         }
      }

      /// <summary>
      /// The sentence a screen reader hears for the pill: the word, and for a live
      /// session the host and the account, which is what "connected" means.
      /// </summary>
      public static string Describe(LinkState state, string host, string userName)
      {
         if (state == LinkState.Connected && !string.IsNullOrEmpty(host))
            return F("Connected to {0} as {1}", host, userName ?? "");

         return WordFor(state);
      }
   }
}
