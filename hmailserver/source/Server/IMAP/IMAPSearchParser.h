// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{

   class IMAPCommandArgument;
   class IMAPSimpleCommandParser;
   class IMAPSortParser;

   class IMAPSearchCriteria
   {
   public:

      enum CriteriaType
      {
         CTUnknown = 0,
         CTHeader = 1,
         CTUID = 4,
         CTDeleted = 5,
         CTRecent = 8,
         CTUndeleted = 10,
         CTSeen = 11,
         CTUnseen = 12,
         CTText = 13,
         CTAll = 14,
         CTBody = 15,
         CTSentOn = 16,
         CTSentBefore = 17,
         CTSentSince = 18,
         CTSubject = 19,
         CTFrom = 20,
         CTTo = 21,
         CTCC = 22,
         CTSince = 23,
         
            
         CTAnswered = 24,
         CTBefore = 25,
         CTDraft = 26,
         CTFlagged = 27,
         CTLarger = 28,
         CTNew = 29,
         CTOld = 30,
         CTSmaller = 31,
         CTUnanswered = 32,
         CTUndraft = 33,
         CTUnflagged = 34,
         CTOn = 35,

         CTSequenceSet = 36,

         // RFC 7162 (CONDSTORE): MODSEQ search key (matches messages whose mod-sequence
         // is greater than or equal to the supplied value).
         CTModSeq = 37,

         CTCharset = 40,

         // RFC 3501 base search keys. No keyword can ever be stored - messageflags is a
         // fixed 8-bit bitmask with no room for client-defined flags and SELECT
         // advertises no \* in PERMANENTFLAGS - so KEYWORD matches nothing and
         // UNKEYWORD matches everything. That is exact rather than approximate, and it
         // is a great deal better than what these used to do, which was to fall through
         // as unrecognised words and leave the search matching every message.
         CTKeyword = 41,
         CTUnkeyword = 42,

         // RFC 5032 (WITHIN): relative-age keys measured against the internal date.
         CTOlder = 43,
         CTYounger = 44,

         CTSubCriteria = 100
      };

      IMAPSearchCriteria() : 
         uidlower_(-1),
         uidupper_(-1),
         positive_(true),
         is_or_(false),
         type_(CTUnknown) {};

      long GetUIDLower() {return uidlower_; }
      void SetUIDLower(long lNewVal) {uidlower_ = lNewVal; }

      long GetUIDUpper() {return uidupper_; }
      void SetUIDUpper(long lNewVal) {uidupper_ = lNewVal; }

      String GetText() {return text_; }
      void SetText(const String &sText) {text_ = sText; }

      bool GetPositive() {return positive_; }
      void SetPositive(bool bNewVal) {positive_ = bNewVal; }

      bool GetIsOR() {return is_or_; }
      void SetIsOR(bool bNewVal) {is_or_ = bNewVal; }

      void SetHeaderField(const String &sField) {header_field_ = sField;}
      String GetHeaderField() {return header_field_;}

      CriteriaType GetType() {return type_;}
      void SetType(CriteriaType newVal) {type_ = newVal; }

      static CriteriaType GetCriteriaTypeByName(const String &sName);

      std::vector<std::shared_ptr<IMAPSearchCriteria> > &GetSubCriterias() {return sub_criterias_;}

      void SetSequenceSet(std::vector<String> newVal) {sequence_set_ = newVal;}
      std::vector<String> &GetSequenceSet() {return sequence_set_;}

   private:

      static bool IsSequenceSet_(const String &item);

      long uidlower_;
      long uidupper_;
      String text_;
      bool positive_;
      CriteriaType type_;

      String header_field_;
      std::vector<std::shared_ptr<IMAPSearchCriteria> > sub_criterias_;
      std::vector<String> sequence_set_;

      bool is_or_;
   };


   // Which command's grammar the criteria are wrapped in. An enum rather than the
   // bool it grew from, because THREAD made three of them and ParseCommand(pArg,
   // true) says nothing about which prefix "true" buys: SORT's prefix is a
   // parenthesised sort-key list then a charset, THREAD's is a bare algorithm atom
   // then a charset, and SEARCH has no prefix at all.
   enum IMAPSearchCommandMode
   {
      IMAPSearchModeSearch = 0,
      IMAPSearchModeSort = 1,
      IMAPSearchModeThread = 2
   };

   class IMAPSearchParser
   {
   public:
	   IMAPSearchParser();
	   virtual ~IMAPSearchParser();

      IMAPResult ParseCommand(std::shared_ptr<IMAPCommandArgument> pArgument, IMAPSearchCommandMode mode);

      // RFC 5182 (SEARCHRES): the UIDs saved by an earlier SEARCH RETURN (SAVE), so the
      // "$" search key can be resolved during parsing. Must be set before ParseCommand.
      void SetSavedSearchResult(const std::vector<__int64> &uids) { saved_search_result_ = uids; }

      std::shared_ptr<IMAPSearchCriteria>  GetCriteria() {return result_criteria_;}
      std::shared_ptr<IMAPSortParser> GetSortParser() {return sort_parser_; }

      // The algorithm atom a THREAD command led with, exactly as the client sent
      // it. Stored rather than validated here: the parser's job is the grammar,
      // and naming an algorithm this server does not implement is the command's
      // BAD to give, with IMAPThread::ParseAlgorithm as the authority.
      String GetThreadAlgorithm() const { return thread_algorithm_; }

      String GetCharsetName() 
      {
         return charset_name_; 
      }

   private:

      bool IsValidCharset_(const String &charsetName);
      static IMAPResult BadCharsetResult_();
      bool NeedsDecoding_(IMAPSearchCriteria::CriteriaType criteriaType);
      String DecodeWordAccordingToCharset_(const String &inputValue);

      static bool IsNonZeroNumber_(const String &value);

      IMAPResult ParseSegment_(std::shared_ptr<IMAPSimpleCommandParser> pSimpleParser, int &currentWord, std::shared_ptr<IMAPSearchCriteria> pCriteria, int iRecursion);

      IMAPResult ParseWord_(std::shared_ptr<IMAPSimpleCommandParser> pSimpleParser, std::shared_ptr<IMAPSearchCriteria> pNewCriteria, int &iCurrentWord);

      std::shared_ptr<IMAPSortParser> sort_parser_;
      String thread_algorithm_;
      std::shared_ptr<IMAPSearchCriteria> result_criteria_;

      String charset_name_;
      std::vector<__int64> saved_search_result_;
   };

}