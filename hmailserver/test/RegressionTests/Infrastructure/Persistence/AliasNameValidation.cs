using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure.Persistence
{
   [TestFixture]
   public class AliasNameValidation : TestFixtureBase
   {
      [Test]
      public void TestAliasWithSpecialCharacters1()
      {
         AssertValidAliasAddress("user+mailbox@example.test");
      }

      [Test]
      public void TestAliasWithSpecialCharacters2()
      {
         AssertValidAliasAddress("customer/department=shipping@example.test");
      }

      [Test]
      public void TestAliasWithSpecialCharacters3()
      {
         AssertValidAliasAddress("$A12345@example.test");
      }

      [Test]
      public void TestAliasWithSpecialCharacters4()
      {
         AssertValidAliasAddress("!def!xyz%abc@example.test");
      }

      [Test]
      public void TestAliasWithSpecialCharacters5()
      {
         AssertValidAliasAddress("_somename@example.test");
      }

      [Test]
      public void TestAliasWithSpecialCharacters6()
      {
         AssertValidAliasAddress("!#$%&'*+-/=?^_`.{|}~@example.test");
      }

      private void AssertValidAliasAddress(string address)
      {
         SingletonProvider<TestSetup>.Instance.AddAlias(_domain, address, "secret@example.com");
      }
   }
}