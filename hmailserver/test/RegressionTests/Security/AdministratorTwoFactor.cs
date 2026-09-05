// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using hMailServer;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    A second factor on the administrator credential, enforced by the SERVER.
   ///
   ///    TOTP protected the Control Panel's own logon for years, checked in C# after
   ///    the server had already accepted the password - a client-side lock on a
   ///    server-side door. This is the door: once a factor is enrolled, the
   ///    administrator password alone authenticates nowhere. Application.Authenticate
   ///    refuses it, AuthenticateWithCode is the only COM way in, and the REST API's
   ///    Basic credential needs the code in an X-hMailServer-OTP header.
   ///
   ///    Every test disables the factor again before it returns, and the fixture does
   ///    so once more at the end, because the whole suite authenticates as the
   ///    administrator with a password: a factor left enrolled would fail every
   ///    fixture that runs afterwards. The disable runs on the already-authenticated
   ///    Settings object, which an enrolled factor does not disturb.
   /// </summary>
   [TestFixture]
   public class AdministratorTwoFactor : TestFixtureBase
   {
      private const string AdminPassword = "testar";
      private const int RestPort = 9512;

      [SetUp]
      public new void SetUp()
      {
         DisableFactor();
      }

      [TearDown]
      public new void TearDown()
      {
         DisableFactor();
         RestoreRestGuarded();
      }

      [OneTimeTearDown]
      public void FinalSafetyNet()
      {
         DisableFactor();
      }

      [Test]
      public void EnrolledTheAdministratorPasswordAloneNoLongerAuthenticatesOverCom()
      {
         string secret = Enrol();

         Assert.IsTrue((bool)_application.AdministratorTOTPEnabled, "The property must report the factor as enrolled.");

         // A brand-new session, the way a fresh client would connect: password
         // alone is refused.
         Assert.IsNull(new Application().Authenticate("Administrator", AdminPassword),
            "The password alone must not authenticate once a factor is enrolled.");

         // The code path lets it back in, and a wrong code does not.
         Assert.IsNotNull(new Application().AuthenticateWithCode("Administrator", AdminPassword, Code(secret)),
            "The password plus a current code must authenticate.");
         Assert.IsNull(new Application().AuthenticateWithCode("Administrator", AdminPassword, "000001"),
            "A wrong code must be refused.");
         Assert.IsNull(new Application().AuthenticateWithCode("Administrator", "wrong-password", Code(secret)),
            "A wrong password with a right code must be refused.");
      }

      [Test]
      public void DisablingRestoresPasswordOnlyLogon()
      {
         Enrol();
         Assert.IsNull(new Application().Authenticate("Administrator", AdminPassword));

         _settings.DisableAdministratorTOTP();

         Assert.IsFalse((bool)_application.AdministratorTOTPEnabled);
         Assert.IsNotNull(new Application().Authenticate("Administrator", AdminPassword),
            "With the factor removed, the password alone signs in again.");
      }

      [Test]
      public void TheSecretIsNotReadableAfterEnrolment()
      {
         Enrol();

         // There is no property that hands the secret back: possessing the server
         // must not yield the factor. The only place it ever appeared was the URI.
         string ini = ReadAdministratorTotpSecretFromIni();
         Assert.IsNotEmpty(ini, "The secret is stored...");
         Assert.That(ini, Does.Not.Contain(" "), "...as the protected envelope, not the base32 secret.");
         StringAssert.StartsWith("DPAPI:", ini, "It is machine-protected at rest, like route passwords: " + ini);
      }

      [Test]
      public void TheRestApiNeedsTheCodeInAHeaderAndSaysSoWhenItIsMissing()
      {
         string secret = Enrol();
         EnableRest();

         // Password alone: refused, and told that a second factor is what is
         // missing - so a client knows to ask for a code rather than assuming the
         // password was wrong.
         var noCode = Http("GET", "/api/v1/status", BasicHeader(), null);
         Assert.AreEqual(401, noCode.status, noCode.body);
         StringAssert.Contains("X-hMailServer-OTP: required", noCode.rawHeaders,
            "The 401 must advertise that a one-time code is required. Headers: " + noCode.rawHeaders);

         // Password plus the code in the header: accepted.
         var withCode = Http("GET", "/api/v1/status", BasicHeader(), Code(secret));
         Assert.AreEqual(200, withCode.status, withCode.body);

         // Password plus a wrong code: refused.
         var wrongCode = Http("GET", "/api/v1/status", BasicHeader(), "000001");
         Assert.AreEqual(401, wrongCode.status, wrongCode.body);
      }

      // ----------------------------------------------------------------------

      private string Enrol()
      {
         string uri = _settings.EnrolAdministratorTOTP();
         StringAssert.StartsWith("otpauth://totp/Administrator", uri, "Got: " + uri);

         Match match = Regex.Match(uri, "secret=([A-Z2-7]+)");
         Assert.IsTrue(match.Success, "No base32 secret in the URI. Got: " + uri);
         return match.Groups[1].Value;
      }

      private void DisableFactor()
      {
         try
         {
            _settings.DisableAdministratorTOTP();
         }
         catch (Exception ex) when (ex is COMException || ex is InvalidComObjectException)
         {
            // A fixture that cannot clear this would poison the rest of the run, so
            // it fails loudly rather than leaving the factor enrolled in silence.
            Assert.Fail("Could not disable the administrator second factor in teardown: " + ex.Message);
         }
      }

      private void EnableRest()
      {
         IniFileSetting.Write("RestApiBindAddress", "127.0.0.1");
         IniFileSetting.Write("RestApiPort", RestPort.ToString());
         IniFileSetting.Write("RestApiCertificateFile", "");
         IniFileSetting.Write("RestApiPrivateKeyFile", "");

         // The secret was written to the ini by Enrol and is read live by the REST
         // server, so reinitializing here (to bind the listener) keeps it. TearDown
         // disables the factor and then reinitializes with the port at 0.
         _application.Reinitialize();
      }

      private void RestoreRest()
      {
         IniFileSetting.Write("RestApiPort", "0");
         _application.Reinitialize();
      }

      private static string BasicHeader()
      {
         return "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("administrator:" + AdminPassword));
      }

      private string ReadAdministratorTotpSecretFromIni()
      {
         const string key = "AdministratorTotpSecret=";

         return IniFileSetting.ExistingIniFiles()
            .SelectMany(iniPath => File.ReadAllLines(iniPath))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Substring(key.Length).Trim())
            .FirstOrDefault(value => value.Length > 0) ?? "";
      }

      // A code from an independent RFC 6238 implementation - the point being that
      // the server accepts what a real authenticator app would produce, not merely
      // what the server itself computes.
      private static string Code(string base32Secret)
      {
         byte[] key = Base32Decode(base32Secret);
         long counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;

         byte[] counterBytes = BitConverter.GetBytes(counter);
         if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

         byte[] hash;
         using (var hmac = new HMACSHA1(key))
            hash = hmac.ComputeHash(counterBytes);

         int offset = hash[hash.Length - 1] & 0x0F;
         int binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

         return (binary % 1000000).ToString("000000");
      }

      private static byte[] Base32Decode(string input)
      {
         const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
         var output = new System.Collections.Generic.List<byte>();
         int buffer = 0, bitsLeft = 0;

         foreach (int index in input.Select(c => alphabet.IndexOf(char.ToUpperInvariant(c))).Where(index => index >= 0))
         {
            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
               bitsLeft -= 8;
               output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
         }
         return output.ToArray();
      }

      // Issues one HTTP/1.0 request against the REST listener over plain TCP and
      // returns the status, the raw header block (so the OTP challenge can be
      // asserted) and the body. `otp`, when given, rides an X-hMailServer-OTP header.
      private (int status, string rawHeaders, string body) Http(string method, string path, string authorization, string otp)
      {
         {
            using (var client = new TcpClient())
            {
               Exception last = null;
               for (int attempt = 0; attempt < 25; attempt++)
               {
                  try { client.Connect("127.0.0.1", RestPort); last = null; break; }
                  catch (SocketException ex) { last = ex; Thread.Sleep(200); }
               }
               if (last != null)
                  throw last;

               var request = new StringBuilder();
               request.Append(method + " " + path + " HTTP/1.0\r\n");
               request.Append("Host: 127.0.0.1\r\n");
               request.Append("Authorization: " + authorization + "\r\n");
               if (otp != null)
                  request.Append("X-hMailServer-OTP: " + otp + "\r\n");
               request.Append("Connection: close\r\n\r\n");

               byte[] bytes = Encoding.ASCII.GetBytes(request.ToString());

               using (NetworkStream stream = client.GetStream())
               using (var memory = new MemoryStream())
               {
                  stream.Write(bytes, 0, bytes.Length);

                  byte[] chunk = new byte[4096];
                  int read;
                  while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                     memory.Write(chunk, 0, read);

                  string response = Encoding.ASCII.GetString(memory.ToArray());
                  int split = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                  string headers = split < 0 ? response : response.Substring(0, split);
                  string body = split < 0 ? "" : response.Substring(split + 4);

                  int status = 0;
                  Match statusMatch = Regex.Match(headers, @"^HTTP/\d\.\d (\d+)");
                  if (statusMatch.Success)
                     status = int.Parse(statusMatch.Groups[1].Value);

                  return (status, headers, body);
               }
            }
         }
      }

      private void RestoreRestGuarded()
      {
         try { RestoreRest(); }
         catch (Exception ex) when (ex is COMException || ex is InvalidComObjectException)
         {
            // Best effort in teardown; a port already 0 is fine.
         }
      }
   }
}
