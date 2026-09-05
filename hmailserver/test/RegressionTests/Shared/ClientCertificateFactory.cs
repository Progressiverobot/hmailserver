// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    Client certificates made in-process, for the mutual-TLS fixtures.
   ///
   ///    A certificate authority, client certificates it issues, and self-signed
   ///    strangers - all generated with CertificateRequest (.NET 4.7.2+), so a fixture
   ///    depends on no external material, no machine trust store and no real-world PKI.
   ///    A client certificate can carry an rfc822Name subjectAltName, which is how a
   ///    certificate names the mailbox SASL EXTERNAL logs on as.
   /// </summary>
   public static class ClientCertificateFactory
   {
      public static X509Certificate2 CreateSelfSignedAuthority(string subjectName)
      {
         using (var key = new RSACng(2048))
         {
            var request = new CertificateRequest(subjectName, key,
               HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(
               new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using (var ephemeral = request.CreateSelfSigned(
               DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2)))
            {
               return MakeSChannelUsable(ephemeral);
            }
         }
      }

      /// <summary>
      ///    A client certificate issued by the authority. With an email address, it also
      ///    carries that address as an rfc822Name subjectAltName.
      /// </summary>
      public static X509Certificate2 IssueClientCertificate(X509Certificate2 authority, string subjectName, string emailAddress = null)
      {
         using (var key = new RSACng(2048))
         {
            var request = new CertificateRequest(subjectName, key,
               HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(
               new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
               new OidCollection {new Oid("1.3.6.1.5.5.7.3.2") /* id-kp-clientAuth */}, true));
            AddEmailAddress(request, emailAddress);
            using (var issued = request.Create(authority,
               DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1),
               Guid.NewGuid().ToByteArray()))
            using (var withKey = issued.CopyWithPrivateKey(key))
            {
               return MakeSChannelUsable(withKey);
            }
         }
      }

      /// <summary>
      ///    A client certificate that chains to nothing the server trusts: the stranger
      ///    in every negative test.
      /// </summary>
      public static X509Certificate2 CreateSelfSignedClientCertificate(string subjectName, string emailAddress = null)
      {
         using (var key = new RSACng(2048))
         {
            var request = new CertificateRequest(subjectName, key,
               HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(
               new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
               new OidCollection {new Oid("1.3.6.1.5.5.7.3.2")}, true));
            AddEmailAddress(request, emailAddress);
            using (var ephemeral = request.CreateSelfSigned(
               DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)))
            {
               return MakeSChannelUsable(ephemeral);
            }
         }
      }

      /// <summary>
      ///    Writes the authority's certificate as a PEM file for a port's
      ///    ClientCertificateCAFile. The caller deletes it.
      /// </summary>
      public static string WriteAuthorityPemFile(X509Certificate2 authority)
      {
         var path = Paths.Combine(Path.GetTempPath(),
            "hmailserver-client-ca-" + TestSetup.UniqueString() + ".pem");
         var pem = "-----BEGIN CERTIFICATE-----\r\n" +
                   Convert.ToBase64String(authority.Export(X509ContentType.Cert),
                      Base64FormattingOptions.InsertLineBreaks) +
                   "\r\n-----END CERTIFICATE-----\r\n";
         File.WriteAllText(path, pem);
         return path;
      }

      private static void AddEmailAddress(CertificateRequest request, string emailAddress)
      {
         if (string.IsNullOrEmpty(emailAddress))
            return;

         var alternativeNames = new SubjectAlternativeNameBuilder();
         alternativeNames.AddEmailAddress(emailAddress);
         request.CertificateExtensions.Add(alternativeNames.Build());
      }

      /// <summary>
      ///    Exports and re-imports the certificate so its private key lands in a
      ///    user key container SChannel can open - SslStream cannot use the
      ///    ephemeral CNG key CertificateRequest attaches. PersistKeySet is
      ///    deliberately not passed, so the container is removed again when the
      ///    certificate is disposed.
      /// </summary>
      private static X509Certificate2 MakeSChannelUsable(X509Certificate2 certificate)
      {
         var pfx = certificate.Export(X509ContentType.Pfx, "regression");
         return new X509Certificate2(pfx, "regression",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
      }
   }
}
