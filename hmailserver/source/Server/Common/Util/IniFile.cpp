// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The Win32 profile API over hMailServer.INI, for the POSIX build.
//
// GetPrivateProfileString and its three companions are the only way this
// server has ever read its own configuration, and there are more than forty
// call sites. Rewriting them all to something portable would be a change to
// the shipping Windows server for the sake of a build it does not take part
// in, so instead the four functions are provided here, under their own names,
// with the same signatures and the same answers.
//
// WHAT IS REPRODUCED, because callers in this tree depend on each of these:
//
//   * A missing file, a missing section or a missing key all give the caller's
//     default back, silently. A configuration file that is not there yet is
//     the normal state of a fresh installation, not an error.
//   * A key that IS present but empty reads as an empty string, and through
//     GetPrivateProfileInt as 0 - NOT as the caller's default. That difference
//     is why IniSettingStore deletes a key rather than writing "Key=" when it
//     removes a setting, and the comment there says it has bitten this project
//     twice.
//   * Section and key names match without regard to case.
//   * Whitespace around the key name and around the value is separator, not
//     content: "  Key  =  value  " is the key "Key" with the value "value".
//   * A value wrapped in a matching pair of single or double quotation marks
//     loses them - but only through GetPrivateProfileString. Win32 does not
//     strip them in GetPrivateProfileSection, and neither does this.
//   * GetPrivateProfileInt reads an unsigned decimal number and stops at the
//     first character that is not a digit, so "5x" is 5 and "x" is 0. A
//     negative value is 0, which Win32 documents. It returns a UINT and wraps
//     at 2^32, which LdapSettings::LoadSettings_ relies on being true.
//   * GetPrivateProfileSection hands back "key=value" entries separated by
//     nulls and terminated by one further null, and reports nSize - 2 when the
//     buffer was too small - which is exactly the signal the grow-and-retry
//     loops in IniSettingStore and RateLimiter test for.
//   * A write creates the file if it is absent and the section if it is
//     absent; a null key name deletes the whole section; a null value deletes
//     just that key.
//   * The file's own encoding decides how values are read and written. Win32
//     writes UTF-16LE only into a file that already opens with a UTF-16LE byte
//     order mark and narrow text into everything else, and so does this.
//     hMailServer.INI as this tree writes it has no mark and is narrow; the
//     language files under Languages\ do have one and are UTF-16LE.
//   * Comments, blank lines, key order, section order and the spelling of an
//     existing key all survive a write untouched. An administrator's file is
//     an administrator's file.
//
// WHAT IS DELIBERATELY NOT REPRODUCED, and why:
//
//   * There is no cache. Win32 keeps the most recently used profile in memory
//     and WritePrivateProfileString(nullptr, nullptr, nullptr, file) flushes
//     it; here every call reads the file, so that call has nothing to do and
//     returns success. Callers make it so that the next read sees the write -
//     and here it always does.
//   * "Narrow" means UTF-8, not a Windows ANSI code page, because POSIX has no
//     such thing. A file that is not valid UTF-8 is read and written back as
//     Latin-1 so that its bytes survive a round trip rather than being turned
//     into question marks; if a value is then written that Latin-1 cannot
//     spell, the whole file moves up to UTF-8 rather than losing characters.
//   * A file whose line endings are mixed is written back with one style
//     throughout - the one that occurs in it, CRLF winning a tie. Win32 makes
//     no promise here either, and the alternative is remembering a terminator
//     per line for no reader's benefit.
//   * Only ';' starts a comment, as on Win32. A line starting with '#' is a
//     key whose name begins with '#', which is what Win32 makes of it; the two
//     callers that read a whole section discard both, so nothing turns on it.
//   * Deleting a key or a section that is not there succeeds. Win32's
//     documented failure is an inability to write the file, and every caller
//     here treats FALSE as "the store could not be written" - one of them
//     refuses an API key revocation on it. "Already gone" is not that.
//   * The write is not in place: it goes to a temporary file beside the
//     original and is renamed over it, so a crash or a full disk leaves the
//     previous configuration rather than half of the new one. The mode of the
//     existing file is carried across; a file created here is created for its
//     owner alone, because the first thing written into it is a password hash.
#include "StdAfx.h"

