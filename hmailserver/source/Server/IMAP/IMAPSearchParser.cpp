// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPSearchParser.h"
#include "IMAPSimpleCommandParser.h"
#include "IMAPCommand.h"
#include "IMAPSortParser.h"

#include "../Common/Util/RegularExpression.h"
#include "../Common/Util/Unicode.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPSearchCriteria::CriteriaType 
   IMAPSearchCriteria::GetCriteriaTypeByName(const String &sName)
   {
      String sTmp = sName;
      sTmp.MakeLower();
      
      if (sTmp.Compare(_T("")) == 0)
         return CTUnknown;
      else if (sTmp.Compare(_T("charset")) == 0)
         return CTCharset;
      else if (sTmp == _T("on"))
         return CTOn;
      else if (sTmp == _T("header"))
         return CTHeader;
      else if (sTmp == _T("text"))
         return CTText;
      else if (sTmp == _T("body"))
         return CTBody;
      else if (sTmp == _T("subject"))
         return CTSubject;
      else if (sTmp == _T("from"))
         return CTFrom;
      else if (sTmp == _T("cc"))
         return CTCC;
      else if (sTmp == _T("to"))
         return CTTo;
      else if (sTmp == _T("senton"))
         return CTSentOn;
      else if (sTmp == _T("sentbefore"))
         return CTSentBefore;
      else if (sTmp == _T("sentsince"))
         return CTSentSince;
      else if (sTmp == _T("since"))
         return CTSince;
      else if (sTmp == _T("deleted"))
         return CTDeleted;
      else if (sTmp == _T("recent"))
         return CTRecent;
      else if (sTmp == _T("seen"))
         return CTSeen;
      else if (sTmp == _T("unseen"))
         return CTUnseen;
      else if (sTmp == _T("undeleted"))
         return CTUndeleted;
      else if (sTmp == _T("uid"))
         return CTUID;
      else if (sTmp == _T("answered"))
         return CTAnswered;
      else if (sTmp == _T("before"))
         return CTBefore;
      else if (sTmp == _T("draft"))
         return CTDraft;
      else if (sTmp == _T("flagged"))
         return CTFlagged;
      else if (sTmp == _T("larger"))
         return CTLarger;
      else if (sTmp == _T("new"))
         return CTNew;
      else if (sTmp == _T("old"))
         return CTOld;
      else if (sTmp == _T("smaller"))
         return CTSmaller;
      else if (sTmp == _T("modseq"))
         return CTModSeq;
      else if (sTmp == _T("unanswered"))
         return CTUnanswered;
      else if (sTmp == _T("undraft"))
         return CTUndraft;
      else if (sTmp == _T("unflagged"))
         return CTUnflagged;
      else if (sTmp == _T("all"))
         return CTAll;
      else if (sTmp == _T("keyword"))
         return CTKeyword;
      else if (sTmp == _T("unkeyword"))
         return CTUnkeyword;
      // RFC 5032 (WITHIN).
      else if (sTmp == _T("older"))
         return CTOlder;
      else if (sTmp == _T("younger"))
         return CTYounger;
      else if (IsSequenceSet_(sTmp))
         return CTSequenceSet;

      return CTUnknown;
   }

   bool 
   IMAPSearchCriteria::IsSequenceSet_(const String &item)
   {
      const String sequenceSetRegex = "[0-9:,*]*";

      return RegularExpression::TestExactMatch(sequenceSetRegex, item);
   }



   IMAPSearchParser::IMAPSearchParser()
   {


   }

   IMAPSearchParser::~IMAPSearchParser()
   {

   }

   IMAPResult
   IMAPSearchParser::ParseCommand(std::shared_ptr<IMAPCommandArgument> pArgument, IMAPSearchCommandMode mode)
   {
      // Replace literals in the command.
      std::shared_ptr<IMAPSimpleCommandParser> pSimpleParser = std::shared_ptr<IMAPSimpleCommandParser> (new IMAPSimpleCommandParser);

      if (mode == IMAPSearchModeSort || mode == IMAPSearchModeThread)
      {
         pSimpleParser->Parse(pArgument);
         pSimpleParser->UnliteralData();

         // Checked before Word(0) is touched, not after. The parser produces NO words at
         // all when the command fails validation - which unbalanced parentheses outside a
         // quoted string do - and Word() used to index the vector directly, so "SORT ("
         // read parsed_words_[0] on an empty vector. One line on an authenticated
         // connection, and the fault it raises is swallowed by the /EHa catch(...) in
         // TCPConnection::AsyncReadCompleted, which logs it as error 5136, drops the
         // session and rethrows for a minidump. Word() is bounds-checked now as well;
         // this ordering is what makes the client's answer the right one.
         if (pSimpleParser->WordCount() < 2)
         {
            return IMAPResult(IMAPResult::ResultBad, "SearchCharacter set must be specified.");
         }

         if (mode == IMAPSearchModeSort)
         {
            std::shared_ptr<IMAPSimpleWord> pSort = pSimpleParser->Word(0);
            if (pSort->Paranthezied())
            {
               sort_parser_ = std::shared_ptr<IMAPSortParser>(new IMAPSortParser);
               sort_parser_->Parse(pSort->Value());
            }
         }
         else
         {
            // RFC 5256: THREAD leads with a bare algorithm atom, not a
            // parenthesised list - "THREAD REFERENCES UTF-8 ALL". A parenthesised
            // word here is a malformed command, and it is refused rather than
            // unwrapped because unwrapping would make this server accept syntax no
            // other server accepts, which a client author then ships against.
            std::shared_ptr<IMAPSimpleWord> pAlgorithm = pSimpleParser->Word(0);

            // Clammerized is the literal form ("{10}\r\nREFERENCES"). Refused with
            // the others not only because it is absurd for an atom, but because the
            // prefix trim below cuts the algorithm's own text off the command
            // string - a literal leaves "{10}" standing there instead, and the
            // criteria parser would then eat a brace token as a search key.
            if (pAlgorithm->Paranthezied() || pAlgorithm->Quoted() || pAlgorithm->Clammerized() ||
                pAlgorithm->Value().IsEmpty())
               return IMAPResult(IMAPResult::ResultBad, "Thread algorithm must be specified.");

            thread_algorithm_ = pAlgorithm->Value();
         }

         charset_name_ = pSimpleParser->Word(1)->Value();
         if (!IsValidCharset_(charset_name_))
            return BadCharsetResult_();

         // Trim away the SORT/THREAD prefix of the expression, since only the
         // search criteria matter below.
         String tempString = pArgument->Command();

         if (mode == IMAPSearchModeSort)
         {
            if (tempString.Find(_T(")")) > 0)
               tempString = tempString.Mid(tempString.Find(_T(")"))+2);
         }
         else
         {
            // The algorithm atom is the first word of the remaining command string
            // and cannot be quoted or a literal (both were refused above), so
            // cutting its own length off the front is exact.
            tempString.TrimLeft();

            if (tempString.Mid(0, thread_algorithm_.GetLength()).CompareNoCase(thread_algorithm_) == 0)
               tempString = tempString.Mid(thread_algorithm_.GetLength());
         }

         // The charset has already been taken into charset_name_ above, and it is not a
         // search key, so it has to come out of the string the criteria parser sees.
         // It used to be left in and quietly discarded further down as an unrecognised
         // word - which stopped being viable the moment an unrecognised word became an
         // error, because "SORT (DATE) US-ASCII ALL" would then fail on "US-ASCII".
         tempString.TrimLeft();

         if (!tempString.IsEmpty() && tempString.GetAt(0) == '"')
         {
            // Quoted charset: drop everything up to and including the closing quote.
            int closingQuote = tempString.Find(_T("\""), 1);
            if (closingQuote > 0)
               tempString = tempString.Mid(closingQuote + 1);
         }
         else if (!tempString.IsEmpty() && tempString.GetAt(0) == '{')
         {
            // Charset sent as a literal. What stands in the command is the "{n}" token,
            // while the value above came from the literal data. Both have to go, and
            // together: leaving the token behind makes the criteria parser read "UTF-8"
            // as a search key, and dropping only the token shifts every remaining literal
            // by one so the wrong data is paired with the wrong word.
            int closingBrace = tempString.Find(_T("}"));
            if (closingBrace > 0)
            {
               tempString = tempString.Mid(closingBrace + 1);

               std::vector<String> remainingLiterals = pArgument->Literals();
               if (!remainingLiterals.empty())
               {
                  remainingLiterals.erase(remainingLiterals.begin());
                  pArgument->Literals(remainingLiterals);
               }
            }
         }
         else if (!charset_name_.IsEmpty() &&
                  tempString.Mid(0, charset_name_.GetLength()).CompareNoCase(charset_name_) == 0)
         {
            tempString = tempString.Mid(charset_name_.GetLength());
         }

         tempString.TrimLeft();

         pArgument->Command(tempString);
      }

      /* 
         Remove all parenthesis outside of strings. Some IMAP
         clients sends parenthesis, some doesn't. We remove
         them here to prevent the behaviors from differing.

         It should be safe to do this, since
            Criteria1 OR (Criteria2 Criteria 3)
         means the same as
            Criteria1 OR Criteria2 Criteria 3
      */

      String resultString;
      const String inputString = pArgument->Command();
      bool insideString = false;
      for (int i = 0; i < inputString.GetLength(); i++)
      {
         wchar_t curChar = inputString.GetAt(i);

         switch (curChar)
         {
         case '"':
            insideString = !insideString;
            break;
         case '(':
         case ')':
            if (!insideString)
               continue;
         }

         resultString += curChar;
      }

      // Replace literals in the command.
      pSimpleParser = std::shared_ptr<IMAPSimpleCommandParser> (new IMAPSimpleCommandParser);
      pArgument->Command(resultString);

      pSimpleParser->Parse(pArgument);
      pSimpleParser->UnliteralData();

      std::shared_ptr<IMAPSearchCriteria> pCriteria = std::shared_ptr<IMAPSearchCriteria> (new IMAPSearchCriteria);

      int currentWord = 0;
      IMAPResult result = ParseSegment_(pSimpleParser, currentWord, pCriteria, 0);
      if (result.GetResult() != IMAPResult::ResultOK)
         return result;

      // RFC 3501: a SEARCH must carry at least one search key. Nothing at all here used
      // to be treated as "no restrictions", and DoesMessageMatch_ answers true for an
      // empty criteria list - so a command whose keys were all dropped, or a bare
      // "SEARCH", reported every message in the mailbox as a match. A client that acts
      // on a result set in bulk (Thunderbird's Search Messages window offering Delete,
      // an archiver moving "what the search returned") would then act on the whole
      // mailbox. Refusing the command is the only answer the protocol lets a client
      // detect.
      if (pCriteria->GetSubCriterias().empty())
         return IMAPResult(IMAPResult::ResultBad, "No search criteria specified.");

      result_criteria_ = pCriteria;

      return IMAPResult();
   }

   IMAPResult
   IMAPSearchParser::ParseSegment_(std::shared_ptr<IMAPSimpleCommandParser> pSimpleParser, int &currentWord, std::shared_ptr<IMAPSearchCriteria> pCriteria, int iRecursion)
   {
      iRecursion++;
      if (iRecursion > 50)
      {
         return IMAPResult(IMAPResult::ResultNo, "Search failed due to excessive search expression recursion.");
      }
      
      int originalCriteriaCount = (int) pCriteria->GetSubCriterias().size();
      for (; currentWord < (int) pSimpleParser->WordCount(); currentWord++)
      {
         std::shared_ptr<IMAPSimpleWord> pWord = pSimpleParser->Word(currentWord);
         String sCurCommand = pWord->Value().ToUpper();

         if (sCurCommand == _T("OR"))
         {
            // We have a sub argument.
            std::shared_ptr<IMAPSearchCriteria> pSubCriteria = std::shared_ptr<IMAPSearchCriteria> (new IMAPSearchCriteria());

            pSubCriteria->SetType(IMAPSearchCriteria::CTSubCriteria);
            pSubCriteria->SetIsOR(true);

            currentWord++;
            IMAPResult result = ParseSegment_(pSimpleParser, currentWord, pSubCriteria, iRecursion);
            if (result.GetResult() != IMAPResult::ResultOK)
               return result;
            
            pCriteria->GetSubCriterias().push_back(pSubCriteria);

            if (iRecursion > 1)
            {
               // This is a sub criteria. We only add two words here.
               if (originalCriteriaCount + 2 == pCriteria->GetSubCriterias().size())
               {
                  return IMAPResult();
               }
            }

            continue;
         }
 
         std::shared_ptr<IMAPSearchCriteria> pNewCriteria = std::shared_ptr<IMAPSearchCriteria> (new IMAPSearchCriteria);
         IMAPResult result = ParseWord_(pSimpleParser, pNewCriteria, currentWord );
         if (result.GetResult() != IMAPResult::ResultOK)
            return result;
         
         if (pNewCriteria->GetType() != IMAPSearchCriteria::CTUnknown)
            pCriteria->GetSubCriterias().push_back(pNewCriteria);

         if (iRecursion > 1)
         {
            // This is a sub criteria. We only add two words here.
            if (originalCriteriaCount + 2 == pCriteria->GetSubCriterias().size())
            {
               return IMAPResult();
            }
         }
      }

      return IMAPResult();
   }

   IMAPResult 
   IMAPSearchParser::ParseWord_(std::shared_ptr<IMAPSimpleCommandParser> pSimpleParser, std::shared_ptr<IMAPSearchCriteria> pNewCriteria, int &iCurrentWord)
   {
      String sCurCommand = pSimpleParser->Word(iCurrentWord)->Value();

      // Compared case-insensitively because IMAP keywords are case-insensitive. This was
      // an exact match against "NOT", so a client sending "not deleted" had the NOT
      // silently discarded as an unrecognised word and got the messages that ARE
      // deleted - the exact opposite of what it asked for.
      if (sCurCommand.CompareNoCase(_T("NOT")) == 0)
      {
         pNewCriteria->SetPositive(false);
         iCurrentWord++;

         if (iCurrentWord > (int) pSimpleParser->WordCount() - 1)
            return IMAPResult(IMAPResult::ResultBad, "Syntax error. NOT used but no search criteria specified.");

         std::shared_ptr<IMAPSimpleWord> pWord = pSimpleParser->Word(iCurrentWord);
         sCurCommand = pWord->Value().ToUpper();
      }
      else
         pNewCriteria->SetPositive(true);

      // RFC 5182 (SEARCHRES): "$" stands for the result saved by an earlier
      // SEARCH RETURN (SAVE), and RFC 5182 section 2.1 uses it as a search key
      // ("SEARCH $ SMALLER 4096") as well as in FETCH/STORE/COPY. The saved result is a
      // UID list, so it is matched as a UID set - the same set of messages whether this
      // is a SEARCH or a UID SEARCH. An empty saved result becomes the set "0", which
      // no message matches, so the search returns nothing rather than everything.
      if (sCurCommand == _T("$"))
      {
         std::vector<String> savedSet;

         for (__int64 savedUid : saved_search_result_)
         {
            String uidText;
            uidText.Format(_T("%I64d"), savedUid);
            savedSet.push_back(uidText);
         }

         if (savedSet.empty())
            savedSet.push_back(_T("0"));

         pNewCriteria->SetSequenceSet(savedSet);
         pNewCriteria->SetType(IMAPSearchCriteria::CTUID);

         return IMAPResult();
      }

      IMAPSearchCriteria::CriteriaType ct = IMAPSearchCriteria::GetCriteriaTypeByName(sCurCommand);

      if (ct == IMAPSearchCriteria::CTHeader)
      {
         // The client want's to search for a 
         // header field.
         iCurrentWord++;

         if (iCurrentWord > (int) pSimpleParser->WordCount() - 1)
            return IMAPResult(IMAPResult::ResultBad, "Syntax error. No header specified in header-search.");

         String sHeaderField = pSimpleParser->Word(iCurrentWord)->Value();            

         // Go to the header value.
         iCurrentWord++;

         if (iCurrentWord > (int) pSimpleParser->WordCount() - 1)
            return IMAPResult(IMAPResult::ResultBad, "Syntax error. No value specified in header-search.");

         String sHeaderValue = pSimpleParser->Word(iCurrentWord)->Value();

         sHeaderValue = DecodeWordAccordingToCharset_(sHeaderValue);

         pNewCriteria->SetHeaderField(sHeaderField);
         pNewCriteria->SetText(sHeaderValue);
         pNewCriteria->SetType(IMAPSearchCriteria::CTHeader);         

      }
      else if (ct == IMAPSearchCriteria::CTText ||
         ct == IMAPSearchCriteria::CTSentOn ||
         ct == IMAPSearchCriteria::CTSentBefore ||
         ct == IMAPSearchCriteria::CTSentSince ||
         ct == IMAPSearchCriteria::CTBody ||
         ct == IMAPSearchCriteria::CTSubject ||
         ct == IMAPSearchCriteria::CTTo ||
         ct == IMAPSearchCriteria::CTFrom ||
         ct == IMAPSearchCriteria::CTCC ||
         ct == IMAPSearchCriteria::CTSince || 
         ct == IMAPSearchCriteria::CTBefore ||
         ct == IMAPSearchCriteria::CTLarger ||
         ct == IMAPSearchCriteria::CTSmaller ||
         ct == IMAPSearchCriteria::CTModSeq ||
         ct == IMAPSearchCriteria::CTOn)
      {
         iCurrentWord++;

         if (iCurrentWord > (int) pSimpleParser->WordCount() - 1)
            return IMAPResult(IMAPResult::ResultBad, "Syntax error. Missing value.");

         std::shared_ptr<IMAPSimpleWord> pWord = pSimpleParser->Word(iCurrentWord);

         if (pWord)
         {
            String searchValue = pWord->Value();

            if (NeedsDecoding_(ct))
               searchValue = DecodeWordAccordingToCharset_(searchValue);

            pNewCriteria->SetText(searchValue);
            pNewCriteria->SetType(ct);
         }

      }
      else if (ct == IMAPSearchCriteria::CTOlder ||
         ct == IMAPSearchCriteria::CTYounger)
      {
         // RFC 5032 (WITHIN): the interval is an nz-number of seconds. Validated here
         // rather than left to the matcher, because a non-numeric or zero interval would
         // otherwise silently become "0 seconds ago" and match either everything or
         // nothing depending on which key it was.
         iCurrentWord++;

         if (iCurrentWord > (int) pSimpleParser->WordCount() - 1)
            return IMAPResult(IMAPResult::ResultBad, "Syntax error. Missing interval.");

         String interval = pSimpleParser->Word(iCurrentWord)->Value();

         if (!IsNonZeroNumber_(interval))
            return IMAPResult(IMAPResult::ResultBad, "Syntax error. The OLDER and YOUNGER intervals must be a positive number of seconds.");

         pNewCriteria->SetText(interval);
         pNewCriteria->SetType(ct);
      }
      else if (ct == IMAPSearchCriteria::CTKeyword ||
         ct == IMAPSearchCriteria::CTUnkeyword)
      {
         // The keyword name is consumed but never looked at: no keyword can be stored,
         // so KEYWORD matches nothing and UNKEYWORD matches everything whatever the name
         // is. It still has to be consumed, or it would be read as a search key of its
         // own on the next pass.
         iCurrentWord++;

         if (iCurrentWord > (int) pSimpleParser->WordCount() - 1)
            return IMAPResult(IMAPResult::ResultBad, "Syntax error. Missing keyword name.");

         pNewCriteria->SetText(pSimpleParser->Word(iCurrentWord)->Value());
         pNewCriteria->SetType(ct);
      }
      else if (ct == IMAPSearchCriteria::CTUID)
      {
         // Check which UID's we should send back.
         //

         iCurrentWord++;

         if (iCurrentWord > (int) pSimpleParser->WordCount() - 1)
            return IMAPResult(IMAPResult::ResultBad, "Syntax error. UID parameters missing.");

         std::shared_ptr<IMAPSimpleWord> pWord = pSimpleParser->Word(iCurrentWord);
         if (pWord)
         {
            String sTemp = pWord->Value();
            std::vector<String> vecSplit = StringParser::SplitString(sTemp, ",");
            pNewCriteria->SetSequenceSet(vecSplit);
            pNewCriteria->SetType(ct);

         }            
      }
      else if (ct == IMAPSearchCriteria::CTDeleted ||
         ct == IMAPSearchCriteria::CTUndeleted ||
         ct == IMAPSearchCriteria::CTSeen ||
         ct == IMAPSearchCriteria::CTUnseen ||
         ct == IMAPSearchCriteria::CTRecent ||
         ct == IMAPSearchCriteria::CTAnswered ||
         ct == IMAPSearchCriteria::CTDraft ||
         ct == IMAPSearchCriteria::CTFlagged ||
         ct == IMAPSearchCriteria::CTNew ||
         ct == IMAPSearchCriteria::CTOld ||
         ct == IMAPSearchCriteria::CTUnanswered ||
         ct == IMAPSearchCriteria::CTUndraft ||
         ct == IMAPSearchCriteria::CTUnflagged ||
         ct == IMAPSearchCriteria::CTAll)
      {
         pNewCriteria->SetType(ct);
      }
      else if (ct == IMAPSearchCriteria::CTSequenceSet)
      {
         pNewCriteria->SetType(ct);
         pNewCriteria->SetSequenceSet(StringParser::SplitString(sCurCommand, ","));
      }
      else if (ct == IMAPSearchCriteria::CTCharset)
      {
         iCurrentWord++;

         if (iCurrentWord > (int) pSimpleParser->WordCount() - 1)
            return IMAPResult(IMAPResult::ResultBad, "Syntax error. Missing charset name.");

         std::shared_ptr<IMAPSimpleWord> pWord = pSimpleParser->Word(iCurrentWord);

         if (pWord)
         {
            String charsetName = pWord->Value();

            if (!IsValidCharset_(charsetName))
               return BadCharsetResult_();

            charset_name_ = charsetName;
         }
      }
      else if (!sCurCommand.IsEmpty())
      {
         // An unrecognised search key used to be dropped on the floor here, which is the
         // worst of the three possible answers. The key the client relied on to narrow
         // the search was simply not applied, and a command whose keys were ALL
         // unrecognised - "SEARCH KEYWORD $Label1" before KEYWORD existed, or anything
         // from an extension this server does not implement - left an empty criteria list
         // that matches every message in the mailbox. RFC 3501 requires BAD for a key the
         // server does not know, and BAD is also the only answer a client can detect: a
         // "* SEARCH" line is indistinguishable from a correct one.
         //
         // The IsEmpty() guard is deliberate. The word splitter emits an empty word for
         // each run of two or more spaces, and clients do send those ("SEARCH FLAGGED  "),
         // so an empty word is whitespace rather than a key and stays ignored.
         return IMAPResult(IMAPResult::ResultBad,
            Formatter::Format("Unrecognized search key: {0}", sCurCommand));
      }

      return IMAPResult();
   }

   bool
   IMAPSearchParser::IsNonZeroNumber_(const String &value)
   {
      if (value.IsEmpty())
         return false;

      for (int i = 0; i < value.GetLength(); i++)
      {
         TCHAR ch = value.GetAt(i);
         if (ch < _T('0') || ch > _T('9'))
            return false;
      }

      return _ttoi64(value) > 0;
   }

   IMAPResult
   IMAPSearchParser::BadCharsetResult_()
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // RFC 3501 section 7.1: the BADCHARSET response code may name the charsets the server
   // does support, and every response code is followed by human-readable text.
   //
   // This used to be the bare "[BADCHARSET]" with nothing after it, which is not a
   // well-formed resp-text - text is not optional in the grammar - and tells the client
   // nothing it can act on. The list is the part that helps: a client whose first choice
   // is refused can reissue the same search in a charset that will work, instead of
   // reporting the search as broken.
   //---------------------------------------------------------------------------()
   {
      return IMAPResult(IMAPResult::ResultNo,
         "[BADCHARSET (US-ASCII UTF-8 ISO-8859-1)] The specified charset is not supported.");
   }

   bool
   IMAPSearchParser::IsValidCharset_(const String &charsetName)
   {
      if (charsetName.CompareNoCase(_T("UTF-8")) == 0 ||
          charsetName.CompareNoCase(_T("US-ASCII")) == 0 ||
          charsetName.CompareNoCase(_T("ISO-8859-1")) == 0)
          return true;
      else
          return false;
   }

   bool 
   IMAPSearchParser::NeedsDecoding_(IMAPSearchCriteria::CriteriaType criteriaType)
   {
      switch (criteriaType)
      {
      case IMAPSearchCriteria::CTHeader:
      case IMAPSearchCriteria::CTText:
      case IMAPSearchCriteria::CTBody:
      case IMAPSearchCriteria::CTSubject:
      case IMAPSearchCriteria::CTFrom:
      case IMAPSearchCriteria::CTTo:
      case IMAPSearchCriteria::CTCC:
         return true;
      default:
         return false;
      }
   }

   String
   IMAPSearchParser::DecodeWordAccordingToCharset_(const String &inputValue)
   {
      if (charset_name_.CompareNoCase(_T("UTF-8")) == 0)
      {
         String resultValue;
         if (Unicode::MultiByteToWide(inputValue, resultValue))
            return resultValue;
         else
            return inputValue;
      }
      else
         return inputValue;
   }

 
}