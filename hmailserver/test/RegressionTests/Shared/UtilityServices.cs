using NUnit.Framework;

namespace RegressionTests.Shared
{
   [TestFixture]
   public class UtilityServices : TestFixtureBase
   {
      [OneTimeSetUp]
      public void SetUpFixture()
      {
         SingletonProvider<TestSetup>.Instance.Authenticate();
      }

      [Test]
      [Description("Test usage of algorithm MD5.")]
      public void TestMD5Hashing()
      {
         var utilities = _application.Utilities;

         var passwordHash = utilities.MD5("Password");
      }

      [Test]
      public void TestStrongPasswords()
      {
         var utilities = _application.Utilities;

         // The password should not be a part of a user name.
         Assert.IsFalse(utilities.IsStrongPassword("testar@example.test", "testar"));

         // The password should be more than 4 characters long.
         Assert.IsFalse(utilities.IsStrongPassword("vaffe@example.test", "test"));

         // If the password is 6 characters or less, it should contain a number or special char
         Assert.IsFalse(utilities.IsStrongPassword("vaffe@example.test", "testar"));

         // Typically bad passwords...
         Assert.IsFalse(utilities.IsStrongPassword("vaffe@example.test", "secret"));
         Assert.IsFalse(utilities.IsStrongPassword("vaffe@example.test", "password"));
         Assert.IsFalse(utilities.IsStrongPassword("vaffe@example.test", "info"));
         Assert.IsFalse(utilities.IsStrongPassword("vaffe@example.test", "webmaster"));
         Assert.IsFalse(utilities.IsStrongPassword("vaffe@example.test", "admin"));
         Assert.IsFalse(utilities.IsStrongPassword("vaffe@example.test", "12345"));
         Assert.IsFalse(utilities.IsStrongPassword("vaffe@example.test", "qwerty"));

         // Contains more than 6 characters - OK
         Assert.IsTrue(utilities.IsStrongPassword("vaffe@example.test", "testarp"));

         // Contains more than 6 characters - OK
         Assert.IsTrue(utilities.IsStrongPassword("vaffe@example.test", "test_"));
      }
   }
}