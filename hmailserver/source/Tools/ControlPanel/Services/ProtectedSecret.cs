// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Security.Cryptography;
using System.Text;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Holds a credential for the lifetime of a session without leaving the
   /// plaintext sitting on the managed heap, where it would otherwise survive
   /// into a crash dump or the page file. The key exists only inside this
   /// process, so this guards against incidental exposure - not against a
   /// debugger attached to the running Control Panel.
   /// </summary>
   public sealed class ProtectedSecret : IDisposable
   {
      private const int TagBytes = 16;

      private readonly byte[] key_ = RandomNumberGenerator.GetBytes(32);
      private readonly byte[] nonce_ = RandomNumberGenerator.GetBytes(12);
      private readonly byte[] tag_ = new byte[TagBytes];
      private readonly byte[] cipher_;
      private bool disposed_;

      public ProtectedSecret(string value)
      {
         byte[] plain = Encoding.UTF8.GetBytes(value ?? "");
         cipher_ = new byte[plain.Length];

         try
         {
            using var aes = new AesGcm(key_, TagBytes);
            aes.Encrypt(nonce_, plain, cipher_, tag_);
         }
         finally
         {
            Array.Clear(plain, 0, plain.Length);
         }
      }

      /// <summary>Returns the plaintext. Keep the result short-lived.</summary>
      public string Reveal()
      {
         if (disposed_)
            throw new ObjectDisposedException(nameof(ProtectedSecret));

         byte[] plain = new byte[cipher_.Length];
         try
         {
            using var aes = new AesGcm(key_, TagBytes);
            aes.Decrypt(nonce_, cipher_, tag_, plain);
            return Encoding.UTF8.GetString(plain);
         }
         finally
         {
            Array.Clear(plain, 0, plain.Length);
         }
      }

      public void Dispose()
      {
         if (disposed_)
            return;

         disposed_ = true;
         Array.Clear(key_, 0, key_.Length);
         Array.Clear(cipher_, 0, cipher_.Length);
         Array.Clear(tag_, 0, tag_.Length);
      }
   }
}
