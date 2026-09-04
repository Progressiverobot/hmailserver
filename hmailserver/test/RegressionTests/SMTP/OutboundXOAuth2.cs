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
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    Outbound XOAUTH2 - the Microsoft 365 Basic-auth cutover item (December
   ///    2026). When a route's destination is on the configured OAuth host list,
   ///    the outbound AUTH step presents a bearer token in Microsoft's XOAUTH2
   ///    shape instead of LOGIN with the password; everywhere else, nothing
   ///    changes. Both halves are asserted on the EXACT BYTES the relay receives,
   ///    decoded from the AUTH line - because the blob's precise shape
   ///    (user=...\x01auth=Bearer ...\x01\x01, base64, one round trip) is the
   ///    entire interoperability contract with Exchange Online.
   ///
   ///    The token here is a fixed one (OutboundOAuth2FixedToken), the setting
   ///    that exists for tokens obtained outside this server - which also makes
   ///    the test independent of any identity provider. The fetch-and-cache path
   ///    shares everything downstream of the token's origin.
   /// </summary>
   [TestFixture]
   public class OutboundXOAuth2 : TestFixtureBase
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

      private Route AddAuthedRoute(int port)
      {
         var route = _settings.Routes.Add();
         route.DomainName = "oauth-relay.test";
         route.TargetSMTPHost = "localhost";
         route.TargetSMTPPort = port;
         route.NumberOfTries = 1;
         route.MinutesBetweenTry = 5;
         route.RelayerRequiresAuth = true;
         route.RelayerAuthUsername = "relay-account@example.test";
         route.SetRelayerAuthPassword("basic-password");
         route.Save();
         return route;
      }

      [Test]
      [Description("A destination on the OAuth host list gets AUTH XOAUTH2 with the exact Microsoft blob.")]
      public void TheConfiguredHostGetsTheBearerBlob()
      {
         WriteSetting("OutboundOAuth2Hosts", "localhost");
         WriteSetting("OutboundOAuth2FixedToken", "test-bearer-token-12345");
         _application.Reinitialize();

         try
         {
            var smtpServerPort = TestSetup.GetNextFreePort();
            AddAuthedRoute(smtpServerPort);

            var deliveryResults = new Dictionary<string, int> { ["dummy@oauth-relay.test"] = 250 };

            using (var server = new SmtpServerSimulator(1, smtpServerPort))
            {
               server.AddRecipientResult(deliveryResults);
               server.StartListen();

               SmtpClientSimulator.StaticSend("sender@example.test", "dummy@oauth-relay.test", "OAuth relay", "Body");

               server.WaitForCompletion();

               Assert.IsNotEmpty(server.XOAuth2Blob,
                  "The relay never received AUTH XOAUTH2 although its host is on the OAuth list.");

               string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(server.XOAuth2Blob));

               Assert.AreEqual("user=relay-account@example.test\u0001auth=Bearer test-bearer-token-12345\u0001\u0001", decoded,
                  "The XOAUTH2 blob is not the shape Exchange Online parses - user, ^A, auth=Bearer token, ^A^A.");

               Assert.IsTrue(server.MessageData.Contains("OAuth relay"),
                  "The message did not deliver after the bearer login succeeded.");
            }
         }
         finally
         {
            WriteSetting("OutboundOAuth2Hosts", "smtp.office365.com");
            WriteSetting("OutboundOAuth2FixedToken", "");
            _application.Reinitialize();
         }
      }

      /// <summary>
      ///    The negative control: a destination NOT on the host list keeps the
      ///    password login it always had. An implementation that sent bearers
      ///    everywhere would pass the positive test and fail here - by leaking a
      ///    token to every relay the server talks to.
      /// </summary>
      [Test]
      [Description("A destination off the OAuth host list still authenticates with LOGIN - the control.")]
      public void AnUnlistedHostStillUsesLogin()
      {
         WriteSetting("OutboundOAuth2Hosts", "some-other-relay.example.net");
         WriteSetting("OutboundOAuth2FixedToken", "test-bearer-token-12345");
         _application.Reinitialize();

         try
         {
            var smtpServerPort = TestSetup.GetNextFreePort();
            AddAuthedRoute(smtpServerPort);

            var deliveryResults = new Dictionary<string, int> { ["dummy@oauth-relay.test"] = 250 };

            using (var server = new SmtpServerSimulator(1, smtpServerPort))
            {
               server.AddRecipientResult(deliveryResults);
               server.StartListen();

               SmtpClientSimulator.StaticSend("sender@example.test", "dummy@oauth-relay.test", "Password relay", "Body");

               server.WaitForCompletion();

               Assert.IsEmpty(server.XOAuth2Blob,
                  "A bearer token was presented to a relay that is not on the OAuth host list.");
               Assert.IsTrue(server.MessageData.Contains("Password relay"),
                  "The ordinary LOGIN delivery stopped working with OAuth configured for a different host.");
            }
         }
         finally
         {
            WriteSetting("OutboundOAuth2Hosts", "smtp.office365.com");
            WriteSetting("OutboundOAuth2FixedToken", "");
            _application.Reinitialize();
         }
      }
   }
}
