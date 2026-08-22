// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   public class NumericFieldTests
   {
      [Fact]
      public void EmptyText_ReturnsTrue_NoValue()
      {
         bool result = NumericField.TryValidate("", "Port", 1, 65535, out int value, out bool hasValue, out string error);
         Assert.True(result);
         Assert.False(hasValue);
         Assert.Null(error);
      }

      [Fact]
      public void WhitespaceText_ReturnsTrue_NoValue()
      {
         bool result = NumericField.TryValidate("   ", "Port", 1, 65535, out _, out bool hasValue, out _);
         Assert.True(result);
         Assert.False(hasValue);
      }

      [Fact]
      public void ValidNumber_ReturnsTrue_WithValue()
      {
         bool result = NumericField.TryValidate("25", "Port", 1, 65535, out int value, out bool hasValue, out string error);
         Assert.True(result);
         Assert.True(hasValue);
         Assert.Equal(25, value);
         Assert.Null(error);
      }

      [Fact]
      public void BelowMin_ReturnsFalse_WithError()
      {
         bool result = NumericField.TryValidate("0", "Port", 1, 65535, out _, out _, out string error);
         Assert.False(result);
         Assert.NotNull(error);
         Assert.Contains("1", error);
         Assert.Contains("65535", error);
      }

      [Fact]
      public void NonNumericText_ReturnsFalse_WithError()
      {
         bool result = NumericField.TryValidate("abc", "Port", 1, 65535, out _, out _, out string error);
         Assert.False(result);
         Assert.NotNull(error);
      }
   }
}
