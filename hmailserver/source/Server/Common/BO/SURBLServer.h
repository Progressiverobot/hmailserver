// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class SURBLServer : public BusinessObject<SURBLServer>
   {
   public:
      SURBLServer(void);
      ~SURBLServer(void);

      // All objects should have an GetName()
      String GetName() const {return dnshost_; }

      bool GetIsActive() const  {return active_; }
      void SetIsActive(bool bNewVal) {active_ = bNewVal;}
   
      int GetScore() {return score_; }
      void SetScore(int iNewVal) {score_ = iNewVal; }

      String GetRejectMessage() const  {return reject_message_; }
      void SetRejectMessage(const String &sNewVal) {reject_message_ = sNewVal;}

      String GetDNSHost() const  {return dnshost_; }
      void SetDNSHost(const String &sNewVal) {dnshost_ = sNewVal;}

      // The answers that mean listed, in the DNSBL syntax - 127.0.1.0-255 or
      // 127.0.0.2*, ranges and wildcards joined by |. Empty means any answer
      // but the codes in 127.255.255.0/24, with which the Spamhaus zones refuse
      // the query itself rather than answer it.
      String GetExpectedResult() const {return expected_result_; }
      void SetExpectedResult(const String &sNewVal) {expected_result_ = sNewVal;}

      bool XMLStore(XNode *pNode, int iOptions);
      bool XMLLoad(XNode *pNode, int iOptions);
      bool XMLLoadSubItems (XNode *pNode, int iOptions) {return true;};

   private:
      bool active_;
      
      String dnshost_;
      String reject_message_;
      String expected_result_;

      int score_;
   };
}