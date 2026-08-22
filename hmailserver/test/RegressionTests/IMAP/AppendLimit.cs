// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   /// <summary>
   ///    APPENDLIMIT (RFC 7889). The APPEND size limit has always been enforced -
   ///    global maximum, tightened per-domain, capped at a hard 2 GB - but it was
   ///    undiscoverable, so a client learnt it by uploading a message and failing.
   ///    Now CAPABILITY advertises the bare APPENDLIMIT form before login (the
   ///    limit depends on the account's domain, so the number is not yet known),
   ///    the exact APPENDLIMIT=n form after login, and STATUS answers the
   ///    APPENDLIMIT item - which the bare form obliges the server to do.
   /// </summary>
   [TestFixture]
   public class AppendLimit : TestFixtureBase
   {
      private int _originalGlobalMaxKB;

      [SetUp]
      public new void SetUp()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "appendlimit@example.test", "test");
         _originalGlobalMaxKB = _settings.MaxMessageSize;
      }

      [TearDown]
      public void TearDownLimits()
      {
         _settings.MaxMessageSize = _originalGlobalMaxKB;
      }

      [Test]
      [Description("Before login the limit is unknown, so CAPABILITY advertises the bare form.")]
      public void PreLoginCapabilityAdvertisesTheBareForm()
      {
         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A01 OK");

         StringAssert.Contains(" APPENDLIMIT", capabilities,
            "The pre-login CAPABILITY must advertise APPENDLIMIT. Got: " + capabilities);
         ClassicAssert.IsFalse(capabilities.Contains("APPENDLIMIT="),
            "The pre-login CAPABILITY must not claim a number - the limit depends on the " +
            "account's domain, which is not known yet. Got: " + capabilities);

         socket.Disconnect();
      }

      [Test]
      [Description("After login CAPABILITY carries the exact enforced limit in bytes.")]
      public void PostLoginCapabilityCarriesTheExactLimit()
      {
         _settings.MaxMessageSize = 1024; // KB

         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 LOGIN appendlimit@example.test test\r\n");
         socket.ReadUntil("A01 OK");

         socket.Send("A02 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A02 OK");

         StringAssert.Contains("APPENDLIMIT=1048576", capabilities,
            "The post-login CAPABILITY must carry the enforced limit - 1024 KB - in bytes. Got: " + capabilities);

         socket.Disconnect();
      }

      [Test]
      [Description("A tighter domain limit is the advertised limit, and STATUS reports the same number.")]
      public void TheDomainLimitTightensTheNumberAndStatusAgrees()
      {
         _settings.MaxMessageSize = 1024; // KB
         _domain.MaxMessageSize = 512;    // KB - tighter, so this one wins
         _domain.Save();

         try
         {
            var socket = new TcpConnection();
            ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
            socket.ReadUntil("* OK");

            socket.Send("A01 LOGIN appendlimit@example.test test\r\n");
            socket.ReadUntil("A01 OK");

            socket.Send("A02 CAPABILITY\r\n");
            var capabilities = socket.ReadUntil("A02 OK");

            StringAssert.Contains("APPENDLIMIT=524288", capabilities,
               "The domain's 512 KB maximum is the effective limit and must be the advertised one. Got: " + capabilities);

            // The bare pre-login advertisement obliges the server to answer this.
            socket.Send("A03 STATUS INBOX (APPENDLIMIT)\r\n");
            var status = socket.ReadUntil("A03 OK");

            StringAssert.Contains("APPENDLIMIT 524288", status,
               "STATUS (APPENDLIMIT) must report the same enforced limit. Got: " + status);

            socket.Disconnect();
         }
         finally
         {
            _domain.MaxMessageSize = 0;
            _domain.Save();
         }
      }

      /// <summary>
      ///    The control that keeps the number honest: with no configured maximum
      ///    the enforcement still refuses anything above 2 GB, so that - not 0,
      ///    which would mean "no APPEND accepted at all" - is what must be
      ///    advertised.
      /// </summary>
      [Test]
      [Description("An unlimited configuration advertises the hard 2 GB ceiling, not zero.")]
      public void AnUnlimitedConfigurationAdvertisesTheHardCeiling()
      {
         _settings.MaxMessageSize = 0;

         var socket = new TcpConnection();
         ClassicAssert.IsTrue(socket.Connect(143), "Could not connect to the IMAP server on port 143.");
         socket.ReadUntil("* OK");

         socket.Send("A01 LOGIN appendlimit@example.test test\r\n");
         socket.ReadUntil("A01 OK");

         socket.Send("A02 CAPABILITY\r\n");
         var capabilities = socket.ReadUntil("A02 OK");

         StringAssert.Contains("APPENDLIMIT=2147483648", capabilities,
            "With no configured maximum, the 2 GB absolute ceiling is the enforced limit " +
            "and must be the advertised one. Got: " + capabilities);

         socket.Disconnect();
      }
   }
}
