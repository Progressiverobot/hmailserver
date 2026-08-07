using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   public class PasswordGeneratorTests
   {
      [Fact]
      public void DefaultLength_Returns16Characters()
      {
         string password = PasswordGenerator.Generate();
         Assert.Equal(16, password.Length);
      }

      [Fact]
      public void RequestedLength_ReturnsCorrectLength()
      {
         string password = PasswordGenerator.Generate(20);
         Assert.Equal(20, password.Length);
      }

      [Fact]
      public void BelowMinimum_ClampsTo8()
      {
         string password = PasswordGenerator.Generate(4);
         Assert.Equal(8, password.Length);
      }

      [Fact]
      public void Generated_ContainsLowercase()
      {
         string password = PasswordGenerator.Generate(32);
         Assert.Contains(password, c => char.IsLower(c));
      }

      [Fact]
      public void Generated_ContainsUppercase()
      {
         string password = PasswordGenerator.Generate(32);
         Assert.Contains(password, c => char.IsUpper(c));
      }

      [Fact]
      public void Generated_ContainsDigit()
      {
         string password = PasswordGenerator.Generate(32);
         Assert.Contains(password, c => char.IsDigit(c));
      }

      [Fact]
      public void Generated_ContainsSymbol()
      {
         string password = PasswordGenerator.Generate(32);
         Assert.Contains(password, c => !char.IsLetterOrDigit(c));
      }
   }
}
