// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// One verifiable statement about a configured SSL certificate: a status
   /// level, a short wording for a grid cell, and the full sentence for the
   /// details pane. Rendered through <see cref="StatusSemantics"/>, so every
   /// finding reaches the screen as colour, shape AND word.
   /// </summary>
   public sealed class CertificateFinding
   {
      internal CertificateFinding(StatusLevel level, string word, string detail)
      {
         Level = level;
         Word = word;
         Detail = detail;
      }

      public StatusLevel Level { get; }

      /// <summary>Short state text for a grid cell, e.g. "Expired 2026-08-01 (13 days ago)".</summary>
      public string Word { get; }

      /// <summary>The full sentence, including what the server will do about it.</summary>
      public string Detail { get; }
   }

   /// <summary>Everything <see cref="CertificateInspector.Inspect"/> can determine about one certificate/key pair.</summary>
   public sealed class CertificateHealth
   {
      public CertificateFinding CertificateFile { get; internal set; }
      public CertificateFinding PrivateKeyFile { get; internal set; }
      public CertificateFinding Pair { get; internal set; }

      /// <summary>Whether the key file is passphrase-protected; null when that could not be determined.</summary>
      public bool? KeyIsEncrypted { get; internal set; }

      public DateTime? ExpiresOn { get; internal set; }
      public int? DaysRemaining { get; internal set; }
   }

   /// <summary>
   /// Local inspection of the certificate and private-key files an SSL
   /// certificate entry points at. Everything here is a plain file read plus
   /// parsing - no COM, no network - so it can (and must) run off the UI thread;
   /// the files may sit on a slow network share.
   ///
   /// Every claim a finding makes was verified against the server source:
   ///
   ///  - A missing or unparseable certificate or key file makes
   ///    SslContextInitializer::InitServer return false (error 5113), and the
   ///    listener configured with it DOES NOT START.
   ///  - An encrypted key with no stored passphrase is reported as error 6170
   ///    ("The key was not loaded") and the listener does not start either; a
   ///    wrong passphrase fails the same way under error 5113. That is inbound
   ///    mail stopping because of a configuration mistake, which is why these
   ///    findings are Critical and spelled out.
   ///  - An EXPIRED certificate is the opposite case: OpenSSL loads it without
   ///    complaint, the listener starts, and the server reports error 5991 while
   ///    clients that check the date refuse to connect. The wording here says
   ///    exactly that rather than pretending the port goes down.
   ///
   /// Honest degradation: when the Control Panel is connected to a REMOTE
   /// server, the paths in the configuration are paths on that machine's disk,
   /// and reading this machine's disk would answer a question nobody asked. All
   /// file findings then say "cannot be checked from here" - never a false
   /// green. The same rule as ExternalSetupChecks and the DNS records page.
   /// </summary>
   public static class CertificateInspector
   {
      /// <summary>
      /// Days before expiry at which the certificate starts reading as a warning.
      ///
      /// Kept under its original name because this is what the certificate page and
      /// its tests call it, but the number itself now lives in
      /// <see cref="StatusSemantics.CertificateExpiryWarningDays"/> - the transport
      /// encryption overview needs the same threshold, and a second literal 30 is a
      /// second thing to forget to change.
      /// </summary>
      public const int ExpiryWarningDays = StatusSemantics.CertificateExpiryWarningDays;

      /// <summary>
      /// True when the Control Panel is connected to the machine it runs on, so
      /// the file paths in the server's configuration are readable from here.
      ///
      /// Delegates rather than repeating the test. This was a second copy of the
      /// same four comparisons, and when ServerSession's copy learned to recognise
      /// this machine's own name - which /connect takes, and which an administrator
      /// sitting at the server will type - the two immediately disagreed: the
      /// certificate page would have gone on saying "not readable from this
      /// machine" while looking straight at the files. One implementation, so
      /// there is nothing to drift.
      /// </summary>
      public static bool SessionIsLocal(string host) => ServerSession.IsLocalHost(host);

      /// <summary>
      /// Inspects one certificate entry. Never throws; every failure becomes a
      /// finding. <paramref name="filesReadableHere"/> is false for a remote
      /// session, in which case every file check degrades to "cannot be checked
      /// from here".
      /// </summary>
      public static CertificateHealth Inspect(string certificateFile, string privateKeyFile, bool filesReadableHere)
      {
         var health = new CertificateHealth();

         // A using declaration, because the two calls between the load and the end
         // of the method can throw and the certificate holds an unmanaged key
         // handle. Without it, an exception out of InspectPrivateKey_ or
         // InspectPair_ leaks that handle - and this method is called once per
         // certificate every time the page is opened, so a file that reliably
         // throws leaks on every visit until the Control Panel is restarted.
         //
         // The dispose runs on the exception path as well, and the exception itself
         // is still the caller's to see, because this method's promise is about
         // FINDINGS, not about swallowing faults it does not understand.
         using X509Certificate2 certificate = InspectCertificate_(certificateFile, filesReadableHere, health);

         string keyPem = InspectPrivateKey_(privateKeyFile, filesReadableHere, health);
         InspectPair_(certificate, keyPem, filesReadableHere, health);

         return health;
      }

      /// <summary>
      /// True when a PEM private key file is passphrase-protected. These are the
      /// two markers OpenSSL's own loader keys off: the PKCS#8 "ENCRYPTED
      /// PRIVATE KEY" block, and the PKCS#1 "Proc-Type: 4,ENCRYPTED" header.
      /// Only the head of the file is read - both markers sit before the key
      /// material. Throws on I/O failure; callers report that as "cannot tell".
      /// (Same detection as ExternalSetupChecks, whose copy is private to it.)
      /// </summary>
      public static bool LooksLikeEncryptedPem(string path)
      {
         using var reader = new StreamReader(path);
         var buffer = new char[4096];
         int read = reader.Read(buffer, 0, buffer.Length);
         string head = new string(buffer, 0, Math.Max(read, 0));
         return head.Contains("ENCRYPTED PRIVATE KEY") || head.Contains("Proc-Type: 4,ENCRYPTED");
      }

      // ---- the certificate file ---------------------------------------------

      private static X509Certificate2 InspectCertificate_(string path, bool local, CertificateHealth health)
      {
         if (string.IsNullOrWhiteSpace(path))
         {
            health.CertificateFile = new CertificateFinding(StatusLevel.Critical, "No file set",
               "No certificate file is configured, so this entry cannot be used by any TLS port.");
            return null;
         }

         if (!local)
         {
            health.CertificateFile = CannotCheckFromHere_("certificate file");
            return null;
         }

         if (!File.Exists(path))
         {
            health.CertificateFile = new CertificateFinding(StatusLevel.Critical, "File missing",
               "The certificate file does not exist on this machine (" + path + "). Every TLS port configured "
               + "to use this certificate will fail to start (the server logs error 5113) - mail on those ports stops.");
            return null;
         }

         string pem;
         try
         {
            pem = File.ReadAllText(path);
         }
         catch (Exception ex)
         {
            health.CertificateFile = new CertificateFinding(StatusLevel.Information, "Cannot read",
               "The certificate file exists but could not be read from this panel (" + ex.Message
               + "), so nothing about it is verified.");
            return null;
         }

         X509Certificate2 certificate;
         try
         {
            // CreateFromPem takes the first CERTIFICATE block, which in a
            // standard fullchain.pem is the leaf - the one whose dates matter.
            certificate = X509Certificate2.CreateFromPem(pem);
         }
         catch (Exception)
         {
            health.CertificateFile = new CertificateFinding(StatusLevel.Critical, "Not a PEM certificate",
               "The file could not be parsed as a PEM certificate. The server loads certificates in PEM format "
               + "only, so this file will not load and every TLS port using it will fail to start (server error 5113).");
            return null;
         }

         DateTime expires = certificate.NotAfter;   // local time
         DateTime now = DateTime.Now;
         int daysRemaining = (int)Math.Floor((expires - now).TotalDays);

         health.ExpiresOn = expires;
         health.DaysRemaining = daysRemaining;

         string date = expires.ToString("yyyy-MM-dd");

         if (expires < now)
         {
            int daysAgo = Math.Max(0, (int)Math.Floor((now - expires).TotalDays));
            health.CertificateFile = new CertificateFinding(StatusLevel.Critical,
               "Expired " + date + " (" + Days_(daysAgo) + " ago)",
               "The certificate expired on " + date + ". The server still loads it and its ports still start "
               + "(it reports error 5991 in the application log), but every client and mail server that checks "
               + "the date refuses to connect - which, today, is nearly all of them. Replace the certificate.");
         }
         else if (certificate.NotBefore > now)
         {
            health.CertificateFile = new CertificateFinding(StatusLevel.Warning,
               "Not valid until " + certificate.NotBefore.ToString("yyyy-MM-dd"),
               "The certificate is not valid until " + certificate.NotBefore.ToString("yyyy-MM-dd")
               + ". The server serves it (error 5991 in the application log) and clients refuse it until that "
               + "date - check the certificate and this server's clock.");
         }
         else if (daysRemaining <= ExpiryWarningDays)
         {
            health.CertificateFile = new CertificateFinding(StatusLevel.Warning,
               "Expires " + date + " (" + Days_(daysRemaining) + ")",
               "The certificate expires on " + date + " - " + Days_(daysRemaining) + " from now. Renew it before "
               + "then: once it expires the ports keep listening, but clients that check the date refuse to connect.");
         }
         else
         {
            health.CertificateFile = new CertificateFinding(StatusLevel.Good,
               "Valid to " + date + " (" + Days_(daysRemaining) + ")",
               "The certificate file parses correctly and is valid until " + date + ", "
               + Days_(daysRemaining) + " from now.");
         }

         return certificate;
      }

      // ---- the private key file ---------------------------------------------

      /// <summary>Returns the key PEM text when it was readable and unencrypted, else null.</summary>
      private static string InspectPrivateKey_(string path, bool local, CertificateHealth health)
      {
         if (string.IsNullOrWhiteSpace(path))
         {
            health.PrivateKeyFile = new CertificateFinding(StatusLevel.Critical, "No file set",
               "No private key file is configured, so this certificate cannot be used by any TLS port.");
            return null;
         }

         if (!local)
         {
            health.PrivateKeyFile = CannotCheckFromHere_("key file");
            return null;
         }

         if (!File.Exists(path))
         {
            health.PrivateKeyFile = new CertificateFinding(StatusLevel.Critical, "File missing",
               "The private key file does not exist on this machine (" + path + "). Every TLS port using this "
               + "certificate will fail to start (the server logs error 5113) - mail on those ports stops.");
            return null;
         }

         bool encrypted;
         try
         {
            encrypted = LooksLikeEncryptedPem(path);
         }
         catch (Exception ex)
         {
            health.PrivateKeyFile = new CertificateFinding(StatusLevel.Information, "Cannot read",
               "The key file exists but could not be read from this panel (" + ex.Message
               + "), so whether it is encrypted is unknown.");
            return null;
         }

         health.KeyIsEncrypted = encrypted;

         if (encrypted)
         {
            // The level and the consequences depend on whether a passphrase is
            // stored, which is COM state the caller holds - this finding only
            // states the file fact.
            health.PrivateKeyFile = new CertificateFinding(StatusLevel.Information, "Encrypted key",
               "The key file is passphrase-protected (an encrypted PEM). It only loads if the matching "
               + "passphrase is stored on this certificate.");
            return null;
         }

         health.PrivateKeyFile = new CertificateFinding(StatusLevel.Good, "Key file present",
            "The key file exists and is a plain (unencrypted) PEM, so no passphrase is needed.");

         try
         {
            return File.ReadAllText(path);
         }
         catch (Exception)
         {
            return null;
         }
      }

      // ---- do the certificate and the key belong together --------------------

      private static void InspectPair_(X509Certificate2 certificate, string keyPem, bool local, CertificateHealth health)
      {
         if (!local)
         {
            health.Pair = CannotCheckFromHere_("files");
            return;
         }

         if (health.KeyIsEncrypted == true)
         {
            health.Pair = new CertificateFinding(StatusLevel.Information, "Cannot check while encrypted",
               "The key is encrypted and this panel deliberately does not decrypt it, so whether it matches "
               + "the certificate is only proven when the server loads the pair.");
            return;
         }

         if (certificate == null || keyPem == null)
         {
            health.Pair = new CertificateFinding(StatusLevel.Information, "Cannot check",
               "The match cannot be checked until both files are present and readable.");
            return;
         }

         byte[] keyPublic = PublicKeyOfPrivateKey_(keyPem);
         if (keyPublic == null)
         {
            health.Pair = new CertificateFinding(StatusLevel.Information, "Cannot check this key type",
               "This panel can compare RSA and ECDSA keys; this key parses as neither, so whether it matches "
               + "the certificate is only proven when the server loads the pair.");
            return;
         }

         byte[] certificatePublic;
         try
         {
            certificatePublic = certificate.PublicKey.ExportSubjectPublicKeyInfo();
         }
         catch (Exception)
         {
            health.Pair = new CertificateFinding(StatusLevel.Information, "Cannot check",
               "The certificate's public key could not be exported for comparison.");
            return;
         }

         if (keyPublic.AsSpan().SequenceEqual(certificatePublic))
         {
            health.Pair = new CertificateFinding(StatusLevel.Good, "Matches the certificate",
               "The private key's public half is identical to the certificate's public key - the pair belongs together.");
         }
         else
         {
            health.Pair = new CertificateFinding(StatusLevel.Critical, "Does not match the certificate",
               "The private key does not belong to this certificate: their public keys differ. The pair will "
               + "not load (server error 5113) and every TLS port using this certificate will fail to start. "
               + "Point this entry at the key file that was generated with this certificate.");
         }
      }

      /// <summary>
      /// The SubjectPublicKeyInfo derived from an unencrypted private key PEM,
      /// or null when the key is neither RSA nor ECDSA. Comparing SPKI blobs is
      /// the algorithm-neutral form of the "compare public keys" check.
      /// </summary>
      private static byte[] PublicKeyOfPrivateKey_(string keyPem)
      {
         try
         {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(keyPem);
            return rsa.ExportSubjectPublicKeyInfo();
         }
         catch (Exception)
         {
            // Not RSA; try the other type the server commonly serves.
         }

         try
         {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(keyPem);
            return ecdsa.ExportSubjectPublicKeyInfo();
         }
         catch (Exception)
         {
            return null;
         }
      }

      private static CertificateFinding CannotCheckFromHere_(string what)
      {
         return new CertificateFinding(StatusLevel.Information, "Cannot be checked from here",
            "This Control Panel is connected to a remote server, and the " + what + " is on that machine's "
            + "disk - it cannot be read from this machine, so nothing about it is verified. This is \"unknown\", "
            + "not \"OK\": check it on the server itself, or from its External setup page.");
      }

      private static string Days_(int days) => days == 1 ? "1 day" : days + " days";
   }
}
