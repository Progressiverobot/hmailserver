// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPSpecialUse.h"

#include "../Common/BO/IMAPFolder.h"
#include "../Common/BO/IMAPFolders.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   // The folder-name fallback table, in precedence order. Two properties of this
   // table are load bearing:
   //
   //   * The names are exactly the ones hMailServer 6.2.18 recognised, so a client
   //     that never asks for SPECIAL-USE sees no change whatsoever. The one visible
   //     difference is that a mailbox holding two names that map to the same
   //     attribute now gets the attribute on one folder instead of both.
   //   * Within a designation the canonical IMAP name comes first, so that an account
   //     holding both "Sent" and "Sent Items" designates "Sent". Order, not folder id,
   //     decides, because folder ids depend on the order the user happened to create
   //     the folders in and would make the answer differ between two accounts with
   //     identical folder names.
   //
   // \All and \Flagged deliberately have no entry. \All means "every message in the
   // store" and \Flagged means "every flagged message"; both describe virtual
   // mailboxes that this server does not have, so guessing them from a folder called
   // "All Mail" would advertise a mailbox whose contents do not match the promise and
   // send clients hunting for messages that are not in it. They can only be assigned
   // deliberately, through CREATE ... USE.
   const IMAPSpecialUse::NameMapping IMAPSpecialUse::name_mappings_[] =
   {
      { _T("Archive"),          IMAPSpecialUse::DesignationArchive },
      { _T("Archives"),         IMAPSpecialUse::DesignationArchive },
      { _T("Drafts"),           IMAPSpecialUse::DesignationDrafts  },
      { _T("Junk"),             IMAPSpecialUse::DesignationJunk    },
      { _T("Junk E-mail"),      IMAPSpecialUse::DesignationJunk    },
      { _T("Junk Email"),       IMAPSpecialUse::DesignationJunk    },
      { _T("Spam"),             IMAPSpecialUse::DesignationJunk    },
      { _T("Sent"),             IMAPSpecialUse::DesignationSent    },
      { _T("Sent Items"),       IMAPSpecialUse::DesignationSent    },
      { _T("Sent Messages"),    IMAPSpecialUse::DesignationSent    },
      { _T("Trash"),            IMAPSpecialUse::DesignationTrash   },
      { _T("Deleted Items"),    IMAPSpecialUse::DesignationTrash   },
      { _T("Deleted Messages"), IMAPSpecialUse::DesignationTrash   }
   };

   const size_t IMAPSpecialUse::name_mapping_count_ = sizeof(name_mappings_) / sizeof(name_mappings_[0]);

   bool
   IMAPSpecialUse::ParseUseParameter(const String &parameterContent, int &designations)
   {
      designations = DesignationNone;

      // What arrives here is the inside of the parenthesised CREATE parameter, i.e.
      // the simple command parser has already turned  (USE (\Drafts))  into
      // USE (\Drafts).
      String content = parameterContent;
      content.Trim();

      if (content.Left(3).CompareNoCase(_T("USE")) != 0)
         return false;

      content = content.Mid(3);
      content.Trim();

      // RFC 4466 spells the parameter  "USE" SP "(" use-attr *(SP use-attr) ")"  but
      // be liberal about the separating space; a client that omits it is unambiguous.
      //
      // Requiring the last character to be the closing parenthesis is what rejects a
      // second create-parameter we do not implement, e.g. "USE (\Sent) OTHERPARAM x".
      // RFC 4466 section 2.2 tells servers to reject unrecognised parameters, and the
      // reason to bother is that accepting one silently would hand the client a
      // mailbox that does not have the property it asked for.
      if (content.GetLength() < 2 ||
          content.GetAt(0) != '(' ||
          content.GetAt(content.GetLength() - 1) != ')')
         return false;

      String attributes = content.Mid(1, content.GetLength() - 2);

      int parsed = DesignationNone;
      if (!ParseAttributeList(attributes, parsed))
      {
         // The word IS a USE parameter, it just names something we cannot provide.
         // RFC 6154 section 3 distinguishes the two cases and so must we: a malformed
         // command is BAD, an unsupported use is NO with a USEATTR response code, and
         // a client that gets BAD for \Foo will retry forever instead of falling back
         // to a plain CREATE. Report success with an empty mask so the caller can
         // tell them apart.
         return true;
      }

      designations = parsed;
      return true;
   }

   bool
   IMAPSpecialUse::ParseAttributeList(const String &attributeList, int &designations)
   {
      designations = DesignationNone;

      std::vector<String> tokens = StringParser::SplitString(attributeList, _T(" "));

      int parsed = DesignationNone;
      bool anyAttribute = false;

      for (const String &token : tokens)
      {
         String attribute = token;
         attribute.Trim();

         // SplitString keeps the empty run between two adjacent separators, so
         // "\Sent  \Drafts" yields three tokens with the middle one empty.
         if (attribute.IsEmpty())
            continue;

         int designation = DesignationFromAttribute_(attribute);
         if (designation == DesignationNone)
            return false;

         parsed |= designation;
         anyAttribute = true;
      }

      // "USE ()" is not legal syntax and, more importantly, accepting it as "no
      // designation requested" would hand back a plain folder to a client that
      // believes it asked for a special one - the exact silent-divergence this
      // extension exists to prevent.
      if (!anyAttribute)
         return false;

      designations = parsed;
      return true;
   }

   String
   IMAPSpecialUse::FormatDesignations(int designations)
   {
      String result;

      // Lowest bit first, so the attribute order for a given mailbox is stable
      // between runs and between backends. Clients do not care, but a listing that
      // reorders itself makes every regression test on LIST output flaky and makes
      // two log excerpts impossible to diff.
      for (int bit = 1; bit <= (int) DesignationTrash; bit <<= 1)
      {
         if ((designations & bit) == 0)
            continue;

         const wchar_t *attribute = AttributeFromDesignation_(bit);
         if (!attribute)
            continue;

         if (!result.IsEmpty())
            result += _T(" ");

         result += attribute;
      }

      return result;
   }

   void
   IMAPSpecialUse::Resolve(std::shared_ptr<IMAPFolders> accountFolders, std::map<__int64, int> &designationsByFolderID)
   {
      designationsByFolderID.clear();

      if (!accountFolders)
         return;

      int claimed = DesignationNone;

      // Pass one: what the client actually asked for, at any depth in the tree. A
      // designation is not restricted to top-level folders because a client using a
      // "." hierarchy under INBOX legitimately does CREATE "INBOX.Sent" (USE (\Sent)),
      // and refusing that would push it straight back to name guessing.
      CollectExplicit_(accountFolders, designationsByFolderID, claimed, 0, 0);

      // Pass two: the name fallback, top level only - which is where 6.2.18 applied
      // it, and widening it now would start attaching \Trash to somebody's
      // "Projects.Trash" subfolder and invite a client to empty it.
      for (size_t i = 0; i < name_mapping_count_; i++)
      {
         const NameMapping &mapping = name_mappings_[i];

         if ((claimed & mapping.designation_) != 0)
            continue;

         std::shared_ptr<IMAPFolder> folder = accountFolders->GetFolderByName(mapping.name_, false);
         if (!folder)
            continue;

         // A folder the client has explicitly designated is not evidence about
         // anything else. Without this, a folder created as
         // CREATE "Sent" (USE (\Drafts)) - unusual, but a client migrating a mailbox
         // does it - would come back as both \Drafts and \Sent, and the mailbox would
         // then have no folder the client could safely file sent mail into.
         if ((folder->GetSpecialUseFlags() & DesignationMask) != DesignationNone)
            continue;

         designationsByFolderID[folder->GetID()] |= mapping.designation_;
         claimed |= mapping.designation_;
      }
   }

   int
   IMAPSpecialUse::GetExplicitDesignations(std::shared_ptr<IMAPFolders> accountFolders, __int64 excludeFolderID)
   {
      // The map is discarded; only the accumulated mask is wanted. Sharing the walk
      // with Resolve() rather than writing a second one keeps the "first folder wins"
      // rule in exactly one place - two copies of that rule would eventually disagree
      // and CREATE would start refusing designations LIST never reports.
      std::map<__int64, int> unused;
      int claimed = DesignationNone;

      CollectExplicit_(accountFolders, unused, claimed, excludeFolderID, 0);

      return claimed;
   }

   void
   IMAPSpecialUse::CollectExplicit_(std::shared_ptr<IMAPFolders> folders, std::map<__int64, int> &designationsByFolderID, int &claimed, __int64 excludeFolderID, size_t depth)
   {
      if (!folders)
         return;

      // The same depth guard the LIST walk uses. A folderparentid cycle cannot be
      // built through the IMAP commands, but a hand-edited or partially restored
      // database can contain one, and unbounded recursion here would take the whole
      // service down with a stack overflow rather than spoiling one LIST response.
      if (depth > (size_t) IMAPFolder::MaxFolderDepth)
         return;

      for (std::shared_ptr<IMAPFolder> folder : folders->GetVector())
      {
         if (!folder)
            continue;

         // The excluded folder is skipped but still descended into: its children are
         // other folders and their claims are real. Only the one row is invisible.
         if (excludeFolderID == 0 || folder->GetID() != excludeFolderID)
         {
            int stored = folder->GetSpecialUseFlags() & DesignationMask;

            // Drop any attribute an earlier folder already claimed. The database cannot
            // enforce "at most one \Sent per account" on its own: it would need a unique
            // index over a bit test, which neither SQL CE nor MySQL 5.x can express. So
            // the invariant is enforced when the designation is written (CREATE refuses
            // the second one) and enforced again here when it is read, which is what
            // covers rows written by an older build, restored from a backup, or edited
            // by hand.
            int grant = stored & ~claimed;
            if (grant != DesignationNone)
            {
               designationsByFolderID[folder->GetID()] |= grant;
               claimed |= grant;
            }
         }

         CollectExplicit_(folder->GetSubFolders(), designationsByFolderID, claimed, excludeFolderID, depth + 1);
      }
   }

   int
   IMAPSpecialUse::DesignationFromAttribute_(const String &attribute)
   {
      // IMAP flag and attribute names are case insensitive, and clients do vary -
      // "\sent" from at least one mobile client, "\Sent" from Thunderbird.
      if (attribute.CompareNoCase(_T("\\All")) == 0)
         return DesignationAll;

      if (attribute.CompareNoCase(_T("\\Archive")) == 0)
         return DesignationArchive;

      if (attribute.CompareNoCase(_T("\\Drafts")) == 0)
         return DesignationDrafts;

      if (attribute.CompareNoCase(_T("\\Flagged")) == 0)
         return DesignationFlagged;

      if (attribute.CompareNoCase(_T("\\Junk")) == 0)
         return DesignationJunk;

      if (attribute.CompareNoCase(_T("\\Sent")) == 0)
         return DesignationSent;

      if (attribute.CompareNoCase(_T("\\Trash")) == 0)
         return DesignationTrash;

      return DesignationNone;
   }

   const wchar_t *
   IMAPSpecialUse::AttributeFromDesignation_(int designation)
   {
      switch (designation)
      {
      case DesignationAll:
         return _T("\\All");
      case DesignationArchive:
         return _T("\\Archive");
      case DesignationDrafts:
         return _T("\\Drafts");
      case DesignationFlagged:
         return _T("\\Flagged");
      case DesignationJunk:
         return _T("\\Junk");
      case DesignationSent:
         return _T("\\Sent");
      case DesignationTrash:
         return _T("\\Trash");
      default:
         return 0;
      }
   }
}
