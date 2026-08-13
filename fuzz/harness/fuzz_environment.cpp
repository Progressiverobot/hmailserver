// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
//
// Out-of-line definitions the fuzz build needs so the MIME translation units
// link, for the two dependencies whose real headers are used unmodified:
// Common\Util\File and Common\Util\Strings\Formatter.
//
// Everything in here is *environment*, never parser logic. The rule the whole
// fuzz build follows is: if a translation unit contains MIME parsing, it is
// compiled from the real source in Server\Common; if it only exists so the
// parser can report an error, open a file or format a log line, it is stubbed
// here. Stubbing a byte of parsing would make the fuzzer's findings worthless.

#include "stdafx.h"

#include "Util/ByteBuffer.h"
#include "Util/File.h"

namespace HM
{
   // ------------------------------------------------------------------
   // File
   // ------------------------------------------------------------------
   //
   // Open() always fails, so every file-backed entry point in the parser
   // returns early:
   //
   //   MimeBody::LoadFromFile        -> returns false before reading
   //   MimeBody::ReadFromFile        -> returns false before reading
   //   MimeBody::ReadBodyFromSourceFile -> returns ""
   //
   // None of the three are called by the current targets - they all drive
   // MimeBody::Load / MimeHeader::Load on a buffer, which is exactly what
   // LoadFromFile does once File has handed it the bytes. Refusing to open is
   // still the right stub rather than an abort(), for two reasons:
   //
   //   - A fuzz target that touches the filesystem is not reproducible. The
   //     hundredth execution would see the file the first execution wrote, so a
   //     crash artifact would only reproduce with the same directory state, and
   //     the artifact is the only thing a finding is worth.
   //   - abort() inside a stub is indistinguishable, in libFuzzer's report,
   //     from a real crash in the parser. Wasting a triage cycle on "the
   //     harness called something it should not have" is worse than the target
   //     quietly doing nothing.
   //
   // If a future target does want to fuzz LoadFromFile end to end (worth doing:
   // it owns the body_byte_offset_/body_byte_end_ arithmetic that SaveAllToFile
   // then trusts, and that arithmetic is what keeps a DKIM body hash valid),
   // implement Open/ReadTextFile here against an in-memory blob supplied by the
   // harness instead of pointing them at the real disk.

   File::File() :
      file_(nullptr)
   {
   }

   File::~File()
   {
   }

   bool File::Open(const String &filename, OpenType)
   {
      name_ = filename;
      return false;
   }

   std::shared_ptr<ByteBuffer> File::ReadTextFile()
   {
      // Unreachable while Open() fails, but the symbol is referenced from
      // MimeBody::LoadFromFile and ReadBodyFromSourceFile so it must exist.
      // Returns an empty buffer rather than a null pointer: LoadFromFile
      // dereferences the result without checking it, and a null here would be a
      // crash the harness manufactured.
      return std::shared_ptr<ByteBuffer>(new ByteBuffer());
   }

   std::shared_ptr<ByteBuffer> File::ReadFile()
   {
      return std::shared_ptr<ByteBuffer>(new ByteBuffer());
   }

   // ------------------------------------------------------------------
   // FormatArgument / Formatter
   // ------------------------------------------------------------------
   //
   // The real implementations live in Common\Util\Strings and drag in
   // StringFormat plus the placeholder scanner. The MIME code uses the
   // formatter only to build two strings that end up in ErrorManager reports,
   // and the fuzz build's ErrorManager throws its input away, so a minimal
   // {0}/{1}-style substitution is all that is needed.
   //
   // Every declared overload is defined even though only two are called. The
   // alternative - define what is referenced today - turns any future call site
   // in the parser into an unresolved-external at link time, which is a
   // confusing failure a long way from its cause.

   FormatArgument::FormatArgument(const String &value) :
      unicode_string_value_(value), numeric_value_(0), unsigned_numeric_value_(0),
      bool_value_(false), type_(TypeUnicodeString)
   {
   }

   FormatArgument::FormatArgument(const AnsiString &value) :
      ansi_string_value_(value), numeric_value_(0), unsigned_numeric_value_(0),
      bool_value_(false), type_(TypeAnsiString)
   {
   }

   FormatArgument::FormatArgument(const int &value) :
      numeric_value_(value), unsigned_numeric_value_(0),
      bool_value_(false), type_(TypeNumber)
   {
   }

   FormatArgument::FormatArgument(const unsigned int &value) :
      numeric_value_(0), unsigned_numeric_value_(value),
      bool_value_(false), type_(TypeUnsignedNumber)
   {
   }

   FormatArgument::FormatArgument(const __int64 &value) :
      numeric_value_(value), unsigned_numeric_value_(0),
      bool_value_(false), type_(TypeNumber)
   {
   }

   FormatArgument::FormatArgument(const unsigned __int64 &value) :
      numeric_value_(0), unsigned_numeric_value_(value),
      bool_value_(false), type_(TypeUnsignedNumber)
   {
   }

