// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// How a listener protects the conversation, in the terms the port editor uses.
   /// Values match eConnectionSecurity so the view can cast the COM value straight in.
   /// </summary>
   public enum TlsListenerSecurity
   {
      /// <summary>Nothing. Everything on this port - including AUTH - crosses the network in the clear.</summary>
      None = 0,

      /// <summary>TLS from the first byte (the old "SSL" ports: 465, 993, 995).</summary>
      Implicit = 1,

      /// <summary>STARTTLS offered. A client that does not ask for it continues unencrypted.</summary>
      StartTlsOptional = 2,

      /// <summary>STARTTLS offered and nothing else permitted until it has been used.</summary>
      StartTlsRequired = 3
   }

   /// <summary>One configured listener, as the port editor holds it.</summary>
   public sealed class TlsListener
   {
      /// <summary>"SMTP", "POP3" or "IMAP".</summary>
      public string Protocol { get; set; } = "";

      public string Address { get; set; } = "";
      public int Port { get; set; }
      public TlsListenerSecurity Security { get; set; }

      /// <summary>The name of the assigned certificate, or empty when none is assigned.</summary>
      public string CertificateName { get; set; } = "";
   }

   /// <summary>A certificate as the SSL certificates page has already inspected it.</summary>
   public sealed class TlsCertificate
   {
      /// <summary>
      /// The database id a port refers to it by. Kept so a listener's certificate can
      /// be resolved against the collection this page has already read, rather than
      /// asking the server again once per port.
      /// </summary>
      public int Id { get; set; }

      public string Name { get; set; } = "";

      /// <summary>Days until it expires; negative when it already has. Null when it could not be read.</summary>
      public int? DaysRemaining { get; set; }

      /// <summary>A problem with the files themselves - missing, unreadable, mismatched pair. Null when there is none.</summary>
      public string Problem { get; set; }

      /// <summary>Whether any listener actually uses it.</summary>
      public bool InUse { get; set; }
   }

   /// <summary>What a listener's arrangement amounts to, with the wording to say it.</summary>
   public sealed class TlsListenerVerdict
   {
      internal TlsListenerVerdict(TlsListener listener, StatusLevel level, string word, string detail)
      {
         Listener = listener;
         Level = level;
         Word = word;
         Detail = detail;
      }

      public TlsListener Listener { get; }
      public StatusLevel Level { get; }

      /// <summary>"Encrypted", "Opportunistic", "In the clear", "Will not start" - never colour alone.</summary>
      public string Word { get; }

      /// <summary>What that means for this particular port, in one sentence.</summary>
      public string Detail { get; }
   }

   /// <summary>Something worth saying about the transport-security posture as a whole.</summary>
   public sealed class TlsPostureNote
   {
      internal TlsPostureNote(StatusLevel level, string text)
      {
         Level = level;
         Text = text;
      }

      public StatusLevel Level { get; }
      public string Text { get; }
   }

   /// <summary>
   /// A snapshot of everything that decides whether traffic to and from this server
   /// is encrypted, gathered from the four pages that own the pieces.
   /// </summary>
   public sealed class TlsPostureConfig
   {
      public List<TlsListener> Listeners { get; set; } = new();
      public List<TlsCertificate> Certificates { get; set; } = new();

      public bool Tls10Enabled { get; set; }
      public bool Tls11Enabled { get; set; }
      public bool Tls12Enabled { get; set; }
      public bool Tls13Enabled { get; set; }

      /// <summary>The OpenSSL cipher list for TLS 1.2 and below, or the AEAD-ONLY preset name.</summary>
      public string CipherList { get; set; } = "";

      /// <summary>Whether certificates are verified on the connections this server makes when delivering.</summary>
      public bool VerifyRemoteCertificates { get; set; }

      /// <summary>IP ranges that refuse AUTH until the session is encrypted, and how many ranges there are.</summary>
      public int RangesRequiringTlsForAuth { get; set; }
      public int RangesTotal { get; set; }

      /// <summary>Whether the certificate files could be examined from this machine at all.</summary>
      public bool CertificateFilesReadable { get; set; } = true;
   }

   /// <summary>
   /// What this server's transport security actually amounts to, derived from the
   /// settings rather than from what they are called.
   ///
   /// No WPF here: every judgement is a pure function of a
   /// <see cref="TlsPostureConfig"/> and is tested directly.
   ///
   /// The pieces live on four separate pages - the ports decide per-listener
   /// security, the certificates page decides what can be presented, the SSL/TLS
   /// page decides which versions and ciphers are negotiable, and the IP ranges page
   /// decides whether AUTH is allowed before the session is encrypted. Each is a
   /// correct editor for its own subject, and no one of them can answer the question
   /// that matters: is anything on this server carrying a password in the clear, and
   /// will it still be serving a valid certificate next month?
   /// </summary>
   public static class TlsPosture
   {
      /// <summary>
      /// The same number CertificateInspector uses, taken from the one place that
      /// holds it, so this page and the certificates page cannot disagree about when
      /// a certificate becomes worth mentioning.
      /// </summary>
      public const int ExpiryWarningDays = StatusSemantics.CertificateExpiryWarningDays;

      /// <summary>
      /// Whether TLS is possible at all on this port's configuration.
      /// </summary>
      private static bool WantsTls(TlsListenerSecurity security) => security != TlsListenerSecurity.None;

      /// <summary>
      /// Whether this port is one where a mail CLIENT authenticates, as opposed to
      /// port 25, where the peer is normally another mail server.
      ///
      /// The distinction is the whole reason this page can be useful rather than
      /// merely alarming. Requiring TLS on 25 breaks inbound mail from senders that
      /// do not offer it, so opportunistic STARTTLS is the correct configuration
      /// there and flagging it would train the reader to ignore the page. On every
      /// other port the peer is a client with a password, and the same setting is a
      /// different thing entirely.
      /// </summary>
      public static bool IsClientPort(TlsListener listener) =>
         !(string.Equals(listener.Protocol, "SMTP", StringComparison.OrdinalIgnoreCase) && listener.Port == 25);

      /// <summary>Every listener, with what its arrangement amounts to.</summary>
      public static IReadOnlyList<TlsListenerVerdict> Listeners(TlsPostureConfig config)
      {
         var verdicts = new List<TlsListenerVerdict>();

         if (config?.Listeners == null)
            return verdicts;

         foreach (TlsListener listener in config.Listeners)
         {
            verdicts.Add(VerdictFor(listener, config));
         }

         return verdicts;
      }

      private static TlsListenerVerdict VerdictFor(TlsListener listener, TlsPostureConfig config)
      {
         // A TLS port with no certificate does not fall back to plaintext - it does
         // not come up. SslContextInitializer::InitServer reports HM5113 at High and
         // returns false, TCPServer::Run gives up, and the port simply is not there.
         // That is a different failure from "unencrypted" and gets its own word.
         if (WantsTls(listener.Security) && string.IsNullOrWhiteSpace(listener.CertificateName))
         {
            return new TlsListenerVerdict(listener, StatusLevel.Critical, "Will not start",
               "TLS is configured with no certificate assigned, so this listener fails to start and nothing can "
               + "connect to this port at all. The server log records this as error HM5113 on every start.");
         }

         // Every TLS listener shares one set of enabled protocol versions. With none
         // of them enabled the handshake has nothing to agree on.
         if (WantsTls(listener.Security) && !AnyVersionEnabled(config))
         {
            return new TlsListenerVerdict(listener, StatusLevel.Critical, "Cannot negotiate",
               "No TLS version is enabled anywhere on this server, so no handshake on this port can succeed.");
         }

         switch (listener.Security)
         {
            case TlsListenerSecurity.Implicit:
               return new TlsListenerVerdict(listener, StatusLevel.Good, "Encrypted",
                  "TLS from the first byte. Nothing on this port is ever sent unencrypted.");

            case TlsListenerSecurity.StartTlsRequired:
               return new TlsListenerVerdict(listener, StatusLevel.Good, "Encrypted",
                  "The session starts in the clear and nothing but STARTTLS is permitted until it has been used, "
                  + "so no credential and no message body crosses unencrypted.");

            case TlsListenerSecurity.StartTlsOptional:
               return IsClientPort(listener)
                  ? new TlsListenerVerdict(listener, StatusLevel.Warning, "Opportunistic",
                     "STARTTLS is offered but not required. A client that does not ask for it - or one talked out "
                     + "of asking by something in the middle - authenticates in the clear, and neither end is told.")
                  : new TlsListenerVerdict(listener, StatusLevel.Good, "Opportunistic",
                     "STARTTLS is offered and not required, which is the correct arrangement for port 25: requiring "
                     + "it would refuse mail from senders that cannot offer it.");

            default:
               return IsClientPort(listener)
                  ? new TlsListenerVerdict(listener, StatusLevel.Critical, "In the clear",
                     "No encryption is available on this port at all. Every password and every message on it crosses "
                     + "the network readable by anything on the path.")
                  : new TlsListenerVerdict(listener, StatusLevel.Warning, "In the clear",
                     "No encryption is available on this port. Inbound mail from other servers is normally "
                     + "unencrypted anyway, but this port also accepts AUTH, and a client using it sends its "
                     + "password in the clear.");
         }
      }

      private static bool AnyVersionEnabled(TlsPostureConfig config) =>
         config != null && (config.Tls10Enabled || config.Tls11Enabled || config.Tls12Enabled || config.Tls13Enabled);

      /// <summary>The enabled protocol versions, newest first, as a readable list.</summary>
      public static string VersionSummary(TlsPostureConfig config)
      {
         if (config == null)
            return "";

         var enabled = new List<string>();
         if (config.Tls13Enabled) enabled.Add("TLS 1.3");
         if (config.Tls12Enabled) enabled.Add("TLS 1.2");
         if (config.Tls11Enabled) enabled.Add("TLS 1.1");
         if (config.Tls10Enabled) enabled.Add("TLS 1.0");

         return enabled.Count == 0
            ? "No TLS version is enabled."
            : string.Join(", ", enabled);
      }

      /// <summary>One sentence answering "is anything here in the clear".</summary>
      public static string Verdict(TlsPostureConfig config)
      {
         if (config == null)
            return "The transport security configuration could not be read.";

         IReadOnlyList<TlsListenerVerdict> listeners = Listeners(config);

         if (listeners.Count == 0)
            return "No listeners are configured, so nothing can connect to this server.";

         int broken = listeners.Count(v => v.Word == "Will not start" || v.Word == "Cannot negotiate");
         int exposed = listeners.Count(v => IsClientPort(v.Listener) && v.Listener.Security == TlsListenerSecurity.None);
         int optional = listeners.Count(v => IsClientPort(v.Listener) && v.Listener.Security == TlsListenerSecurity.StartTlsOptional);

         if (broken > 0)
         {
            return Count(broken, "listener is", "listeners are") + " configured for TLS but cannot serve it, so "
                   + (broken == 1 ? "that port is not listening" : "those ports are not listening") + " at all.";
         }

         if (exposed > 0)
         {
            return Count(exposed, "client listener has", "client listeners have")
                   + " no encryption available, so passwords used on "
                   + (exposed == 1 ? "it cross" : "them cross") + " the network in the clear.";
         }

         if (optional > 0)
         {
            return Count(optional, "client listener offers", "client listeners offer")
                   + " STARTTLS without requiring it, so a client that does not ask for encryption authenticates "
                   + "without it and nothing reports that it happened.";
         }

         return "Every listener that a mail client connects to requires encryption before it will accept a password.";
      }

      /// <summary>
      /// Everything about this posture an administrator would rather be told than
      /// discover from a packet capture.
      /// </summary>
      public static IReadOnlyList<TlsPostureNote> Notes(TlsPostureConfig config)
      {
         var notes = new List<TlsPostureNote>();

         if (config == null)
            return notes;

         IReadOnlyList<TlsListenerVerdict> listeners = Listeners(config);

         // ---- cannot work ------------------------------------------------------

         if (!AnyVersionEnabled(config) && listeners.Any(v => WantsTls(v.Listener.Security)))
         {
            notes.Add(new TlsPostureNote(StatusLevel.Critical,
               "No TLS version is enabled, and there are listeners configured to use TLS. Not one of their "
               + "handshakes can succeed until at least TLS 1.2 is switched on."));
         }

         foreach (TlsListenerVerdict verdict in listeners.Where(v => v.Word == "Will not start"))
         {
            notes.Add(new TlsPostureNote(StatusLevel.Critical,
               verdict.Listener.Protocol + " on port " + verdict.Listener.Port + " is set to use TLS with no "
               + "certificate assigned, so that port does not come up. Anything expecting to reach this server on "
               + "it gets a refused connection, not an unencrypted session."));
         }

         foreach (TlsListenerVerdict verdict in listeners.Where(v =>
                     IsClientPort(v.Listener) && v.Listener.Security == TlsListenerSecurity.None))
         {
            notes.Add(new TlsPostureNote(StatusLevel.Critical,
               verdict.Listener.Protocol + " on port " + verdict.Listener.Port + " has no encryption available. "
               + "Mail clients authenticate on this port, so every password used on it is readable by anything "
               + "between the client and this server."));
         }

         // ---- weak, rather than absent -----------------------------------------

         if (config.Tls10Enabled || config.Tls11Enabled)
         {
            string which = config.Tls10Enabled && config.Tls11Enabled ? "TLS 1.0 and TLS 1.1 are"
               : config.Tls10Enabled ? "TLS 1.0 is" : "TLS 1.1 is";

            notes.Add(new TlsPostureNote(StatusLevel.Warning,
               which + " enabled. Both are deprecated by RFC 8996 and are refused outright by current clients, so "
               + "leaving them on rarely buys the compatibility they were kept for, and a client that does "
               + "negotiate one gets materially weaker protection than the rest."));
         }

         if (IsAeadOnlyPreset(config.CipherList) && (config.Tls10Enabled || config.Tls11Enabled))
         {
            // From the cipher-list blurb on the SSL/TLS page, which says so in prose;
            // this is the same statement made about the configuration in front of you.
            notes.Add(new TlsPostureNote(StatusLevel.Warning,
               "The cipher list is the AEAD-ONLY preset while TLS 1.0 or 1.1 is enabled. Those versions have no "
               + "AEAD suite at all, so they are advertised and then cannot complete a handshake - which looks to "
               + "the client like a broken server rather than a policy."));
         }

         if (!config.Tls13Enabled && config.Tls12Enabled)
         {
            notes.Add(new TlsPostureNote(StatusLevel.Information,
               "TLS 1.3 is off. It is faster to negotiate than 1.2 and removes the older key exchanges "
               + "outright; there is rarely a reason to leave it off."));
         }

         // ---- opportunistic where it should not be -----------------------------

         var optional = listeners
            .Where(v => IsClientPort(v.Listener) && v.Listener.Security == TlsListenerSecurity.StartTlsOptional)
            .ToList();

         if (optional.Count > 0)
         {
            notes.Add(new TlsPostureNote(StatusLevel.Warning,
               "STARTTLS is optional on " + Ports(optional) + ". A client that never asks for it, or one whose "
               + "STARTTLS offer is stripped in transit, carries on unencrypted and neither end is told. "
               + (config.RangesRequiringTlsForAuth > 0
                  ? "Requiring TLS for authentication on the IP ranges closes the credential half of this."
                  : "No IP range requires TLS for authentication either, so nothing else is closing this.")));
         }

         if (config.RangesTotal > 0 && config.RangesRequiringTlsForAuth == 0 && optional.Count > 0)
         {
            notes.Add(new TlsPostureNote(StatusLevel.Warning,
               "No IP range requires TLS before authentication. That setting lives on each IP range rather than "
               + "with the rest of TLS, which is why it is easy to have never seen: it is the one control that "
               + "refuses a plaintext password regardless of what the port allows."));
         }

         // ---- certificates -----------------------------------------------------

         foreach (TlsCertificate certificate in config.Certificates ?? new List<TlsCertificate>())
         {
            if (certificate.Problem != null && certificate.InUse)
            {
               notes.Add(new TlsPostureNote(StatusLevel.Critical,
                  "The certificate \"" + certificate.Name + "\" is in use by a listener and has a problem: "
                  + certificate.Problem));
               continue;
            }

            if (certificate.DaysRemaining == null)
               continue;

            int days = certificate.DaysRemaining.Value;

            if (days < 0)
            {
               notes.Add(new TlsPostureNote(certificate.InUse ? StatusLevel.Critical : StatusLevel.Warning,
                  "The certificate \"" + certificate.Name + "\" expired " + Math.Abs(days)
                  + (Math.Abs(days) == 1 ? " day" : " days") + " ago"
                  + (certificate.InUse
                     ? " and is still assigned to a listener. Clients are refusing or warning about every connection to it."
                     : ". It is not assigned to any listener.")));
            }
            else if (days <= ExpiryWarningDays)
            {
               notes.Add(new TlsPostureNote(certificate.InUse ? StatusLevel.Warning : StatusLevel.Information,
                  "The certificate \"" + certificate.Name + "\" expires in " + days
                  + (days == 1 ? " day" : " days")
                  + (certificate.InUse ? " and is in use by a listener." : ". It is not assigned to any listener.")));
            }
         }

         if (!config.CertificateFilesReadable)
         {
            notes.Add(new TlsPostureNote(StatusLevel.Information,
               "The certificate files are on the server and this Control Panel is connected to it remotely, so "
               + "expiry dates and file contents cannot be checked from here. Everything else on this page is read "
               + "over COM and is accurate."));
         }

         // ---- outbound ---------------------------------------------------------

         if (!config.VerifyRemoteCertificates)
         {
            notes.Add(new TlsPostureNote(StatusLevel.Information,
               "Certificates are not verified on the connections this server makes when delivering. That is the "
               + "normal setting for mail between servers - most of the internet's SMTP certificates would fail "
               + "verification and the mail would stop - but it does mean outbound TLS protects against a passive "
               + "listener only, not against something in the path. DANE and MTA-STS are how a specific "
               + "destination gets more than that."));
         }

         return notes;
      }

      /// <summary>The single worst note, for a one-line summary.</summary>
      public static StatusLevel WorstLevel(TlsPostureConfig config)
      {
         return Notes(config).Aggregate(StatusLevel.Normal, (worst, note) => note.Level > worst ? note.Level : worst);
      }

      /// <summary>
      /// Whether the cipher list is the AEAD-ONLY named preset rather than an
      /// OpenSSL cipher string. Matched case-insensitively, as the server does.
      /// </summary>
      public static bool IsAeadOnlyPreset(string cipherList) =>
         string.Equals((cipherList ?? "").Trim(), "AEAD-ONLY", StringComparison.OrdinalIgnoreCase);

      private static string Ports(IEnumerable<TlsListenerVerdict> verdicts) =>
         string.Join(", ", verdicts.Select(v => v.Listener.Protocol + " " + v.Listener.Port));

      private static string Count(int n, string singular, string plural) =>
         n + " " + (n == 1 ? singular : plural);
   }
}
