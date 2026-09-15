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
   /// The pill in a feature-settings page's header is what says whether the
   /// values on screen are the ones the service runs. These hold each state to
   /// a distinct word and to the severity an administrator would give it, so a
   /// failed restart is never drawn in the colour of an applied one.
   /// </summary>
   public class SettingsPageStatusTests
   {
      [Fact]
      public void EveryStateHasADistinctWordAndIsListed()
      {
         Assert.Equal(Enum.GetValues<SettingsPageState>().Length, SettingsPageStatus.AllStates.Count);

         foreach (SettingsPageState state in Enum.GetValues<SettingsPageState>())
         {
            Assert.Contains(state, SettingsPageStatus.AllStates);
            Assert.False(string.IsNullOrWhiteSpace(SettingsPageStatus.WordFor(state)), state + " has no word.");
         }

         Assert.Equal(SettingsPageStatus.AllStates.Count,
            SettingsPageStatus.AllStates.Select(SettingsPageStatus.WordFor).Distinct(StringComparer.Ordinal).Count());
      }

      [Fact]
      public void TheLevelsMatchWhatEachStateMeansToAnAdministrator()
      {
         // The ordinary state of the page, not a fault.
         Assert.Equal(StatusLevel.Normal, SettingsPageStatus.LevelFor(SettingsPageState.Editing));
         Assert.Equal(StatusLevel.Good, SettingsPageStatus.LevelFor(SettingsPageState.Saved));
         Assert.Equal(StatusLevel.Good, SettingsPageStatus.LevelFor(SettingsPageState.Applied));
         // The values on screen are not yet the server's.
         Assert.Equal(StatusLevel.Warning, SettingsPageStatus.LevelFor(SettingsPageState.Restarting));
         // The settings are live but the page cannot see the server.
         Assert.Equal(StatusLevel.Warning, SettingsPageStatus.LevelFor(SettingsPageState.NotConnected));
         // The file and the running service disagree, or there is no file at all.
         Assert.Equal(StatusLevel.Critical, SettingsPageStatus.LevelFor(SettingsPageState.RestartFailed));
         Assert.Equal(StatusLevel.Critical, SettingsPageStatus.LevelFor(SettingsPageState.Unavailable));
      }

      [Fact]
      public void OnlyAWrittenOrAppliedFileIsDrawnAsGood()
      {
         foreach (SettingsPageState state in SettingsPageStatus.AllStates
                     .Where(s => s != SettingsPageState.Saved && s != SettingsPageState.Applied))
            Assert.NotEqual(StatusLevel.Good, SettingsPageStatus.LevelFor(state));
      }
   }
}
