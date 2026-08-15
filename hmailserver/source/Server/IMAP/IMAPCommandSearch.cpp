// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "IMAPCommandSEARCH.h"
#include "IMAPConnection.h"
#include "IMAPSort.h"
#include "IMAPThread.h"
#include "IMAPConfiguration.h"
#include "IMAPListLookup.h"

#include "../Common/Application/IniFileSettings.h"
#include "../Common/BO/Account.h"
#include "../Common/BO/IMAPFolder.h"
#include "../Common/Persistence/PersistentMessage.h"
#include "../Common/BO/Message.h"
#include "../Common/BO/Messages.h"
#include "../Common/BO/MessageData.h"
#include "../Common/Mime/Mime.h"
#include "../Common/Util/Time.h"
#include "../Common/Util/VariantDateTime.h"
#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPCommandSEARCH::IMAPCommandSEARCH(IMAPSearchCommandMode mode) :
      is_sort_(mode == IMAPSearchModeSort),
      is_thread_(mode == IMAPSearchModeThread),
      is_uid_(false),
      is_esearch_(false),
      esearch_min_(false),
      esearch_max_(false),
      esearch_all_(false),
      esearch_count_(false),
      esearch_save_(false)
   {
      modseq_search_ = false;
      highest_modseq_ = 0;
      max_uid_ = 0;
      message_count_ = 0;
      search_start_tick_ = 0;
      bytes_examined_ = 0;
      max_bytes_examined_ = 0;
      search_timeout_seconds_ = 0;
   }

   IMAPCommandSEARCH::~IMAPCommandSEARCH()
   {
      
   }

   IMAPResult
   IMAPCommandSEARCH::ExecuteCommand(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPCommandArgument> pArgument)
   {
      // First statement in the command: the ceiling is measured from here, so it
      // covers parsing as well as scanning. The handler for a plain SEARCH is
      // created once per connection and reused for every SEARCH on it, so the
      // per-search state has to be cleared here rather than in the constructor.
      ResetSearchState_();

      if (is_sort_ && !Configuration::Instance()->GetIMAPConfiguration()->GetUseIMAPSort())
         return IMAPResult(IMAPResult::ResultNo, "IMAP SORT is not enabled.");

      if (!pConnection->IsAuthenticated())
         return IMAPResult(IMAPResult::ResultNo, "Authenticate first");

      if (!pConnection->GetCurrentFolder())
         return IMAPResult(IMAPResult::ResultNo, "No folder selected.");

      if (!pArgument)
         return IMAPResult(IMAPResult::ResultNo, "Internal error IMAP-SEARCH-1.");

      {
         // The IMAP Search parser should not parse
         // the beginning of the command, UID SEARCH
         // or SEARCH
         String sCommand = pArgument->Command();

         // Find() answers -1 when the command word carries no arguments at all - a bare
         // "SEARCH" - and adding one turned that into Mid(0), which handed the criteria
         // parser the word "SEARCH" as if the client had sent it as a search key. Pass an
         // empty criteria string instead, so the parser reports the criteria as missing
         // rather than complaining about a key nobody typed.
         const int iSeparatorPos = is_uid_ ? sCommand.Find(_T(" "), 4) : sCommand.Find(_T(" "));

         sCommand = (iSeparatorPos < 0) ? String() : sCommand.Mid(iSeparatorPos + 1); // 4 as in "UID "

         pArgument->Command(sCommand);
      }

      // RFC 4731 (ESEARCH): an optional "RETURN (...)" result-options clause may
      // follow the SEARCH keyword. Detect and consume it before criteria parsing.
      // (RETURN with SORT is ESORT/RFC 5267 and is intentionally not handled here,
      // and RFC 5267's THREAD variant likewise.)
      if (!is_sort_ && !is_thread_)
      {
         String sTrimmed = pArgument->Command();
         sTrimmed.TrimLeft();

         if (sTrimmed.Mid(0, 6).CompareNoCase(_T("RETURN")) == 0)
         {
            int iOpen = sTrimmed.Find(_T("("));
            int iClose = (iOpen >= 0) ? sTrimmed.Find(_T(")"), iOpen) : -1;
            if (iOpen < 0 || iClose < 0)
               return IMAPResult(IMAPResult::ResultBad, "Invalid ESEARCH RETURN options.");

            is_esearch_ = true;

            String sOptions = sTrimmed.Mid(iOpen + 1, iClose - iOpen - 1);
            sOptions.MakeUpper();
            if (sOptions.Find(_T("MIN")) >= 0)   esearch_min_ = true;
            if (sOptions.Find(_T("MAX")) >= 0)   esearch_max_ = true;
            if (sOptions.Find(_T("COUNT")) >= 0) esearch_count_ = true;
            if (sOptions.Find(_T("ALL")) >= 0)   esearch_all_ = true;
            // RFC 5182 (SEARCHRES): "SAVE" stores the result for later "$" references.
            if (sOptions.Find(_T("SAVE")) >= 0)  esearch_save_ = true;

            // RFC 4731: an empty RETURN option list is equivalent to (ALL). SAVE on its
            // own does not imply ALL: it updates "$" but adds no data to the response.
            if (!esearch_min_ && !esearch_max_ && !esearch_count_ && !esearch_all_ && !esearch_save_)
               esearch_all_ = true;

            // The search criteria follow the closing parenthesis.
            String sCriteria = sTrimmed.Mid(iClose + 1);
            sCriteria.TrimLeft();
            pArgument->Command(sCriteria);
         }
      }

      // RFC 9051 (IMAP4rev2): once the client has enabled IMAP4rev2, a SEARCH (or UID
      // SEARCH) without an explicit RETURN clause still returns its results in an
      // ESEARCH response (with the ALL result option), not the legacy "* SEARCH" line.
      if (!is_sort_ && !is_thread_ && !is_esearch_ && pConnection->GetImap4Rev2Enabled())
      {
         is_esearch_ = true;
         esearch_all_ = true;
      }

      std::shared_ptr<IMAPSearchParser> pParser = std::shared_ptr<IMAPSearchParser>(new IMAPSearchParser());

      // RFC 5182 (SEARCHRES): the parser resolves a "$" search key against this.
      pParser->SetSavedSearchResult(pConnection->GetSavedSearchResult());

      IMAPResult result = pParser->ParseCommand(pArgument,
         is_thread_ ? IMAPSearchModeThread : (is_sort_ ? IMAPSearchModeSort : IMAPSearchModeSearch));
      if (result.GetResult() != IMAPResult::ResultOK)
         return result;

      if (is_sort_ && !pParser->GetSortParser())
         return IMAPResult(IMAPResult::ResultBad, "Incorrect search commands.");

      // RFC 5256: naming an algorithm the server did not advertise is a protocol
      // error. BAD rather than a silent fallback, because a client that asked for
      // REFERENCES and silently got ORDEREDSUBJECT would render wrong conversation
      // trees with no way to know it.
      IMAPThread::Algorithm threadAlgorithm = IMAPThread::AlgorithmReferences;

      if (is_thread_ && !IMAPThread::ParseAlgorithm(pParser->GetThreadAlgorithm(), threadAlgorithm))
         return IMAPResult(IMAPResult::ResultBad, "Unsupported threading algorithm.");

      // Mails in current box
      std::shared_ptr<IMAPFolder> pCurFolder =  pConnection->GetCurrentFolder();

      if (!pCurFolder)
         return IMAPResult(IMAPResult::ResultBad, "No selected folder");

      std::vector<std::shared_ptr<Message>> messages = pCurFolder->GetMessages()->GetCopy();

      message_count_ = (int) messages.size();
      max_uid_ = 0;
      for(std::shared_ptr<Message> pMessage : messages)
      {
         // Scanned rather than read off the last message: the collection is not
         // guaranteed to be ordered by UID.
         if ((int) pMessage->GetUID() > max_uid_)
            max_uid_ = (int) pMessage->GetUID();
      }

      std::vector<String> sMatchingVec;
      // RFC 5182 (SEARCHRES): UIDs of the matched messages in ascending order, used when SAVE
      // is requested. Captured regardless of UID/sequence mode so "$" stays stable.
      std::vector<__int64> sMatchingUids;
      if (messages.size() > 0)
      {
         // Iterate through the messages and see which ones match.
         std::vector<std::pair<int, std::shared_ptr<Message> > > vecMatchingMessages;

         int index = 0;
         for(std::shared_ptr<Message> pMessage : messages)
         {
            // Checked before this message is examined rather than after it, so the
            // check only ever stops work that is still to come. A search whose last
            // message happens to push it past a ceiling has already paid for all of
            // it, and there is nothing left to protect by discarding a result that
            // is complete and correct.
            if (BoundExceeded_())
               return AbortSearch_(pConnection, index);

            const String fileName = PersistentMessage::GetFileName(pConnection->GetAccount(), pMessage);

            index++;
            if (pMessage && DoesMessageMatch_(pConnection, pParser->GetCriteria(), fileName, pMessage, index))
            {
               // Yup we got a match.
               vecMatchingMessages.push_back(make_pair(index, pMessage));

               // RFC 7162: track the highest mod-sequence so a MODSEQ search can report it.
               if (pMessage->GetModSeq() > highest_modseq_)
                  highest_modseq_ = pMessage->GetModSeq();
            }
         }

         if (is_sort_)
         {
            IMAPSort oSorter;
            oSorter.Sort(pConnection, vecMatchingMessages, pParser->GetCharsetName(), pParser->GetSortParser());
            // Sort the message vector

            // Sorting reads a header per matching message and cannot be interrupted
            // from here, so the ceiling is re-checked once it returns. Without this a
            // SORT could report a result produced long past the ceiling it was given.
            if (BoundExceeded_())
               return AbortSearch_(pConnection, message_count_);
         }

         if (is_thread_)
         {
            // Threading reads a header per matching message, and the ceiling is
            // handed IN rather than checked afterwards. SORT above does the latter,
            // and the difference matters: a post-hoc check can only disown a result
            // the connection thread has already spent minutes producing, whereas
            // this stops before the next file is opened. On a folder large enough
            // for the timeout to bite, that is the difference between a bounded
            // command and a bounded-looking one.
            IMAPThread oThreader;

            const bool completed = oThreader.BuildThreads(pConnection, vecMatchingMessages, is_uid_,
                                                          threadAlgorithm,
                                                          [this]() { return BoundExceeded_(); },
                                                          thread_response_body_);

            if (!completed || BoundExceeded_())
               return AbortSearch_(pConnection, message_count_);
         }

         typedef std::pair<int, std::shared_ptr<Message> > MessagePair;
         for(MessagePair messagePair : vecMatchingMessages)
         {
            int index = messagePair.first;
            std::shared_ptr<Message> pMessage = messagePair.second;

            String sID;
            if (is_uid_)
               sID.Format(_T("%u"), pMessage->GetUID());
            else
               sID.Format(_T("%d"), index);

            sMatchingVec.push_back(sID);
            sMatchingUids.push_back((__int64) pMessage->GetUID());
         }

      }

      // Send response
      String sMatching;
      if (sMatchingVec.size() > 0)
      {
         // If we don't find any matches, we shouldn't return a whitespace
         // after SEARCH/SORT below. That's why we add the white space here.
         sMatching = " " + StringParser::JoinVector(sMatchingVec, " ") ;
      }
      
      String sResponse;
      if (is_esearch_)
      {
         // RFC 4731 ESEARCH response. Messages are accumulated in ascending
         // sequence/UID order, so the first match is the minimum and the last
         // is the maximum.
         sResponse = "* ESEARCH (TAG \"" + pArgument->Tag() + "\")";

         if (is_uid_)
            sResponse += " UID";

         if (!sMatchingVec.empty())
         {
            if (esearch_min_)
               sResponse += " MIN " + sMatchingVec.front();

            if (esearch_max_)
               sResponse += " MAX " + sMatchingVec.back();

            if (esearch_all_)
               sResponse += " ALL " + StringParser::JoinVector(sMatchingVec, ",");
         }

         if (esearch_count_)
         {
            String sCount;
            sCount.Format(_T(" COUNT %u"), (unsigned int) sMatchingVec.size());
            sResponse += sCount;
         }

         // RFC 7162: report the highest mod-sequence of the matched messages.
         if (modseq_search_ && !sMatchingVec.empty())
         {
            String sModSeq;
            sModSeq.Format(_T(" MODSEQ %I64d"), highest_modseq_);
            sResponse += sModSeq;
         }

         sResponse += "\r\n";
      }
      else if (is_sort_)
         sResponse = "* SORT" + sMatching + "\r\n";
      else if (is_thread_)
      {
         // The body carries its own leading space when there is one - an empty
         // result is a bare "* THREAD", not "* THREAD ".
         sResponse = "* THREAD" + thread_response_body_ + "\r\n";
      }
      else
      {
         sResponse = "* SEARCH" + sMatching;

         // RFC 7162: a SEARCH using the MODSEQ key appends "(MODSEQ <highest>)".
         if (modseq_search_ && !sMatchingVec.empty())
         {
            String sModSeq;
            sModSeq.Format(_T(" (MODSEQ %I64d)"), highest_modseq_);
            sResponse += sModSeq;
         }

         sResponse += "\r\n";
      }

      // RFC 5182 (SEARCHRES): store the result referenced later by "$". When SAVE is combined
      // only with MIN and/or MAX (and not ALL/COUNT), just those extremes are saved; otherwise
      // the complete set of matched messages is saved.
      if (esearch_save_)
      {
         std::vector<__int64> savedUids;
         if ((esearch_min_ || esearch_max_) && !esearch_all_ && !esearch_count_)
         {
            if (esearch_min_ && !sMatchingUids.empty())
               savedUids.push_back(sMatchingUids.front());
            if (esearch_max_ && !sMatchingUids.empty())
            {
               __int64 maxUid = sMatchingUids.back();
               if (savedUids.empty() || savedUids.back() != maxUid)
                  savedUids.push_back(maxUid);
            }
         }
         else
         {
            savedUids = sMatchingUids;
         }

         pConnection->SetSavedSearchResult(savedUids);
      }

      if (!is_uid_) 
         // if this is a UID command, IMAPCommandUID takes care of the below line.
         sResponse += pArgument->Tag() + " OK Search completed\r\n";

      pConnection->SendAsciiData(sResponse);

      return IMAPResult();
   }

   bool
   IMAPCommandSEARCH::DoesMessageMatch_(std::shared_ptr<IMAPConnection> pConnection, std::shared_ptr<IMAPSearchCriteria> pParentCriteria, const String &fileName, std::shared_ptr<Message> pMessage, int index)
   {
      message_data_.reset();
      mime_header_.reset();

      bool bIsOrCriteria = pParentCriteria->GetIsOR();

      // Loop over the criterias in the command.
      std::vector<std::shared_ptr<IMAPSearchCriteria> > &vecCriterias = pParentCriteria->GetSubCriterias();
      auto iterCriteria = vecCriterias.begin();

      bool bMessageIsMatchingCriteria = true;
      while (iterCriteria != vecCriterias.end())
      {
         bMessageIsMatchingCriteria = true;

         std::shared_ptr<IMAPSearchCriteria> pCriteria = (*iterCriteria);

         if (pCriteria->GetType() == IMAPSearchCriteria::CTSubCriteria)
         {
            if (!DoesMessageMatch_(pConnection, pCriteria, fileName, pMessage, index))
               bMessageIsMatchingCriteria = false;
         }

         switch (pCriteria->GetType())
         {
         case IMAPSearchCriteria::CTDeleted:
            {
               if (pCriteria->GetPositive() && !pMessage->GetFlagDeleted() ||
                   !pCriteria->GetPositive() && pMessage->GetFlagDeleted())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTAll:
            {
               if (!pCriteria->GetPositive())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }

         case IMAPSearchCriteria::CTUndeleted:
            {
               if (pCriteria->GetPositive() && pMessage->GetFlagDeleted() ||
                   !pCriteria->GetPositive() && !pMessage->GetFlagDeleted())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTSeen:
            {
               if (pCriteria->GetPositive() && !pMessage->GetFlagSeen() ||
                   !pCriteria->GetPositive() && pMessage->GetFlagSeen())
               {
                  bMessageIsMatchingCriteria = false;;
               }
               break;
            }
         case IMAPSearchCriteria::CTUnseen:
            {
               if (pCriteria->GetPositive() && pMessage->GetFlagSeen() ||
                   !pCriteria->GetPositive() && !pMessage->GetFlagSeen())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTRecent:
            {
               bool is_recent = IsMessageRecent_(pConnection, pMessage->GetID());

               if (pCriteria->GetPositive() && !is_recent ||
                  !pCriteria->GetPositive() && is_recent)
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTHeader:
            {
               if (!MatchesHeaderCriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;

               break;
            }
         case IMAPSearchCriteria::CTUID:
            {
               if (!MatchesUIDCriteria_(pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTSequenceSet:
            {
               if (!MatchesSequenceSetCriteria_(pMessage, pCriteria, index))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTText:
            {
               if (!MatchesTEXTCriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTBody:
            {
               if (!MatchesBODYCriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTSubject:
            {
               pCriteria->SetHeaderField("Subject");
               if (!MatchesHeaderCriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTFrom:
            {
               pCriteria->SetHeaderField("From");
               if (!MatchesHeaderCriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTTo:
            {
               pCriteria->SetHeaderField("To");
               if (!MatchesHeaderCriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTCC:
            {
               pCriteria->SetHeaderField("CC");
               if (!MatchesHeaderCriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTOn:
            {
               if (!MatchesONCriteria_(pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTSentOn:
            {
               if (!MatchesSENTONCriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTSentBefore:
            {
               if (!MatchesSENTBEFORECriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTSentSince:
            {
               if (!MatchesSENTSINCECriteria_(fileName, pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTSince:
            {
               if (!MatchesSINCECriteria_(pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTAnswered:
            {
               if (pCriteria->GetPositive() && !pMessage->GetFlagAnswered() ||
                  !pCriteria->GetPositive() && pMessage->GetFlagAnswered())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTDraft:
            {
               if (pCriteria->GetPositive() && !pMessage->GetFlagDraft() ||
                  !pCriteria->GetPositive() && pMessage->GetFlagDraft())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTFlagged:
            {
               if (pCriteria->GetPositive() && !pMessage->GetFlagFlagged() ||
                  !pCriteria->GetPositive() && pMessage->GetFlagFlagged())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTNew:
            {
               bool is_recent = IsMessageRecent_(pConnection, pMessage->GetID());

               bool bSet = is_recent && !pMessage->GetFlagSeen();
               if (pCriteria->GetPositive() && !bSet ||
                  !pCriteria->GetPositive() && bSet)
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTOld:
            {
               bool is_recent = IsMessageRecent_(pConnection, pMessage->GetID());
               bool bSet = !is_recent;

               if (pCriteria->GetPositive() && !bSet ||
                  !pCriteria->GetPositive() && bSet)
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTUnanswered:
            {
               if (pCriteria->GetPositive() && pMessage->GetFlagAnswered() ||
                  !pCriteria->GetPositive() && !pMessage->GetFlagAnswered())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTUndraft:
            {
               if (pCriteria->GetPositive() && pMessage->GetFlagDraft() ||
                  !pCriteria->GetPositive() && !pMessage->GetFlagDraft())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTUnflagged:
            {
               if (pCriteria->GetPositive() && pMessage->GetFlagFlagged() ||
                  !pCriteria->GetPositive() && !pMessage->GetFlagFlagged())
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }
         case IMAPSearchCriteria::CTBefore:
            {
               if (!MatchesBEFORECriteria_(pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTLarger:
            {
               if (!MatchesLARGERCriteria_(pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTSmaller:
            {
               if (!MatchesSMALLERCriteria_(pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTKeyword:
            {
               // No keyword can ever be set on a message here - messageflags is a fixed
               // 8-bit bitmask and PERMANENTFLAGS advertises no \* - so a positive
               // KEYWORD matches nothing at all, and NOT KEYWORD matches everything.
               // Before this existed, KEYWORD and its argument were both discarded as
               // unrecognised words, which left the criteria list empty and reported
               // every message in the mailbox as a match.
               if (pCriteria->GetPositive())
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTUnkeyword:
            {
               // The mirror image: no message has the keyword, so UNKEYWORD matches
               // every message and NOT UNKEYWORD matches none.
               if (!pCriteria->GetPositive())
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTOlder:
         case IMAPSearchCriteria::CTYounger:
            {
               if (!MatchesWITHINCriteria_(pMessage, pCriteria))
                  bMessageIsMatchingCriteria = false;
               break;
            }
         case IMAPSearchCriteria::CTModSeq:
            {
               // RFC 7162 (CONDSTORE): match messages whose mod-sequence is >= the value.
               modseq_search_ = true;
               __int64 wantedModSeq = _ttoi64(pCriteria->GetText());
               bool bMatches = pMessage->GetModSeq() >= wantedModSeq;
               if (pCriteria->GetPositive() && !bMatches ||
                  !pCriteria->GetPositive() && bMatches)
               {
                  bMessageIsMatchingCriteria = false;
               }
               break;
            }

         }

         if (bIsOrCriteria)
         {
            if (bMessageIsMatchingCriteria)
               return true;
         }
         else
         {
            // This isn't an OR criteria.
            if (!bMessageIsMatchingCriteria)
               return false;
         }

         iterCriteria++;
      }

      return bMessageIsMatchingCriteria;

    }

   void
   IMAPCommandSEARCH::ResetSearchState_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Clears everything that belongs to one execution of the command, and takes the
   // absolute deadline. IMAPConnection creates one IMAPCommandSEARCH per connection
   // for plain SEARCH and one for SORT, and reuses them, so without this the second
   // search on a connection would inherit the first one's budget - and its ESEARCH
   // result options.
   //---------------------------------------------------------------------------()
   {
      search_start_tick_ = GetTickCount64();
      bytes_examined_ = 0;
      bound_reason_.Empty();

      // The handler is reused per connection like everything else here, so a THREAD
      // that matched nothing must not answer with the PREVIOUS thread's tree.
      thread_response_body_.Empty();

      // Read once, here. If they were read per message a configuration reload
      // during a long search could raise the ceiling out from under it.
      search_timeout_seconds_ = IniFileSettings::Instance()->GetIMAPSearchTimeout();

      const int max_megabytes = IniFileSettings::Instance()->GetIMAPSearchMaxMegabytes();
      max_bytes_examined_ = (max_megabytes > 0) ? ((unsigned __int64) max_megabytes) * 1024 * 1024 : 0;

      // Result state from a previous search on the same connection. is_esearch_ and
      // the options below are set while parsing this command, so a leftover value
      // would otherwise decide the response format for a command that never asked
      // for it - and would make the abort path below clear "$" for a search that
      // never requested SAVE.
      is_esearch_ = false;
      esearch_min_ = false;
      esearch_max_ = false;
      esearch_all_ = false;
      esearch_count_ = false;
      esearch_save_ = false;
      modseq_search_ = false;
      highest_modseq_ = 0;
      max_uid_ = 0;
      message_count_ = 0;
   }

   bool
   IMAPCommandSEARCH::BoundExceeded_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // True once this search has passed either configured ceiling.
   //
   // Both are absolute and measured from the start of the search. Nothing here
   // re-arms, and that is the whole point: every bug of this shape this codebase
   // has had came from an idle timeout that a steady trickle of progress kept
   // alive forever, so there is deliberately no "since the last message" notion
   // anywhere in this function.
   //---------------------------------------------------------------------------()
   {
      if (max_bytes_examined_ > 0 && bytes_examined_ >= max_bytes_examined_)
      {
         bound_reason_.Format(_T("examined %I64u KB of message content (IMAPSearchMaxMegabytes limit is %I64u KB)"),
            bytes_examined_ / 1024, max_bytes_examined_ / 1024);
         return true;
      }

      if (search_timeout_seconds_ > 0)
      {
         const unsigned __int64 elapsed = GetTickCount64() - search_start_tick_;

         if (elapsed >= (unsigned __int64) search_timeout_seconds_ * 1000)
         {
            bound_reason_.Format(_T("ran for %I64u ms (IMAPSearchTimeout limit is %d seconds)"),
               elapsed, search_timeout_seconds_);
            return true;
         }
      }

      return false;
   }

   IMAPResult
   IMAPCommandSEARCH::AbortSearch_(std::shared_ptr<IMAPConnection> pConnection, int messages_scanned)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Ends an over-budget search: reports it at application level and returns NO,
   // having sent no untagged response at all.
   //
   // Returning NO is the deliberate choice, over the two alternatives:
   //
   // Returning the matches found so far as though the search were complete is
   // silently wrong and, worse, undetectably wrong. RFC 3501 has no concept of a
   // partial SEARCH result, so a truncated "* SEARCH" line is indistinguishable
   // from a complete one, and untagged data is meant to be processed whatever the
   // tagged status turns out to be. Truncation only ever omits matches, never
   // invents them - but a false negative loses mail just as effectively as a
   // false positive does when the client acts on the set in bulk. Thunderbird's
   // Search Messages window would offer Delete over a list quietly missing
   // matches; any archiver or migration tool whose rule is "keep what the search
   // returned and discard the rest" would discard messages it should have kept.
   //
   // Returning OK with an untagged "[ALERT]" is that same failure with a dialog
   // box on top. The tagged OK still says the command succeeded and the result set
   // the client has kept is still wrong, and most clients do not surface an alert
   // attached to a successful command at all - our own future webmail would have
   // to be written specifically to look for it, which is another way of saying
   // every other client gets the silent-truncation behaviour.
   //
   // NO is the only answer the protocol lets a client detect. The command failed,
   // there is no result set to act on, and the client's existing error path runs:
   // Thunderbird reports the search as failed and the user still has every
   // message; our webmail can turn it into "that search was too expensive, narrow
   // it". The response code is RFC 9051's LIMIT ("the operation ran up against an
   // implementation limit"), which older clients ignore harmlessly.
   //---------------------------------------------------------------------------()
   {
      // RFC 5182 leaves "$" undefined after a failed SEARCH. Clear it rather than
      // leave the previous search's result in place: a client that goes on to use
      // "$" then acts on nothing, which is a no-op, instead of applying a bulk
      // STORE or COPY to a stale and entirely unrelated set of messages.
      if (esearch_save_)
         pConnection->SetSavedSearchResult(std::vector<__int64>());

      String account_address;
      if (pConnection->GetAccount())
         account_address = pConnection->GetAccount()->GetAddress();

      String folder_name;
      if (pConnection->GetCurrentFolder())
         folder_name = pConnection->GetCurrentFolder()->GetName();

      // Application level, never ErrorManager: a user searching a large mailbox is
      // not a server fault, and putting it in the error log would make every such
      // search look like one.
      String sMessage;
      sMessage.Format(_T("IMAPCommandSEARCH - %s abandoned for account %s in folder %s: %s after scanning %d of %d messages (session %d). ")
                      _T("Raise IMAPSearchTimeout or IMAPSearchMaxMegabytes in hMailServer.ini if this is a legitimate search."),
         is_sort_ ? _T("SORT") : _T("SEARCH"),
         account_address.c_str(),
         folder_name.c_str(),
         bound_reason_.c_str(),
         messages_scanned,
         message_count_,
         (int) pConnection->GetSessionID());

      LOG_APPLICATION(sMessage);

      if (is_sort_)
         return IMAPResult(IMAPResult::ResultNo, "[LIMIT] Sort was too expensive to complete. Narrow the criteria or contact your administrator.");

      return IMAPResult(IMAPResult::ResultNo, "[LIMIT] Search was too expensive to complete. Narrow the criteria or contact your administrator.");
   }

   bool
   IMAPCommandSEARCH::MatchesBODYCriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   {
      if (!message_data_)
      {
         // Charged before the load, not after: the whole file is read and MIME
         // parsed whether or not it turns out to match, and the recorded message
         // size is what that costs. Charged here rather than in the caller because
         // an OR/sub-criteria search can load the same message more than once, and
         // each of those loads is real work.
         const int message_size = pMessage->GetSize();
         if (message_size > 0)
            bytes_examined_ += (unsigned __int64) message_size;

         message_data_ = std::shared_ptr<MessageData>(new MessageData());
         message_data_->LoadFromMessage(fileName, pMessage);
      }

      String sBody = message_data_->GetBody();
      String sHTMLBody = message_data_->GetHTMLBody();

      String sTextToSearchIn = sBody + sHTMLBody;
      String sTextToFind = pCriteria->GetText();

      if (sTextToSearchIn.ContainsNoCase(sTextToFind))
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesONCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   {
      String sCreationDate = pMessage->GetCreateTime();

      DateTime dt = Time::GetDateFromSystemDate(sCreationDate);
      DateTime criteriaDate = Time::GetDateFromIMAP(pCriteria->GetText()); 

      bool bMatch = false;
      if (dt.GetYear() == criteriaDate.GetYear() &&
          dt.GetMonth() == criteriaDate.GetMonth() &&
          dt.GetDay() == criteriaDate.GetDay())
      {
         bMatch = true;
      }

      if (bMatch)
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesSENTONCriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   {
      String sDateHeader = GetHeaderValue_(fileName, pMessage, "Date");
      sDateHeader = Time::GetIMAPDateFromMimeHeader(sDateHeader);

      if (sDateHeader == pCriteria->GetText())
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesSENTBEFORECriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   {
      String sDateHeader = GetHeaderValue_(fileName, pMessage, "Date");

      DateTime dtSentDate = Time::GetDateFromMimeHeader(sDateHeader);
      DateTime dtCriteria = Time::GetDateFromIMAP(pCriteria->GetText());

      if (dtSentDate <= dtCriteria)
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesSENTSINCECriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   {
      String sDateHeader = GetHeaderValue_(fileName, pMessage, "Date");

      DateTime dtSentDate = Time::GetDateFromMimeHeader(sDateHeader);
      DateTime dtCriteria = Time::GetDateFromIMAP(pCriteria->GetText());

      if (dtSentDate >= dtCriteria)
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesSINCECriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Messages whose internal date is within or later
   // than the specified date.
   //---------------------------------------------------------------------------()
   {
      DateTime dtSentDate = Time::GetDateFromSystemDate(pMessage->GetCreateTime());
      DateTime dtCriteria = Time::GetDateFromIMAP(pCriteria->GetText());

      if (dtSentDate >= dtCriteria)
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesBEFORECriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Messages whose internal date is before the specified date
   //---------------------------------------------------------------------------()
   {
      DateTime dtMessageDate = Time::GetDateFromSystemDate(pMessage->GetCreateTime());
      DateTime dtCriteria = Time::GetDateFromIMAP(pCriteria->GetText());

      if (dtMessageDate < dtCriteria)
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesWITHINCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // RFC 5032 (WITHIN): OLDER <n> matches messages whose internal date is at or before
   // (now - n seconds), YOUNGER <n> those whose internal date is after it.
   //
   // Unlike BEFORE and SINCE, which compare calendar dates only, these compare the whole
   // timestamp - that is the point of the extension and the reason "YOUNGER 3600" can be
   // answered at all. The interval was validated as a positive integer by the parser.
   //
   // Both sides are server-local: GetCreateTime is written from GetLocalTime and
   // DateTime::GetCurrentTime goes through localtime_s, so no timezone correction is
   // needed here - and adding one would be wrong.
   //---------------------------------------------------------------------------()
   {
      __int64 intervalSeconds = _ttoi64(pCriteria->GetText());

      // A century back is indistinguishable from any longer interval for stored mail,
      // and clamping keeps the day count safely inside the long that SetDateTimeSpan
      // takes however many digits the client sent.
      const __int64 maxIntervalSeconds = (__int64) 100 * 365 * 24 * 60 * 60;
      if (intervalSeconds > maxIntervalSeconds)
         intervalSeconds = maxIntervalSeconds;

      DateTimeSpan span;
      span.SetDateTimeSpan((long) (intervalSeconds / 86400), 0, 0, (int) (intervalSeconds % 86400));

      const DateTime cutoff = DateTime::GetCurrentTime() - span;
      const DateTime internalDate = Time::GetDateFromSystemDate(pMessage->GetCreateTime());

      // Written as statements rather than as an assignment from the comparison, because
      // the DateTime operators return BOOL and /WX would reject the narrowing.
      bool bMatch = false;

      if (pCriteria->GetType() == IMAPSearchCriteria::CTOlder)
      {
         if (internalDate <= cutoff)
            bMatch = true;
      }
      else
      {
         if (internalDate > cutoff)
            bMatch = true;
      }

      if (bMatch)
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesLARGERCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Messages whose size is larger than the size specified in critera.
   //---------------------------------------------------------------------------()
   {
      int iMessageSize = pMessage->GetSize();
      int iCriteriaSize = _ttoi(pCriteria->GetText());

      if (iMessageSize > iCriteriaSize)
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesSMALLERCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Messages whose size is smaller than the size specified in critera.
   //---------------------------------------------------------------------------()
{
      int iMessageSize = pMessage->GetSize();
      int iCriteriaSize = _ttoi(pCriteria->GetText());

      if (iMessageSize < iCriteriaSize)
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool
   IMAPCommandSEARCH::MatchesTEXTCriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   {
      if (!message_data_)
      {
         // See MatchesBODYCriteria_ - TEXT reads the same whole message.
         const int message_size = pMessage->GetSize();
         if (message_size > 0)
            bytes_examined_ += (unsigned __int64) message_size;

         message_data_ = std::shared_ptr<MessageData>(new MessageData());
         message_data_->LoadFromMessage(fileName, pMessage);
      }

      String sHeader = message_data_->GetHeader();
      String sBody = message_data_->GetBody();
      String sHTMLBody = message_data_->GetHTMLBody();

      String sTextToFind = pCriteria->GetText();

      if (pCriteria->GetPositive())
      {
         if (!sHeader.ContainsNoCase(sTextToFind) && 
             !sBody.ContainsNoCase(sTextToFind) &&
             !sHTMLBody.ContainsNoCase(sTextToFind))
             return false;
      }
      else
      {
         if (sHeader.ContainsNoCase(sTextToFind) ||
             sBody.ContainsNoCase(sTextToFind) ||
             sHTMLBody.ContainsNoCase(sTextToFind))
             return false;
      }

      return true;


   }

   bool
   IMAPCommandSEARCH::MatchesUIDCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   {
      std::vector<String> split = pCriteria->GetSequenceSet();

      bool found = IMAPListLookup::IsItemInList(split, (int) pMessage->GetUID(), max_uid_);
      
      if (pCriteria->GetPositive())
         return found;
      else
         return !found;
   }

   bool
   IMAPCommandSEARCH::MatchesSequenceSetCriteria_(std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria, int index)
   {
      std::vector<String> split = pCriteria->GetSequenceSet();

      bool found = IMAPListLookup::IsItemInList(split, index, message_count_);

      if (pCriteria->GetPositive())
         return found;
      else
         return !found;
   }


   bool
   IMAPCommandSEARCH::MatchesHeaderCriteria_(const String &fileName, std::shared_ptr<Message> pMessage, std::shared_ptr<IMAPSearchCriteria> pCriteria)
   {
      String sHeaderField = pCriteria->GetHeaderField();
      String sTextToFind = pCriteria->GetText();
      
      String sHeaderFieldValue = GetHeaderValue_(fileName, pMessage, sHeaderField);

      if (sHeaderFieldValue.ContainsNoCase(sTextToFind))
         return pCriteria->GetPositive();
      else
         return !pCriteria->GetPositive();
   }

   bool 
   IMAPCommandSEARCH::IsMessageRecent_(std::shared_ptr<IMAPConnection> pConnection, __int64 message_uid)
   {
      auto& recent_messages = pConnection->GetRecentMessages();

      auto recent_messages_iter = recent_messages.find(message_uid);
      return recent_messages_iter != recent_messages.end();
   }

   String
   IMAPCommandSEARCH::GetHeaderValue_(const String &fileName, std::shared_ptr<Message> pMessage, const String &sHeaderField)
   {
      if (message_data_)
      {
         return message_data_->GetFieldValue(sHeaderField);
      }
      
      if (!mime_header_)
      {
         // Load header
         AnsiString sHeader = PersistentMessage::LoadHeader(fileName);

         // A HEADER search outside the five indexed fields (date, from, subject, to,
         // cc) is a full scan of the mailbox as well, so it is charged too - but only
         // for the header it actually read, not for the whole message. That is what
         // keeps the byte ceiling from firing on a cheap header search over a large
         // mailbox while still bounding one.
         bytes_examined_ += (unsigned __int64) sHeader.GetLength();

         mime_header_ = std::shared_ptr<MimeHeader>(new MimeHeader);
         mime_header_->Load(sHeader, sHeader.GetLength(), true);
      }

      AnsiString sHeaderFieldStr = sHeaderField;
      return mime_header_->GetUnicodeFieldValue(sHeaderFieldStr);

   }

}
