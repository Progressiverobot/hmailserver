using System.Runtime.InteropServices;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure.Persistence
{
   [TestFixture]
   public class AccountNameValidation : TestFixtureBase
   {
      [Test]
      public void TestAccountContainingBackwardSlashInMailbox()
      {
         AssertInvalidEmailAddress("\\@example.test");
      }

      [Test]
      public void TestAccountContainingForwardSlashInMailbox()
      {
         AssertInvalidEmailAddress("/@example.test");
      }

      [Test]
      public void TestAccountContainingBackwardSlashInDomainName()
      {
         AssertInvalidEmailAddress("john@te\\st.com");
      }

      [Test]
      public void TestAccountContainingForwardSlashInDomainName()
      {
         AssertInvalidEmailAddress("john@te//st.com");
      }


      [Test]
      public void TestAccountContainingSpaceInMailboxNameWithoutQuotes()
      {
         AssertInvalidEmailAddress("John Smith@example.test");
      }

      [Test]
      public void TestAccountContainingSpaceInMailboxNameWithQuotes()
      {
         AssertInvalidEmailAddress("\"JohnSmith\"@example.test",
            "Failed to save object. The account address may not contain spaces or quotes.");
      }

      [Test]
      public void TestAccountContainingSpaceInMailboxNameWithQuoteAndSpace()
      {
         AssertInvalidEmailAddress("\"John Smith\"@example.test",
            "Failed to save object. The account address may not contain spaces or quotes.");
      }

      [Test]
      public void TestAccountContainingSlashInMailboxNameWithQuotes()
      {
         AssertInvalidEmailAddress("\"John\\Smith\"@example.test");
      }

      [Test]
      public void TestAccountContainingSpecialCharacters1()
      {
         AssertValidEmailAddress("user+mailbox@example.test");
      }

      [Test]
      public void TestAccountContainingSpecialCharacters2()
      {
         AssertInvalidEmailAddress("customer/department=shipping@example.test");
      }

      [Test]
      public void TestAccountContainingSpecialCharacters3()
      {
         AssertValidEmailAddress("$A12345@example.test");
      }

      [Test]
      public void TestAccountContainingSpecialCharacters4()
      {
         AssertValidEmailAddress("!def!xyz%abc@example.test");
      }

      [Test]
      public void TestAccountContainingSpecialCharacters5()
      {
         AssertValidEmailAddress("_somename@example.test");
      }

      [Test]
      public void TestAccountContainingSpecialCharacters6()
      {
         AssertInvalidEmailAddress("!#$%&'*+-/=?^_`.{|}~@example.test");
      }

      [Test]
      public void TestAccountWithoutAddress()
      {
         AssertInvalidEmailAddress("");
      }

      [Test]
      public void TestAccountBelongingToAnotherDomain()
      {
         AssertInvalidEmailAddress("");

         var exception = Assert.Throws<COMException>(() =>
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@other.example.com", "secret"));
         Assert.AreEqual("Failed to save object. The account address domain does not match the owning domain name.",
            exception.Message);
      }

      [Test]
      public void TestAccountWithLeadingDotInLocalPart()
      {
         AssertInvalidEmailAddress(".user@example.test");
      }

      [Test]
      public void TestAccountWithTrailingDotInLocalPart()
      {
         AssertInvalidEmailAddress("user.@example.test");
      }

      [Test]
      public void TestAccountWithConsecutiveDotsInLocalPart()
      {
         AssertInvalidEmailAddress("us..er@example.test");
      }

      [Test]
      public void TestAccountWithSingleDotInLocalPart()
      {
         AssertValidEmailAddress("us.er@example.test");
      }

      [Test]
      public void TestAccountWithMaxLengthLocalPart()
      {
         // 64-char local part is the RFC 5321 maximum and should be accepted
         AssertValidEmailAddress("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@example.test");
      }

      [Test]
      public void TestAccountWithTooLongLocalPart()
      {
         // 65-char local part exceeds the RFC 5321 maximum
         // Supported for backwards compatibility.
         AssertValidEmailAddress("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa@example.test");
      }

      /// <summary>
      ///    A colon in the local part, which was accepted until 15 August 2026.
      ///
      ///    It belongs with the other path characters this fixture rejects for exactly
      ///    the same reason as them, not a special one. The local part becomes a
      ///    DIRECTORY component of the message store path - PersistentMessage builds
      ///    data\domain\localpart\(two guid chars)\guid.eml - and CreateDirectory
      ///    refuses a name containing a colon outright ("The directory name is
      ///    invalid"). So an account at "a:b@example.test" used to save without
      ///    complaint, appear in every list, accept mail at RCPT TO, and then fail when
      ///    the message was filed: a mailbox that could never work, created silently.
      ///
      ///    Recorded because the first version of this comment was wrong, and wrong in
      ///    a way worth not repeating: a colon opens an NTFS alternate data stream only
      ///    where the component is a FILE name. Here it is an intermediate directory,
      ///    two components above the file, so the outcome is a plain refusal and not a
      ///    hidden stream. Measured on Windows 11 rather than reasoned about.
      ///
      ///    The colon must still be legal in the DOMAIN half of the expression, where
      ///    an IPv6 address literal needs it.
      /// </summary>
      [Test]
      [Description("A colon in the local part is refused, because the local part is a directory name in the " +
                   "message store and Windows cannot create a directory containing one.")]
      public void TestAccountContainingColonInMailbox()
      {
         AssertInvalidEmailAddress("a:b@example.test");
      }

      [Test]
      [Description("A colon at the start of the local part is refused for the same reason.")]
      public void TestAccountContainingLeadingColonInMailbox()
      {
         AssertInvalidEmailAddress(":stream@example.test");
      }

      /// <summary>
      ///    The negative control for the two above. A quoted local part is a different
      ///    branch of the same expression, and excluding the colon from one class and
      ///    not the other would leave the whole guard bypassable by adding two quotes.
      /// </summary>
      [Test]
      [Description("A colon inside a QUOTED local part is refused too - quoting must not be a way round the rule.")]
      public void TestAccountContainingColonInQuotedMailbox()
      {
         AssertInvalidEmailAddress("\"a:b\"@example.test");
      }

      private void AssertInvalidEmailAddress(string address,
         string expectedErrorMessage = "Failed to save object. The account address is not a valid email address.")
      {
         var exception = Assert.Throws<COMException>(() =>
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "secret"));
         Assert.AreEqual(expectedErrorMessage, exception.Message);
      }

      private void AssertValidEmailAddress(string address)
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, "secret");
      }
   }
}