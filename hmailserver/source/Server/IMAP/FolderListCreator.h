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

      // RFC 5258 (LIST-EXTENDED) and RFC 6154 (SPECIAL-USE): a normal "* LIST" listing
      // that can be filtered to subscribed folders only (selection option SUBSCRIBED),
      // filtered to folders carrying a special-use attribute (selection option
      // SPECIAL-USE), and/or annotate the \Subscribed attribute (return option
      // SUBSCRIBED).
      static String GetIMAPFolderListExtended(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix, bool bOnlySubscribed, bool bAnnotateSubscribed, bool bOnlySpecialUse);

   private:

      // Everything one listing needs, carried as a unit through the recursive walk.
      //
      // This used to be a positional parameter list, and adding the RFC 6154 options
      // would have taken CreateFolderLine_ to ten parameters, five of them bool. That
      // is the shape where a caller transposes two flags, every call site still
      // compiles clean at /W3, and the only symptom is that LSUB starts answering like
      // LIST. Naming them costs a struct and removes the whole class of mistake.
      struct ListRequest_
      {
         ListRequest_() :
            only_subscribed_(false),
            annotate_subscribed_(false),
            only_special_use_(false),
            emit_as_list_(true)
         {
         }

         // LSUB, and the LIST-EXTENDED SUBSCRIBED selection option: skip folders the
         // user is not subscribed to.
         bool only_subscribed_;

         // LIST-EXTENDED SUBSCRIBED return option: add \Subscribed to the attributes.
         bool annotate_subscribed_;

         // RFC 6154 SPECIAL-USE selection option: skip folders that carry no
         // special-use attribute.
         bool only_special_use_;

         // True for "* LIST" output, false for "* LSUB" output.
         bool emit_as_list_;

         // Folder database id -> RFC 6154 designation bitmask, resolved once for the
         // whole mailbox before the walk starts. Deliberately empty when listing
         // public folders: a special use belongs to one user's mailbox, and a shared
         // folder advertised as \Trash to everybody who can see it would have one
         // user's client expunging another user's mail.
         std::map<__int64, int> special_use_;
      };

      static String Create_(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix, ListRequest_ &request);

      static String CreateFolderLine_(std::shared_ptr<IMAPFolder> currentFolder, bool hasSubFolders, String &sFullPath, const String &sWildcard, bool isSelectable, String hierarchyDelimiter, const ListRequest_ &request);
      static void CreateIMAPFolderList_(__int64 iAccountID, std::shared_ptr<IMAPFolders> pStartFolders, const String &sWildcard, const String &sPrefix, std::vector<String> &vecCurrentFolder, std::vector<String> &vecMatchingFolders, const ListRequest_ &request);

      // The designations resolved for this folder, or IMAPSpecialUse::DesignationNone.
      static int GetDesignations_(std::shared_ptr<IMAPFolder> currentFolder, const ListRequest_ &request);

      static bool FolderWildcardMatch_(const String &sFolderName, const String &sWildcard, const String &hierarchyDelimiter_);
      static void AdjustCaseToClientCase_(String &sPath, const String &sWildcard, const String &hierarchyDelimiter);

   };


}
