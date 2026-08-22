// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using hMailServer.ControlPanel.Services;
using Xunit;
using static hMailServer.ControlPanel.Services.PasswordStrength;

namespace hMailServer.ControlPanel.Tests.Services
{
   public class PasswordStrengthTests
   {
      [Fact]
      public void EmptyPassword_ReturnsEmpty()
      {
         var (level, summary) = PasswordStrength.Evaluate("");
         Assert.Equal(Level.Empty, level);
         Assert.Equal("", summary);
      }

      [Fact]
      public void NullPassword_ReturnsEmpty()
      {
         var (level, _) = PasswordStrength.Evaluate(null);
         Assert.Equal(Level.Empty, level);
      }

      [Fact]
      public void ShortSingleClass_ReturnsWeak()
      {
         var (level, _) = PasswordStrength.Evaluate("abc");
         Assert.Equal(Level.Weak, level);
      }

      [Fact]
      public void LongMixedPassword_ReturnsStrong()
      {
         var (level, summary) = PasswordStrength.Evaluate("MyP@ssw0rd123!");
         Assert.Equal(Level.Strong, level);
         Assert.Equal("Strong password.", summary);
      }

      [Fact]
      public void ModeratePassword_ReturnsFair()
      {
         var (level, _) = PasswordStrength.Evaluate("Password1");
         Assert.Equal(Level.Fair, level);
      }
   }
}
