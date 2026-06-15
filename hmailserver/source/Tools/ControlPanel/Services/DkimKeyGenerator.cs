using System;
using System.Security.Cryptography;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Generates an RSA key pair for DKIM signing and produces the matching DNS
   /// TXT record. The private key is written as a PEM file (the format
   /// hMailServer's OpenSSL-based signer reads); the public key is emitted as the
   /// base64 SubjectPublicKeyInfo used in the <c>p=</c> tag of the DNS record.
   /// </summary>
   public static class DkimKeyGenerator
   {
      public class Result
      {
         /// <summary>PEM text of the private key (PKCS#1 "RSA PRIVATE KEY").</summary>
         public string PrivateKeyPem { get; set; }

         /// <summary>The DNS host name for the record, e.g. "selector._domainkey.example.com".</summary>
         public string DnsHost { get; set; }

         /// <summary>The full TXT record value, e.g. "v=DKIM1; k=rsa; p=...".</summary>
         public string DnsTxtValue { get; set; }
      }

      /// <summary>
      /// Creates a new <paramref name="keySize"/>-bit RSA key pair for the given
      /// selector and domain.
      /// </summary>
      public static Result Generate(string selector, string domain, int keySize = 2048)
      {
         if (string.IsNullOrWhiteSpace(selector))
            selector = "dkim";
         selector = selector.Trim();
         domain = (domain ?? "").Trim();

         using var rsa = RSA.Create(keySize);
         string privatePem = rsa.ExportRSAPrivateKeyPem();
         string publicBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

         return new Result
         {
            PrivateKeyPem = privatePem + Environment.NewLine,
            DnsHost = selector + "._domainkey." + domain,
            DnsTxtValue = "v=DKIM1; k=rsa; p=" + publicBase64
         };
      }
   }
}
