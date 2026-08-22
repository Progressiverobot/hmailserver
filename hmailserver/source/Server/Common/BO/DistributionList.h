// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{

   class DistributionListRecipients;
   
   class DistributionList : public BusinessObject<DistributionList>
   {
   public:
      DistributionList(void);
      ~DistributionList(void);

      enum ListMode
      {
         LMPublic = 0,
         LMMembership = 1,
         LMAnnouncement = 2,
         LMDomainMembers = 3,
      };

      String GetName() {return address_; }

      __int64 GetDomainID() const {return domain_id_;}
      void SetDomainID(__int64 newVal) {domain_id_ = newVal;}

      String GetAddress() const {return address_;}
      void SetAddress(const String & sNewVal) {address_ = sNewVal;}

      bool GetActive() const {return enabled_;}
      void SetActive(bool bNewVal) {enabled_ = bNewVal;}

      bool GetRequireAuth() const {return require_auth_;}
      void SetRequireAuth(bool bNewVal) {require_auth_ = bNewVal;}

      String GetRequireAddress() const {return require_address_;}
      void SetRequireAddress(const String & sNewVal) {require_address_ = sNewVal;}

      // The list's moderator. Empty means moderation is off, which is the previous
      // behaviour byte for byte: a sender the list's mode refuses gets a 550 at
      // RCPT TO. When set, such a posting is accepted and forwarded to this address
      // instead of being distributed, and the moderator approves it by resending it
      // to the list from an AUTHENTICATED session - an authenticated sender whose
      // address is the moderator's may always post. The authentication requirement
      // is the security boundary: MAIL FROM is free text, so without it anyone who
      // could spell the moderator's address could approve their own posting.
      String GetModeratorAddress() const {return moderator_address_;}
      void SetModeratorAddress(const String & sNewVal) {moderator_address_ = sNewVal;}

      // The envelope sender (MAIL FROM / Return-Path) given to every copy this
      // list sends: distributed copies to members, and moderation forwards to the
      // moderator. Empty means the previous behaviour - copies keep the original
      // poster's envelope sender, so a dead subscriber's bounces hammer whoever
      // happened to post last. Set it to the list owner's mailbox and the bounces
      // go to the one person who can act on them.
      String GetBounceAddress() const {return bounce_address_;}
      void SetBounceAddress(const String & sNewVal) {bounce_address_ = sNewVal;}

      ListMode GetListMode() const {return list_mode_; }
      void SetListMode(ListMode m) {list_mode_ = m; }

      // True when only the single authorized address is allowed to post. RFC 2369
      // requires "List-Post: NO" for such a list rather than the list address,
      // since telling a subscriber to reply to the list would be telling them to
      // do something the server will refuse.
      bool IsAnnouncementOnly() const {return list_mode_ == LMAnnouncement; }

      // The RFC 2919 List-Id value, angle brackets included, derived from the list
      // address: list@example.com becomes <list.example.com>. Returns an empty
      // string when the list address has no local part or no domain, in which case
      // no valid identifier can be formed.
      String GetRfc2919ListId() const;

      bool XMLStore(XNode *pParentNode, int iOptions);
      bool XMLLoad(XNode *pParentNode, int iRestoreOptions);
      bool XMLLoadSubItems(XNode *pParentNode, int iRestoreOptions);

      std::shared_ptr<DistributionListRecipients> GetMembers() const;

      size_t GetEstimatedCachingSize();

   protected:

      String address_;
      __int64 domain_id_;
      bool enabled_;

      bool require_auth_;
      String require_address_;

      String moderator_address_;
      String bounce_address_;

      ListMode list_mode_;
   };
}
