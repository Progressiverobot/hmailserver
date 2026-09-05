// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SSL
{
   /// <summary>
   ///    SASL EXTERNAL (RFC 4422 Appendix A) over a client certificate, on SMTP, IMAP
   ///    and POP3.
   ///
   ///    The mechanism carries no credential of its own: the proof is the certificate
   ///    the TLS handshake verified against the port's CA (TCPIPPort.ClientCertificatePolicy
   ///    and ClientCertificateCAFile), and the server maps the address that certificate
   ///    names - an rfc822Name subjectAltName, here - to a mailbox. So the fixture is
   ///    about three things: the mechanism is offered exactly when such a certificate is
   ///    on the connection, a certificate logs on as the mailbox it names and no other,
   ///    and a certificate that did not verify buys nothing at all.
   ///
   ///    The certificates are made in-process (ClientCertificateFactory) and the three
   ///    listeners are implicit-TLS ports added for the test and removed again in the
   ///    finally block, the way the inbound-client-certificate fixture does it.
   /// </summary>
   [TestFixture]
   public class ExternalSasl : TestFixtureBase
   {
      private const int PolicyRequest = 1;
      private const int PolicyRequire = 2;

      private Account _account;
      private bool _saslWasEnabled;

      [SetUp]
      public new void SetUp()
      {
         _account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mtls@" + _domain.Name, "test");

         // IMAP AUTHENTICATE sits behind one switch for every mechanism, and its shipped
         // default is off.
         _saslWasEnabled = _settings.IMAPSASLPlainEnabled;
         _settings.IMAPSASLPlainEnabled = true;
      }

      [TearDown]
      public new void TearDown()
      {
         _settings.IMAPSASLPlainEnabled = _saslWasEnabled;
      }

      [Test]
      public void SmtpOffersExternalToAVerifiedCertificateAndLogsItOnAsTheMailboxItNames()
      {
         using (var authority = ClientCertificateFactory.CreateSelfSignedAuthority("CN=hMailServer Regression Client CA"))
         using (var client = ClientCertificateFactory.IssueClientCertificate(authority, "CN=mtls client", _account.Address))
         {
            var caFile = ClientCertificateFactory.WriteAuthorityPemFile(authority);
            var port = TestSetup.GetNextFreePort();
            try
            {
               AddMutualTlsListeners(caFile, PolicyRequire, port, 0, 0);

               using (var session = TlsSession.Connect(port, client))
               {
                  Assert.That(session.ReadLine(), Does.StartWith("220"));
                  session.Send("EHLO regression");
                  var ehlo = session.ReadUntil(IsFinalSmtpLine);
                  Assert.That(ehlo, Does.Contain("EXTERNAL"), "EHLO did not offer EXTERNAL to a verified certificate: " + ehlo);

                  session.Send("AUTH EXTERNAL =");
                  var reply = session.ReadLine();
                  Assert.That(reply, Does.StartWith("235"), "The certificate naming the mailbox was refused: " + reply);

                  // Authenticated for real: a second AUTH is what RFC 4954 says it is.
                  session.Send("AUTH EXTERNAL =");
                  Assert.That(session.ReadLine(), Does.StartWith("503"));
               }
            }
            finally
            {
               RestoreDefaultPortsAndRebindListeners();
               CustomAsserts.AssertDeleteFile(caFile);
            }
         }
      }

      [Test]
      public void SmtpAcceptsAnAuthorizationIdentityOnlyWhenTheCertificateNamesIt()
      {
         using (var authority = ClientCertificateFactory.CreateSelfSignedAuthority("CN=hMailServer Regression Client CA"))
         using (var client = ClientCertificateFactory.IssueClientCertificate(authority, "CN=mtls client", _account.Address))
         {
            var caFile = ClientCertificateFactory.WriteAuthorityPemFile(authority);
            var port = TestSetup.GetNextFreePort();
            try
            {
               AddMutualTlsListeners(caFile, PolicyRequire, port, 0, 0);

               // The mailbox the certificate names, asked for by name: fine.
               using (var session = TlsSession.Connect(port, client))
               {
                  session.ReadLine();
                  session.Send("EHLO regression");
                  session.ReadUntil(IsFinalSmtpLine);
                  session.Send("AUTH EXTERNAL " + Base64(_account.Address.ToUpperInvariant()));
                  Assert.That(session.ReadLine(), Does.StartWith("235"));
               }

               // Somebody else: a certificate for one user is not a credential for another.
               var other = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "other-mtls@" + _domain.Name, "test");
               using (var session = TlsSession.Connect(port, client))
               {
                  session.ReadLine();
                  session.Send("EHLO regression");
                  session.ReadUntil(IsFinalSmtpLine);
                  session.Send("AUTH EXTERNAL " + Base64(other.Address));
                  Assert.That(session.ReadLine(), Does.StartWith("535"));
               }
            }
            finally
            {
               RestoreDefaultPortsAndRebindListeners();
               CustomAsserts.AssertDeleteFile(caFile);
            }
         }
      }

      [Test]
      public void SmtpRefusesACertificateThatNamesNoMailbox()
      {
         using (var authority = ClientCertificateFactory.CreateSelfSignedAuthority("CN=hMailServer Regression Client CA"))
         using (var client = ClientCertificateFactory.IssueClientCertificate(authority, "CN=mtls client", "nobody-mtls@" + _domain.Name))
         {
            var caFile = ClientCertificateFactory.WriteAuthorityPemFile(authority);
            var port = TestSetup.GetNextFreePort();
            try
            {
               AddMutualTlsListeners(caFile, PolicyRequire, port, 0, 0);

               using (var session = TlsSession.Connect(port, client))
               {
                  session.ReadLine();
                  session.Send("EHLO regression");
                  var ehlo = session.ReadUntil(IsFinalSmtpLine);
                  // The certificate verified and names an address, so the mechanism is
                  // offered; whether the address is a mailbox is the logon's question.
                  Assert.That(ehlo, Does.Contain("EXTERNAL"));

                  session.Send("AUTH EXTERNAL =");
                  Assert.That(session.ReadLine(), Does.StartWith("535"));
               }
            }
            finally
            {
               RestoreDefaultPortsAndRebindListeners();
               CustomAsserts.AssertDeleteFile(caFile);
            }
         }
      }

      [Test]
      public void ACertificateThatDidNotVerifyIsNotOfferedExternalAndCannotUseIt()
      {
         using (var authority = ClientCertificateFactory.CreateSelfSignedAuthority("CN=hMailServer Regression Client CA"))
         using (var stranger = ClientCertificateFactory.CreateSelfSignedClientCertificate("CN=stranger", _account.Address))
         {
            var caFile = ClientCertificateFactory.WriteAuthorityPemFile(authority);
            var port = TestSetup.GetNextFreePort();
            try
            {
               // "request": the handshake admits everyone, and the stranger's certificate
               // names the mailbox - which is exactly the case that must buy nothing.
               AddMutualTlsListeners(caFile, PolicyRequest, port, 0, 0);

               using (var session = TlsSession.Connect(port, stranger))
               {
                  Assert.That(session.ReadLine(), Does.StartWith("220"));
                  session.Send("EHLO regression");
                  var ehlo = session.ReadUntil(IsFinalSmtpLine);
                  Assert.That(ehlo, Does.Not.Contain("EXTERNAL"), "EHLO offered EXTERNAL to an unverified certificate: " + ehlo);

                  session.Send("AUTH EXTERNAL =");
                  Assert.That(session.ReadLine(), Does.StartWith("504"));
               }

               // And no certificate at all.
               using (var session = TlsSession.Connect(port, null))
               {
                  Assert.That(session.ReadLine(), Does.StartWith("220"));
                  session.Send("EHLO regression");
                  Assert.That(session.ReadUntil(IsFinalSmtpLine), Does.Not.Contain("EXTERNAL"));
               }
            }
            finally
            {
               RestoreDefaultPortsAndRebindListeners();
               CustomAsserts.AssertDeleteFile(caFile);
            }
         }
      }

      [Test]
      public void ImapAuthenticatesExternalWithAndWithoutAnInitialResponse()
      {
         using (var authority = ClientCertificateFactory.CreateSelfSignedAuthority("CN=hMailServer Regression Client CA"))
         using (var client = ClientCertificateFactory.IssueClientCertificate(authority, "CN=mtls client", _account.Address))
         {
            var caFile = ClientCertificateFactory.WriteAuthorityPemFile(authority);
            var port = TestSetup.GetNextFreePort();
            try
            {
               AddMutualTlsListeners(caFile, PolicyRequire, 0, port, 0);

               // SASL-IR: the empty initial response spelt "=".
               using (var session = TlsSession.Connect(port, client))
               {
                  Assert.That(session.ReadLine(), Does.StartWith("* OK"));
                  session.Send("A1 CAPABILITY");
                  var capability = session.ReadUntil(line => line.StartsWith("A1 "));
                  Assert.That(capability, Does.Contain("AUTH=EXTERNAL"), capability);

                  session.Send("A2 AUTHENTICATE EXTERNAL =");
                  Assert.That(session.ReadUntil(line => line.StartsWith("A2 ")), Does.Contain("A2 OK"));

                  session.Send("A3 SELECT INBOX");
                  Assert.That(session.ReadUntil(line => line.StartsWith("A3 ")), Does.Contain("A3 OK"));
               }

               // A continuation carrying the authorization identity.
               using (var session = TlsSession.Connect(port, client))
               {
                  session.ReadLine();
                  session.Send("A1 AUTHENTICATE EXTERNAL");
                  Assert.That(session.ReadLine(), Does.StartWith("+"));
                  session.Send(Base64(_account.Address));
                  Assert.That(session.ReadUntil(line => line.StartsWith("A1 ")), Does.Contain("A1 OK"));
               }

               // A continuation carrying nothing: the well-formed answer for "whoever the
               // certificate says", and the one an unprepared parser would ask for again
               // forever.
               using (var session = TlsSession.Connect(port, client))
               {
                  session.ReadLine();
                  session.Send("A1 AUTHENTICATE EXTERNAL");
                  Assert.That(session.ReadLine(), Does.StartWith("+"));
                  session.Send("");
                  Assert.That(session.ReadUntil(line => line.StartsWith("A1 ")), Does.Contain("A1 OK"));
               }
            }
            finally
            {
               RestoreDefaultPortsAndRebindListeners();
               CustomAsserts.AssertDeleteFile(caFile);
            }
         }
      }

      [Test]
      public void Pop3AuthenticatesExternal()
      {
         using (var authority = ClientCertificateFactory.CreateSelfSignedAuthority("CN=hMailServer Regression Client CA"))
         using (var client = ClientCertificateFactory.IssueClientCertificate(authority, "CN=mtls client", _account.Address))
         {
            var caFile = ClientCertificateFactory.WriteAuthorityPemFile(authority);
            var port = TestSetup.GetNextFreePort();
            try
            {
               AddMutualTlsListeners(caFile, PolicyRequire, 0, 0, port);

               using (var session = TlsSession.Connect(port, client))
               {
                  Assert.That(session.ReadLine(), Does.StartWith("+OK"));
                  session.Send("CAPA");
                  var capa = session.ReadUntil(line => line == ".");
                  Assert.That(capa, Does.Contain("EXTERNAL"), capa);

                  session.Send("AUTH EXTERNAL =");
                  Assert.That(session.ReadLine(), Does.StartWith("+OK"));

                  session.Send("STAT");
                  Assert.That(session.ReadLine(), Does.StartWith("+OK 0"));
               }

               // The two-step form, with the address as the continuation.
               using (var session = TlsSession.Connect(port, client))
               {
                  session.ReadLine();
                  session.Send("AUTH EXTERNAL");
                  Assert.That(session.ReadLine(), Does.StartWith("+"));
                  session.Send(Base64(_account.Address));
                  Assert.That(session.ReadLine(), Does.StartWith("+OK"));
               }
            }
            finally
            {
               RestoreDefaultPortsAndRebindListeners();
               CustomAsserts.AssertDeleteFile(caFile);
            }
         }
      }

      // ----------------------------------------------------------------------

      private static bool IsFinalSmtpLine(string line)
      {
         return line.Length >= 4 && line[3] == ' ';
      }

      private static string Base64(string text)
      {
         return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
      }

      /// <summary>
      ///    Adds implicit-TLS listeners with the given client-certificate policy for
      ///    whichever of the three protocols has a non-zero port, on a stopped server,
      ///    and starts it again.
      /// </summary>
      private void AddMutualTlsListeners(string caFile, int policy, int smtpPort, int imapPort, int pop3Port)
      {
         _application.Stop();
         try
         {
            var certificate = _settings.SSLCertificates.Add();
            certificate.Name = "ExternalSasl";
            certificate.CertificateFile = Paths.Combine(SslSetup.GetSslCertPath(), "example.crt");
            certificate.PrivateKeyFile = Paths.Combine(SslSetup.GetSslCertPath(), "example.key");
            certificate.Save();

            AddListener(smtpPort, eSessionType.eSTSMTP, certificate.ID, policy, caFile);
            AddListener(imapPort, eSessionType.eSTIMAP, certificate.ID, policy, caFile);
            AddListener(pop3Port, eSessionType.eSTPOP3, certificate.ID, policy, caFile);
         }
         finally
         {
            _application.Start();
         }
      }

      private void AddListener(int portNumber, eSessionType protocol, int certificateId, int policy, string caFile)
      {
         if (portNumber == 0)
            return;

         var port = _settings.TCPIPPorts.Add();
         port.Address = "127.0.0.1";
         port.PortNumber = portNumber;
         port.Protocol = protocol;
         port.ConnectionSecurity = eConnectionSecurity.eCSTLS;
         port.SSLCertificateID = certificateId;
         port.ClientCertificatePolicy = policy;
         port.ClientCertificateCAFile = caFile;
         port.Save();
      }

      /// <summary>
      ///    Puts the server back as every other fixture expects to find it: the default
      ///    ports, no SSL certificates, and the listeners rebound to that set.
      /// </summary>
      private void RestoreDefaultPortsAndRebindListeners()
      {
         _application.Stop();
         try
         {
            _settings.TCPIPPorts.SetDefault();
            _settings.SSLCertificates.Clear();
         }
         finally
         {
            _application.Start();
         }
      }

      /// <summary>
      ///    A TLS session to 127.0.0.1, optionally presenting a client certificate, read
      ///    and written line by line.
      /// </summary>
      private sealed class TlsSession : IDisposable
      {
         private readonly TcpClient _client;
         private readonly SslStream _ssl;
         private readonly StreamReader _reader;
         private readonly StreamWriter _writer;

         private TlsSession(TcpClient client, SslStream ssl)
         {
            _client = client;
            _ssl = ssl;
            _reader = new StreamReader(ssl, Encoding.ASCII, false, 4096, true);
            _writer = new StreamWriter(ssl, Encoding.ASCII, 4096, true) {AutoFlush = true, NewLine = "\r\n"};
         }

         public static TlsSession Connect(int port, X509Certificate2 clientCertificate)
         {
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            client.ReceiveTimeout = 20000;
            client.SendTimeout = 20000;

            LocalCertificateSelectionCallback selection = null;
            if (clientCertificate != null)
               selection = (sender, host, localCertificates, remoteCertificate, issuers) => clientCertificate;

            var ssl = new SslStream(client.GetStream(), false, (sender, certificate, chain, errors) => true, selection);
            var clientCertificates = clientCertificate == null
               ? new X509CertificateCollection()
               : new X509CertificateCollection(new X509Certificate[] {clientCertificate});
            ssl.AuthenticateAsClient("localhost", clientCertificates, TcpConnection.ModernProtocols, false);
            ssl.ReadTimeout = 20000;
            ssl.WriteTimeout = 20000;

            return new TlsSession(client, ssl);
         }

         public string ReadLine()
         {
            var line = _reader.ReadLine();
            Assert.IsNotNull(line, "The server closed the connection.");
            return line;
         }

         public string ReadUntil(Func<string, bool> isLast)
         {
            var text = new StringBuilder();
            while (true)
            {
               var line = ReadLine();
               text.AppendLine(line);
               if (isLast(line))
                  return text.ToString();
            }
         }

         public void Send(string line)
         {
            _writer.WriteLine(line);
         }

         public void Dispose()
         {
            _writer.Dispose();
            _reader.Dispose();
            _ssl.Dispose();
            _client.Close();
         }
      }
   }
}