#include "IniFile.h"

#ifdef HM_PLATFORM_POSIX

#include <mutex>
#include <string>
#include <vector>

#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#include "../Application/ErrorManager.h"

namespace
{
   // ------------------------------------------------------------- the encodings
   //
   // Which bytes the file is made of. The value is decided when the file is
   // read and used again when it is written, so that a write preserves the
   // form of the file it found rather than imposing one.
   enum FileEncoding
   {
      EncodingUtf16Le,       // opens with FF FE
      EncodingUtf8WithMark,  // opens with EF BB BF
      EncodingUtf8,          // no mark, and the bytes are valid UTF-8
      EncodingLatin1         // no mark, and the bytes are not valid UTF-8
   };

   // A profile file as lines of text. Everything is kept - comments, blank
   // lines, the order of the sections - because a write has to give the file
   // back to its owner in the state it was found, minus the one line it
   // changed.
   struct ProfileFile
   {
      ProfileFile() :
         encoding(EncodingUtf8),
         existed(false),
         newline(L"\r\n")
      {
      }

      FileEncoding encoding;
      bool existed;
      std::wstring newline;
      std::vector<std::wstring> lines;
   };

   // ------------------------------------------------------------- transcoding
   //
   // wchar_t is 32 bits here and 16 bits on Windows, so a UTF-16 file has to be
   // decoded into code points rather than copied. These four functions are the
   // whole of that; they are local because the tree's own converters are
   // private members of classes that this file must not depend on.
   void AppendUtf8(std::string &out, unsigned int codePoint)
   {
      if (codePoint < 0x80)
      {
         out.push_back((char) codePoint);
      }
      else if (codePoint < 0x800)
      {
         out.push_back((char) (0xC0 | (codePoint >> 6)));
         out.push_back((char) (0x80 | (codePoint & 0x3F)));
      }
      else if (codePoint < 0x10000)
      {
         out.push_back((char) (0xE0 | (codePoint >> 12)));
         out.push_back((char) (0x80 | ((codePoint >> 6) & 0x3F)));
         out.push_back((char) (0x80 | (codePoint & 0x3F)));
      }
      else
      {
         out.push_back((char) (0xF0 | (codePoint >> 18)));
         out.push_back((char) (0x80 | ((codePoint >> 12) & 0x3F)));
         out.push_back((char) (0x80 | ((codePoint >> 6) & 0x3F)));
         out.push_back((char) (0x80 | (codePoint & 0x3F)));
      }
   }

   std::string EncodeUtf8(const std::wstring &text)
   {
      std::string bytes;
      bytes.reserve(text.size());

      for (size_t index = 0; index < text.size(); index++)
         AppendUtf8(bytes, (unsigned int) text[index]);

      return bytes;
   }

