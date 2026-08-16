// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "PasswordRemover.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      bool IsWhitespace(wchar_t character)
      {
         return character == ' ' || character == '\t' || character == '\r' || character == '\n';
      }

      bool IsBase64Character(wchar_t character)
      {
         return (character >= 'A' && character <= 'Z') ||
                (character >= 'a' && character <= 'z') ||
                (character >= '0' && character <= '9') ||
                character == '+' || character == '/' || character == '=';
      }

      int SkipWhitespace(const String &line, int position)
      {
         while (position < line.GetLength() && IsWhitespace(line.GetAt(position)))
            position++;

         return position;
      }

      // Position just past the space-delimited word starting at position. Returns the
      // length of the line when the word runs to the end of it.
      int FindTokenEnd(const String &line, int position)
      {
         while (position < line.GetLength() && !IsWhitespace(line.GetAt(position)))
            position++;

         return position;
      }

      String GetToken(const String &line, int position)
      {
         return line.Mid(position, FindTokenEnd(line, position) - position);
      }

      // Keeps the first keepLength characters and replaces the rest with the mask. A
      // line with nothing but whitespace left to hide is left exactly as it is, so
      // that a bare "PASS" or a "LOGIN {12}" whose credential arrives on a later line
      // is not rewritten into something that never went over the wire.
      void MaskAfter(String &line, int keepLength)
      {
         if (keepLength < 0 || keepLength > line.GetLength())
            return;

         if (SkipWhitespace(line, keepLength) >= line.GetLength())
            return;

         line = line.Mid(0, keepLength);
         line += _T(" ***");
      }

      // A client line that is a single word is either a protocol verb taking no
      // argument or - the case we care about - the continuation of a SASL exchange,
      // which carries the password (PLAIN), a bearer token (XOAUTH2/OAUTHBEARER) or a
      // SCRAM client proof. The connection classes flag the exchanges whose state they
      // track; this catches the rest, including SCRAM.
      bool IsProtocolVerb(const String &token)
      {
         static const wchar_t *verbs[] =
         {
            // POP3 (RFC 1939, 2449, 5034, 6856)
            _T("USER"), _T("PASS"), _T("APOP"), _T("QUIT"), _T("STAT"), _T("LIST"),
            _T("RETR"), _T("DELE"), _T("NOOP"), _T("RSET"), _T("TOP"), _T("UIDL"),
            _T("CAPA"), _T("AUTH"), _T("STLS"), _T("HELP"), _T("UTF8"), _T("LANG"),
            // SMTP (RFC 5321, 3030, 3207, 4954)
            _T("HELO"), _T("EHLO"), _T("MAIL"), _T("RCPT"), _T("DATA"), _T("VRFY"),
            _T("EXPN"), _T("BDAT"), _T("STARTTLS")
         };

         for (size_t i = 0; i < sizeof(verbs) / sizeof(verbs[0]); i++)
         {
            if (token.CompareNoCase(verbs[i]) == 0)
               return true;
         }

         return false;
      }

      bool IsBareCredentialResponse(const String &line)
      {
         int start = SkipWhitespace(line, 0);
         int end = FindTokenEnd(line, start);

         // More than one word: it is a command, not a response.
         if (SkipWhitespace(line, end) < line.GetLength())
            return false;

         // The shortest useful base64 credential (a one character user name and a one
         // character password in SASL PLAIN) is eight characters. Keeping shorter
         // words readable means an unknown four letter command is still diagnosable.
         if (end - start < 8)
            return false;

         if (IsProtocolVerb(line.Mid(start, end - start)))
            return false;

         for (int i = start; i < end; i++)
         {
            if (!IsBase64Character(line.GetAt(i)))
               return false;
         }

         return true;
      }

      // End of one IMAP argument: an atom, a quoted string, or the {n} prefix of a
      // literal. Needed so that a quoted user name containing a space does not make
      // the scan stop in the middle of it.
      int FindImapArgumentEnd(const String &line, int position)
      {
         int length = line.GetLength();
         if (position >= length)
            return length;

         wchar_t first = line.GetAt(position);

         if (first == '\"')
         {
            for (int i = position + 1; i < length; i++)
            {
               wchar_t character = line.GetAt(i);

               if (character == '\\')
               {
                  i++;
                  continue;
               }

               if (character == '\"')
                  return i + 1;
            }

            return length;
         }

         if (first == '{')
         {
            int closing = line.Find(_T("}"), position);
            if (closing < 0)
               return length;

            return closing + 1;
         }

         return FindTokenEnd(line, position);
      }

      // AUTH / AUTHENTICATE <mechanism> [initial-response]
      //
      // The verb and the mechanism stay visible: knowing that a client tried
      // OAUTHBEARER, or SCRAM-SHA-256, is the whole diagnostic value of an
      // authentication log line. Everything after the mechanism is credential
      // material for every mechanism except LOGIN, whose initial response is the user
      // name and whose password only ever arrives on the following line.
      void MaskAuthenticationArguments(String &line, int verbEnd)
      {
         int mechanismStart = SkipWhitespace(line, verbEnd);
         if (mechanismStart >= line.GetLength())
            return;

         int mechanismEnd = FindTokenEnd(line, mechanismStart);

         if (GetToken(line, mechanismStart).CompareNoCase(_T("LOGIN")) == 0)
         {
            MaskAfter(line, FindTokenEnd(line, SkipWhitespace(line, mechanismEnd)));
            return;
         }

         MaskAfter(line, mechanismEnd);
      }

      void RemoveIMAP(String &line)
      {
         // <tag> <command> [arguments]. An untagged line has no command to look at -
         // that includes the "***" the IMAP connection has already substituted for a
         // credential it tracked itself, which must be left alone.
         int tagEnd = FindTokenEnd(line, SkipWhitespace(line, 0));
         int commandStart = SkipWhitespace(line, tagEnd);
         if (commandStart >= line.GetLength())
            return;

         int commandEnd = FindTokenEnd(line, commandStart);
         String command = line.Mid(commandStart, commandEnd - commandStart);

         if (command.CompareNoCase(_T("LOGIN")) == 0)
         {
            // Keep the user name. It is not a secret, and the POP3/IMAP logs are what
            // tell an administrator which account a session belongs to and which
            // account is being brute forced.
            int userStart = SkipWhitespace(line, commandEnd);
            if (userStart >= line.GetLength())
               return;

            MaskAfter(line, FindImapArgumentEnd(line, userStart));
            return;
         }

         if (command.CompareNoCase(_T("AUTHENTICATE")) == 0)
            MaskAuthenticationArguments(line, commandEnd);
      }

      void RemovePOP3(String &line)
      {
         int commandStart = SkipWhitespace(line, 0);
         int commandEnd = FindTokenEnd(line, commandStart);
         String command = line.Mid(commandStart, commandEnd - commandStart);

         if (command.CompareNoCase(_T("PASS")) == 0)
         {
            MaskAfter(line, commandEnd);
            return;
         }

         if (command.CompareNoCase(_T("APOP")) == 0)
         {
            // APOP <user> <digest>. The digest is replayable inside the timestamp
            // window and crackable offline, so it is a credential; the user name is
            // kept for the reason given in RemoveIMAP.
            MaskAfter(line, FindTokenEnd(line, SkipWhitespace(line, commandEnd)));
            return;
         }

         if (command.CompareNoCase(_T("AUTH")) == 0)
         {
            MaskAuthenticationArguments(line, commandEnd);
            return;
         }

         if (command.CompareNoCase(_T("USER")) == 0)
            return;

         // The original implementation tested only the first four characters, so a
         // malformed "PASSsecret" was masked. Keep that safety net for anything that
         // did not parse as a clean verb.
         if (line.Mid(commandStart, 4).CompareNoCase(_T("PASS")) == 0 ||
             line.Mid(commandStart, 4).CompareNoCase(_T("APOP")) == 0)
         {
            MaskAfter(line, commandStart + 4);
            return;
         }

         if (IsBareCredentialResponse(line))
            line = _T("***");
      }

      void RemoveSMTP(String &line)
      {
         int commandStart = SkipWhitespace(line, 0);
         int commandEnd = FindTokenEnd(line, commandStart);

         if (GetToken(line, commandStart).CompareNoCase(_T("AUTH")) == 0)
         {
            MaskAuthenticationArguments(line, commandEnd);
            return;
         }

         if (IsBareCredentialResponse(line))
            line = _T("***");
      }
   }

   PasswordRemover::PasswordRemover()
   {

   }

   PasswordRemover::~PasswordRemover()
   {

   }

   void
   PasswordRemover::Remove(PRType prt, String &sClientCommand)
   {
      if (sClientCommand.IsEmpty())
         return;

      switch (prt)
      {
      case PRIMAP:
         RemoveIMAP(sClientCommand);
         break;
      case PRPOP3:
         RemovePOP3(sClientCommand);
         break;
      case PRSMTP:
         RemoveSMTP(sClientCommand);
         break;
      }
   }
}
