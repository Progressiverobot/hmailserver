// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "SpamAssassinLearner.h"
#include "SpamAssassinClient.h"

#include "../AntiSpamConfiguration.h"
#include "../../Application/Application.h"
#include "../../Application/Configuration.h"
#include "../../Application/ErrorManager.h"
#include "../../Application/IniFileSettings.h"
#include "../../BO/Account.h"
#include "../../BO/IMAPFolder.h"
#include "../../BO/Message.h"
#include "../../Persistence/PersistentMessage.h"
#include "../../TCPIP/DNSResolver.h"
#include "../../TCPIP/IOService.h"
#include "../../Threading/WorkQueue.h"
#include "../../Util/Event.h"
#include "../../Util/Unicode.h"
#include "../../Util/Strings/Formatter.h"
#include "../../../IMAP/IMAPSpecialUse.h"
#include "../../../IMAP/IMAPFolderContainer.h"
#include <map>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   void
   SpamAssassinLearner::LearnFromMove(std::shared_ptr<IMAPFolder> source, std::shared_ptr<IMAPFolder> destination,
                                      std::shared_ptr<Message> copy, std::shared_ptr<const Account> destinationOwner)
   {
      if (!IniFileSettings::Instance()->GetSpamAssassinLearnOnMove())
         return;

      if (!Configuration::Instance()->GetAntiSpamConfiguration().GetSpamAssassinEnabled())
         return;

      if (!source || !destination || !copy || !destinationOwner)
         return;

      // A lesson is a move between a user's own folders: into their Junk folder
      // (spam) or out of it (ham). Public folders and other people's mailboxes are
      // not the user's to teach with, and Junk to Junk says nothing.
      if (destination->IsPublicFolder() || source->IsPublicFolder())
         return;

      if (source->GetAccountID() != destination->GetAccountID())
         return;

      // Which folder is the Junk folder is what LIST tells the client: the explicit
      // designation when one was stored (CREATE ... USE, or the Control Panel), else
      // the one the folder's name earns it - so a folder simply named Junk counts,
      // the way it does in every client that files spam by name.
      std::map<__int64, int> designations;
      IMAPSpecialUse::Resolve(IMAPFolderContainer::Instance()->GetFoldersForAccount(destination->GetAccountID()), designations);
      const int sourceDesignation = designations.count(source->GetID()) ? designations[source->GetID()] : 0;
      const int destinationDesignation = designations.count(destination->GetID()) ? designations[destination->GetID()] : 0;
      const bool fromJunk = (sourceDesignation & IMAPSpecialUse::DesignationJunk) != 0;
      const bool toJunk = (destinationDesignation & IMAPSpecialUse::DesignationJunk) != 0;
      if (fromJunk == toJunk)
         return;

      // The user spamd learns as: the mailbox owner when preferences follow the
      // recipient, else the fixed profile, else nobody - the same rule the scan
      // applies, so the lesson lands in the store the verdicts come from.
      String user = IniFileSettings::Instance()->GetSpamAssassinUser();
      if (IniFileSettings::Instance()->GetSpamAssassinUserFromRecipient())
         user = destinationOwner->GetAddress();

      const String messageFile = PersistentMessage::GetFileName(destinationOwner, copy);

      std::shared_ptr<WorkQueue> queue = Application::Instance()->GetAsyncWorkQueue();
      if (!queue)
         return;

      std::shared_ptr<SpamAssassinLearnTask> task =
         std::shared_ptr<SpamAssassinLearnTask>(new SpamAssassinLearnTask(messageFile, toJunk, user));
      queue->AddTask(task);
   }

   bool
   SpamAssassinLearner::Tell(const String &messageFile, bool spam, const String &user)
   {
      AntiSpamConfiguration &config = Configuration::Instance()->GetAntiSpamConfiguration();

      DNSResolver resolver;
      std::vector<String> ipAddresses;
      resolver.GetIpAddresses(config.GetSpamAssassinHost(), ipAddresses, true);
      if (ipAddresses.empty())
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5508, "SpamAssassinLearner::Tell",
            Formatter::Format("The IP address for SpamAssassin ({0}) could not be resolved; the lesson for {1} was not delivered.",
               config.GetSpamAssassinHost(), messageFile));
         return false;
      }

      std::shared_ptr<IOService> ioService = Application::Instance()->GetIOService();
      std::shared_ptr<bool> completed = std::make_shared<bool>(false);
      std::shared_ptr<Event> disconnected = std::shared_ptr<Event>(new Event());

      std::shared_ptr<SpamAssassinClient> client = std::shared_ptr<SpamAssassinClient>(
         new SpamAssassinClient(messageFile, ioService->GetIOContext(), ioService->GetClientContext(), disconnected, completed));
      client->SetUser(Unicode::ToANSI(user));
      client->SetLearning(spam);

      if (!client->Connect(*ipAddresses.begin(), config.GetSpamAssassinPort(), IPAddress()))
         return false;

      // Ownership went to the connection layer; the connection's own session
      // ceiling guarantees the event fires.
      client.reset();
      disconnected->Wait();

      return *completed;
   }

   SpamAssassinLearnTask::SpamAssassinLearnTask(const String &messageFile, bool spam, const String &user) :
      Task("SpamAssassinLearnTask"),
      message_file_(messageFile),
      spam_(spam),
      user_(user)
   {
   }

   void
   SpamAssassinLearnTask::DoWork()
   {
      const bool acknowledged = SpamAssassinLearner::Tell(message_file_, spam_, user_);

      LOG_APPLICATION(Formatter::Format("SpamAssassin was told {0} is {1}{2}: {3}.",
         message_file_, spam_ ? String("spam") : String("ham"),
         user_.IsEmpty() ? String("") : String(" for ") + user_,
         acknowledged ? String("acknowledged") : String("not acknowledged")));
   }
}