   // Strict: an overlong form, a lone surrogate or a truncated sequence all
   // make this say no, and the caller then reads the file as Latin-1 instead.
   // Being strict is the point - it is what tells a UTF-8 file apart from a
   // code page one, and a lenient decoder would turn every code page file into
   // replacement characters.
   bool DecodeUtf8(const std::string &bytes, size_t from, std::wstring &out)
   {
      out.clear();
      out.reserve(bytes.size() - from);

      size_t index = from;

      while (index < bytes.size())
      {
         const unsigned char lead = (unsigned char) bytes[index];
         unsigned int codePoint = 0;
         size_t continuations = 0;

         if (lead < 0x80)
         {
            codePoint = lead;
            continuations = 0;
         }
         else if ((lead & 0xE0) == 0xC0)
         {
            codePoint = lead & 0x1Fu;
            continuations = 1;
         }
         else if ((lead & 0xF0) == 0xE0)
         {
            codePoint = lead & 0x0Fu;
            continuations = 2;
         }
         else if ((lead & 0xF8) == 0xF0)
         {
            codePoint = lead & 0x07u;
            continuations = 3;
         }
         else
         {
            return false;
         }

         if (index + continuations >= bytes.size())
            return false;

         for (size_t offset = 1; offset <= continuations; offset++)
         {
            const unsigned char continuation = (unsigned char) bytes[index + offset];

            if ((continuation & 0xC0) != 0x80)
               return false;

            codePoint = (codePoint << 6) | (continuation & 0x3Fu);
         }

         if (continuations == 1 && codePoint < 0x80)
            return false;
         if (continuations == 2 && codePoint < 0x800)
            return false;
         if (continuations == 3 && codePoint < 0x10000)
            return false;
         if (codePoint > 0x10FFFF)
            return false;
         if (codePoint >= 0xD800 && codePoint <= 0xDFFF)
            return false;

         out.push_back((wchar_t) codePoint);
         index += continuations + 1;
      }

      return true;
   }

   std::wstring DecodeUtf16Le(const std::string &bytes, size_t from)
   {
      std::wstring text;
      text.reserve((bytes.size() - from) / 2);

      size_t index = from;

      while (index + 1 < bytes.size())
      {
         unsigned int unit = (unsigned int) (unsigned char) bytes[index] |
                             ((unsigned int) (unsigned char) bytes[index + 1] << 8);
         index += 2;

         // A high surrogate followed by a low one is one code point. An
         // unpaired surrogate is passed through rather than dropped, so that a
         // file this program did not write survives a read and a write.
         if (unit >= 0xD800 && unit <= 0xDBFF && index + 1 < bytes.size())
         {
            const unsigned int low = (unsigned int) (unsigned char) bytes[index] |
                                     ((unsigned int) (unsigned char) bytes[index + 1] << 8);

            if (low >= 0xDC00 && low <= 0xDFFF)
            {
               unit = 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00);
               index += 2;
            }
         }

         text.push_back((wchar_t) unit);
      }

