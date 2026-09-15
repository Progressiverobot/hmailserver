// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Exercises the configurable message-store durability barrier
   ///    (MessageStoreFsync, in the settings store): when enabled, a received
   ///    message is flushed all the way to physical disk before the spool file is
   ///    closed (the SMTP accept point). This test verifies that turning the
   ///    barrier on does not break normal delivery.
   /// </summary>
   [TestFixture]
   public class MessageStoreDurability : TestFixtureBase
   {

      private void WriteSetting(string key, string value)
      {
         IniFileSetting.Write(key, value);
      }

      [Test]
      [Description("With MessageStoreFsync enabled, a message is still received, persisted durably, and delivered.")]
      public void TestMessageDeliveredWithFsyncEnabled()
      {
         // Create the recipient before reinitializing (account creation is unaffected by the barrier).
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "fsync@example.test", "test");

         WriteSetting("MessageStoreFsync", "1");

         // Reinitialize (not just Stop/Start) so the cached MessageStoreFsync flag is re-read.
         _application.Reinitialize();

         try
         {
            ImapClientSimulator.AssertMessageCount("fsync@example.test", "test", "Inbox", 0);

            SmtpClientSimulator smtp = new SmtpClientSimulator();
            List<string> recipients = new List<string> { "fsync@example.test" };
            smtp.Send("fsync@example.test", recipients, "Durable", "Durable body written with fsync.");

            // Delivery must still succeed end-to-end with the durability barrier active.
            Pop3ClientSimulator.AssertMessageCount("fsync@example.test", "test", 1);
         }
         finally
         {
            WriteSetting("MessageStoreFsync", "0");
            _application.Reinitialize();
         }
      }
   }
}
