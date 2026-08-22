// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandList.h"
#include "IMAPCommandStatus.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"
#include "../Common/Application/ACLManager.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/IMAPFolders.h"
#include "../Common/Cache/CacheContainer.h"

#include "FolderListCreator.h"
#include "IMAPConfiguration.h"
#include "IMAPFolderContainer.h"

#include <algorithm>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPCommandLIST::IMAPCommandLIST()
   {

   }

   IMAPCommandLIST::~IMAPCommandLIST()
   {

   }

   IMAPResult
   IMAPCommandLIST::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      String sTag = pArgument->Tag();

      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      pParser->Parse(pArgument);

      // RFC 5258 (LIST-EXTENDED) generalises the LIST syntax:
      //   LIST [(<selection options>)] <reference> <mailbox pattern(s)> [RETURN (<return options>)]
      // Word 0 is the LIST keyword; remaining words are walked positionally.
      size_t iWordCount = pParser->WordCount();
      if (iWordCount < 3)
         return IMAPResult(IMAPResult::ResultBad, "LIST Command requires 2 parameters.");

      size_t iIdx = 1;

      // Optional leading selection-option list, e.g. "(SUBSCRIBED)" or
      // "(SPECIAL-USE)".
      bool bOnlySubscribed = false;
      bool bOnlySpecialUse = false;
      if (pParser->Word(iIdx)->Paranthezied())
      {
         String sSelection = pParser->Word(iIdx)->Value();

         bOnlySubscribed = HasOption_(sSelection, _T("SUBSCRIBED"));

         // RFC 6154 section 4.1: return only mailboxes that have a special use.
         bOnlySpecialUse = HasOption_(sSelection, _T("SPECIAL-USE"));

         // REMOTE / RECURSIVEMATCH are accepted but have no effect (no remote folders).
         iIdx++;
      }

      // Need at least a reference name and one mailbox pattern.
      if (iIdx + 1 >= iWordCount)
         return IMAPResult(IMAPResult::ResultBad, "LIST Command requires 2 parameters.");

      String sReferenceName = pParser->Word(iIdx)->Value();
      iIdx++;

      // Mailbox pattern(s): a single pattern, or a parenthesised list of patterns.
      std::vector<String> patterns;
      std::shared_ptr<IMAPSimpleWord> pPatternWord = pParser->Word(iIdx);
      if (pPatternWord->Paranthezied())
         ExtractPatterns_(pPatternWord->Value(), patterns);
      else
         patterns.push_back(pPatternWord->Value());
      iIdx++;

      // Optional trailing "RETURN (<options>)".
      bool bAnnotateSubscribed = false;
      String sStatusItems;
      if (iIdx < iWordCount && pParser->Word(iIdx)->Value().CompareNoCase(_T("RETURN")) == 0)
      {
         iIdx++;
         if (iIdx < iWordCount && pParser->Word(iIdx)->Paranthezied())
         {
            String sReturn = pParser->Word(iIdx)->Value();

            if (HasOption_(sReturn, _T("SUBSCRIBED")))
               bAnnotateSubscribed = true;

            // RFC 5819 (LIST-STATUS): "STATUS (<items>)" asks for a STATUS
            // response after each selectable listed mailbox, saving the client
            // one round trip per mailbox at startup. The ABNF puts a space
            // between STATUS and its item list, so the whole-token check works.
            if (HasOption_(sReturn, _T("STATUS")))
            {
               int iStatusPos = sReturn.FindNoCase(_T("STATUS"));
               int iItemsStart = sReturn.Find(_T("("), iStatusPos);
               int iItemsEnd = iItemsStart >= 0 ? sReturn.Find(_T(")"), iItemsStart) : -1;

               if (iItemsStart < 0 || iItemsEnd < 0)
                  return IMAPResult(IMAPResult::ResultBad, "STATUS return option requires an item list.");

               sStatusItems = sReturn.Mid(iItemsStart + 1, iItemsEnd - iItemsStart - 1);
            }

            // CHILDREN is always reported (\HasChildren / \HasNoChildren), so it needs
            // no flag.
            //
            // RFC 6154 section 4.2, SPECIAL-USE, is likewise accepted and needs no
            // flag: the special-use attributes are part of every LIST response this
            // server produces. RFC 6154 allows that, and hMailServer 6.2.18 already
            // did it for the name-derived attributes, so making them conditional on
            // the return option would silently strip them from every existing client
            // that does not know to ask. The option is still parsed, so that a client
            // sending it is not answered BAD.
            iIdx++;
         }
      }

      bool bExtended = bOnlySubscribed || bAnnotateSubscribed || bOnlySpecialUse;

      // RFC 5258: every mailbox returned for a SUBSCRIBED selection is, by
      // definition, subscribed, so it must carry the \Subscribed attribute.
      if (bOnlySubscribed)
         bAnnotateSubscribed = true;

      std::shared_ptr<IMAPFolders> pFolders = pConnection->GetAccountFolders();
      std::shared_ptr<IMAPFolders> pPublicFolders = pConnection->GetPublicFolders();

      if (!pFolders || !pPublicFolders)
         return IMAPResult(IMAPResult::ResultNo, "LIST failed - No folders.");

      String hierarchyDelimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();
      String sPublicFolderName = Configuration::Instance()->GetIMAPConfiguration()->GetIMAPPublicFolderName();

      // Shared mailboxes, the "#Users" namespace: the accounts that have shared
      // at least one of their folders with somebody. Resolved once, before the
      // pattern loop; whether THIS caller may see any given folder is decided
      // per folder by ACLManager inside the listing walk, exactly as it is for
      // public folders - an owner who shared nothing with this caller produces
      // no output and therefore no trace in the response.
      std::vector<__int64> vecShareOwnerIds;
      if (ACLManager::GetOtherUsersNamespaceEnabled())
      {
         ACLManager aclManager;
         vecShareOwnerIds = aclManager.GetAccountsWithFolderShares();
      }

      String sOtherUsersName = ACLManager::GetOtherUsersFolderName();

      std::vector<String> seenLines;
      String sResult;
      bool bAllPatternsEmpty = true;

      for (const String &sWildcards : patterns)
      {
         if (!sWildcards.IsEmpty())
            bAllPatternsEmpty = false;

         String folderSpecifier = sWildcards;
         if (sReferenceName.GetLength() > 0)
            folderSpecifier = sReferenceName + folderSpecifier;

         String sPatternResult;
         if (bExtended)
         {
            sPatternResult = FolderListCreator::GetIMAPFolderListExtended(pConnection->GetAccount()->GetID(), pFolders, folderSpecifier, "", bOnlySubscribed, bAnnotateSubscribed, bOnlySpecialUse) +
                             FolderListCreator::GetIMAPFolderListExtended(pConnection->GetAccount()->GetID(), pPublicFolders, folderSpecifier, sPublicFolderName, bOnlySubscribed, bAnnotateSubscribed, bOnlySpecialUse);
         }
         else
         {
            sPatternResult = FolderListCreator::GetIMAPFolderList(pConnection->GetAccount()->GetID(), pFolders, folderSpecifier, "") +
                             FolderListCreator::GetIMAPFolderList(pConnection->GetAccount()->GetID(), pPublicFolders, folderSpecifier, sPublicFolderName);
         }

         // One listing per sharing owner, prefixed "#Users.owner@domain". The
         // walk skips every folder the caller lacks the lookup right on, so a
         // caller with no rights sees neither the folders nor the owner node.
         bool bAnyDelegatedVisible = false;
         for (__int64 iOwnerAccountID : vecShareOwnerIds)
         {
            if (iOwnerAccountID == pConnection->GetAccount()->GetID())
               continue;   // the caller's own folders are the personal namespace.

            std::shared_ptr<const Account> pOwnerAccount = CacheContainer::Instance()->GetAccount(iOwnerAccountID);
            if (!pOwnerAccount)
               continue;

            std::shared_ptr<IMAPFolders> pOwnerFolders = IMAPFolderContainer::Instance()->GetFoldersForAccount(iOwnerAccountID);
            if (!pOwnerFolders)
               continue;

            String sOwnerPrefix = sOtherUsersName + hierarchyDelimiter + pOwnerAccount->GetAddress();

            bool bOwnerVisible = false;
            if (bExtended)
               sPatternResult += FolderListCreator::GetIMAPFolderListExtended(pConnection->GetAccount()->GetID(), pOwnerFolders, folderSpecifier, sOwnerPrefix, bOnlySubscribed, bAnnotateSubscribed, bOnlySpecialUse, &bOwnerVisible);
            else
               sPatternResult += FolderListCreator::GetIMAPFolderList(pConnection->GetAccount()->GetID(), pOwnerFolders, folderSpecifier, sOwnerPrefix, &bOwnerVisible);

            if (bOwnerVisible)
            {
               bAnyDelegatedVisible = true;

               // The owner's address usually contains the hierarchy delimiter
               // (with the default "." every dotted domain does), so a client
               // walking the hierarchy level by level with "%" passes through
               // intermediate levels - "#Users.info@example" - that are not
               // real nodes. Report each as \Noselect, the way the namespace
               // root itself is, so "%" discovery reaches the real folders.
               // The full owner node ("#Users.info@example.test") is emitted by
               // the listing walk above as its prefix root.
               std::vector<String> vecAddressParts = StringParser::SplitString(pOwnerAccount->GetAddress(), hierarchyDelimiter);

               String sIntermediate = sOtherUsersName;
               for (size_t iPart = 0; iPart + 1 < vecAddressParts.size(); iPart++)
               {
                  sIntermediate += hierarchyDelimiter + vecAddressParts[iPart];
                  sPatternResult += FolderListCreator::CreateNamespaceRootLine(sIntermediate, folderSpecifier);
               }
            }
         }

         // The namespace root itself, "#Users" - \Noselect, like "#Public" - is
         // reported exactly when at least one shared folder is visible to the
         // caller, whether or not the current pattern happened to match any of
         // them ("%" matches the root and none of the folders beneath it).
         if (bAnyDelegatedVisible)
            sPatternResult += FolderListCreator::CreateNamespaceRootLine(sOtherUsersName, folderSpecifier);

         // De-duplicate lines so a mailbox matching multiple patterns is listed once.
         std::vector<String> lines = StringParser::SplitString(sPatternResult, "\r\n");
         for (const String &sLine : lines)
         {
            if (sLine.IsEmpty())
               continue;
            if (std::find(seenLines.begin(), seenLines.end(), sLine) != seenLines.end())
               continue;
            seenLines.push_back(sLine);
            sResult += sLine + "\r\n";

            // RFC 5819: the STATUS response follows the LIST line it belongs to.
            if (!sStatusItems.IsEmpty())
               sResult += CreateListStatusLine_(pConnection, sLine, sStatusItems);
         }
      }

      // When nothing matched a non-wildcard request, report the hierarchy root.
      //
      // RFC 5258 SUBSCRIBED and RFC 6154 SPECIAL-USE selection legitimately return an
      // empty list, so the root must not be synthesised for them: it is a plain-LIST
      // convention from RFC 3501, and the root is \Noselect and can never have a
      // special use, so a client filtering on SPECIAL-USE would have to know to throw
      // it away again.
      if (sResult.IsEmpty() && bAllPatternsEmpty && !bOnlySubscribed && !bOnlySpecialUse)
      {
         hierarchyDelimiter.Replace(_T("\\"), _T("\\\\"));
         sResult = _T("* LIST (\\Noselect) \"") + hierarchyDelimiter + _T("\" \"\"\r\n");
      }

      sResult += sTag + " OK LIST completed\r\n";
      pConnection->SendAsciiData(sResult);   

      return IMAPResult();
   }

   String
   IMAPCommandLIST::CreateListStatusLine_(std::shared_ptr<IMAPConnection> pConnection, const String &sListLine, const String &sStatusItems)
   {
      // RFC 5819 section 2: no STATUS for a mailbox that cannot be selected. The
      // attributes sit in the parenthesised list right after "* LIST", and no
      // attribute name contains ')', so the first one closes the list.
      int iAttrEnd = sListLine.Find(_T(")"));
      if (iAttrEnd < 0)
         return "";

      String sAttributes = sListLine.Mid(0, iAttrEnd);
      if (sAttributes.FindNoCase(_T("\\Noselect")) >= 0 ||
          sAttributes.FindNoCase(_T("\\NonExistent")) >= 0)
         return "";

      // The line ends '... "delimiter" "escaped name"' - both always quoted, the
      // name with " and \ backslash-escaped. Walk past the delimiter's quotes to
      // the name's opening quote, then read to its closing quote honouring the
      // escapes.
      int iDelimStart = sListLine.Find(_T("\""), iAttrEnd);
      if (iDelimStart < 0)
         return "";

      int iDelimEnd = sListLine.Find(_T("\""), iDelimStart + 1);
      if (iDelimEnd < 0)
         return "";

      int iNameStart = sListLine.Find(_T("\""), iDelimEnd + 1);
      if (iNameStart < 0)
         return "";

      String sEscapedName;
      for (int i = iNameStart + 1; i < sListLine.GetLength(); i++)
      {
         wchar_t ch = sListLine.GetAt(i);

         if (ch == '\\' && i + 1 < sListLine.GetLength())
         {
            sEscapedName += ch;
            i++;
            sEscapedName += sListLine.GetAt(i);
            continue;
         }

         if (ch == '"')
            break;

         sEscapedName += ch;
      }

      String sFolderName = sEscapedName;
      IMAPFolder::UnescapeFolderString(sFolderName);

      std::shared_ptr<IMAPFolder> pFolder = pConnection->GetFolderByFullPath(sFolderName);
      if (!pFolder)
         return "";

      // A mailbox the user may not read is still listed, just not STATUSed - one
      // off-limits folder must not fail the whole LIST.
      if (!pConnection->CheckPermission(pFolder, ACLPermission::PermissionRead))
         return "";

      return IMAPCommandSTATUS::CreateStatusLine(pConnection, pFolder, sFolderName, sStatusItems);
   }

   bool
   IMAPCommandLIST::HasOption_(const String &sParenContent, const String &sOption)
   {
      // Whole-token comparison rather than a substring search.
      //
      // A substring search is what this code used to do, and it was already one option
      // name away from being wrong: RFC 6154's SPECIAL-USE arrived alongside RFC 5258's
      // SUBSCRIBED, and any option whose name embeds another - the registry already has
      // RECURSIVEMATCH next to REMOTE - would silently switch on a filter the client
      // never asked for. For SUBSCRIBED that means a client suddenly being shown only
      // part of its mailbox, with nothing in the exchange to explain why.
      std::vector<String> tokens = StringParser::SplitString(sParenContent, _T(" "));

      for (const String &token : tokens)
      {
         String candidate = token;
         candidate.Trim();

         if (candidate.CompareNoCase(sOption) == 0)
            return true;
      }

      return false;
   }

   void
   IMAPCommandLIST::ExtractPatterns_(const String &sParenContent, std::vector<String> &patterns)
   {
      // The parenthesised content holds one or more space-separated patterns,
      // each optionally double-quoted, e.g.  "INBOX" "Sent"  or  INBOX Sent.
      int iPos = 0;
      int iLen = sParenContent.GetLength();
      while (iPos < iLen)
      {
         wchar_t ch = sParenContent.GetAt(iPos);
         if (ch == ' ')
         {
            iPos++;
            continue;
         }

         if (ch == '"')
         {
            int iEnd = sParenContent.Find(_T("\""), iPos + 1);
            if (iEnd < 0)
               iEnd = iLen;
            patterns.push_back(sParenContent.Mid(iPos + 1, iEnd - iPos - 1));
            iPos = iEnd + 1;
         }
         else
         {
            int iEnd = sParenContent.Find(_T(" "), iPos);
            if (iEnd < 0)
               iEnd = iLen;
            patterns.push_back(sParenContent.Mid(iPos, iEnd - iPos));
            iPos = iEnd + 1;
         }
      }

      if (patterns.empty())
         patterns.push_back(_T(""));
   }
}
