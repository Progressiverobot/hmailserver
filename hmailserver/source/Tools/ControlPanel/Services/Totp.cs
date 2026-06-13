// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Time-based one-time password (TOTP, RFC 6238) support used for the
// Control Panel two-factor authentication login. The secret is stored under
// HKLM\SOFTWARE\hMailServer (value "AdminTotpSecret"), machine-scope DPAPI
// protected, exactly like hMailServer Administrator, so an existing 2FA
// configuration carries over between the two admin tools.

using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>RFC 6238 TOTP (HMAC-SHA1, 30 second period, 6 digits).</summary>
   internal static class Totp
   {
      private const int PeriodSeconds = 30;
      private const int Digits = 6;
      private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

      public static string GenerateSecret()
      {
         byte[] buffer = new byte[20];
         using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(buffer);
         return Base32Encode(buffer);
      }

      public static bool VerifyCode(string base32Secret, string code)
      {
         if (string.IsNullOrEmpty(base32Secret) || string.IsNullOrEmpty(code))
            return false;

         code = code.Trim();

         byte[] key;
         try
         {
            key = Base32Decode(base32Secret);
         }
         catch (FormatException)
         {
            return false;
         }

         long counter = GetCurrentCounter();
         for (long offset = -1; offset <= 1; offset++)
         {
            string expected = ComputeCode(key, counter + offset);
            if (FixedTimeEquals(expected, code))
               return true;
         }

         return false;
      }

      public static string BuildOtpAuthUri(string accountLabel, string base32Secret)
      {
         return string.Format(
            "otpauth://totp/{0}?secret={1}&issuer=hMailServer&digits={2}&period={3}",
            Uri.EscapeDataString(accountLabel), base32Secret, Digits, PeriodSeconds);
      }

      private static long GetCurrentCounter()
      {
         long unixTime = (long) (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
         return unixTime / PeriodSeconds;
      }

      private static string ComputeCode(byte[] key, long counter)
      {
         byte[] counterBytes = BitConverter.GetBytes(counter);
         if (BitConverter.IsLittleEndian)
            Array.Reverse(counterBytes);

         byte[] hash;
         using (var hmac = new HMACSHA1(key))
            hash = hmac.ComputeHash(counterBytes);

         int dynamicOffset = hash[hash.Length - 1] & 0x0F;
         int binaryCode =
            ((hash[dynamicOffset] & 0x7F) << 24) |
            ((hash[dynamicOffset + 1] & 0xFF) << 16) |
            ((hash[dynamicOffset + 2] & 0xFF) << 8) |
            (hash[dynamicOffset + 3] & 0xFF);

         int otp = binaryCode % (int) Math.Pow(10, Digits);
         return otp.ToString(new string('0', Digits));
      }

      private static bool FixedTimeEquals(string left, string right)
      {
         if (left.Length != right.Length)
            return false;

         int difference = 0;
         for (int i = 0; i < left.Length; i++)
            difference |= left[i] ^ right[i];
         return difference == 0;
      }

      private static string Base32Encode(byte[] data)
      {
         var result = new StringBuilder((data.Length * 8 + 4) / 5);
         int bitBuffer = 0;
         int bitCount = 0;

         foreach (byte value in data)
         {
            bitBuffer = (bitBuffer << 8) | value;
            bitCount += 8;
            while (bitCount >= 5)
            {
               result.Append(Base32Alphabet[(bitBuffer >> (bitCount - 5)) & 0x1F]);
               bitCount -= 5;
            }
         }

         if (bitCount > 0)
            result.Append(Base32Alphabet[(bitBuffer << (5 - bitCount)) & 0x1F]);

         return result.ToString();
      }

      private static byte[] Base32Decode(string encoded)
      {
         encoded = encoded.Trim().Replace(" ", "").Replace("-", "").ToUpperInvariant();
         var result = new System.Collections.Generic.List<byte>(encoded.Length * 5 / 8);
         int bitBuffer = 0;
         int bitCount = 0;

         foreach (char character in encoded)
         {
            if (character == '=')
               break;

            int value = Base32Alphabet.IndexOf(character);
            if (value < 0)
               throw new FormatException("Invalid base32 character.");

            bitBuffer = (bitBuffer << 5) | value;
            bitCount += 5;
            if (bitCount >= 8)
            {
               result.Add((byte) ((bitBuffer >> (bitCount - 8)) & 0xFF));
               bitCount -= 8;
            }
         }

         return result.ToArray();
      }
   }

   /// <summary>
   /// Stores and verifies the administrator two-factor secret. The secret is
   /// protected with machine-scope DPAPI and kept under HKLM\SOFTWARE\hMailServer,
   /// so changing two-factor settings requires local administrator rights.
   /// DPAPI is called directly through crypt32.dll so the .NET 8 Control Panel
   /// produces/consumes the exact same blobs as the .NET Framework Administrator.
   /// </summary>
   internal static class TotpManager
   {
      private const string RegistryKeyPath = @"SOFTWARE\hMailServer";
      private const string RegistryValueName = "AdminTotpSecret";

      private const int CryptProtectUiForbidden = 0x1;
      private const int CryptProtectLocalMachine = 0x4;

      public static bool IsConfigured() => !string.IsNullOrEmpty(ReadSecret());

      public static string ReadSecret()
      {
         try
         {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath))
            {
               if (key == null)
                  return null;

               if (key.GetValue(RegistryValueName) is not string protectedValue || protectedValue.Length == 0)
                  return null;

               byte[] unprotected = Unprotect(Convert.FromBase64String(protectedValue));
               return unprotected == null ? null : Encoding.ASCII.GetString(unprotected);
            }
         }
         catch (Exception)
         {
            return null;
         }
      }

      public static void SaveSecret(string secret)
      {
         byte[] protectedValue = Protect(Encoding.ASCII.GetBytes(secret));
         if (protectedValue == null)
            throw new InvalidOperationException("Could not protect the two-factor secret.");

         using (RegistryKey key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath))
            key.SetValue(RegistryValueName, Convert.ToBase64String(protectedValue), RegistryValueKind.String);
      }

      public static void RemoveSecret()
      {
         using (RegistryKey key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, true))
         {
            if (key != null && key.GetValue(RegistryValueName) != null)
               key.DeleteValue(RegistryValueName);
         }
      }

      // ---- DPAPI (machine scope) via crypt32.dll ----

      [StructLayout(LayoutKind.Sequential)]
      private struct DataBlob
      {
         public int cbData;
         public IntPtr pbData;
      }

      [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
      private static extern bool CryptProtectData(
         ref DataBlob pDataIn, string szDataDescr, IntPtr pOptionalEntropy,
         IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

      [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
      private static extern bool CryptUnprotectData(
         ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
         IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

      [DllImport("kernel32.dll")]
      private static extern IntPtr LocalFree(IntPtr hMem);

      private static byte[] Protect(byte[] data)
      {
         var inBlob = new DataBlob();
         var outBlob = new DataBlob();
         try
         {
            inBlob.pbData = Marshal.AllocHGlobal(data.Length);
            inBlob.cbData = data.Length;
            Marshal.Copy(data, 0, inBlob.pbData, data.Length);

            if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                  CryptProtectLocalMachine | CryptProtectUiForbidden, ref outBlob))
               return null;

            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
         }
         finally
         {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
         }
      }

      private static byte[] Unprotect(byte[] data)
      {
         var inBlob = new DataBlob();
         var outBlob = new DataBlob();
         try
         {
            inBlob.pbData = Marshal.AllocHGlobal(data.Length);
            inBlob.cbData = data.Length;
            Marshal.Copy(data, 0, inBlob.pbData, data.Length);

            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                  CryptProtectLocalMachine | CryptProtectUiForbidden, ref outBlob))
               return null;

            byte[] result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
         }
         finally
         {
            if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
         }
      }
   }
}
