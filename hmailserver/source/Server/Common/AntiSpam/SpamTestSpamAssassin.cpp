// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include "SpamTestSpamAssassin.h"

#include "SpamTestData.h"
#include "SpamTestResult.h"

#include "AntiSpamConfiguration.h"

#include "SpamAssassin/SpamAssassinClient.h"

#include "../TCPIP/IOService.h"
#include "../TCPIP/TCPConnection.h"
#include "../TCPIP/DNSResolver.h"

#include "../BO/MessageData.h"
#include "../BO/Message.h"
#include "../Util/event.h"
#include "../Util/TraceHeaderWriter.h"
#include "../Persistence/PersistentMessage.h"


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   String 
   SpamTestSpamAssassin::GetName() const
   {
      return "SpamTestSpamAssassin";
   }

   bool 
   SpamTestSpamAssassin::GetIsEnabled()
   {
      AntiSpamConfiguration &config = Configuration::Instance()->GetAntiSpamConfiguration();
      if (config.GetSpamAssassinEnabled())
         return true;
      else
         return false;
   }

   std::set<std::shared_ptr<SpamTestResult> >
   SpamTestSpamAssassin::RunTest(std::shared_ptr<SpamTestData> pTestData)
   {
      std::set<std::shared_ptr<SpamTestResult> > setSpamTestResults;
      
      AntiSpamConfiguration& config = Configuration::Instance()->GetAntiSpamConfiguration();

      std::shared_ptr<Message> pMessage = pTestData->GetMessageData()->GetMessage();
      const String sFilename = PersistentMessage::GetFileName(pMessage);

      // SMTP servers making final delivery MAY/SHOULD remove Return-path header fields before adding their own. See: rfc2821 and rfc5321
      while (!pTestData->GetMessageData()->GetReturnPath().IsEmpty())
      {
         pTestData->GetMessageData()->DeleteField("Return-Path");
      }

      // Add Return-Path as topmost header to help SpamAssassin with its SPF checks.
      // SpamAssassin default rules and custom rules also rely on Return-Path header being present
      // We delete this header again after SpamAssassin checking has completed
      std::vector<std::pair<AnsiString, AnsiString>> fieldsToWrite;
      String sEnvelopeFrom = pTestData->GetEnvelopeFrom();
      AnsiString sReturnPath = sEnvelopeFrom.IsEmpty() ? "<>" : "<" + sEnvelopeFrom + ">";
      fieldsToWrite.push_back(std::make_pair("Return-Path", sReturnPath));
         
      TraceHeaderWriter writer;
      writer.Write(sFilename, pMessage, fieldsToWrite);
      
      std::shared_ptr<IOService> pIOService = Application::Instance()->GetIOService();

      // Shared with the client (not a reference to this stack frame) so the bounded
      // wait below can return while the connection is still alive.
      std::shared_ptr<bool> testCompleted = std::make_shared<bool>(false);

      std::shared_ptr<Event> disconnectEvent = std::shared_ptr<Event>(new Event());
      std::shared_ptr<SpamAssassinClient> pSAClient = std::shared_ptr<SpamAssassinClient>(new SpamAssassinClient(sFilename, pIOService->GetIOContext(), pIOService->GetClientContext(), disconnectEvent, testCompleted));
      
      String sHost = config.GetSpamAssassinHost();
      int iPort = config.GetSpamAssassinPort();

      
      DNSResolver resolver;

      std::vector<String> ip_addresses;
      resolver.GetIpAddresses(sHost, ip_addresses, true);

      String ip_address;
      if (ip_addresses.size())
      {
         ip_address = *(ip_addresses.begin());
      }
      else
      {
         String message = "The IP address for SpamAssassin could not be resolved. Aborting tests.";
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5507, "SpamTestSpamAssassin::RunTest", message);
         return setSpamTestResults;  
      }

      // Here we handle of the ownership to the TCPIP-connection layer.
      if (pSAClient->Connect(ip_address, iPort, IPAddress()))
      {
         // Keep a weak handle so a stuck connection can be torn down after the
         // wait below, without keeping it alive ourselves.
         std::weak_ptr<TCPConnection> weakClient = pSAClient;
         pSAClient.reset();

         // A hard ceiling on the whole SpamAssassin exchange. The connection's own
         // idle timeout (SAMaxTimeout) is re-armed on every byte, so a spamd that
         // trickles or accepts-then-stalls could otherwise hold this thread - the
         // one that sends the "250" for the message - indefinitely. That is the
         // relayed-mail stall in discussion #18: a slow scanner made reception look
         // complete while no reply was ever sent. Past the ceiling the message is
         // accepted without a SpamAssassin verdict, exactly as when spamd is down.
         const int ceilingSeconds = IniFileSettings::Instance()->GetSAMaxTimeout() + 30;
         disconnectEvent->WaitFor(boost::chrono::seconds(ceilingSeconds));

         if (!*testCompleted)
         {
            // The wait elapsed with the exchange unfinished. Tear the connection
            // down so it cannot linger against a trickling spamd.
            if (std::shared_ptr<TCPConnection> liveClient = weakClient.lock())
               liveClient->EnqueueDisconnect();
         }
      }

      if (!*testCompleted)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 5508, "SpamTestSpamAssassin::RunTest",
            "The SpamAssassin tests did not complete within the time limit, or the configuration (host name and port) is invalid, or SpamAssassin is not running. The message was accepted without a SpamAssassin verdict.");

         return setSpamTestResults;
      }

      // Check if the message is tagged as spam.
      std::shared_ptr<MessageData> pMessageData = pTestData->GetMessageData();

      // Only rewrite the message if it reloaded successfully. If RefreshFromMessage
      // fails (message too large for the MIME parser, or unparseable), the in-memory
      // body is empty and writing it back would truncate the message to a couple of
      // bytes - so leave the file exactly as SpamAssassin left it.
      if (pMessageData->RefreshFromMessage())
      {
         // The Return-Path header was added above to help SpamAssassin with its SPF checks.
         // We should remove it again to restore the headers to original state (except for any added by SA).
         pMessageData->DeleteField("Return-Path");
         pMessageData->Write(sFilename);
      }
      else
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5509, "SpamTestSpamAssassin::RunTest",
            "The message could not be reloaded after SpamAssassin processing; leaving it unchanged. It may be too large for the MIME parser (80 MB) or malformed.");
      }

      bool bIsSpam = false;
      AnsiString sSpamStatus = pMessageData->GetFieldValue("X-Spam-Status");
      if (sSpamStatus.Mid(0, 3).ToUpper() == "YES")
         bIsSpam = true;

      if (bIsSpam)
      {
         int iScore = 0;
         if (config.GetSpamAssassinMergeScore())
            iScore = ParseSpamAssassinScore_(sSpamStatus);
         else
            iScore = config.GetSpamAssassinScore();

         String sMessage = "Tagged as Spam by SpamAssassin";
         std::shared_ptr<SpamTestResult> pResult = std::shared_ptr<SpamTestResult>(new SpamTestResult(GetName(), SpamTestResult::Fail, iScore, sMessage));
         setSpamTestResults.insert(pResult);   
      }
      
      return setSpamTestResults;
   }

   int 
   SpamTestSpamAssassin::ParseSpamAssassinScore_(const AnsiString &sHeader)
   {
      int iStartPos = sHeader.FindNoCase("score=");
      if (iStartPos < 0)
         return 0;

      iStartPos += 6;

      int iScoreEnd = sHeader.Find(".", iStartPos);
      if (iScoreEnd < 0)
         return 0;

      int iScoreLength = iScoreEnd - iStartPos;

      if (iScoreLength <= 0)
         return 0;

      AnsiString sScore = sHeader.Mid(iStartPos, iScoreLength);

      int iRetVal = atoi(sScore);
      return iRetVal;

   }




}