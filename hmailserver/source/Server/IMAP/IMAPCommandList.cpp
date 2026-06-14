// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPCommandList.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/IMAPFolders.h"

#include "FolderListCreator.h"
#include "IMAPConfiguration.h"

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

      // Optional leading selection-option list, e.g. "(SUBSCRIBED)".
      bool bOnlySubscribed = false;
      if (pParser->Word(iIdx)->Paranthezied())
      {
         String sSelection = pParser->Word(iIdx)->Value();
         sSelection.MakeUpper();
         if (sSelection.Find(_T("SUBSCRIBED")) >= 0)
            bOnlySubscribed = true;
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
      if (iIdx < iWordCount && pParser->Word(iIdx)->Value().CompareNoCase(_T("RETURN")) == 0)
      {
         iIdx++;
         if (iIdx < iWordCount && pParser->Word(iIdx)->Paranthezied())
         {
            String sReturn = pParser->Word(iIdx)->Value();
            sReturn.MakeUpper();
            if (sReturn.Find(_T("SUBSCRIBED")) >= 0)
               bAnnotateSubscribed = true;
            // CHILDREN is always reported (\HasChildren / \HasNoChildren), so it needs no flag.
            iIdx++;
         }
      }

      bool bExtended = bOnlySubscribed || bAnnotateSubscribed;

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
            sPatternResult = FolderListCreator::GetIMAPFolderListExtended(pConnection->GetAccount()->GetID(), pFolders, folderSpecifier, "", bOnlySubscribed, bAnnotateSubscribed) +
                             FolderListCreator::GetIMAPFolderListExtended(pConnection->GetAccount()->GetID(), pPublicFolders, folderSpecifier, sPublicFolderName, bOnlySubscribed, bAnnotateSubscribed);
         }
         else
         {
            sPatternResult = FolderListCreator::GetIMAPFolderList(pConnection->GetAccount()->GetID(), pFolders, folderSpecifier, "") +
                             FolderListCreator::GetIMAPFolderList(pConnection->GetAccount()->GetID(), pPublicFolders, folderSpecifier, sPublicFolderName);
         }

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
         }
      }

      // When nothing matched a non-wildcard request, report the hierarchy root.
      // (RFC 5258 SUBSCRIBED selection legitimately returns an empty list.)
      if (sResult.IsEmpty() && bAllPatternsEmpty && !bOnlySubscribed)
      {
         hierarchyDelimiter.Replace(_T("\\"), _T("\\\\"));
         sResult = _T("* LIST (\\Noselect) \"") + hierarchyDelimiter + _T("\" \"\"\r\n");
      }

      sResult += sTag + " OK LIST completed\r\n";
      pConnection->SendAsciiData(sResult);   

      return IMAPResult();
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
