// Copyright (c) 2006 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once


namespace HM
{
   class Message;
   class MimeHeader;
   class IMAPFolder;

   class MessageUtilities
   {
   public:

	   MessageUtilities();
	   virtual ~MessageUtilities();

		static bool MoveToIMAPFolder(std::shared_ptr<Message> pMessage, __int64 iAccountID, const String &sFolderName, bool bAutoSubscribe, bool bSetByGlobalRule,  __int64 &iResultAccount, __int64 &iResultFolder);
      // Moves a message to an IMAP folder. The message should not be saved when this
      // function is called.
      // Returns the ID of the folder the message was moved to.

      static bool RetrieveOriginatingAddress(std::shared_ptr<Message> pMessage, String &hostName, IPAddress &address);
      // Tries to determine the IP address this email originally comes from. This function uses
      // the forwarding relay server list to determine this.

      static bool RetrieveOriginatingAddress(const std::list<String> &receivedHeaders, String &hostName, IPAddress &address);

      static String GetSendersIP(std::shared_ptr<Message> pMessage);

      static bool CopyToIMAPFolder(std::shared_ptr<Message> pMessage, int iDestinationFolderID);

      // Whether a folder named the way a rule or Sieve script names it exists and
      // can be delivered into by this account. Kept HERE, beside MoveToIMAPFolder,
      // because the two must agree: same name cleaning, same hierarchy split, and
      // for a public folder the same insert-permission gate. RFC 5490 3.1 defines
      // "exists" for the mailboxexists test as exists-and-deliverable, which is
      // why a public folder without insert permission answers false.
      static bool FolderExistsForDelivery(__int64 iAccountID, const String &sFolderName);

   private:

      static void CleanIMAPFolderRuleString(String &sIMAPFolder);
      
      static void RetrieveReceivedIPList_(const std::list<String> &headers, std::list<std::pair<String, IPAddress> > &vecAddresses);

      static std::shared_ptr<MimeHeader> GetMessageHeader_(std::shared_ptr<Message> pMessage);
      // Reads the header for the message and returns it.
   };

   class MessageUtilitiesTester
   {
   public:
      void Test();

   private:
      void TestHeloDependentOriginatingAddress_();
   };
}