      return text;
   }

   std::string EncodeUtf16Le(const std::wstring &text)
   {
      std::string bytes;
      bytes.reserve(text.size() * 2);

      for (size_t index = 0; index < text.size(); index++)
      {
         unsigned int codePoint = (unsigned int) text[index];

         if (codePoint >= 0x10000 && codePoint <= 0x10FFFF)
         {
            codePoint -= 0x10000;
            const unsigned int high = 0xD800 + (codePoint >> 10);
            const unsigned int low = 0xDC00 + (codePoint & 0x3FF);

            bytes.push_back((char) (high & 0xFF));
            bytes.push_back((char) ((high >> 8) & 0xFF));
            bytes.push_back((char) (low & 0xFF));
            bytes.push_back((char) ((low >> 8) & 0xFF));
         }
         else
         {
            bytes.push_back((char) (codePoint & 0xFF));
            bytes.push_back((char) ((codePoint >> 8) & 0xFF));
         }
      }

      return bytes;
   }

   std::wstring DecodeLatin1(const std::string &bytes, size_t from)
   {
      std::wstring text;
      text.reserve(bytes.size() - from);

      for (size_t index = from; index < bytes.size(); index++)
         text.push_back((wchar_t) (unsigned char) bytes[index]);

      return text;
   }

   // False when a character will not fit in a byte, which is the signal to
   // promote the whole file to UTF-8 rather than write a question mark into
   // somebody's password.
   bool EncodeLatin1(const std::wstring &text, std::string &bytes)
   {
      bytes.clear();
      bytes.reserve(text.size());

      for (size_t index = 0; index < text.size(); index++)
      {
         if ((unsigned int) text[index] > 0xFF)
            return false;

         bytes.push_back((char) (unsigned char) text[index]);
      }

      return true;
   }

   // ------------------------------------------------------------- line parsing
   //
   // Win32 treats the same characters as separator here that it treats as
   // whitespace anywhere else. CR and LF are in the list because a file with a
   // lone CR in it should not leave one glued to the end of a value.
   bool IsProfileSpace(wchar_t character)
   {
      return character == L' ' || character == L'\t' || character == L'\r' ||
             character == L'\n' || character == L'\v' || character == L'\f';
   }

   std::wstring Trim(const std::wstring &value)
   {
      size_t first = 0;
      while (first < value.size() && IsProfileSpace(value[first]))
         first++;

      size_t last = value.size();
      while (last > first && IsProfileSpace(value[last - 1]))
         last--;

      return value.substr(first, last - first);
   }

   bool EqualsNoCase(const std::wstring &left, const std::wstring &right)
   {
      if (left.size() != right.size())
         return false;

      for (size_t index = 0; index < left.size(); index++)
         if (::towlower((wint_t) left[index]) != ::towlower((wint_t) right[index]))
            return false;

      return true;
   }

   // "[Name]" - the name is what lies between the first '[' and the LAST ']',
   // which is Win32's reading and lets a name contain a bracket.
   bool IsSectionHeader(const std::wstring &line, std::wstring &name)
   {
      const std::wstring trimmed = Trim(line);

      if (trimmed.size() < 2 || trimmed[0] != L'[')
         return false;

      const size_t close = trimmed.rfind(L']');

      if (close == std::wstring::npos || close == 0)
         return false;

      name = Trim(trimmed.substr(1, close - 1));
      return true;
   }

   bool IsComment(const std::wstring &line)
   {
      const std::wstring trimmed = Trim(line);
      return !trimmed.empty() && trimmed[0] == L';';
   }

   // Split at the FIRST '=', both halves trimmed. A line with no '=' is a name
   // with no value; Win32 keeps such a line and reports it from
   // GetPrivateProfileSection with no '=' either.
   void SplitEntry(const std::wstring &line, std::wstring &name, std::wstring &value, bool &hasValue)
   {
      const std::wstring trimmed = Trim(line);
      const size_t separator = trimmed.find(L'=');

      if (separator == std::wstring::npos)
      {
         name = trimmed;
         value.clear();
         hasValue = false;
         return;
      }

      name = Trim(trimmed.substr(0, separator));
      value = Trim(trimmed.substr(separator + 1));
      hasValue = true;
   }

   // ------------------------------------------------------------- file access
   bool ReadWholeFile(const std::string &path, std::string &bytes)
   {
      FILE *handle = ::fopen(path.c_str(), "rb");

      if (!handle)
         return false;

      bytes.clear();

      char block[8192];
      size_t read = 0;

      while ((read = ::fread(block, 1, sizeof(block), handle)) > 0)
         bytes.append(block, read);

      const bool failed = ::ferror(handle) != 0;
      ::fclose(handle);

      return !failed;
   }

   void LoadProfile(const std::wstring &fileName, ProfileFile &file)
   {
      file = ProfileFile();

      std::string bytes;

      if (!ReadWholeFile(EncodeUtf8(fileName), bytes))
         return;

      file.existed = true;

      std::wstring text;

      if (bytes.size() >= 2 &&
          (unsigned char) bytes[0] == 0xFF && (unsigned char) bytes[1] == 0xFE)
      {
         file.encoding = EncodingUtf16Le;
         text = DecodeUtf16Le(bytes, 2);
      }
      else if (bytes.size() >= 3 &&
               (unsigned char) bytes[0] == 0xEF && (unsigned char) bytes[1] == 0xBB &&
               (unsigned char) bytes[2] == 0xBF && DecodeUtf8(bytes, 3, text))
      {
         file.encoding = EncodingUtf8WithMark;
      }
      else if (DecodeUtf8(bytes, 0, text))
      {
         file.encoding = EncodingUtf8;
      }
      else
      {
         // Not UTF-8 at all. Read it as bytes - including any mark at the front,
         // which is then just three more bytes - so that writing it back gives
         // the same file to anything else that reads it.
         file.encoding = EncodingLatin1;
         text = DecodeLatin1(bytes, 0);
      }

      if (text.find(L"\r\n") != std::wstring::npos)
         file.newline = L"\r\n";
      else if (text.find(L'\n') != std::wstring::npos)
         file.newline = L"\n";

      size_t start = 0;

      while (start < text.size())
      {
         const size_t stop = text.find(L'\n', start);

         if (stop == std::wstring::npos)
         {
            file.lines.push_back(text.substr(start));
            break;
         }

         std::wstring line = text.substr(start, stop - start);

         if (!line.empty() && line[line.size() - 1] == L'\r')
            line.erase(line.size() - 1);

         file.lines.push_back(line);
         start = stop + 1;
      }
   }

   bool WriteFileAtomically(const std::string &path, const std::string &bytes, bool existed)
   {
      // The mode of the file that is already there, so that rewriting the
      // administrator's password hash cannot widen who can read it. A file
      // created here belongs to its owner alone - stricter than the directory
      // would give it, and the right default for a file whose first line is a
      // secret.
      mode_t mode = S_IRUSR | S_IWUSR;
      struct stat existing;

      if (existed && ::stat(path.c_str(), &existing) == 0)
         mode = existing.st_mode & 07777;

      std::string pattern = path + ".hmXXXXXX";
      std::vector<char> temporary(pattern.begin(), pattern.end());
      temporary.push_back('\0');

      const int descriptor = ::mkstemp(&temporary[0]);

      if (descriptor == -1)
         return false;

      bool written = true;
      size_t offset = 0;

      while (offset < bytes.size())
      {
         const ssize_t wrote = ::write(descriptor, bytes.data() + offset, bytes.size() - offset);

         if (wrote < 0)
         {
            if (errno == EINTR)
               continue;

            written = false;
            break;
         }

         if (wrote == 0)
         {
            written = false;
            break;
         }

         offset += (size_t) wrote;
      }

      if (written && ::fchmod(descriptor, mode) != 0)
         written = false;

      // The rename is atomic, but only the fsync makes the CONTENT durable
      // before it. Without this a power failure can leave the new name over an
      // empty file - which for this file means a server that has forgotten its
      // own database password.
      if (written && ::fsync(descriptor) != 0)
         written = false;

      if (::close(descriptor) != 0)
         written = false;

      if (written && ::rename(&temporary[0], path.c_str()) == 0)
         return true;

      const int failure = errno;
      ::unlink(&temporary[0]);
      errno = failure;

      return false;
   }

   bool SaveProfile(const std::wstring &fileName, ProfileFile &file)
   {
      std::wstring text;

      for (size_t index = 0; index < file.lines.size(); index++)
      {
         text += file.lines[index];
         text += file.newline;
      }

      std::string bytes;

      switch (file.encoding)
      {
      case EncodingUtf16Le:
         bytes = "\xFF\xFE";
         bytes += EncodeUtf16Le(text);
         break;

      case EncodingUtf8WithMark:
         bytes = "\xEF\xBB\xBF";
         bytes += EncodeUtf8(text);
         break;

      case EncodingLatin1:
         if (EncodeLatin1(text, bytes))
            break;

         // A value arrived that a code page file cannot spell. Writing it as
         // question marks would silently corrupt it, so the file is promoted to
         // UTF-8 instead - the one change of form this code makes, and it makes
         // it only to avoid losing what it was asked to store.
         bytes = EncodeUtf8(text);
         break;

      case EncodingUtf8:
      default:
         bytes = EncodeUtf8(text);
         break;
      }

      return WriteFileAtomically(EncodeUtf8(fileName), bytes, file.existed);
   }

   // ------------------------------------------------------------- lookups
   int FindSection(const ProfileFile &file, const std::wstring &section)
   {
      std::wstring name;

      for (size_t index = 0; index < file.lines.size(); index++)
         if (IsSectionHeader(file.lines[index], name) && EqualsNoCase(name, section))
            return (int) index;

      return -1;
   }

   size_t SectionEnd(const ProfileFile &file, size_t header)
   {
      std::wstring name;

      for (size_t index = header + 1; index < file.lines.size(); index++)
         if (IsSectionHeader(file.lines[index], name))
            return index;

      return file.lines.size();
   }

   bool FindValue(const ProfileFile &file, const std::wstring &section, const std::wstring &key,
                  std::wstring &value)
   {
      const int header = FindSection(file, section);

      if (header < 0)
         return false;

      const size_t stop = SectionEnd(file, (size_t) header);

      for (size_t index = (size_t) header + 1; index < stop; index++)
      {
         if (IsComment(file.lines[index]))
            continue;

         std::wstring name;
         std::wstring found;
         bool hasValue = false;

         SplitEntry(file.lines[index], name, found, hasValue);

         if (!name.empty() && EqualsNoCase(name, key))
         {
            value = found;
            return true;
         }
      }

      return false;
   }

   // ------------------------------------------------------------- copying out
   //
   // Win32's buffer contract, which every caller in this tree is written
   // against: the result is always terminated, a value too long for the buffer
   // is truncated rather than refused, and the count returned never includes
   // the terminator.
   DWORD CopyValue(LPWSTR buffer, DWORD size, const std::wstring &value)
   {
      if (!buffer || size == 0)
         return 0;

      std::wstring copied = value;

      // A matching pair of quotation marks is punctuation, not content. This is
      // GetPrivateProfileString's behaviour and not GetPrivateProfileSection's,
      // so it lives here rather than in the parser.
      if (copied.size() >= 2 &&
          (copied[0] == L'"' || copied[0] == L'\'') &&
          copied[copied.size() - 1] == copied[0])
      {
         copied = copied.substr(1, copied.size() - 2);
      }

      const size_t room = (size_t) size - 1;
      const size_t length = copied.size() < room ? copied.size() : room;

      if (length > 0)
         ::wmemcpy(buffer, copied.c_str(), length);

      buffer[length] = L'\0';

      return (DWORD) length;
   }

   // The double-null terminated form: entries already carry their own null, and
   // this adds the one that ends the run. A buffer that cannot hold the lot is
   // filled and reported as nSize - 2, which is the signal the grow-and-retry
   // loops in IniSettingStore and RateLimiter are waiting for.
   DWORD CopyBlock(LPWSTR buffer, DWORD size, const std::wstring &block)
   {
      if (!buffer || size == 0)
         return 0;

      if (size == 1)
      {
         buffer[0] = L'\0';
         return 0;
      }

      if (block.size() + 1 <= (size_t) size)
      {
         if (!block.empty())
            ::wmemcpy(buffer, block.data(), block.size());

         buffer[block.size()] = L'\0';
         return (DWORD) block.size();
      }

      const size_t room = (size_t) size - 2;

      if (room > 0)
         ::wmemcpy(buffer, block.data(), room);

      buffer[room] = L'\0';
      buffer[room + 1] = L'\0';

      return (DWORD) room;
   }

   // Every write is a read, an edit and a rewrite, so two of them at once would
   // lose one of the two values. Win32 serialises inside the process for the
   // same reason; this is that lock.
   std::mutex &WriterLock()
   {
      static std::mutex lock;
      return lock;
   }
}

