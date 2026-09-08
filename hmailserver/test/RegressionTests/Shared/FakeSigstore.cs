// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    A Sigstore of the suite's own: a certificate authority shaped like Fulcio (a
   ///    root, an intermediate, ten-minute code-signing leaves that name a workflow
   ///    identity in the subject alternative name and the OIDC issuer and repository in
   ///    Fulcio's extensions), and a transparency log shaped like Rekor (a P-256 key
   ///    whose id is the SHA-256 of its DER, a hashedrekord entry, a signed entry
   ///    timestamp, an inclusion proof to a signed checkpoint). It writes the bundle
   ///    cosign would write, so that the server's verifier can be given a file that
   ///    verifies and every way a file can fail to.
   /// </summary>
   public sealed class FakeSigstore : IDisposable
   {
      public const string ReleaseIdentity = "https://github.com/Progressiverobot/hmailserver/.github/workflows/sign-release.yml@refs/heads/master";
      public const string GitHubIssuer = "https://token.actions.githubusercontent.com";
      public const string Repository = "https://github.com/Progressiverobot/hmailserver";

      private readonly ECDsa _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
      private readonly ECDsa _intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
      private readonly ECDsa _logKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
      private readonly X509Certificate2 _root;
      private readonly X509Certificate2 _intermediate;
      private readonly List<string> _files = new List<string>();

      public FakeSigstore(string name = "sigstore.test")
      {
         var rootRequest = new CertificateRequest("O=" + name + ", CN=" + name, _rootKey, HashAlgorithmName.SHA384);
         rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
         rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
         rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
         _root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-1), DateTimeOffset.UtcNow.AddYears(5));

         var intermediateRequest = new CertificateRequest("O=" + name + ", CN=" + name + "-intermediate", _intermediateKey, HashAlgorithmName.SHA384);
         intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
         intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
         intermediateRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new Oid("1.3.6.1.5.5.7.3.3")}, false));
         intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
         _intermediate = intermediateRequest.Create(_root.SubjectName, X509SignatureGenerator.CreateForECDsa(_rootKey),
            DateTimeOffset.UtcNow.AddMonths(-6), DateTimeOffset.UtcNow.AddYears(4), Serial());

         using (var sha = SHA256.Create())
            LogId = sha.ComputeHash(SubjectPublicKeyInfoP256(_logKey));
      }

      /// <summary>SHA-256 of the log key's DER SubjectPublicKeyInfo, which is how Rekor names itself.</summary>
      public byte[] LogId { get; }

      /// <summary>The root and the intermediate, PEM, as an UpdateTrustRootsFile carries them.</summary>
      public string TrustRootsPem
      {
         get { return Pem("CERTIFICATE", _root.RawData) + Pem("CERTIFICATE", _intermediate.RawData); }
      }

      public string LogKeyPem
      {
         get { return Pem("PUBLIC KEY", SubjectPublicKeyInfoP256(_logKey)); }
      }

      public string WriteTrustRootsFile()
      {
         return WriteTemp("roots", TrustRootsPem);
      }

      public string WriteLogKeyFile()
      {
         return WriteTemp("rekor", LogKeyPem);
      }

      /// <summary>How a bundle should deviate from the one cosign would write.</summary>
      public sealed class Options
      {
         public string Identity = ReleaseIdentity;
         public string Issuer = GitHubIssuer;
         public string Repository = Repository_;
         private const string Repository_ = FakeSigstore.Repository;

         /// <summary>When the log recorded the signature. The certificate is valid for ten minutes around SignedAt.</summary>
         public DateTimeOffset SignedAt = DateTimeOffset.UtcNow;
         public DateTimeOffset? IntegratedAt;

         /// <summary>The bytes the signature is over, when not the content itself.</summary>
         public byte[] SignOtherContent;

         public bool IncludePromise = true;
         public bool IncludeProof = true;

         /// <summary>Sign the entry timestamp and the checkpoint with a key that is not the log's.</summary>
         public ECDsa OtherLogKey;

         /// <summary>Record the entry in another instance's log: a certificate from one authority, a record from another log.</summary>
         public FakeSigstore Log;

         /// <summary>Corrupt the inclusion proof's path.</summary>
         public bool BreakProof;

         /// <summary>Use the older bundle form, with a certificate chain instead of a single certificate.</summary>
         public bool ChainForm;

         /// <summary>Leave the code-signing extended key usage off the leaf.</summary>
         public bool NoCodeSigning;

         public long LogIndex = 2620945251;
         public long TreeSize = 2620945290;
      }

      /// <summary>The bundle cosign would write for content, or the deviation options asks for.</summary>
      public string Bundle(byte[] content, Options options = null)
      {
         options = options ?? new Options();
         byte[] signed = options.SignOtherContent ?? content;
         byte[] digest;
         using (var sha = SHA256.Create())
            digest = sha.ComputeHash(signed);

         using (var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
         {
            var request = new CertificateRequest("", leafKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            if (!options.NoCodeSigning)
               request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new Oid("1.3.6.1.5.5.7.3.3")}, false));
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            var names = new SubjectAlternativeNameBuilder();
            names.AddUri(new Uri(options.Identity));
            request.CertificateExtensions.Add(names.Build(true));
            // Fulcio's extensions: the issuer twice (v1 raw, v2 as a DER UTF8String) and
            // the source repository (DER UTF8String).
            request.CertificateExtensions.Add(new X509Extension(new Oid("1.3.6.1.4.1.57264.1.1"), Encoding.ASCII.GetBytes(options.Issuer), false));
            request.CertificateExtensions.Add(new X509Extension(new Oid("1.3.6.1.4.1.57264.1.8"), DerUtf8String(options.Issuer), false));
            if (options.Repository != null)
               request.CertificateExtensions.Add(new X509Extension(new Oid("1.3.6.1.4.1.57264.1.12"), DerUtf8String(options.Repository), false));

            X509Certificate2 leaf = request.Create(_intermediate.SubjectName, X509SignatureGenerator.CreateForECDsa(_intermediateKey),
               options.SignedAt.AddMinutes(-5), options.SignedAt.AddMinutes(5), Serial());
            byte[] leafDer = leaf.RawData;
            string leafPem = Pem("CERTIFICATE", leafDer);

            byte[] signature = DerSignature(leafKey.SignHash(digest));
            long integratedTime = (options.IntegratedAt ?? options.SignedAt).ToUnixTimeSeconds();

            string body = "{\"apiVersion\":\"0.0.1\",\"kind\":\"hashedrekord\",\"spec\":{\"data\":{\"hash\":{\"algorithm\":\"sha256\",\"value\":\"" + Hex(digest) + "\"}}," +
                          "\"signature\":{\"content\":\"" + Convert.ToBase64String(signature) + "\",\"publicKey\":{\"content\":\"" +
                          Convert.ToBase64String(Encoding.ASCII.GetBytes(leafPem)) + "\"}}}}";
            byte[] bodyBytes = Encoding.ASCII.GetBytes(body);
            string bodyBase64 = Convert.ToBase64String(bodyBytes);

            FakeSigstore log = options.Log ?? this;
            byte[] logId = log.LogId;
            ECDsa logSigner = options.OtherLogKey ?? log._logKey;

            var entry = new StringBuilder();
            entry.Append("{\"logIndex\":\"" + options.LogIndex + "\",\"logId\":{\"keyId\":\"" + Convert.ToBase64String(logId) + "\"}," +
                         "\"kindVersion\":{\"kind\":\"hashedrekord\",\"version\":\"0.0.1\"},\"integratedTime\":\"" + integratedTime + "\"");

            if (options.IncludePromise)
            {
               string canonical = "{\"body\":\"" + bodyBase64 + "\",\"integratedTime\":" + integratedTime + ",\"logID\":\"" + Hex(logId) + "\",\"logIndex\":" + options.LogIndex + "}";
               byte[] set = DerSignature(logSigner.SignData(Encoding.ASCII.GetBytes(canonical), HashAlgorithmName.SHA256));
               entry.Append(",\"inclusionPromise\":{\"signedEntryTimestamp\":\"" + Convert.ToBase64String(set) + "\"}");
            }

            if (options.IncludeProof)
            {
               // A small tree with the entry at a chosen leaf: the index and size the
               // bundle states are the entry's position in that tree.
               const int leaves = 7;
               const int position = 5;
               var hashes = new List<byte[]>();
               var random = new Random(12345);
               for (int i = 0; i < leaves; i++)
               {
                  var filler = new byte[32];
                  random.NextBytes(filler);
                  hashes.Add(i == position ? LeafHash(bodyBytes) : filler);
               }
               byte[] root = MerkleRoot(hashes, 0, leaves);
               List<byte[]> path = InclusionPath(hashes, 0, leaves, position);
               if (options.BreakProof)
                  path[0][0] ^= 0x01;

               string checkpointBody = "rekor.test - 1\n" + leaves + "\n" + Convert.ToBase64String(root) + "\n";
               byte[] checkpointSignature = DerSignature(logSigner.SignData(Encoding.UTF8.GetBytes(checkpointBody), HashAlgorithmName.SHA256));
               byte[] hinted = logId.Take(4).Concat(checkpointSignature).ToArray();
               string envelope = checkpointBody + "\n— rekor.test " + Convert.ToBase64String(hinted) + "\n";

               entry.Append(",\"inclusionProof\":{\"logIndex\":\"" + position + "\",\"rootHash\":\"" + Convert.ToBase64String(root) + "\",\"treeSize\":\"" + leaves + "\",\"hashes\":[" +
                            string.Join(",", path.Select(h => "\"" + Convert.ToBase64String(h) + "\"")) + "],\"checkpoint\":{\"envelope\":\"" + JsonEscape(envelope) + "\"}}");
            }

            entry.Append(",\"canonicalizedBody\":\"" + bodyBase64 + "\"}");

            string material = options.ChainForm
               ? "\"x509CertificateChain\":{\"certificates\":[{\"rawBytes\":\"" + Convert.ToBase64String(leafDer) + "\"},{\"rawBytes\":\"" + Convert.ToBase64String(_intermediate.RawData) + "\"}]}"
               : "\"certificate\":{\"rawBytes\":\"" + Convert.ToBase64String(leafDer) + "\"}";

            return "{\"mediaType\":\"application/vnd.dev.sigstore.bundle.v0.3+json\",\"verificationMaterial\":{" + material +
                   ",\"tlogEntries\":[" + entry + "],\"timestampVerificationData\":{}}," +
                   "\"messageSignature\":{\"messageDigest\":{\"algorithm\":\"SHA2_256\",\"digest\":\"" + Convert.ToBase64String(digest) + "\"},\"signature\":\"" +
                   Convert.ToBase64String(signature) + "\"}}";
         }
      }

      public void Dispose()
      {
         _rootKey.Dispose();
         _intermediateKey.Dispose();
         _logKey.Dispose();
         _root.Dispose();
         _intermediate.Dispose();
         foreach (string file in _files)
         {
            try
            {
               File.Delete(file);
            }
            catch (IOException ex)
            {
               // The server reads the trust roots and the log key while a check is
               // running, and a fixture disposes this the moment its test is done, so a
               // file still open here is that race and not a fault. It stays in the
               // temporary directory under a name no other run reuses.
               Trace.WriteLine("FakeSigstore: " + file + " could not be deleted: " + ex.Message);
            }
         }
      }

      // ---- the log's arithmetic (RFC 9162) -------------------------------------------

      public static byte[] LeafHash(byte[] data)
      {
         using (var sha = SHA256.Create())
            return sha.ComputeHash(new byte[] {0x00}.Concat(data).ToArray());
      }

      private static byte[] NodeHash(byte[] left, byte[] right)
      {
         using (var sha = SHA256.Create())
            return sha.ComputeHash(new byte[] {0x01}.Concat(left).Concat(right).ToArray());
      }

      private static int LargestPowerOfTwoBelow(int n)
      {
         int k = 1;
         while (k * 2 < n)
            k *= 2;
         return k;
      }

      private static byte[] MerkleRoot(List<byte[]> leaves, int start, int end)
      {
         int n = end - start;
         if (n == 1)
            return leaves[start];
         int k = LargestPowerOfTwoBelow(n);
         return NodeHash(MerkleRoot(leaves, start, start + k), MerkleRoot(leaves, start + k, end));
      }

      private static List<byte[]> InclusionPath(List<byte[]> leaves, int start, int end, int index)
      {
         int n = end - start;
         if (n == 1)
            return new List<byte[]>();
         int k = LargestPowerOfTwoBelow(n);
         if (index < start + k)
         {
            List<byte[]> path = InclusionPath(leaves, start, start + k, index);
            path.Add(MerkleRoot(leaves, start + k, end));
            return path;
         }
         else
         {
            List<byte[]> path = InclusionPath(leaves, start + k, end, index);
            path.Add(MerkleRoot(leaves, start, start + k));
            return path;
         }
      }

      // ---- encodings ---------------------------------------------------------------------

      private static byte[] Serial()
      {
         var serial = new byte[16];
         using (var random = RandomNumberGenerator.Create())
            random.GetBytes(serial);
         serial[0] &= 0x7F;
         return serial;
      }

      public static string Pem(string label, byte[] der)
      {
         var text = new StringBuilder();
         text.Append("-----BEGIN " + label + "-----\n");
         string base64 = Convert.ToBase64String(der);
         for (int i = 0; i < base64.Length; i += 64)
            text.Append(base64.Substring(i, Math.Min(64, base64.Length - i))).Append('\n');
         text.Append("-----END " + label + "-----\n");
         return text.ToString();
      }

      /// <summary>The DER SubjectPublicKeyInfo of a P-256 key: the fixed prefix and the uncompressed point.</summary>
      public static byte[] SubjectPublicKeyInfoP256(ECDsa key)
      {
         ECParameters parameters = key.ExportParameters(false);
         byte[] prefix =
         {
            0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D,
            0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04
         };
         return prefix.Concat(parameters.Q.X).Concat(parameters.Q.Y).ToArray();
      }

      /// <summary>An r||s signature as the DER SEQUENCE of two INTEGERs the bundle carries.</summary>
      public static byte[] DerSignature(byte[] rs)
      {
         int half = rs.Length / 2;
         byte[] r = DerInteger(rs.Take(half).ToArray());
         byte[] s = DerInteger(rs.Skip(half).ToArray());
         return new byte[] {0x30, (byte) (r.Length + s.Length)}.Concat(r).Concat(s).ToArray();
      }

      private static byte[] DerInteger(byte[] unsigned)
      {
         int start = 0;
         while (start < unsigned.Length - 1 && unsigned[start] == 0)
            start++;
         byte[] magnitude = unsigned.Skip(start).ToArray();
         if ((magnitude[0] & 0x80) != 0)
            magnitude = new byte[] {0x00}.Concat(magnitude).ToArray();
         return new byte[] {0x02, (byte) magnitude.Length}.Concat(magnitude).ToArray();
      }

      private static byte[] DerUtf8String(string text)
      {
         byte[] bytes = Encoding.UTF8.GetBytes(text);
         if (bytes.Length < 128)
            return new byte[] {0x0C, (byte) bytes.Length}.Concat(bytes).ToArray();
         return new byte[] {0x0C, 0x81, (byte) bytes.Length}.Concat(bytes).ToArray();
      }

      public static string Hex(byte[] bytes)
      {
         return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
      }

      private static string JsonEscape(string text)
      {
         return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
      }

      private string WriteTemp(string stem, string contents)
      {
         // The stem is one of this class's own words - "roots", "rekor" - and the rest
         // is a bare hex guid, so the second part is a file name and never a path.
         string path = Paths.Combine(Path.GetTempPath(), "hm-" + stem + "-" + Guid.NewGuid().ToString("N") + ".pem");
         File.WriteAllText(path, contents, new UTF8Encoding(false));
         _files.Add(path);
         return path;
      }
   }
}
