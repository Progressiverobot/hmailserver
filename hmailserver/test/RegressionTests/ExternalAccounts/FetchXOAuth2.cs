// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.ExternalAccounts
{
   /// <summary>
   ///    XOAUTH2 for POP3 fetching - the other half of the Microsoft 365
   ///    Basic-auth story: collecting FROM outlook.office365.com with USER/PASS
   ///    has been off since 2022. When an external account's server is on
   ///    FetchOAuth2Hosts, the fetcher logs in with a bearer token in the same
   ///    one-line shape the outbound relay uses; everywhere else, USER/PASS is
   ///    untouched. Asserted on the exact bytes the POP3 server receives, and on
   ///    the messages actually arriving afterwards - a login that succeeds but
   ///    fetches nothing is not a login worth having.
   /// </summary>
   [TestFixture]
   public class FetchXOAuth2 : TestFixtureBase
   {

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      private FetchAccount AddFetchAccount(Account account, int port)
      {
         var fa = account.FetchAccounts.Add();
         fa.DaysToKeepMessages = 5;
         fa.Enabled = true;
         fa.MinutesBetweenFetch = 10;
         fa.Name = "oauth-fetch";
         fa.Port = port;
         fa.ServerAddress = "127.0.0.1";
         fa.Username = "fetch-user@example.test";
         fa.UseSSL = false;
         fa.Save();
         return fa;
      }

      [Test]
      [Description("A fetch server on the OAuth host list gets AUTH XOAUTH2 with the exact blob, and the mail arrives.")]
      public void TheConfiguredHostGetsTheBearerBlob()
      {
         WriteSetting("FetchOAuth2Hosts", "127.0.0.1");
         WriteSetting("OutboundOAuth2FixedToken", "fetch-bearer-98765");
         _application.Reinitialize();

         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(
               _domain, "fetch-oauth@example.test", "test");

            int port = TestSetup.GetNextFreePort();
            var fa = AddFetchAccount(account, port);

            var messages = new List<string> { "Subject: Fetched over OAuth\r\n\r\nBody.\r\n" };

            using (var pop3Server = new Pop3ServerSimulator(1, port, messages))
            {
               pop3Server.StartListen();
               fa.DownloadNow();
               pop3Server.WaitForCompletion();

               Assert.IsNotEmpty(pop3Server.XOAuth2Blob,
                  "The fetch never presented AUTH XOAUTH2 although its server is on FetchOAuth2Hosts.");
               Assert.IsFalse(pop3Server.UserCommandReceived,
                  "USER was sent alongside the bearer - the password path must not run for an OAuth host.");

               string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(pop3Server.XOAuth2Blob));
               Assert.AreEqual("user=fetch-user@example.test\u0001auth=Bearer fetch-bearer-98765\u0001\u0001", decoded,
                  "The XOAUTH2 blob is not the shape the provider parses.");
            }

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);
            Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);
         }
         finally
         {
            WriteSetting("FetchOAuth2Hosts", "outlook.office365.com");
            WriteSetting("OutboundOAuth2FixedToken", "");
            _application.Reinitialize();
         }
      }

      /// <summary>
      ///    The negative control: a server off the list keeps USER/PASS. An
      ///    implementation that presented bearers to every POP3 server would pass
      ///    the positive test and fail here - by leaking tokens to every mailbox
      ///    this server collects from.
      /// </summary>
      [Test]
      [Description("A fetch server off the OAuth host list still logs in with USER/PASS - the control.")]
      public void AnUnlistedHostStillUsesUserPass()
      {
         WriteSetting("FetchOAuth2Hosts", "some-other-provider.example.net");
         WriteSetting("OutboundOAuth2FixedToken", "fetch-bearer-98765");
         _application.Reinitialize();

         try
         {
            var account = SingletonProvider<TestSetup>.Instance.AddAccount(
               _domain, "fetch-plain@example.test", "test");

            int port = TestSetup.GetNextFreePort();
            var fa = AddFetchAccount(account, port);

            var messages = new List<string> { "Subject: Fetched with a password\r\n\r\nBody.\r\n" };

            using (var pop3Server = new Pop3ServerSimulator(1, port, messages))
            {
               pop3Server.StartListen();
               fa.DownloadNow();
               pop3Server.WaitForCompletion();

               Assert.IsEmpty(pop3Server.XOAuth2Blob,
                  "A bearer token was presented to a POP3 server that is not on FetchOAuth2Hosts.");
               Assert.IsTrue(pop3Server.UserCommandReceived,
                  "The ordinary USER/PASS login stopped working with OAuth configured for a different host.");
            }

            CustomAsserts.AssertRecipientsInDeliveryQueue(0);
            Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);
         }
         finally
         {
            WriteSetting("FetchOAuth2Hosts", "outlook.office365.com");
            WriteSetting("OutboundOAuth2FixedToken", "");
            _application.Reinitialize();
         }
      }
   }
}
