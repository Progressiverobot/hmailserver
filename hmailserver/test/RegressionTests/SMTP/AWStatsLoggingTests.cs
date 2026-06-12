using System;
using System.IO;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   [TestFixture]
   public class AWStatsLoggingTests : TestFixtureBase
   {
      [OneTimeSetUp]
      public void OneTimeSetUp()
      {
         var settings = SingletonProvider<TestSetup>.Instance.GetApp().Settings;
         _logging = settings.Logging;

         _logging.AWStatsEnabled = true;
         _logging.Enabled = true;
      }

      [SetUp]
      public new void SetUp()
      {
         if (File.Exists(_logging.CurrentAwstatsLog))
            File.Delete(_logging.CurrentAwstatsLog);
      }

      private Logging _logging;

      [Test]
      public void SuccessfulDeliveriesShouldBeLogged()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var localAddress = TestSetup.GetLocalIpAddress();
         var smtpClientSimulator = new SmtpClientSimulator(false, 25, localAddress);

         // Delivery from external to local.
         smtpClientSimulator.Send("test@external.com", "test@example.test", "Mail 1", "Mail 1");
         Pop3ClientSimulator.AssertMessageCount("test@example.test", "test", 1);

         // Verify that the delivery is logged
         var contents = TestSetup.ReadExistingTextFile(_logging.CurrentAwstatsLog);
         CustomAsserts.AssertDeleteFile(_logging.CurrentAwstatsLog);
         var expectedString = string.Format("\ttest@external.com\ttest@example.test\t{0}\t127.0.0.1\tSMTP\t?\t250\t",
            localAddress);
         StringAssert.Contains(expectedString, contents);

         // Verify there's just 1 logged line
         Assert.AreEqual(1, contents.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length);
      }

      [Test]
      public void FailedDeliveriesDueToAuthErrorShouldBeLogged()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         var localAddress = TestSetup.GetLocalIpAddress();
         var smtpClientSimulator = new SmtpClientSimulator(false, 25, localAddress);

         // Failed delivery from local to local.
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClientSimulator.Send("test@example.test", "test@example.test", "Mail 1", "Mail 1"));

         // Verify that the failed delivery is logged
         var contents = TestSetup.ReadExistingTextFile(_logging.CurrentAwstatsLog);
         CustomAsserts.AssertDeleteFile(_logging.CurrentAwstatsLog);
         var expectedString = string.Format("\ttest@example.test\ttest@example.test\t{0}\t127.0.0.1\tSMTP\t?\t530\t",
            localAddress);
         StringAssert.Contains(expectedString, contents);

         // Verify there's just 1 logged line
         Assert.AreEqual(1, contents.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length);
      }

      [Test]
      public void FailedDeliveriesDueToGlobalRulesShouldBeLogged()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");

         CreateDeleteAllMailRule();

         // Failed delivery from local to local.
         SmtpClientSimulator.StaticSend("test@example.test", "test@example.test", "Mail 1", "Mail 1");

         // Verify that the failed delivery is logged
         var contents = TestSetup.ReadExistingTextFile(_logging.CurrentAwstatsLog);
         CustomAsserts.AssertDeleteFile(_logging.CurrentAwstatsLog);
         var expectedString = "\ttest@example.test\ttest@example.test\t127.0.0.1\t127.0.0.1\tSMTP\t?\t550\t";
         StringAssert.Contains(expectedString, contents);

         // Verify there's just 1 logged line
         Assert.AreEqual(1, contents.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries).Length);
      }

      private void CreateDeleteAllMailRule()
      {
         var rule = SingletonProvider<TestSetup>.Instance.GetApp().Rules.Add();
         rule.Name = "Global rule test";
         rule.Active = true;

         var ruleCriteria = rule.Criterias.Add();
         ruleCriteria.UsePredefined = true;
         ruleCriteria.PredefinedField = eRulePredefinedField.eFTMessageSize;
         ruleCriteria.MatchType = eRuleMatchType.eMTGreaterThan;
         ruleCriteria.MatchValue = "0";
         ruleCriteria.Save();

         // Add action
         var ruleAction = rule.Actions.Add();
         ruleAction.Type = eRuleActionType.eRADeleteEmail;
         ruleAction.Save();

         // Save the rule in the database
         rule.Save();
      }
   }
}