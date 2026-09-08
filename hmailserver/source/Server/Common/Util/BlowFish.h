// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

// blowfish.h     interface file for blowfish.cpp
// _THE BLOWFISH ENCRYPTION ALGORITHM_
// by Bruce Schneier
// Revised code--3/20/94
// Converted to C++ class 5/96, Jim Conger

#define MAXKEYBYTES  56    // 448 bits max
#define NPASS           16    // SBox passes

// The cipher works on 32-bit words. On Windows unsigned long is 32 bits and the
// name is the one the Win32 headers use; on an LP64 Linux it is 64 bits, and a
// Blowfish over 64-bit halves is not Blowfish - nothing it wrote could be read
// back, on the same machine or any other. uint32_t on POSIX, the same width.
#ifdef HM_PLATFORM_POSIX
#include <cstdint>
#define DWORD        uint32_t
#else
#define DWORD        unsigned long
#endif
#define WORD      unsigned short
#define BYTE      unsigned char

namespace HM
{
   class BlowFishEncryptor
   {
   private:
      DWORD       * PArray ;
      DWORD    (* SBoxes)[256];
      void     Blowfish_encipher (DWORD *xl, DWORD *xr) ;
      void     Blowfish_decipher (DWORD *xl, DWORD *xr) ;

      String ToHex_(BYTE *Buf, int iBufLen);
      int ToByteArray_(const String &sHex, BYTE *OutArray);

   public:
      BlowFishEncryptor () ;
      ~BlowFishEncryptor () ;

      String EncryptToString(const String &sUnEncrypted);
      String DecryptFromString(const String &sEncrypted);

      void     Initialize (BYTE key[], int keybytes) ;
      DWORD    GetOutputLength (DWORD lInputLong) ;
      DWORD    Encode (BYTE * pInput, BYTE * pOutput, DWORD lSize) ;
      void     Decode (BYTE * pInput, BYTE * pOutput, DWORD lSize) ;

   } ;

   class BlowFishEncryptorTester
   {
   public :
      BlowFishEncryptorTester () {};
      ~BlowFishEncryptorTester () {};      
      
      void Test();
   };

   // choose a byte order for your hardware
   #define ORDER_DCBA   // chosing Intel in this case

   #ifdef ORDER_DCBA    // DCBA - little endian - intel
      union aword {
        DWORD dword;
        BYTE byte [4];
        struct {
          unsigned int byte3:8;
          unsigned int byte2:8;
          unsigned int byte1:8;
          unsigned int byte0:8;
        } w;
      };
   #endif

   #ifdef ORDER_ABCD    // ABCD - big endian - motorola
      union aword {
        DWORD dword;
        BYTE byte [4];
        struct {
          unsigned int byte0:8;
          unsigned int byte1:8;
          unsigned int byte2:8;
          unsigned int byte3:8;
        } w;
      };
   #endif

   #ifdef ORDER_BADC    // BADC - vax
      union aword {
        DWORD dword;
        BYTE byte [4];
        struct {
          unsigned int byte1:8;
          unsigned int byte0:8;
          unsigned int byte3:8;
          unsigned int byte2:8;
        } w;
   };
   #endif

}
