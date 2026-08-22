// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "FolderListCreator.h"

#include "../Common/Application/ACLManager.h"

#include "../Common/BO/ACLPermission.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/IMAPFolders.h"

#include "../IMAP/IMAPConfiguration.h"
#include "../IMAP/IMAPSpecialUse.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   FolderListCreator::FolderListCreator()
   {

   }

   FolderListCreator::~FolderListCreator()
   {

   }

   String
   FolderListCreator::GetIMAPFolderList(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix, bool *pAnyFolderVisible)
   {
      ListRequest_ request;
      request.emit_as_list_ = true;

      return Create_(iAccountID, pStartFolders, sWildcard, sPrefix, request, pAnyFolderVisible);
   }

   String
   FolderListCreator::GetIMAPLSUBFolderList(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix)
   {
      ListRequest_ request;
      request.emit_as_list_ = false;
      request.only_subscribed_ = true;

      return Create_(iAccountID, pStartFolders, sWildcard, sPrefix, request, nullptr);
   }

   String
   FolderListCreator::GetIMAPFolderListExtended(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix, bool bOnlySubscribed, bool bAnnotateSubscribed, bool bOnlySpecialUse, bool *pAnyFolderVisible)
   {
      ListRequest_ request;
      request.emit_as_list_ = true;
      request.only_subscribed_ = bOnlySubscribed;
      request.annotate_subscribed_ = bAnnotateSubscribed;
      request.only_special_use_ = bOnlySpecialUse;

      return Create_(iAccountID, pStartFolders, sWildcard, sPrefix, request, pAnyFolderVisible);
   }

   String
   FolderListCreator::CreateNamespaceRootLine(const String &sRootPath, const String &sWildcard)
   {
      String hierarchyDelimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      if (!FolderWildcardMatch_(sRootPath, sWildcard, hierarchyDelimiter))
         return _T("");

      // The same dummy-folder emission the listing walk uses for a namespace
      // root it reached through a prefix ("#Public", "#Users.owner@domain").
      ListRequest_ request;
      request.emit_as_list_ = true;

      std::shared_ptr<IMAPFolder> pFolderDummy;
      String sPath = sRootPath;

      String sLine = CreateFolderLine_(pFolderDummy, true, sPath, sWildcard, false, hierarchyDelimiter, request);
      if (sLine.IsEmpty())
         return _T("");

      return sLine + _T("\r\n");
   }

   String
   FolderListCreator::Create_(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix, ListRequest_ &request, bool *pAnyFolderVisible)
   {
      if (pAnyFolderVisible)
         *pAnyFolderVisible = false;

      // RFC 6154: work out which folder owns which special-use attribute once, before
      // the walk, rather than deciding per folder inside it.
      //
      // It has to be done up front because the rule is a global one - an attribute may
      // be handed out at most once per mailbox - and a walk that emits each line as it
      // reaches it cannot know whether a later folder has a better claim. The cost is
      // one extra pass over the (in-memory, already cached) folder tree per pattern;
      // the walk itself was already a pass over the same tree, so this doubles a cost
      // measured in microseconds for a realistic mailbox.
      //
      // The public folder collection is identified by account id zero and gets no
      // designations at all, deliberately: see ListRequest_::special_use_. A tree
      // being listed on behalf of an account that does not own it - a shared
      // mailbox under "#Users" - gets none either, for the same reason: the
      // owner's \Trash advertised to a delegate is an invitation for the
      // delegate's client to expunge the owner's mail.
      if (pStartFolders && pStartFolders->GetAccountID() != 0 && pStartFolders->GetAccountID() == iAccountID)
         IMAPSpecialUse::Resolve(pStartFolders, request.special_use_);

      std::vector<String> vecCurrentFolder;
      std::vector<String> vecMatchingFolders;

      CreateIMAPFolderList_(iAccountID, pStartFolders, sWildcard, sPrefix, vecCurrentFolder, vecMatchingFolders, request, pAnyFolderVisible);

      String sRet = StringParser::JoinVector(vecMatchingFolders, "\r\n");

      if (!sRet.IsEmpty())
         sRet += "\r\n";

      return sRet;
   }

   void
   FolderListCreator::CreateIMAPFolderList_(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix, std::vector<String> &vecCurrentFolder, std::vector<String> &vecMatchingFolders, const ListRequest_ &request, bool *pAnyFolderVisible)
   {
      if (vecCurrentFolder.size() > IMAPFolder::MaxFolderDepth)
         return;

      String hierarchyDelimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      bool anyFolderVisible = false;

	  ACLManager aclManager;
      for(std::shared_ptr<IMAPFolder> currentFolder : pStartFolders->GetVector())
      {
         // Check if the user has access to this folder. Otherwise just skip it.
         std::shared_ptr<ACLPermission> pPermission = aclManager.GetPermissionForFolder(iAccountID, currentFolder);
         if (!pPermission || !pPermission->GetAllow(ACLPermission::PermissionLookup))
         {
            continue;
         }

         // at least one folder at this level is visible to the user.
         anyFolderVisible = true;

         vecCurrentFolder.push_back(currentFolder->GetFolderName());

         String sFullPath = StringParser::JoinVector(vecCurrentFolder, hierarchyDelimiter);

         if (!sPrefix.IsEmpty())
            sFullPath = sPrefix + hierarchyDelimiter + sFullPath;

         std::shared_ptr<IMAPFolders> subFolders = currentFolder->GetSubFolders();
         bool hasSubFolders = subFolders->GetCount() > 0;

         // Do we match?
         if (FolderWildcardMatch_(sFullPath, sWildcard, hierarchyDelimiter))
         {
            String sFolderLine = CreateFolderLine_(currentFolder, hasSubFolders, sFullPath, sWildcard, true, hierarchyDelimiter, request);

            if (!sFolderLine.IsEmpty())
               vecMatchingFolders.push_back(sFolderLine);
         }

         if (hasSubFolders)
            CreateIMAPFolderList_(iAccountID, subFolders, sWildcard, sPrefix, vecCurrentFolder, vecMatchingFolders, request, nullptr);

         vecCurrentFolder.erase(vecCurrentFolder.end() - 1);
      }

      // Only the outermost frame reports visibility and emits the root: nested
      // frames enter with a non-empty vecCurrentFolder, and the flag means
      // "a TOP-level folder of this tree is visible" - the same condition the
      // root line has always been emitted on.
      if (vecCurrentFolder.size() == 0 && pAnyFolderVisible)
         *pAnyFolderVisible = anyFolderVisible;

      if (vecCurrentFolder.size() == 0 && anyFolderVisible && !sPrefix.IsEmpty())
      {
         // The user can see at least one folder under this namespace root and
         // we're on the top level, so report the root itself as \Noselect. The
         // root is the prefix this listing was invoked with: "#Public" for the
         // public tree (callers pass the configured public folder name as the
         // prefix), "#Users.owner@domain" for a shared mailbox tree.
         String rootName = sPrefix;

         if (FolderWildcardMatch_(rootName, sWildcard, hierarchyDelimiter))
         {
            std::shared_ptr<IMAPFolder> pFolderDummy;
            String sFolderLine = CreateFolderLine_(pFolderDummy, true, rootName, sWildcard, false, hierarchyDelimiter, request);

            if (!sFolderLine.IsEmpty())
               vecMatchingFolders.push_back(sFolderLine);
         }

      }

   }

   String
   FolderListCreator::CreateFolderLine_(std::shared_ptr<IMAPFolder> currentFolder, bool hasSubFolders, String &sFullPath, const String &sWildcard, bool isSelectable, String hierarchyDelimiter, const ListRequest_ &request)
   {
      String nameAttributes = hasSubFolders ? "\\HasChildren" : "\\HasNoChildren";

      if (!isSelectable)
         nameAttributes += " \\Noselect";

      // RFC 6154 special-use attributes, so that clients map Sent/Drafts/Trash/Junk
      // automatically instead of creating duplicates. Which folder owns which
      // attribute was decided once, for the whole mailbox, in Create_.
      //
      // They are emitted whether or not the client asked for them with
      // RETURN (SPECIAL-USE). RFC 6154 section 4.2 permits that, and hMailServer
      // 6.2.18 already emitted the name-derived ones unconditionally: making them
      // conditional now would take the attributes away from every client that relies
      // on the old behaviour without asking, which is the larger regression.
      int designations = GetDesignations_(currentFolder, request);
      if (isSelectable && designations != IMAPSpecialUse::DesignationNone)
         nameAttributes += " " + IMAPSpecialUse::FormatDesignations(designations);

      // RFC 6154 section 4.1, the SPECIAL-USE selection option: return only mailboxes
      // that have a special use. Checked after the attributes are computed and before
      // anything is emitted, so the \Noselect public-folder root - which can never
      // carry a designation - is filtered out too.
      if (request.only_special_use_ && (!isSelectable || designations == IMAPSpecialUse::DesignationNone))
         return _T("");

      // RFC 5258 (LIST-EXTENDED) return option SUBSCRIBED: annotate folders the
      // user is subscribed to with the \Subscribed attribute.
      if (request.annotate_subscribed_ && (!currentFolder || currentFolder->GetIsSubscribed()))
         nameAttributes += " \\Subscribed";

      // Workaround for Outlook "feature".
      AdjustCaseToClientCase_(sFullPath, sWildcard, hierarchyDelimiter);

      
      // We cannot send " or \ directly in the response.
      // We must escape those:
      IMAPFolder::EscapeFolderString(sFullPath);

      // 2008-11-04
      // Always quote the string. This is how GMail acts.
      sFullPath = "\"" + sFullPath + "\"";

      String sFolderLine = "";

      // \ needs to be escaped.
      hierarchyDelimiter.Replace(_T("\\"), _T("\\\\"));

      if (!request.emit_as_list_)
      {
         // LSUB listing: only subscribed folders, emitted as "* LSUB".
         if (request.only_subscribed_ && (!currentFolder || currentFolder->GetIsSubscribed()))
            sFolderLine.Format(_T("* LSUB (%s) \"%s\" %s"), nameAttributes.c_str(), hierarchyDelimiter.c_str(), sFullPath.c_str());
      }
      else
      {
         // LIST / LIST-EXTENDED listing, emitted as "* LIST". When the SUBSCRIBED
         // selection option is active, unsubscribed folders are filtered out.
         if (request.only_subscribed_ && currentFolder && !currentFolder->GetIsSubscribed())
            return _T("");

         sFolderLine.Format(_T("* LIST (%s) \"%s\" %s"), nameAttributes.c_str(), hierarchyDelimiter.c_str(), sFullPath.c_str());
      }

      return sFolderLine;
   }

   int
   FolderListCreator::GetDesignations_(std::shared_ptr<IMAPFolder> currentFolder, const ListRequest_ &request)
   {
      // The dummy folder used for the public-folder root has no row and therefore no
      // designation.
      if (!currentFolder)
         return IMAPSpecialUse::DesignationNone;

      auto iterDesignation = request.special_use_.find(currentFolder->GetID());
      if (iterDesignation == request.special_use_.end())
         return IMAPSpecialUse::DesignationNone;

      return (*iterDesignation).second;
   }

   bool
   FolderListCreator::FolderWildcardMatch_(const String &sFolderName, const String &sWildcard, const String &hierarchyDelimiter)
   {
      // Convert the wildcard path to internal format.
      std::vector<String> vecWildcardPath = StringParser::SplitString(sWildcard, hierarchyDelimiter);
      std::vector<String> vecExistingPath = StringParser::SplitString(sFolderName, hierarchyDelimiter);

      String sTempWildcard = StringParser::JoinVector(vecWildcardPath, hierarchyDelimiter);


      long lRealFolderPos = 0;

      for (int i = 0; i < sTempWildcard.GetLength(); i++)
      {
         wchar_t sCurWildcardChar = sTempWildcard.GetAt(i);

         if (lRealFolderPos >= sFolderName.GetLength())
         {
            // Folder INBOX should match INBOX*.
            if ((sCurWildcardChar == '*' || sCurWildcardChar == '%') && 
               sTempWildcard.GetLength() == i + 1)
               return true;

            return false;
         }

         while (lRealFolderPos < sFolderName.GetLength())
         {

            wchar_t sCurRealCharacter = sFolderName.GetAt(lRealFolderPos);

            // Check if the current char matches the current wildcard char.
            if (sCurWildcardChar == '*')
            {
               // the current wildcard character is a *. we got a match.
               lRealFolderPos++;
               continue;
            }

            if (sCurWildcardChar == '%')
            {
               if (sCurRealCharacter == hierarchyDelimiter.GetAt(0))
               {
                  break;
               }
               else
               {
                  lRealFolderPos++;
                  continue;
               }
            }

            if (toupper(sCurRealCharacter) == toupper(sCurWildcardChar))
            {
               // the current wildcard char matches the current char exactly.
               lRealFolderPos++;
               break;
            }

            // The wildcard character doesn't match the current character in the existing folder.
            return false;



         }

      }

      if (lRealFolderPos < sFolderName.GetLength())
         return false;

      return true;
   }

   void
   FolderListCreator::AdjustCaseToClientCase_(String &sPath, const String &sWildcard, const String &hierarchyDelimiter)
   {
      // Outlook 2003 requires the correct case after creating a new folder.
      // If OE2003 executes CREATE Inbox.SubFolder it requires the response
      // to be Inbox.SubFolder and not INBOX.SubFolder... Even if the real
      // name of the inbox is INBOX and not inbox.. and even if the inbox
      // is working properly.......

      std::vector<String> vecPath = StringParser::SplitString(sPath, hierarchyDelimiter);
      std::vector<String> vecWildcard = StringParser::SplitString(sWildcard, hierarchyDelimiter);

      // Build the response string.
      auto pathIterator = vecPath.begin();
      auto wildIterator = vecWildcard.begin();

      std::vector<String> vecResult;
      while (pathIterator != vecPath.end())
      {
         String sFoldername = (*pathIterator);

         if (wildIterator == vecWildcard.end())
            return;

         String sWildFolder = (*wildIterator);

         if (sFoldername.CompareNoCase(sWildFolder) == 0)
            sFoldername = sWildFolder;

         vecResult.push_back(sFoldername);

         wildIterator ++;
         pathIterator++;

      }

      sPath = StringParser::JoinVector(vecResult, hierarchyDelimiter);
   }



}
