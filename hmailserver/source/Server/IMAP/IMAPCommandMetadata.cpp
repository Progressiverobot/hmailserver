// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandMetadata.h"

#include "IMAPConnection.h"
#include "IMAPMetadataStore.h"
#include "IMAPSimpleCommandParser.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/IMAPFolder.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   bool
   IMAPCommandMetadataBase::ResolveTarget_(std::shared_ptr<IMAPConnection> pConnection, const String &mailboxName,
                                           std::shared_ptr<IMAPFolder> &folder, __int64 &folderId, String &error)
   {
      if (mailboxName.IsEmpty())
      {
         // The server itself (RFC 5464 section 3.2.1): folder id 0.
         folderId = 0;
         return true;
      }

      String unescapedName = mailboxName;
      IMAPFolder::UnescapeFolderString(unescapedName);

      folder = pConnection->GetFolderByFullPath(unescapedName);
      if (!folder)
      {
         error = "Folder could not be found.";
         return false;
      }

      if (!pConnection->CheckPermission(folder, ACLPermission::PermissionRead))
      {
         error = "ACL: Read permission denied (Required for METADATA commands).";
         return false;
      }

      folderId = folder->GetID();
      return true;
   }

   bool
   IMAPCommandMetadataBase::IsValidEntryName_(const String &entryName)
   {
      if (entryName.GetLength() < 10 || entryName.GetLength() > 255)
         return false;

      bool isPrivate = entryName.Mid(0, 9).CompareNoCase(_T("/private/")) == 0;
      bool isShared = entryName.Mid(0, 8).CompareNoCase(_T("/shared/")) == 0;

      if (!isPrivate && !isShared)
         return false;

      if (entryName.Right(1) == _T("/"))
         return false;

      if (entryName.Find(_T("//")) >= 0)
         return false;

      for (int i = 0; i < entryName.GetLength(); i++)
      {
         wchar_t character = entryName.GetAt(i);

         // RFC 5464 section 3.1: no asterisk, no percent, no control characters.
         if (character == '*' || character == '%' || character < 0x20 || character > 0x7e)
            return false;
      }

      return true;
   }

   bool
   IMAPCommandMetadataBase::IsSharedEntry_(const String &entryName)
   {
      return entryName.Mid(0, 8).CompareNoCase(_T("/shared/")) == 0;
   }

   __int64
   IMAPCommandMetadataBase::EntryAccountId_(std::shared_ptr<IMAPConnection> pConnection, const String &entryName)
   {
      if (IsSharedEntry_(entryName))
         return 0;

      return pConnection->GetAccount()->GetID();
   }

   IMAPResult
   IMAPCommandGETMETADATA::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());
      pParser->Parse(pArgument);

      if (pParser->WordCount() < 3)
         return IMAPResult(IMAPResult::ResultBad, "GETMETADATA command requires at least 2 parameters.");

      size_t wordIndex = 1;

      // Optional parenthesised options list: DEPTH 0|1|infinity, MAXSIZE n.
      int depth = 0;
      __int64 maxSize = -1;

      if (pParser->Word(wordIndex)->Paranthezied())
      {
         std::vector<String> optionTokens = StringParser::SplitString(pParser->Word(wordIndex)->Value(), _T(" "));

         for (size_t i = 0; i < optionTokens.size(); i++)
         {
            String option = optionTokens[i];
            option.Trim();

            if (option.CompareNoCase(_T("DEPTH")) == 0 && i + 1 < optionTokens.size())
            {
               String depthValue = optionTokens[++i];
               if (depthValue == _T("0"))
                  depth = 0;
               else if (depthValue == _T("1"))
                  depth = 1;
               else if (depthValue.CompareNoCase(_T("infinity")) == 0)
                  depth = -1;
               else
                  return IMAPResult(IMAPResult::ResultBad, "DEPTH must be 0, 1 or infinity.");
            }
            else if (option.CompareNoCase(_T("MAXSIZE")) == 0 && i + 1 < optionTokens.size())
            {
               maxSize = _ttoi64(optionTokens[++i].c_str());
               if (maxSize <= 0)
                  return IMAPResult(IMAPResult::ResultBad, "MAXSIZE must be a positive number.");
            }
            else if (!option.IsEmpty())
            {
               return IMAPResult(IMAPResult::ResultBad, "Unknown GETMETADATA option.");
            }
         }

         wordIndex++;
      }

      if (wordIndex >= pParser->WordCount())
         return IMAPResult(IMAPResult::ResultBad, "GETMETADATA command requires a mailbox name.");

      String mailboxName = pParser->Word(wordIndex)->Value();
      wordIndex++;

      std::shared_ptr<IMAPFolder> folder;
      __int64 folderId = 0;
      String error;
      if (!ResolveTarget_(pConnection, mailboxName, folder, folderId, error))
         return IMAPResult(IMAPResult::ResultNo, error);

      // The entries: one or more atoms/quoted names, or one parenthesised list.
      std::vector<String> entryNames;

      for (; wordIndex < pParser->WordCount(); wordIndex++)
      {
         std::shared_ptr<IMAPSimpleWord> pWord = pParser->Word(wordIndex);

         if (pWord->Paranthezied())
         {
            std::vector<String> listed = StringParser::SplitString(pWord->Value(), _T(" "));
            for (String listedEntry : listed)
            {
               listedEntry.Trim();
               listedEntry.TrimLeft(_T("\""));
               listedEntry.TrimRight(_T("\""));
               if (!listedEntry.IsEmpty())
                  entryNames.push_back(listedEntry);
            }
         }
         else if (!pWord->Value().IsEmpty())
         {
            entryNames.push_back(pWord->Value());
         }
      }

      if (entryNames.empty())
         return IMAPResult(IMAPResult::ResultBad, "GETMETADATA command requires at least one entry name.");

      for (const String &entryName : entryNames)
      {
         if (!IsValidEntryName_(entryName))
            return IMAPResult(IMAPResult::ResultBad, "Invalid metadata entry name.");
      }

      // Collect the answers. Values always travel as literals: they are
      // free-form text and a quoted string cannot safely carry all of it.
      String itemList;
      __int64 longestSkipped = 0;

      for (const String &entryName : entryNames)
      {
         __int64 accountId = EntryAccountId_(pConnection, entryName);

         if (depth == 0)
         {
            String value;
            bool found = false;
            if (!IMAPMetadataStore::Get(accountId, folderId, entryName, value, found))
               return IMAPResult(IMAPResult::ResultNo, "The metadata store could not be read.");

            if (found && maxSize >= 0 && value.GetLength() > maxSize)
            {
               if (value.GetLength() > longestSkipped)
                  longestSkipped = value.GetLength();
               continue;
            }

            if (!itemList.IsEmpty())
               itemList += " ";

            if (found)
            {
               AnsiString valueUtf8 = value;
               String itemText;
               itemText.Format(_T("%s {%d}\r\n"), entryName.c_str(), (int) valueUtf8.GetLength());
               itemList += itemText + value;
            }
            else
            {
               // RFC 5464 section 4.2.1: an entry with no value is NIL.
               itemList += entryName + _T(" NIL");
            }
         }
         else
         {
            std::vector<IMAPMetadataStore::Entry> entries = IMAPMetadataStore::List(accountId, folderId, entryName, depth);

            for (const IMAPMetadataStore::Entry &entry : entries)
            {
               if (maxSize >= 0 && entry.value.GetLength() > maxSize)
               {
                  if (entry.value.GetLength() > longestSkipped)
                     longestSkipped = entry.value.GetLength();
                  continue;
               }

               if (!itemList.IsEmpty())
                  itemList += " ";

               AnsiString valueUtf8 = entry.value;
               String itemText;
               itemText.Format(_T("%s {%d}\r\n"), entry.name.c_str(), (int) valueUtf8.GetLength());
               itemList += itemText + entry.value;
            }
         }
      }

      String response;

      if (!itemList.IsEmpty())
      {
         String mailboxForResponse = mailboxName;
         response.Format(_T("* METADATA \"%s\" (%s)\r\n"), mailboxForResponse.c_str(), itemList.c_str());
      }

      if (longestSkipped > 0)
      {
         // RFC 5464 section 4.2.1: tell the client the size of the longest
         // entry value MAXSIZE made us omit.
         String okLine;
         okLine.Format(_T("%s OK [METADATA LONGENTRIES %I64d] GETMETADATA completed\r\n"), pArgument->Tag().c_str(), longestSkipped);
         response += okLine;
      }
      else
      {
         response += pArgument->Tag() + " OK GETMETADATA completed\r\n";
      }

      pConnection->SendAsciiData(response);

      return IMAPResult();
   }

   IMAPResult
   IMAPCommandSETMETADATA::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());
      pParser->Parse(pArgument);

      if (pParser->WordCount() < 3)
         return IMAPResult(IMAPResult::ResultBad, "SETMETADATA command requires 2 parameters.");

      String mailboxName = pParser->Word(1)->Value();

      std::shared_ptr<IMAPSimpleWord> pPairsWord;
      for (size_t i = 2; i < pParser->WordCount(); i++)
      {
         if (pParser->Word(i)->Paranthezied())
         {
            pPairsWord = pParser->Word(i);
            break;
         }
      }

      if (!pPairsWord)
         return IMAPResult(IMAPResult::ResultBad, "SETMETADATA command requires a parenthesised entry list.");

      std::shared_ptr<IMAPFolder> folder;
      __int64 folderId = 0;
      String error;
      if (!ResolveTarget_(pConnection, mailboxName, folder, folderId, error))
         return IMAPResult(IMAPResult::ResultNo, error);

      // Parse the "entry value entry value ..." blob. A value is a quoted
      // string (backslash escapes honoured), NIL (remove the entry), or a
      // literal - whose text the connection has already collected, leaving the
      // {n} marker in place; markers map to collected literals in order.
      struct Operation
      {
         String entryName;
         String value;
         bool remove;
      };

      std::vector<Operation> operations;

      String blob = pPairsWord->Value();
      size_t literalIndex = 0;
      int position = 0;
      int blobLength = blob.GetLength();

      while (position < blobLength)
      {
         while (position < blobLength && blob.GetAt(position) == ' ')
            position++;
         if (position >= blobLength)
            break;

         // Entry name: to the next space.
         int nameEnd = blob.Find(_T(" "), position);
         if (nameEnd < 0)
            return IMAPResult(IMAPResult::ResultBad, "SETMETADATA entry without a value.");

         String entryName = blob.Mid(position, nameEnd - position);
         entryName.TrimLeft(_T("\""));
         entryName.TrimRight(_T("\""));
         position = nameEnd + 1;

         while (position < blobLength && blob.GetAt(position) == ' ')
            position++;
         if (position >= blobLength)
            return IMAPResult(IMAPResult::ResultBad, "SETMETADATA entry without a value.");

         Operation operation;
         operation.entryName = entryName;
         operation.remove = false;

         wchar_t first = blob.GetAt(position);

         if (first == '"')
         {
            // Quoted value with backslash escapes.
            position++;
            String value;
            bool closed = false;
            for (; position < blobLength; position++)
            {
               wchar_t character = blob.GetAt(position);
               if (character == '\\' && position + 1 < blobLength)
               {
                  position++;
                  value += blob.GetAt(position);
               }
               else if (character == '"')
               {
                  closed = true;
                  position++;
                  break;
               }
               else
                  value += character;
            }

            if (!closed)
               return IMAPResult(IMAPResult::ResultBad, "SETMETADATA quoted value is not closed.");

            operation.value = value;
         }
         else if (first == '{')
         {
            // A literal marker; its text was collected by the connection.
            int markerEnd = blob.Find(_T("}"), position);
            if (markerEnd < 0)
               return IMAPResult(IMAPResult::ResultBad, "SETMETADATA literal marker is not closed.");

            if (literalIndex >= pArgument->LiteralCount())
               return IMAPResult(IMAPResult::ResultBad, "SETMETADATA literal value is missing.");

            operation.value = pArgument->Literal((unsigned int) literalIndex);
            literalIndex++;
            position = markerEnd + 1;
         }
         else
         {
            // An atom: NIL, or invalid.
            int atomEnd = blob.Find(_T(" "), position);
            if (atomEnd < 0)
               atomEnd = blobLength;

            String atom = blob.Mid(position, atomEnd - position);
            position = atomEnd;

            if (atom.CompareNoCase(_T("NIL")) != 0)
               return IMAPResult(IMAPResult::ResultBad, "SETMETADATA value must be a string, a literal or NIL.");

            operation.remove = true;
         }

         operations.push_back(operation);
      }

      if (operations.empty())
         return IMAPResult(IMAPResult::ResultBad, "SETMETADATA command requires at least one entry.");

      // Validate everything before applying anything: RFC 5464 section 4.3
      // makes the command atomic - if any entry cannot be set, none may be.
      for (const Operation &operation : operations)
      {
         if (!IsValidEntryName_(operation.entryName))
            return IMAPResult(IMAPResult::ResultBad, "Invalid metadata entry name.");

         if (!operation.remove && operation.value.GetLength() > IMAPMetadataStore::MaxValueLength)
         {
            String refusal;
            refusal.Format(_T("[METADATA MAXSIZE %d] The value is too large."), IMAPMetadataStore::MaxValueLength);
            return IMAPResult(IMAPResult::ResultNo, refusal);
         }

         if (IsSharedEntry_(operation.entryName))
         {
            if (mailboxName.IsEmpty())
            {
               // Server-level shared entries would be visible to every user on
               // the server; there is no administrator session over IMAP to own
               // them, so writing them is refused rather than half-supported.
               return IMAPResult(IMAPResult::ResultNo, "[NOPERM] Server-level shared metadata is read-only.");
            }

            if (folder && !pConnection->CheckPermission(folder, ACLPermission::PermissionWriteOthers))
               return IMAPResult(IMAPResult::ResultBad, "ACL: Write permission denied (Required to set shared metadata).");
         }
      }

      for (const Operation &operation : operations)
      {
         __int64 accountId = EntryAccountId_(pConnection, operation.entryName);

         bool succeeded = operation.remove
            ? IMAPMetadataStore::Remove(accountId, folderId, operation.entryName)
            : IMAPMetadataStore::Set(accountId, folderId, operation.entryName, operation.value);

         if (!succeeded)
            return IMAPResult(IMAPResult::ResultNo, "The metadata store could not be written.");
      }

      pConnection->SendAsciiData(pArgument->Tag() + " OK SETMETADATA completed\r\n");

      return IMAPResult();
   }
}