DWORD GetPrivateProfileString(LPCWSTR section, LPCWSTR key, LPCWSTR defaultValue,
                              LPWSTR buffer, DWORD size, LPCWSTR fileName)
{
   if (!buffer || size == 0)
      return 0;

   ProfileFile file;

   if (fileName && *fileName)
      LoadProfile(fileName, file);

   // A null section asks for the names of every section, and a null key for the
   // names of every key in one. Nothing in this tree asks for either; they are
   // here because a caller that did would otherwise get an empty answer with
   // nothing to say why.
   if (!section)
   {
      std::wstring block;
      std::wstring name;

      for (size_t index = 0; index < file.lines.size(); index++)
      {
         if (IsSectionHeader(file.lines[index], name))
         {
            block += name;
            block.push_back(L'\0');
         }
      }

      return CopyBlock(buffer, size, block);
   }

   if (!key)
   {
      std::wstring block;
      const int header = FindSection(file, section);

      if (header >= 0)
      {
         const size_t stop = SectionEnd(file, (size_t) header);

         for (size_t index = (size_t) header + 1; index < stop; index++)
         {
            if (IsComment(file.lines[index]))
               continue;

            std::wstring name;
            std::wstring value;
            bool hasValue = false;

            SplitEntry(file.lines[index], name, value, hasValue);

            if (name.empty())
               continue;

            block += name;
            block.push_back(L'\0');
         }
      }

      return CopyBlock(buffer, size, block);
   }

   std::wstring value;

   if (FindValue(file, section, key, value))
      return CopyValue(buffer, size, value);

   return CopyValue(buffer, size, defaultValue ? std::wstring(defaultValue) : std::wstring());
}

