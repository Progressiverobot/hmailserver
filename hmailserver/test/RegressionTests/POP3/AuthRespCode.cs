// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.POP3
{
   /// <summary>
   ///    AUTH-RESP-CODE (RFC 3206). An authentication failure carries [AUTH] so
   ///    a client can tell "the password is wrong" (reprompt the user) from "the
   ///    server is having trouble" (retry silently later) without parsing prose.
   ///    The capability declaration is what entitles a client to interpret the
   ///    bracketed code, so CAPA advertises AUTH-RESP-CODE - and IMPLEMENTATION
   ///    (RFC 2449 6.9) ships in the same breath, deliberately without a version.
   /// </summary>
   [TestFixture]
   public class AuthRespCode : TestFixtureBase
   {
      private Account _account;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "authcode@example.test", "test");
      }

      private static TcpConnection ConnectAndGreet()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(110), "Could not connect to the POP3 server on port 110.");
         socket.ReadUntil("+OK");
         return socket;
      }

      [Test]
      [Description("A rejected password answers -ERR [AUTH], the code RFC 3206 defines for exactly this")]
      public void ARejectedPasswordCarriesTheAuthCode()
      {
         var socket = ConnectAndGreet();

         socket.Send("USER authcode@example.test\r\n");
         socket.ReadUntil("+OK");

         socket.Send("PASS thewrongpassword\r\n");
         var response = socket.ReadUntil("\r\n");

         StringAssert.Contains("-ERR", response,
            "The wrong password must be refused. Got: " + response);
         StringAssert.Contains("[AUTH]", response,
            "RFC 3206: a credential failure must carry [AUTH]. Got: " + response);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      [Description("CAPA advertises AUTH-RESP-CODE and IMPLEMENTATION")]
      public void CapaAdvertisesTheCapabilities()
      {
         var socket = ConnectAndGreet();

         socket.Send("CAPA\r\n");
         var capabilities = socket.ReadUntil("\r\n.\r\n");

         StringAssert.Contains("AUTH-RESP-CODE", capabilities,
            "A client is not entitled to interpret [AUTH] unless this is advertised. Got: " + capabilities);
         StringAssert.Contains("IMPLEMENTATION hMailServer", capabilities,
            "RFC 2449 6.9 - and the name only: the patch level is deliberately not disclosed. Got: " + capabilities);
         ClassicAssert.IsFalse(capabilities.Contains("IMPLEMENTATION hMailServer "),
            "No version or anything else after the name - disclosing the patch level hands an " +
            "attacker a lookup key into fixed-in-version advisories. Got: " + capabilities);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      [Description("Negative control: a successful login carries no bracketed code, and neither does an ordinary command error")]
      public void SuccessAndOrdinaryErrorsCarryNoCode()
      {
         var socket = ConnectAndGreet();

         socket.Send("USER authcode@example.test\r\n");
         socket.ReadUntil("+OK");

         socket.Send("PASS test\r\n");
         var loggedIn = socket.ReadUntil("\r\n");
         StringAssert.Contains("+OK", loggedIn, "The login must succeed. Got: " + loggedIn);
         ClassicAssert.IsFalse(loggedIn.Contains("[AUTH]"),
            "A success carries no failure code. Got: " + loggedIn);

         // An ordinary TRANSACTION-state error is not an authentication verdict
         // and must not look like one.
         socket.Send("RETR 999\r\n");
         var noSuchMessage = socket.ReadUntil("\r\n");
         StringAssert.Contains("-ERR", noSuchMessage, "Got: " + noSuchMessage);
         ClassicAssert.IsFalse(noSuchMessage.Contains("[AUTH]"),
            "'No such message' is not a credential failure. Got: " + noSuchMessage);

         socket.Send("QUIT\r\n");
         socket.Disconnect();
      }

      [Test]
      [Description("The tenth failed attempt on one connection disconnects - and still says [AUTH] on the way out")]
      public void TheDisconnectingRefusalCarriesTheCodeToo()
      {
         var socket = ConnectAndGreet();

         string lastResponse = "";
         for (int attempt = 0; attempt < 10; attempt++)
         {
            socket.Send("USER authcode@example.test\r\n");
            socket.ReadUntil("+OK");

            socket.Send("PASS stillwrong" + attempt + "\r\n");
            lastResponse = socket.ReadUntil("\r\n");
         }

         StringAssert.Contains("[AUTH]", lastResponse,
            "The final, disconnecting refusal is still a credential failure. Got: " + lastResponse);
         StringAssert.Contains("Too many invalid logon attempts", lastResponse,
            "And it should be the too-many-attempts variant. Got: " + lastResponse);

         socket.Disconnect();
      }
   }
}
