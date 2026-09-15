// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The server status pill in the command bar is the only thing on the window
   /// that says whether the server is there. These hold each link state to a
   /// distinct word and to the severity an administrator would give it - so a
   /// lost link is never drawn in the colour of a live one - and the accessible
   /// sentence to the facts it has to carry.
   /// </summary>
   public class ConnectionStatusTests
   {
      [Fact]
      public void EveryStateHasADistinctWordAndALevel()
      {
         Assert.Equal(Enum.GetValues<LinkState>().Length, ConnectionStatus.AllStates.Count);

         foreach (LinkState state in Enum.GetValues<LinkState>())
         {
            Assert.Contains(state, ConnectionStatus.AllStates);
            Assert.False(string.IsNullOrWhiteSpace(ConnectionStatus.WordFor(state)), state + " has no word.");
         }

         Assert.Equal(ConnectionStatus.AllStates.Count,
            ConnectionStatus.AllStates.Select(ConnectionStatus.WordFor).Distinct(StringComparer.Ordinal).Count());
      }

      [Fact]
      public void TheLevelsMatchWhatEachStateMeansToAnAdministrator()
      {
         // Not connected is the ordinary first screen, not a fault.
         Assert.Equal(StatusLevel.Normal, ConnectionStatus.LevelFor(LinkState.NotConnected));
         Assert.Equal(StatusLevel.Good, ConnectionStatus.LevelFor(LinkState.Connected));
         // The page on screen may be stale while the session heals.
         Assert.Equal(StatusLevel.Warning, ConnectionStatus.LevelFor(LinkState.Reconnecting));
         // Nothing on screen can be trusted or saved.
         Assert.Equal(StatusLevel.Critical, ConnectionStatus.LevelFor(LinkState.Lost));
      }

      [Fact]
      public void OnlyALiveLinkIsDrawnAsGood()
      {
         foreach (LinkState state in ConnectionStatus.AllStates.Where(s => s != LinkState.Connected))
            Assert.NotEqual(StatusLevel.Good, ConnectionStatus.LevelFor(state));
      }

      [Fact]
      public void TheSentenceNamesTheHostAndTheAccountForALiveLinkAndOnlyThen()
      {
         string connected = ConnectionStatus.Describe(LinkState.Connected, "mail.example.test", "Administrator");
         Assert.Contains("mail.example.test", connected);
         Assert.Contains("Administrator", connected);

         foreach (LinkState state in new[] { LinkState.NotConnected, LinkState.Reconnecting, LinkState.Lost })
         {
            string sentence = ConnectionStatus.Describe(state, "mail.example.test", "Administrator");
            Assert.Equal(ConnectionStatus.WordFor(state), sentence);
         }

         // A live link with no host to name falls back to the word rather than
         // to a sentence with a hole in it.
         Assert.Equal(ConnectionStatus.WordFor(LinkState.Connected), ConnectionStatus.Describe(LinkState.Connected, null, null));
      }
   }
}