   FormatArgument::FormatArgument(const char *value) :
      ansi_string_value_(value == nullptr ? "" : value), numeric_value_(0),
      unsigned_numeric_value_(0), bool_value_(false), type_(TypeAnsiString)
   {
   }

   FormatArgument::FormatArgument(const wchar_t *value) :
      unicode_string_value_(value == nullptr ? L"" : value), numeric_value_(0),
      unsigned_numeric_value_(0), bool_value_(false), type_(TypeUnicodeString)
   {
   }

   FormatArgument::FormatArgument(const bool &value) :
      numeric_value_(0), unsigned_numeric_value_(0),
      bool_value_(value), type_(TypeBoolean)
   {
   }

   String FormatArgument::GetValue()
   {
      switch (type_)
      {
      case TypeUnicodeString:
         return unicode_string_value_;
      case TypeAnsiString:
         return String(ansi_string_value_);
      case TypeNumber:
         {
            String result;
            result.Format(_T("%I64d"), numeric_value_);
            return result;
         }
      case TypeUnsignedNumber:
         {
            String result;
            result.Format(_T("%I64u"), unsigned_numeric_value_);
            return result;
         }
      case TypeBoolean:
         return bool_value_ ? _T("true") : _T("false");
      }

      return _T("");
   }

   namespace
   {
      // Replaces {0}, {1}, ... in place. No validation and no error on an
      // unmatched placeholder: the real Formatter reports one via ErrorManager,
      // and reproducing that here would only add a code path the fuzzer can
      // reach without learning anything about MIME.
      String SubstituteFuzzPlaceholders_(const String &format, FormatArgument *arguments, size_t argument_count)
      {
         String result = format;

         for (size_t index = 0; index < argument_count; index++)
         {
            String placeholder;
            placeholder.Format(_T("{%Iu}"), index);
            result.Replace(placeholder, arguments[index].GetValue());
         }

         return result;
      }
   }

   String Formatter::Format(const AnsiString &fmt, const FormatArgument &argument1)
   {
      FormatArgument arguments[] = { argument1 };
      return SubstituteFuzzPlaceholders_(String(fmt), arguments, 1);
   }

   String Formatter::Format(const AnsiString &fmt, const FormatArgument &argument1, const FormatArgument &argument2)
   {
      FormatArgument arguments[] = { argument1, argument2 };
      return SubstituteFuzzPlaceholders_(String(fmt), arguments, 2);
   }

   String Formatter::Format(const AnsiString &fmt, const FormatArgument &argument1, const FormatArgument &argument2,
                            const FormatArgument &argument3)
   {
      FormatArgument arguments[] = { argument1, argument2, argument3 };
      return SubstituteFuzzPlaceholders_(String(fmt), arguments, 3);
   }

   String Formatter::Format(const AnsiString &fmt, const FormatArgument &argument1, const FormatArgument &argument2,
                            const FormatArgument &argument3, const FormatArgument &argument4)
   {
      FormatArgument arguments[] = { argument1, argument2, argument3, argument4 };
      return SubstituteFuzzPlaceholders_(String(fmt), arguments, 4);
   }

   String Formatter::Format(const AnsiString &fmt, const FormatArgument &argument1, const FormatArgument &argument2,
                            const FormatArgument &argument3, const FormatArgument &argument4,
                            const FormatArgument &argument5)
   {
      FormatArgument arguments[] = { argument1, argument2, argument3, argument4, argument5 };
      return SubstituteFuzzPlaceholders_(String(fmt), arguments, 5);
   }

   AnsiString Formatter::FormatAsAnsi(const AnsiString &fmt, const FormatArgument &argument1)
   {
      return AnsiString(Format(fmt, argument1));
   }

   AnsiString Formatter::FormatAsAnsi(const AnsiString &fmt, const FormatArgument &argument1, const FormatArgument &argument2)
   {
      return AnsiString(Format(fmt, argument1, argument2));
   }

   AnsiString Formatter::FormatAsAnsi(const AnsiString &fmt, const FormatArgument &argument1, const FormatArgument &argument2,
                                      const FormatArgument &argument3)
   {
      return AnsiString(Format(fmt, argument1, argument2, argument3));
   }

   AnsiString Formatter::FormatAsAnsi(const AnsiString &fmt, const FormatArgument &argument1, const FormatArgument &argument2,
                                      const FormatArgument &argument3, const FormatArgument &argument4)
   {
      return AnsiString(Format(fmt, argument1, argument2, argument3, argument4));
   }

   AnsiString Formatter::FormatAsAnsi(const AnsiString &fmt, const FormatArgument &argument1, const FormatArgument &argument2,
                                      const FormatArgument &argument3, const FormatArgument &argument4,
                                      const FormatArgument &argument5)
   {
      return AnsiString(Format(fmt, argument1, argument2, argument3, argument4, argument5));
   }
}