UINT GetPrivateProfileInt(LPCWSTR section, LPCWSTR key, INT defaultValue, LPCWSTR fileName)
{
   if (!section || !key)
      return (UINT) defaultValue;

   ProfileFile file;

   if (fileName && *fileName)
      LoadProfile(fileName, file);

   std::wstring value;

   // Only a key that is not there falls back to the default. A key that is
   // there and empty is 0, and the two are not the same setting.
   if (!FindValue(file, section, key, value))
      return (UINT) defaultValue;

   // Win32 reads an unsigned decimal number and stops at the first character
   // that is not a digit, so "5x" is 5 and "x" - or "-1", or "true" - is 0. It
   // keeps the low 32 bits of what it read rather than saturating, which is
   // what makes LdapSettings::LoadSettings_'s unsigned ceiling necessary and
   // correct.
   unsigned long long number = 0;
   size_t index = 0;

   while (index < value.size() && value[index] >= L'0' && value[index] <= L'9')
   {
      number = (number * 10 + (unsigned long long) (value[index] - L'0')) & 0xFFFFFFFFULL;
      index++;
   }

   return (UINT) number;
}

DWORD GetPrivateProfileSection(LPCWSTR section, LPWSTR buffer, DWORD size, LPCWSTR fileName)
{
   if (!buffer || size == 0)
      return 0;

   ProfileFile file;

   if (fileName && *fileName)
      LoadProfile(fileName, file);

   std::wstring block;
   const int header = section ? FindSection(file, section) : -1;

   if (header >= 0)
   {
      const size_t stop = SectionEnd(file, (size_t) header);

      for (size_t index = (size_t) header + 1; index < stop; index++)
      {
         const std::wstring &line = file.lines[index];

         if (IsComment(line))
            continue;

         std::wstring name;
         std::wstring value;
         bool hasValue = false;

         SplitEntry(line, name, value, hasValue);

         // A blank line is not an entry. Quotation marks are left where they
         // are: Win32 strips them from GetPrivateProfileString and not from
         // here, and a mirror that disagreed with the reader it mirrors would
         // be worse than useless.
         if (name.empty())
            continue;

         block += name;

         if (hasValue)
         {
            block.push_back(L'=');
            block += value;
         }

         block.push_back(L'\0');
      }
   }

   return CopyBlock(buffer, size, block);
}

