// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // One entry on the sender blacklist: either a full address
   // ("spammer@example.com") or a whole domain ("example.com", which also
   // covers every subdomain of it). The entry is matched against the SMTP
   // envelope sender (MAIL FROM), which the sender controls completely -
   // this is a nuisance filter for senders who keep using the same address,
   // not an authentication mechanism.
   class BlockedSender : public BusinessObject<BlockedSender>
   {
   public:
      BlockedSender(void);
      ~BlockedSender(void);

      String GetName() const {return address_; }

      String GetAddress() const {return address_; }
      void SetAddress(const String &sNewVal) {address_ = sNewVal;}

      int GetScore() const {return score_; }
      void SetScore(int iNewVal) {score_ = iNewVal; }

      String GetDescription() const {return description_; }
      void SetDescription(const String &sNewVal) {description_ = sNewVal;}

      // True if this entry covers the given envelope sender address.
      //
      // The rules, which are deliberately narrower than the whitelist's
      // wildcard matching: an entry containing '@' matches only the exact
      // address (case-insensitively); an entry without '@' is a domain and
      // matches that domain and any subdomain of it, anchored at a label
      // boundary so that "example.com" never matches "notexample.com" or
      // "example.com.attacker.net". A leading '@' on a domain entry is
      // tolerated ("@example.com" means "example.com"). The null sender
      // (an empty address) never matches anything: <> carries bounces, and
      // refusing those breaks RFC 5321.
      bool Matches(const String &sFromAddress) const;

      bool XMLStore(XNode *pNode, int iOptions);
      bool XMLLoad(XNode *pNode, int iOptions);
      bool XMLLoadSubItems (XNode *pNode, int iOptions) {return true;};

   private:

      String address_;
      String description_;

      int score_;
   };
}
