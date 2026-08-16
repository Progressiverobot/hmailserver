// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{

   class IMAPCommandArgument;

   class IMAPSimpleWord
   {
   public:
      IMAPSimpleWord();
      virtual ~IMAPSimpleWord();

      bool Quoted();
      void Quoted(bool bNewVal) {is_quoted_ = bNewVal; }

      bool Paranthezied();
      void Paranthezied(bool bNewVal) {is_paranthezied_ = bNewVal; }

      bool Clammerized ();
      void Clammerized(bool bNewVal) {is_clammerized_ = bNewVal; }

      String Value();
      void Value(const String &sNewVal);

      String LiteralData() {return literal_data_;}
      void LiteralData(const String &sNewVal) {literal_data_ = sNewVal; }

   private:
      String word_;
      String literal_data_;

      bool is_quoted_;
      bool is_paranthezied_;
      bool is_clammerized_;

   };

   class IMAPSimpleCommandParser  
   {
   public:
	   IMAPSimpleCommandParser();
	   virtual ~IMAPSimpleCommandParser();

      void Parse(std::shared_ptr<IMAPCommandArgument> pArgument);
      size_t WordCount() {return parsed_words_.size(); }

      // Parameters are the words after the command word. This was "size() - 1" on a
      // size_t, and Parse() produces NO words at all when the command fails validation -
      // which unbalanced parentheses outside a quoted string do. Zero words therefore
      // meant a parameter count of SIZE_MAX, which passes every "ParamCount() < n" guard
      // in the command handlers instead of failing it. Two of those guards are the only
      // thing standing in front of an unchecked Word(1): "SETACL (" and
      // "GETQUOTAROOT (" each got as far as indexing an empty vector.
      size_t ParamCount() { return parsed_words_.empty() ? 0 : parsed_words_.size() - 1; }

      String GetParamValue(std::shared_ptr<IMAPCommandArgument> pArguments, int iParamIndex);

      // Bounds-checked deliberately, in the same spirit as IMAPCommandArgument::Literal.
      // This indexed the vector directly, so an out-of-range read was an access violation
      // rather than an error: the /EHa catch(...) in TCPConnection::AsyncReadCompleted
      // logs it as error 5136, drops the session and rethrows for a minidump - and an
      // entry in the ERROR log is itself enough to break the next test fixture. Around
      // thirty call sites dereference the result without checking it, so handing back an
      // empty word rather than an empty pointer is what actually helps: the command then
      // follows the "parameter missing" or "folder could not be found" path it already
      // has.
      std::shared_ptr<IMAPSimpleWord> Word(size_t iIndex)
      {
         if (iIndex >= parsed_words_.size())
            return std::shared_ptr<IMAPSimpleWord>(new IMAPSimpleWord());

         return parsed_words_[iIndex];
      }

      std::shared_ptr<IMAPSimpleWord> QuotedWord();
      std::shared_ptr<IMAPSimpleWord> ParantheziedWord();
      std::shared_ptr<IMAPSimpleWord> ClammerizedWord();

      void AddWord(std::shared_ptr<IMAPSimpleWord> pWord) {parsed_words_.push_back(pWord); }

      void RemoveWord(int iWordIdx);

      void UnliteralData();

   private:


      int FindEndOfQuotedString_(const String &sInputString, int iWordStartPos);
      std::vector<std::shared_ptr<IMAPSimpleWord> > parsed_words_;

      bool Validate_(const String &command);

   };

   class IMAPSimpleCommandParserTester
   {
   public:
      IMAPSimpleCommandParserTester();

      bool Test();
   };

}
;