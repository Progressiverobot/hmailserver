// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class IMAPFolders;
   class IMAPFolder;

   class FolderListCreator
   {
   public:

      FolderListCreator();
      virtual ~FolderListCreator();


      static String GetIMAPFolderList(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix);
      static String GetIMAPLSUBFolderList(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix);

      // RFC 5258 (LIST-EXTENDED): a normal "* LIST" listing that can be filtered to
      // subscribed folders only (selection option SUBSCRIBED) and/or annotate the
      // \Subscribed attribute (return option SUBSCRIBED).
      static String GetIMAPFolderListExtended(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix, bool bOnlySubscribed, bool bAnnotateSubscribed);

   private:

      static String CreateFolderLine_(std::shared_ptr<IMAPFolder> currentFolder, bool bOnlySubscribed, bool hasSubFolders, String &sFullPath, const String &sWildcard, bool isSelectable, String hierarchyDelimiter, bool bEmitAsList, bool bAnnotateSubscribed);
      static void CreateIMAPFolderList_(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, bool bOnlySubscribed, const String &sPrefix, std::vector<String> &vecCurrentFolder, std::vector<String> &vecMatchingFolders, bool bEmitAsList, bool bAnnotateSubscribed);

      // RFC 6154: returns the special-use attribute (e.g. "\\Sent") for
      // well-known top-level folder names, or an empty string.
      static String GetSpecialUseAttribute_(const String &folderName);

      static bool FolderWildcardMatch_(const String &sFolderName, const String &sWildcard, const String &hierarchyDelimiter_);
      static void AdjustCaseToClientCase_(String &sPath, const String &sWildcard, const String &hierarchyDelimiter);

   };


}