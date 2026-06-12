// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.AntiSpam
{
   [TestFixture]
   internal class WhiteListing : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         _antiSpam = _settings.AntiSpam;

         // We should always mark messages as spam.
         _antiSpam.SpamDeleteThreshold = 5;
         _antiSpam.SpamMarkThreshold = 2;

         _antiSpam.AddHeaderReason = true;
         _antiSpam.AddHeaderSpam = true;
         _antiSpam.PrependSubject = true;
         _antiSpam.PrependSubjectText = "ThisIsSpam";

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "whitelist@example.test", "test");
      }

      private const string SurblTestPointBody =
         "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-";

      private hMailServer.AntiSpam _antiSpam;

      [Test]
      public void TestEnabled()
      {
         var addresses = _antiSpam.WhiteListAddresses;
         var address = addresses.Add();

         address.EmailAddress = "whitelist@microsoft.com";
         address.LowerIPAddress = "0.0.0.0";
         address.UpperIPAddress = "255.255.255.255";
         address.Description = "Test";
         address.Save();

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         SmtpClientSimulator.StaticSend("whitelist@microsoft.com", "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");

         addresses.DeleteByDBID(address.ID);

         // Check that it's detected as spam again.
         CustomAsserts.Throws<DeliveryFailedException>(() => SmtpClientSimulator.StaticSend("whitelist@microsoft.com",
            "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-"));

         Pop3ClientSimulator.AssertMessageCount("whitelist@example.test", "test", 1);
      }

      [Test]
      public void TestFormatVariations()
      {
         var addresses = _antiSpam.WhiteListAddresses;

         var address = addresses.Add();

         var emailAddress = @"A%B/C\D_@microsoft.com";
         address.EmailAddress = emailAddress;
         Assert.AreEqual(emailAddress, address.EmailAddress);

         emailAddress = @"\%%%__\_@microsoft.com";
         address.EmailAddress = emailAddress;
         Assert.AreEqual(emailAddress, address.EmailAddress);
      }

      [Test]
      public void TestHelo()
      {
         var addresses = _antiSpam.WhiteListAddresses;
         var address = addresses.Add();

         address.EmailAddress = "whitelist@microsoft.com";
         address.LowerIPAddress = "0.0.0.0";
         address.UpperIPAddress = "255.255.255.255";
         address.Description = "Test";
         address.Save();

         // Test that we can send spam now.
         // Create a test account
         // Fetch the default domain

         _antiSpam.CheckHostInHelo = true;
         _antiSpam.CheckHostInHeloScore = 125;

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("whitelist@microsoft.com", "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");

         addresses.DeleteByDBID(address.ID);

         // Check that it's deteceted as spam again.
         CustomAsserts.Throws<DeliveryFailedException>(() => smtpClientSimulator.Send("whitelist@microsoft.com",
            "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-"));

         Pop3ClientSimulator.AssertMessageCount("whitelist@example.test", "test", 1);
      }


      [Test]
      public void TestWildcardEscapedCharacters()
      {
         var addresses = _antiSpam.WhiteListAddresses;

         var address = addresses.Add();
         address.EmailAddress = "white%li_st@microsoft.com";
         address.LowerIPAddress = "0.0.0.0";
         address.UpperIPAddress = "255.255.255.255";
         address.Description = "Test";
         address.Save();

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.

         SmtpClientSimulator.StaticSend("white%li_st@microsoft.com", "whitelist@example.test",
            "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");
         CustomAsserts.Throws<DeliveryFailedException>(() => SmtpClientSimulator.StaticSend("whiteAlist@micro_soft.com",
            "whitelist@example.test",
            "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-"));

         CustomAsserts.Throws<DeliveryFailedException>(() => SmtpClientSimulator.StaticSend("whiteAlist@microEsoft.com",
            "whitelist@example.test",
            "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-"));

         Pop3ClientSimulator.AssertMessageCount("whitelist@example.test", "test", 1);
      }

      [Test]
      public void TestWildcardQuestionMark()
      {
         var addresses = _antiSpam.WhiteListAddresses;

         var address = addresses.Add();
         address.EmailAddress = "whitelist@?icrosoft.com";
         address.LowerIPAddress = "0.0.0.0";
         address.UpperIPAddress = "255.255.255.255";
         address.Description = "Test";
         address.Save();


         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         SmtpClientSimulator.StaticSend("whitelist@microsoft.com", "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");
         CustomAsserts.Throws<DeliveryFailedException>(() => SmtpClientSimulator.StaticSend("whitelist@icrosoft.com",
            "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-"));
         SmtpClientSimulator.StaticSend("whitelist@microsoft.com", "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");
         CustomAsserts.Throws<DeliveryFailedException>(() => SmtpClientSimulator.StaticSend("whitelist@icrosoft.com",
            "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-"));

         Pop3ClientSimulator.AssertMessageCount("whitelist@example.test", "test", 2);
      }


      [Test]
      public void TestWildcardSingleQuote()
      {
         var addresses = _antiSpam.WhiteListAddresses;

         var address = addresses.Add();
         address.EmailAddress = "white'list@example.com";
         address.LowerIPAddress = "0.0.0.0";
         address.UpperIPAddress = "255.255.255.255";
         address.Description = "Test";
         address.Save();

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         SmtpClientSimulator.StaticSend("white'list@example.com", "whitelist@example.test",
            "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");
         CustomAsserts.Throws<DeliveryFailedException>(() => SmtpClientSimulator.StaticSend("whitelist@micro_soft.com",
            "whitelist@example.test",
            "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-"));

         Pop3ClientSimulator.AssertMessageCount("whitelist@example.test", "test", 1);
      }

      [Test]
      public void TestWildcardStar()
      {
         var addresses = _antiSpam.WhiteListAddresses;

         var address = addresses.Add();
         address.EmailAddress = "white*@microsoft.com";
         address.LowerIPAddress = "0.0.0.0";
         address.UpperIPAddress = "255.255.255.255";
         address.Description = "Test";
         address.Save();

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Send a messages to this account.
         SmtpClientSimulator.StaticSend("whitelist@microsoft.com", "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");
         CustomAsserts.Throws<DeliveryFailedException>(() => SmtpClientSimulator.StaticSend("blacklist@microsoft.com",
            "whitelist@example.test", "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-"));
         SmtpClientSimulator.StaticSend("whitesomething@microsoft.com", "whitelist@example.test",
            "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-");
         CustomAsserts.Throws<DeliveryFailedException>(() => SmtpClientSimulator.StaticSend(
            "blacksomething@microsoft.com", "whitelist@example.test",
            "SURBL-Match",
            "This is a test message with a SURBL url: -> http://surbl-org-permanent-test-point.com/ <-"));

         Pop3ClientSimulator.AssertMessageCount("whitelist@example.test", "test", 2);
      }

      [Test]
      public void TestWhitelistSpecificIPv6Address()
      {
         var addresses = GetAllLocalAddresses(AddressFamily.InterNetworkV6);

         if (addresses.Length == 0) Assert.Inconclusive("IPv6 not available.");

         var firstAddress = addresses[0];

         // Enable this port
         var tcpIpPort = _application.Settings.TCPIPPorts.Add();
         tcpIpPort.Address = firstAddress;
         tcpIpPort.Protocol = eSessionType.eSTSMTP;
         tcpIpPort.PortNumber = 25;
         tcpIpPort.Save();

         // Add an IP range for ALL ipv6 source port.
         foreach (var address in addresses)
         {
            var ipRange = _application.Settings.SecurityRanges.Add();
            ipRange.Name = "IPv6Range" + address;
            ipRange.AllowDeliveryFromLocalToLocal = true;
            ipRange.AllowDeliveryFromLocalToRemote = true;
            ipRange.AllowDeliveryFromRemoteToLocal = true;
            ipRange.AllowDeliveryFromRemoteToRemote = true;
            ipRange.AllowSMTPConnections = true;
            ipRange.RequireAuthForDeliveryToLocal = false;
            ipRange.EnableSpamProtection = true;
            ipRange.LowerIP = address;
            ipRange.UpperIP = address;
            ipRange.Save();
         }

         _application.Stop();
         _application.Start();

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Make sure we are now blacklisted.
         var smtpClient = new SmtpClientSimulator(false, 25, IPAddress.Parse(firstAddress));
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody));

         // White list all IPv6 addresses
         foreach (var ip in addresses)
         {
            var address = _antiSpam.WhiteListAddresses.Add();
            address.EmailAddress = "*";
            address.LowerIPAddress = ip;
            address.UpperIPAddress = ip;
            address.Description = "Test";
            address.Save();
         }

         // Make sure we can now send again.
         smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody);

         Pop3ClientSimulator.AssertMessageCount("whitelist@example.test", "test", 1);
      }

      [Test]
      public void TestWhitelistOutOfRangeIPv6Address()
      {
         var addresses = GetAllLocalAddresses(AddressFamily.InterNetworkV6);

         if (addresses.Length == 0) Assert.Inconclusive("IPv6 not available.");

         var firstAddress = addresses[0];

         // Enable this port
         var tcpIpPort = _application.Settings.TCPIPPorts.Add();
         tcpIpPort.Address = firstAddress;
         tcpIpPort.Protocol = eSessionType.eSTSMTP;
         tcpIpPort.PortNumber = 25;
         tcpIpPort.Save();

         // Add an IP range for ALL ipv6 source port.
         foreach (var ipAddress in addresses)
         {
            var ipRange = _application.Settings.SecurityRanges.Add();
            ipRange.Name = "IPv6Range" + ipAddress;
            ipRange.AllowDeliveryFromLocalToLocal = true;
            ipRange.AllowDeliveryFromLocalToRemote = true;
            ipRange.AllowDeliveryFromRemoteToLocal = true;
            ipRange.AllowDeliveryFromRemoteToRemote = true;
            ipRange.AllowSMTPConnections = true;
            ipRange.RequireAuthForDeliveryToLocal = false;
            ipRange.EnableSpamProtection = true;
            ipRange.LowerIP = ipAddress;
            ipRange.UpperIP = ipAddress;
            ipRange.Save();
         }

         _application.Stop();
         _application.Start();

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Make sure we are now blacklisted.
         var smtpClient = new SmtpClientSimulator(false, 25, IPAddress.Parse(firstAddress));
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody));

         // White list all IPv6 addresses
         var address = _antiSpam.WhiteListAddresses.Add();
         address.EmailAddress = "*";
         address.LowerIPAddress = "1111::1110";
         address.UpperIPAddress = "1111::1111";
         address.Description = "Test";
         address.Save();

         // Make sure we can now send again.
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody));
      }

      [Test]
      public void TestWhitelistSpecificIpV4Address()
      {
         var addresses = GetAllLocalAddresses(AddressFamily.InterNetwork);
         var firstAddress = addresses[0];

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Make sure we are now blacklisted.
         var smtpClient = new SmtpClientSimulator(false, 25, IPAddress.Parse(firstAddress));
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody));

         // White list all IPv4 addresses
         foreach (var ip in addresses)
         {
            var address = _antiSpam.WhiteListAddresses.Add();
            address.EmailAddress = "*";
            address.LowerIPAddress = ip;
            address.UpperIPAddress = ip;
            address.Description = "Test";
            address.Save();
         }

         // Make sure we can now send again.
         smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody);

         Pop3ClientSimulator.AssertMessageCount("whitelist@example.test", "test", 1);
      }

      [Test]
      public void TestWhitelistOutOfRangeAddress()
      {
         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Make sure we are now blacklisted.
         var smtpClient = new SmtpClientSimulator(false, 25);
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody));

         // White list all IPv4 addresses
         var address = _antiSpam.WhiteListAddresses.Add();
         address.EmailAddress = "*";
         address.LowerIPAddress = "1.1.1.1";
         address.UpperIPAddress = "1.1.1.5";
         address.Description = "Test";
         address.Save();

         // Make sure we are still blacklisted.
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody));
      }

      [Test]
      public void TestWhitelistAllIPv6Addresses()
      {
         var addresses = GetAllLocalAddresses(AddressFamily.InterNetworkV6);

         if (addresses.Length == 0) Assert.Inconclusive("IPv6 not available.");

         var firstAddress = addresses[0];

         // Enable this port
         var tcpIpPort = _application.Settings.TCPIPPorts.Add();
         tcpIpPort.Address = firstAddress;
         tcpIpPort.Protocol = eSessionType.eSTSMTP;
         tcpIpPort.PortNumber = 25;
         tcpIpPort.Save();

         // Add an IP range for ALL ipv6 source port.
         foreach (var ipAddress in addresses)
         {
            var ipRange = _application.Settings.SecurityRanges.Add();
            ipRange.Name = "IPv6Range" + ipAddress;
            ipRange.AllowDeliveryFromLocalToLocal = true;
            ipRange.AllowDeliveryFromLocalToRemote = true;
            ipRange.AllowDeliveryFromRemoteToLocal = true;
            ipRange.AllowDeliveryFromRemoteToRemote = true;
            ipRange.AllowSMTPConnections = true;
            ipRange.RequireAuthForDeliveryToLocal = false;
            ipRange.EnableSpamProtection = true;
            ipRange.LowerIP = ipAddress;
            ipRange.UpperIP = ipAddress;
            ipRange.Save();
         }

         _application.Stop();
         _application.Start();

         // Enable SURBL.
         var surblServer = _antiSpam.SURBLServers[0];
         surblServer.Active = true;
         surblServer.Score = 5;
         surblServer.Save();

         // Make sure we are now blacklisted.
         var smtpClient = new SmtpClientSimulator(false, 25, IPAddress.Parse(firstAddress));
         CustomAsserts.Throws<DeliveryFailedException>(() =>
            smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody));

         // White list all IPv6 addresses
         var address = _antiSpam.WhiteListAddresses.Add();
         address.EmailAddress = "*";
         address.LowerIPAddress = "::";
         address.UpperIPAddress = "ffff::ffff";
         address.Description = "Test";
         address.Save();

         // Make sure we can now send again.
         smtpClient.Send("user@example.com", "whitelist@example.test", "Hello", SurblTestPointBody);

         Pop3ClientSimulator.AssertMessageCount("whitelist@example.test", "test", 1);
      }

      [Test]
      public void TestSpamScoreHeadersAreNotCreatedIfNoSpamMatch()
      {
         // Send a messages to this account.
         var smtpClientSimulator = new SmtpClientSimulator();

         smtpClientSimulator.Send("whitelist@microsoft.com", "whitelist@example.test", "SURBL-Match",
            "Message body");

         var message = Pop3ClientSimulator.AssertGetFirstMessageText("whitelist@example.test", "test");
         Assert.IsFalse(message.Contains("X-hMailServer-Reason-Score"));
      }


      private string[] GetAllLocalAddresses(AddressFamily family)
      {
         var result = new List<string>();

         var strHostName = Dns.GetHostName();
         ;
         var ipEntry = Dns.GetHostEntry(strHostName);

         foreach (var address in ipEntry.AddressList)
            if (address.AddressFamily == family)
            {
               var addr = address.ToString();

               if (addr.Contains("%"))
                  result.Add(addr.Substring(0, addr.IndexOf("%", StringComparison.InvariantCulture)));
               else
                  result.Add(addr);
            }

         return result.ToArray();
      }
   }
}