BOOL WritePrivateProfileString(LPCWSTR section, LPCWSTR key, LPCWSTR value, LPCWSTR fileName)
{
   if (!fileName || !*fileName)
      return FALSE;

   // Win32's "flush the cached file to disk" call. Callers make it straight
   // after a write so that the next read sees what they wrote; here every read
   // opens the file, so it already does and there is nothing to flush.
   if (!section)
      return TRUE;

   std::lock_guard<std::mutex> guard(WriterLock());

   ProfileFile file;
   LoadProfile(fileName, file);

   const int header = FindSection(file, section);
   bool changed = false;

   if (!key)
   {
      // Delete the whole section: its header and everything down to the next
      // one. A section that is not there is already in the state asked for.
      if (header < 0)
         return TRUE;

      const size_t stop = SectionEnd(file, (size_t) header);
      file.lines.erase(file.lines.begin() + header, file.lines.begin() + stop);
      changed = true;
   }
   else
   {
      const std::wstring wanted(key);
      size_t stop = header >= 0 ? SectionEnd(file, (size_t) header) : 0;
      size_t existing = (size_t) -1;
      std::wstring existingName;

      if (header >= 0)
      {
         for (size_t index = (size_t) header + 1; index < stop; index++)
         {
            if (IsComment(file.lines[index]))
               continue;

            std::wstring name;
            std::wstring found;
            bool hasValue = false;

            SplitEntry(file.lines[index], name, found, hasValue);

            if (!name.empty() && EqualsNoCase(name, wanted))
            {
               existing = index;
               existingName = name;
               break;
            }
         }
      }

      if (!value)
      {
         // Delete just this key. Again, one that is not there is already gone;
         // the callers that test this return value are asking whether the store
         // could be written, not whether there was anything to remove.
         if (existing == (size_t) -1)
            return TRUE;

         file.lines.erase(file.lines.begin() + existing);
         changed = true;
      }
      else if (existing != (size_t) -1)
      {
         // Rewrite the line, keeping the name as the file spells it. An
         // administrator who wrote "MaxMessageSize" and a caller that asks for
         // "MAXMESSAGESIZE" are talking about the same setting, and the file is
         // the administrator's.
         const std::wstring line = existingName + L"=" + value;

         if (file.lines[existing] == line)
            return TRUE;

         file.lines[existing] = line;
         changed = true;
      }
      else
      {
         if (header < 0)
         {
            // A new section goes at the end, after a blank line if the file
            // does not already end in one - so that appending to a file written
            // by hand does not run the new header onto the last setting.
            if (!file.lines.empty() && !Trim(file.lines.back()).empty())
               file.lines.push_back(std::wstring());

            file.lines.push_back(std::wstring(L"[") + section + L"]");
            stop = file.lines.size();
         }
         else
         {
            // Inside an existing section the key goes after its last real line,
            // so that blank lines separating this section from the next stay
            // where the administrator put them.
            while (stop > (size_t) header + 1 && Trim(file.lines[stop - 1]).empty())
               stop--;
         }

         file.lines.insert(file.lines.begin() + stop, wanted + L"=" + value);
         changed = true;
      }
   }

   if (!changed)
      return TRUE;

   if (SaveProfile(fileName, file))
      return TRUE;

   // Win32 reports a failure to write only through the return value, and every
   // caller here acts on it. One line in the error log as well, because the
   // reason - a full disk, a read-only directory, the wrong owner after a
   // restore - is in errno at this moment and nowhere else afterwards.
   const int failure = errno;

   HM::ErrorManager::Instance()->ReportError(HM::ErrorManager::Medium, 6400,
      "WritePrivateProfileString",
      HM::String(L"The settings file could not be written. Setting: [") + section + L"] " +
      (key ? key : L"(the whole section)") + L". File: " + fileName +
      L". Error: " + HM::String(::strerror(failure)));

   return FALSE;
}

#endif
