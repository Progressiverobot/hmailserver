// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "IMAPCommandCreate.h"
#include "IMAPConnection.h"
#include "IMAPSimpleCommandParser.h"
#include "IMAPConfiguration.h"
#include "IMAPFolderUtilities.h"
#include "IMAPSpecialUse.h"

#include "../Common/BO/IMAPFolders.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/ACLPermission.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPResult
   IMAPCommandCREATE::ExecuteCommand(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");


      std::shared_ptr<IMAPSimpleCommandParser> pParser = std::shared_ptr<IMAPSimpleCommandParser>(new IMAPSimpleCommandParser());

      pParser->Parse(pArgument);

      // RFC 4466 generalises CREATE to  CREATE <mailbox> [(<parameters>)]  and RFC 6154
      // section 3 defines the only parameter this server understands, USE (<attrs>).
      if (pParser->ParamCount() != 1 && pParser->ParamCount() != 2)
         return IMAPResult(IMAPResult::ResultBad, "CREATE Command requires 1 parameter.");

      // Fetch the name of the mailbox to create.
      String sFolderName = pParser->GetParamValue(pArgument, 0);

      if (sFolderName.IsEmpty())
         return IMAPResult(IMAPResult::ResultNo, "Folder name not specified.");

      int requestedDesignations = IMAPSpecialUse::DesignationNone;
      if (pParser->ParamCount() == 2)
      {
         std::shared_ptr<IMAPSimpleWord> pParameterWord = pParser->Word(2);

         // Anything that is not a parenthesised USE parameter is a syntax error, and a
         // syntax error is BAD. Getting this wrong in the other direction matters: a
         // client answered BAD for an attribute we merely do not implement treats the
         // whole command as broken and never falls back to a plain CREATE, whereas
         // NO [USEATTR] is exactly the signal RFC 6154 defines for that case.
         if (!pParameterWord->Paranthezied())
            return IMAPResult(IMAPResult::ResultBad, "CREATE: Unrecognized parameter.");

         if (!IMAPSpecialUse::ParseUseParameter(pParameterWord->Value(), requestedDesignations))
            return IMAPResult(IMAPResult::ResultBad, "CREATE: Unrecognized parameter.");

         if (requestedDesignations == IMAPSpecialUse::DesignationNone)
            return IMAPResult(IMAPResult::ResultNo, "[USEATTR] The requested special-use attribute is not supported.");
      }

      // Check so that it does not already exist.
      std::shared_ptr<IMAPFolder> pExistsCheck = pConnection->GetFolderByFullPath(sFolderName);
      if (pExistsCheck)
      {
         if (requestedDesignations == IMAPSpecialUse::DesignationNone)
            return IMAPResult(IMAPResult::ResultNo, "Folder already exists.");

         // RFC 6154 only ever assigns a special use at CREATE time, which leaves no way
         // at all to tell the server that an existing "Enviados" is the sent folder -
         // and an existing folder full of mail is the common case, because the mailbox
         // was created by a client, by IMAP migration, or by hMailServer itself long
         // before any client asked about special use.
         //
         // The alternatives were worse. Refusing (plain "NO Folder already exists")
         // leaves the client with exactly the choice this extension exists to remove:
         // give up, or create a second sent folder next to the first and split the
         // user's mail across the two. SETMETADATA (RFC 5464) would be the tidy answer
         // but is a whole command this server does not implement. So CREATE ... USE on
         // an existing mailbox is treated as the designation request it plainly is.
         //
         // The deviation from RFC 3501's "NO if the mailbox already exists" is narrow
         // on purpose: a plain CREATE with no USE parameter still answers NO, exactly
         // as before, so no existing client sees any change. Only a client that sent
         // USE - which means a client that has read CREATE-SPECIAL-USE in our
         // CAPABILITY reply - reaches the new path, and for that client OK plus a
         // designated mailbox is the answer it wanted.
         IMAPResult designateResult = DesignateExistingFolder_(pConnection, pExistsCheck, requestedDesignations);
         if (designateResult.GetResult() != IMAPResult::ResultOK)
            return designateResult;

         // The same completion line the create path sends below. A designation request
         // is answered OK CREATE because that is the command the client sent; inventing
         // a different text here would only give a client something new to fail to
         // parse.
         String sDesignateResponse = pArgument->Tag() + " OK CREATE Completed\r\n";
         pConnection->SendAsciiData(sDesignateResponse);

         return IMAPResult();
      }

      String hierarchyDelimiter = Configuration::Instance()->GetIMAPConfiguration()->GetHierarchyDelimiter();

      std::vector<String> vecFolderPath = StringParser::SplitString(sFolderName, hierarchyDelimiter);

      bool bIsPublicFolder = IMAPFolderUtilities::IsPublicFolder(vecFolderPath);
      if (bIsPublicFolder)
         vecFolderPath.erase(vecFolderPath.begin());

      if (!IMAPFolder::IsValidFolderName(vecFolderPath, bIsPublicFolder))
         return IMAPResult(IMAPResult::ResultNo, "CREATE The folder name is invalid.");

      IMAPResult result = ConfirmPossibleToCreate(pConnection, vecFolderPath, bIsPublicFolder);
      if (result.GetResult() != IMAPResult::ResultOK)
         return result;

      // RFC 6154 section 3: "If the server cannot create a mailbox with the designated
      // special use defined, for whatever reason, it MUST NOT create the mailbox".
      // So the designation is validated before anything is written, not after.
      if (requestedDesignations != IMAPSpecialUse::DesignationNone)
      {
         IMAPResult useResult = ConfirmPossibleToDesignate_(pConnection, bIsPublicFolder, requestedDesignations, 0);
         if (useResult.GetResult() != IMAPResult::ResultOK)
            return useResult;
      }

      std::shared_ptr<IMAPFolders> pParentFolderContainer;
      if (!bIsPublicFolder)
         pParentFolderContainer = pConnection->GetAccountFolders();
      else
         pParentFolderContainer = pConnection->GetPublicFolders();

      bool bSubscribeToFolder = bIsPublicFolder;

      pParentFolderContainer->CreatePath(pParentFolderContainer, vecFolderPath, bSubscribeToFolder);

      if (requestedDesignations != IMAPSpecialUse::DesignationNone)
      {
         IMAPResult designateResult = ApplyDesignations_(pConnection, sFolderName, requestedDesignations);
         if (designateResult.GetResult() != IMAPResult::ResultOK)
            return designateResult;
      }

      // RFC 8474 (OBJECTID): the tagged OK carries the new mailbox's id.
      String sResponse;
      std::shared_ptr<IMAPFolder> pCreatedFolder = pConnection->GetFolderByFullPath(sFolderName);
      if (pCreatedFolder)
         sResponse.Format(_T("%s OK [MAILBOXID (F%I64d)] CREATE Completed\r\n"), pArgument->Tag().c_str(), pCreatedFolder->GetID());
      else
         sResponse = pArgument->Tag() + " OK CREATE Completed\r\n";

      pConnection->SendAsciiData(sResponse);

      HM_ASSERT(pParentFolderContainer->GetCount() > 0);

      // Send a notification to everyone subscribing to this event.
      std::shared_ptr<IMAPFolder> firstFolder = pParentFolderContainer->GetItem(0);

      return IMAPResult();
   }

   IMAPResult
   IMAPCommandCREATE::ConfirmPossibleToCreate(std::shared_ptr<HM::IMAPConnection> pConnection, const std::vector<String> &vecNewPath, bool bIsPublicFolder)
   {
      if (bIsPublicFolder)
      {
         std::shared_ptr<IMAPFolders> pFolders = pConnection->GetPublicFolders();

         std::vector<String> vecTempPath = vecNewPath;
         vecTempPath.erase(vecTempPath.end()-1);
         std::shared_ptr<IMAPFolder> pParentFolder = IMAPFolderUtilities::GetTopMostExistingFolder(pFolders, vecTempPath);

         // Check if the user has permission to create a folder in the parent folder
         if (pParentFolder)
         {
            if (!pConnection->CheckPermission(pParentFolder, ACLPermission::PermissionCreate))
               return IMAPResult(IMAPResult::ResultNo, "ACL: Create permission denied (Required for CREATE command).");
         }

         // Check if the user is trying to create a new root public folder, such as Public folders/Test
         if (bIsPublicFolder && !pParentFolder)
            return IMAPResult(IMAPResult::ResultNo, "ACL: Root public folders can only be created using administration tools.");
      }

      return IMAPResult();
   }

   IMAPResult
   IMAPCommandCREATE::ConfirmPossibleToDesignate_(std::shared_ptr<HM::IMAPConnection> pConnection, bool bIsPublicFolder, int requestedDesignations, __int64 excludeFolderID)
   {
      // A special use is a property of one user's mailbox. A public folder is visible
      // to every account that has been granted lookup rights on it, so designating one
      // \Trash would tell all of those clients that this is the folder to move
      // deletions into and to empty - including the ones with read-only access, whose
      // expunges would then fail in a way the user cannot act on.
      if (bIsPublicFolder)
         return IMAPResult(IMAPResult::ResultNo, "[USEATTR] A public folder cannot be given a special-use attribute.");

      int existingDesignations = IMAPSpecialUse::GetExplicitDesignations(pConnection->GetAccountFolders(), excludeFolderID);
      int conflictingDesignations = existingDesignations & requestedDesignations;

      if (conflictingDesignations != IMAPSpecialUse::DesignationNone)
      {
         // RFC 6154 leaves it to the server whether to allow two mailboxes with the
         // same special use, and refusing is the only answer that helps anyone. With
         // \Sent on two folders the client has no way to arbitrate: it picks whichever
         // line it happened to parse last, different clients pick differently, and the
         // user's sent mail ends up split across both folders - which is the exact
         // failure this extension exists to prevent, reintroduced by the extension
         // itself. Name the clashing attribute in the text so a client can tell the
         // user which designation it needs to release first.
         String message;
         message.Format(_T("[USEATTR] The special-use attribute %s is already assigned to another folder."),
            IMAPSpecialUse::FormatDesignations(conflictingDesignations).c_str());

         return IMAPResult(IMAPResult::ResultNo, message);
      }

      return IMAPResult();
   }

   IMAPResult
   IMAPCommandCREATE::ApplyDesignations_(std::shared_ptr<HM::IMAPConnection> pConnection, const String &sFolderName, int requestedDesignations)
   {
      // CreatePath does not hand back the folder it created, and changing its signature
      // would mean editing a business object shared with the COM API and the backup
      // code. Looking the leaf up again costs one walk of an already cached tree.
      std::shared_ptr<IMAPFolder> pCreatedFolder = pConnection->GetFolderByFullPath(sFolderName);

      if (!pCreatedFolder)
      {
         String message;
         message.Format(_T("The folder %s was created but could not be found again, so its special-use designation (mask %d) was not stored."),
            sFolderName.c_str(), requestedDesignations);

         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5901, "IMAPCommandCREATE::ApplyDesignations_", message);

         return IMAPResult(IMAPResult::ResultNo, "[USEATTR] The folder was created but its special-use attribute could not be stored.");
      }

      return StoreDesignations_(pCreatedFolder, sFolderName, requestedDesignations);
   }

   IMAPResult
   IMAPCommandCREATE::DesignateExistingFolder_(std::shared_ptr<HM::IMAPConnection> pConnection, std::shared_ptr<IMAPFolder> pFolder, int requestedDesignations)
   {
      // A folder with account id zero is a public folder, whatever path the client used
      // to name it. Asking the object rather than re-deriving the answer from the path
      // means the two checks - this one and the one on the create path - cannot drift
      // apart if the public folder prefix is reconfigured.
      IMAPResult confirmResult = ConfirmPossibleToDesignate_(pConnection, pFolder->IsPublicFolder(), requestedDesignations, pFolder->GetID());
      if (confirmResult.GetResult() != IMAPResult::ResultOK)
         return confirmResult;

      int stored = pFolder->GetSpecialUseFlags() & IMAPSpecialUse::DesignationMask;
      int merged = stored | requestedDesignations;

      // Nothing to write: the client is telling us something we already know, which is
      // what happens when it re-runs its setup or when two of the user's devices both
      // decide to designate the same folder. Answer OK without touching the database -
      // a no-op UPDATE would be harmless but this also makes the whole command
      // idempotent, which is what lets a client retry it after a dropped connection.
      if (merged == stored)
         return IMAPResult();

      // The requested attributes are ADDED rather than assigned. "Enviados is my sent
      // folder" says nothing about whether it is also the archive, and a client that
      // asked only about \Sent must not silently strip an \Archive somebody else set.
      // Removing a designation therefore needs a deliberate act and is not something
      // CREATE can do by accident.
      return StoreDesignations_(pFolder, pFolder->GetFolderName(), merged);
   }

   IMAPResult
   IMAPCommandCREATE::StoreDesignations_(std::shared_ptr<IMAPFolder> pFolder, const String &sFolderName, int designations)
   {
      // A targeted single-column UPDATE keyed on the folder id, not the whole-row
      // PersistentIMAPFolder::SaveObject. SaveObject infers INSERT versus UPDATE from
      // the id the cached object happens to be carrying, so a folder whose insert
      // failed - CreatePath ignores the result of its own save - would have its
      // designation written as a second INSERT, i.e. a duplicate hm_imapfolders row
      // for a folder that already had one. See IMAPFolder::StoreSpecialUseFlags.
      if (pFolder->StoreSpecialUseFlags(designations))
         return IMAPResult();

      String message;
      message.Format(_T("The special-use designation (mask %d) for folder %s could not be written to the database."),
         designations, sFolderName.c_str());

      ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5900, "IMAPCommandCREATE::StoreDesignations_", message);

      // On the create path the folder itself is left in place, which is a deliberate
      // deviation from "MUST NOT create the mailbox". Undoing the creation would mean
      // deleting folders in response to a database error - including any parents
      // CreatePath had to create on the way down, which are indistinguishable
      // afterwards from folders the user already had - and destroying mailboxes because
      // a write failed is a far worse failure mode than leaving one undesignated folder
      // that the client can see, use, and delete itself. The client is told the
      // designation did not happen, which is the part it can act on.
      return IMAPResult(IMAPResult::ResultNo, "[USEATTR] The special-use attribute could not be stored.");
   }
}
