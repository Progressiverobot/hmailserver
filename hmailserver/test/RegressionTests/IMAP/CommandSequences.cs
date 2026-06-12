// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Text;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   [TestFixture]
   public class CommandSequences : TestFixtureBase
   {
      [Test]
      public void TestBatchOfCommands()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "batch@example.test", "test");

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");

         var commandSequence = "";
         for (var i = 0; i < 200; i++) commandSequence += "A" + i + " SELECT INBOX\r\n";
         commandSequence = commandSequence.TrimEnd("\r\n".ToCharArray());

         var result = simulator.Send(commandSequence);
         Assert.IsFalse(result.StartsWith("* BYE"));

         simulator.Disconnect();

         sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         commandSequence = "";
         for (var i = 0; i < 500; i++) commandSequence += "A" + i + " SELECT INBOX\r\n";
         commandSequence = commandSequence.TrimEnd("\r\n".ToCharArray());

         result = simulator.Send(commandSequence);
         Assert.IsFalse(result.StartsWith("* BYE Excessive number of buffered commands"));
         simulator.Disconnect();
      }

      [Test]
      public void TestLongCommand()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "batch@example.test", "test");

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");

         var sb = new StringBuilder();

         for (var i = 0; i < 240000; i++) sb.Append("A");

         var result = simulator.Send("A01 " + sb);
         Assert.IsTrue(result.Length == 0 || result.StartsWith("A01"));
      }
   }
}