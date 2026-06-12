// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.MIME
{
   [TestFixture]
   public class Parameters : TestFixtureBase
   {
      [Test]
      [Description("Issue 238, If charset parameter contains double quotes, the string isn't parsed properly.")]
      public void TestFetchCharsetInQuotesWithSpaceAfter()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address,
            "From: test@example.test\r\n" +
            "Content-Type: text/plain; charset =\"iso-8859-1\" \r\n" +
            "\r\n" +
            "Test\r\n");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator(account.Address, "test", "Inbox");
         var result = sim.Fetch("1 BODYSTRUCTURE");
         sim.Disconnect();

         Assert.IsTrue(result.Contains("(\"CHARSET\" \"iso-8859-1\")"), result);
      }

      [Test]
      [Description("Issue 238, If charset parameter contains double quotes, the string isn't parsed properly.")]
      public void TestFetchCharsetInQuotesWithSpaceBefore()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address,
            "From: test@example.test\r\n" +
            "Content-Type: text/plain; charset = \"iso-8859-1\"\r\n" +
            "\r\n" +
            "Test\r\n");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator(account.Address, "test", "Inbox");
         var result = sim.Fetch("1 BODYSTRUCTURE");
         sim.Disconnect();

         Assert.IsTrue(result.Contains("(\"CHARSET\" \"iso-8859-1\")"), result);
      }

      [Test]
      [Description("Issue 238, If charset parameter contains double quotes, the string isn't parsed properly.")]
      public void TestFetchCharsetInQuotesWithoutQuotesWithSpace()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address,
            "From: test@example.test\r\n" +
            "Content-Type: text/plain; charset = iso-8859-1 \r\n" +
            "\r\n" +
            "Test\r\n");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator(account.Address, "test", "Inbox");
         var result = sim.Fetch("1 BODYSTRUCTURE");
         sim.Disconnect();

         Assert.IsTrue(result.Contains("(\"CHARSET\" \"iso-8859-1\")"), result);
      }

      [Test]
      [Description("Issue 238, If charset parameter contains double quotes, the string isn't parsed properly.")]
      public void TestFetchCharsetInQuotesWithoutQuotesWithoutSpace()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address,
            "From: test@example.test\r\n" +
            "Content-Type: text/plain; charset=iso-8859-1 \r\n" +
            "\r\n" +
            "Test\r\n");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator(account.Address, "test", "Inbox");
         var result = sim.Fetch("1 BODYSTRUCTURE");
         sim.Disconnect();

         Assert.IsTrue(result.Contains("(\"CHARSET\" \"iso-8859-1\")"), result);
      }

      [Test]
      [Description("Issue 238, If charset parameter contains double quotes, the string isn't parsed properly.")]
      public void TestFetchCharsetInQuotesWithoutSpace()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         SmtpClientSimulator.StaticSendRaw(account.Address, account.Address,
            "From: test@example.test\r\n" +
            "Content-Type: text/plain; charset =\"iso-8859-1\"\r\n" +
            "\r\n" +
            "Test\r\n");

         ImapClientSimulator.AssertMessageCount(account.Address, "test", "Inbox", 1);

         var sim = new ImapClientSimulator(account.Address, "test", "Inbox");
         var result = sim.Fetch("1 BODYSTRUCTURE");
         sim.Disconnect();

         Assert.IsTrue(result.Contains("(\"CHARSET\" \"iso-8859-1\")"), result);
      }
   }
}