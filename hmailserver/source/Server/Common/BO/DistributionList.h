// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

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

      ListMode list_mode_;
   };
}
