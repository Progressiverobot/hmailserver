// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The transport encryption overview: the page that answers "is anything on this
   /// server carrying a password in the clear, and will it still serve a valid
   /// certificate next month".
   ///
   /// Four pages own the pieces and each is a correct editor for its own subject.
   /// What none of them can do is put the four together, and every behaviour pinned
   /// below only exists in the combination — each was read out of the server rather
   /// than assumed:
   ///
   ///   - SslContextInitializer::InitServer returns false when the certificate is
   ///     null, and TCPServer::Run gives up on it, so a TLS port with no certificate
   ///     assigned does not fall back to plaintext: the port never comes up;
   ///   - the AEAD-ONLY cipher preset excludes every CBC-mode suite, and TLS 1.0 and
   ///     1.1 have nothing else, so enabling them alongside it advertises versions
   ///     that then cannot complete a handshake;
   ///   - SecurityRange::GetRequireTLSForAuth is per IP range, not global, and it is
   ///     the only control that refuses a plaintext password whatever the port
   ///     permits;
   ///   - and port 25 has to be judged differently from every other port, because
   ///     requiring TLS there refuses mail from senders that cannot offer it.
   /// </summary>
   public class TlsPostureTests
   {
      private static TlsListener Listener(string protocol, int port, TlsListenerSecurity security,
         string certificate = "server cert")
      {
         return new TlsListener
         {
            Protocol = protocol,
            Address = "0.0.0.0",
            Port = port,
            Security = security,
            CertificateName = security == TlsListenerSecurity.None ? "" : certificate
         };
      }

      /// <summary>
      /// A well-configured server: submission and the mailbox protocols require
      /// encryption, port 25 offers it opportunistically, TLS 1.2 and 1.3 only.
      /// </summary>
      private static TlsPostureConfig Healthy()
      {
         return new TlsPostureConfig
         {
            Listeners = new List<TlsListener>
            {
               Listener("SMTP", 25, TlsListenerSecurity.StartTlsOptional),
               Listener("SMTP", 587, TlsListenerSecurity.StartTlsRequired),
               Listener("IMAP", 993, TlsListenerSecurity.Implicit),
               Listener("POP3", 995, TlsListenerSecurity.Implicit)
            },
            Certificates = new List<TlsCertificate>
            {
               new() { Name = "server cert", DaysRemaining = 200, InUse = true }
            },
            Tls12Enabled = true,
            Tls13Enabled = true,
            CipherList = "",
            VerifyRemoteCertificates = false,
            RangesTotal = 2,
            RangesRequiringTlsForAuth = 2
         };
      }

      /// <summary>hMailServer's stock port set: 25, 110, 143 and 587, none of them with TLS.</summary>
      private static TlsPostureConfig StockPorts()
      {
         TlsPostureConfig config = Healthy();
         config.Listeners = new List<TlsListener>
         {
            Listener("SMTP", 25, TlsListenerSecurity.None),
            Listener("POP3", 110, TlsListenerSecurity.None),
            Listener("IMAP", 143, TlsListenerSecurity.None),
            Listener("SMTP", 587, TlsListenerSecurity.None)
         };
         config.Certificates = new List<TlsCertificate>();
         config.RangesRequiringTlsForAuth = 0;
         return config;
      }

      private static bool HasNote(TlsPostureConfig config, StatusLevel level, string fragment) =>
         TlsPosture.Notes(config).Any(n => n.Level == level && n.Text.Contains(fragment));

      private static TlsListenerVerdict VerdictFor(TlsPostureConfig config, int port) =>
         TlsPosture.Listeners(config).Single(v => v.Listener.Port == port);

      // ---- port 25 is not the other ports -------------------------------------

      [Fact]
      public void OpportunisticStartTlsOnPortTwentyFiveIsCorrectRatherThanAFinding()
      {
         // Requiring TLS on 25 refuses mail from senders that cannot offer it, so
         // flagging this would train the reader to ignore the page.
         TlsPostureConfig config = Healthy();

         TlsListenerVerdict verdict = VerdictFor(config, 25);

         Assert.Equal(StatusLevel.Good, verdict.Level);
         Assert.Contains("correct arrangement for port 25", verdict.Detail);
      }

      [Fact]
      public void OpportunisticStartTlsOnASubmissionPortIsAFinding()
      {
         TlsPostureConfig config = Healthy();
         config.Listeners = new List<TlsListener> { Listener("SMTP", 587, TlsListenerSecurity.StartTlsOptional) };

         Assert.Equal(StatusLevel.Warning, VerdictFor(config, 587).Level);
         Assert.True(HasNote(config, StatusLevel.Warning, "STARTTLS is optional on"));
      }

      [Fact]
      public void PlaintextOnPortTwentyFiveIsAWarningAndOnAClientPortIsCritical()
      {
         TlsPostureConfig config = StockPorts();

         // Port 25 still accepts AUTH, so it is not nothing - but it is not the same
         // as a port whose entire purpose is authenticated clients.
         Assert.Equal(StatusLevel.Warning, VerdictFor(config, 25).Level);
         Assert.Equal(StatusLevel.Critical, VerdictFor(config, 110).Level);
         Assert.Equal(StatusLevel.Critical, VerdictFor(config, 143).Level);
         Assert.Equal(StatusLevel.Critical, VerdictFor(config, 587).Level);

         Assert.Contains("no encryption available", TlsPosture.Verdict(config));
         Assert.Equal(StatusLevel.Critical, TlsPosture.WorstLevel(config));
      }

      [Fact]
      public void PortTwentyFiveIsTheOnlyPortTreatedAsServerToServer()
      {
         Assert.False(TlsPosture.IsClientPort(Listener("SMTP", 25, TlsListenerSecurity.None)));
         Assert.True(TlsPosture.IsClientPort(Listener("SMTP", 587, TlsListenerSecurity.None)));
         Assert.True(TlsPosture.IsClientPort(Listener("POP3", 110, TlsListenerSecurity.None)));

         // Protocol and port both, so a POP3 listener that happens to be on 25 is
         // still judged as a client port.
         Assert.True(TlsPosture.IsClientPort(Listener("POP3", 25, TlsListenerSecurity.None)));
      }

      // ---- the states that are not "encrypted or not" -------------------------

      [Fact]
      public void ATlsPortWithNoCertificateIsReportedAsNotStartingRatherThanAsPlaintext()
      {
         // The distinction matters operationally: the administrator is not looking
         // for an unencrypted session, they are looking for a refused connection.
         TlsPostureConfig config = Healthy();
         config.Listeners = new List<TlsListener>
         {
            new() { Protocol = "IMAP", Port = 993, Security = TlsListenerSecurity.Implicit, CertificateName = "" }
         };

         TlsListenerVerdict verdict = VerdictFor(config, 993);

         Assert.Equal(StatusLevel.Critical, verdict.Level);
         Assert.Equal("Will not start", verdict.Word);
         Assert.Contains("HM5113", verdict.Detail);

         Assert.Contains("cannot serve it", TlsPosture.Verdict(config));
         Assert.True(HasNote(config, StatusLevel.Critical, "does not come up"));
      }

      [Fact]
      public void NoTlsVersionEnabledMeansNoTlsListenerCanNegotiate()
      {
         TlsPostureConfig config = Healthy();
         config.Tls10Enabled = false;
         config.Tls11Enabled = false;
         config.Tls12Enabled = false;
         config.Tls13Enabled = false;

         Assert.Equal("Cannot negotiate", VerdictFor(config, 993).Word);
         Assert.True(HasNote(config, StatusLevel.Critical, "No TLS version is enabled"));
         Assert.Equal("No TLS version is enabled.", TlsPosture.VersionSummary(config));
      }

      // ---- versions and ciphers ----------------------------------------------

      [Fact]
      public void DeprecatedProtocolVersionsAreReported()
      {
         TlsPostureConfig config = Healthy();
         config.Tls10Enabled = true;

         Assert.True(HasNote(config, StatusLevel.Warning, "RFC 8996"));
      }

      [Fact]
      public void TheAeadOnlyPresetWithOldProtocolsIsReportedAsSelfDefeating()
      {
         // The cipher-list blurb on the settings page says this in prose about the
         // preset. Here it is said about the configuration actually in front of you.
         TlsPostureConfig config = Healthy();
         config.CipherList = "aead-only";
         config.Tls11Enabled = true;

         Assert.True(TlsPosture.IsAeadOnlyPreset(config.CipherList));
         Assert.True(HasNote(config, StatusLevel.Warning, "no AEAD suite at all"));
      }

      [Fact]
      public void TheAeadOnlyPresetIsMatchedCaseInsensitivelyAndTrimmed()
      {
         Assert.True(TlsPosture.IsAeadOnlyPreset("AEAD-ONLY"));
         Assert.True(TlsPosture.IsAeadOnlyPreset(" aead-only "));
         Assert.False(TlsPosture.IsAeadOnlyPreset("ECDHE-RSA-AES256-GCM-SHA384"));
         Assert.False(TlsPosture.IsAeadOnlyPreset(""));
         Assert.False(TlsPosture.IsAeadOnlyPreset(null));
      }

      [Fact]
      public void VersionsAreListedNewestFirst()
      {
         TlsPostureConfig config = Healthy();
         config.Tls10Enabled = true;
         config.Tls11Enabled = true;

         Assert.Equal("TLS 1.3, TLS 1.2, TLS 1.1, TLS 1.0", TlsPosture.VersionSummary(config));
      }

      // ---- certificates -------------------------------------------------------

      [Fact]
      public void AnExpiredCertificateInUseIsCriticalAndOneNotInUseIsNot()
      {
         TlsPostureConfig config = Healthy();
         config.Certificates = new List<TlsCertificate>
         {
            new() { Name = "live", DaysRemaining = -3, InUse = true },
            new() { Name = "spare", DaysRemaining = -90, InUse = false }
         };

         Assert.True(HasNote(config, StatusLevel.Critical, "\"live\" expired 3 days ago"));
         Assert.True(HasNote(config, StatusLevel.Warning, "\"spare\" expired 90 days ago"));
      }

      [Fact]
      public void ACertificateExpiringSoonIsReportedAtTheSameThresholdTheCertificatesPageUses()
      {
         // One threshold, in one place. Two pages disagreeing about when a
         // certificate becomes urgent is worse than neither of them saying so.
         //
         // Compared against StatusSemantics rather than against
         // CertificateInspector.ExpiryWarningDays, which is the same number: the
         // inspector reaches ServerSession, and ServerSession reaches COM and
         // System.ServiceProcess, so compiling it into this assembly to read one
         // constant would be a poor trade. StatusSemantics is where the number lives
         // and both of the others are defined from it.
         Assert.Equal(StatusSemantics.CertificateExpiryWarningDays, TlsPosture.ExpiryWarningDays);

         TlsPostureConfig config = Healthy();
         config.Certificates = new List<TlsCertificate>
         {
            new() { Name = "soon", DaysRemaining = TlsPosture.ExpiryWarningDays, InUse = true }
         };

         Assert.True(HasNote(config, StatusLevel.Warning, "expires in " + TlsPosture.ExpiryWarningDays));
      }

      [Fact]
      public void AHealthyCertificateProducesNoNote()
      {
         TlsPostureConfig config = Healthy();

         Assert.DoesNotContain(TlsPosture.Notes(config), n => n.Text.Contains("server cert"));
      }

      [Fact]
      public void AFileProblemOnACertificateInUseIsReported()
      {
         TlsPostureConfig config = Healthy();
         config.Certificates = new List<TlsCertificate>
         {
            new() { Name = "server cert", DaysRemaining = 200, InUse = true, Problem = "The certificate file does not exist." }
         };

         Assert.True(HasNote(config, StatusLevel.Critical, "The certificate file does not exist."));
      }

      [Fact]
      public void RemoteSessionsSayWhyExpiryIsUnknownRatherThanReportingItMissing()
      {
         TlsPostureConfig config = Healthy();
         config.CertificateFilesReadable = false;
         config.Certificates = new List<TlsCertificate>
         {
            new() { Name = "server cert", DaysRemaining = null, InUse = true }
         };

         Assert.True(HasNote(config, StatusLevel.Information, "cannot be checked from here"));
         Assert.DoesNotContain(TlsPosture.Notes(config), n => n.Level == StatusLevel.Critical);
      }

      // ---- the control that lives somewhere else ------------------------------

      [Fact]
      public void RangesNotRequiringTlsForAuthAreReportedWhenAPortIsOptional()
      {
         TlsPostureConfig config = Healthy();
         config.Listeners = new List<TlsListener> { Listener("IMAP", 143, TlsListenerSecurity.StartTlsOptional) };
         config.RangesRequiringTlsForAuth = 0;

         Assert.True(HasNote(config, StatusLevel.Warning, "No IP range requires TLS before authentication"));
      }

      [Fact]
      public void RangesRequiringTlsForAuthAreCreditedInTheOptionalStartTlsNote()
      {
         TlsPostureConfig config = Healthy();
         config.Listeners = new List<TlsListener> { Listener("IMAP", 143, TlsListenerSecurity.StartTlsOptional) };
         config.RangesRequiringTlsForAuth = 2;

         Assert.True(HasNote(config, StatusLevel.Warning, "closes the credential half of this"));
         Assert.False(HasNote(config, StatusLevel.Warning, "No IP range requires TLS before authentication"));
      }

      // ---- outbound -----------------------------------------------------------

      [Fact]
      public void NotVerifyingRemoteCertificatesIsExplainedRatherThanCondemned()
      {
         // It is the correct default for mail between servers, and a page that
         // called it a fault would be wrong. Information, with the reason.
         TlsPostureConfig config = Healthy();
         config.VerifyRemoteCertificates = false;

         Assert.True(HasNote(config, StatusLevel.Information, "passive listener only"));
         Assert.False(HasNote(config, StatusLevel.Warning, "passive listener only"));
      }

      // ---- the healthy case has to actually be quiet --------------------------

      [Fact]
      public void AWellConfiguredServerRaisesNothingWorseThanInformation()
      {
         // A page that shouts at a correct configuration is a page that gets
         // ignored, so this is as much a test of the page's usefulness as of it.
         TlsPostureConfig config = Healthy();

         Assert.Equal(StatusLevel.Information, TlsPosture.WorstLevel(config));
         Assert.Contains("requires encryption before it will accept a password", TlsPosture.Verdict(config));
      }

      [Fact]
      public void EveryNoteCarriesTextAndAUsableLevel()
      {
         var configs = new List<TlsPostureConfig> { Healthy(), StockPorts() };

         foreach (TlsPostureConfig config in configs)
         {
            foreach (TlsPostureNote note in TlsPosture.Notes(config))
            {
               Assert.False(string.IsNullOrWhiteSpace(note.Text));
               Assert.NotNull(StatusSemantics.For(note.Level));
            }
         }
      }

      [Fact]
      public void ANullConfigurationIsSurvivedRatherThanThrown()
      {
         Assert.Empty(TlsPosture.Listeners(null));
         Assert.Empty(TlsPosture.Notes(null));
         Assert.Equal(StatusLevel.Normal, TlsPosture.WorstLevel(null));
         Assert.False(string.IsNullOrWhiteSpace(TlsPosture.Verdict(null)));
      }

      [Fact]
      public void AServerWithNoListenersSaysSo()
      {
         TlsPostureConfig config = Healthy();
         config.Listeners = new List<TlsListener>();

         Assert.Contains("No listeners are configured", TlsPosture.Verdict(config));
      }
   }
}
