// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class DatabaseSettings;
   class DateTime;
   class SQLCommand;

   class  SQLStatement  
   {
   public:

      enum eColType
      {
         ColTypeUnknown = 0,
         ColTypeValue = 1,
         ColTypeString = 2,
         ColTypeRaw = 4
      };

      enum eStatementType
      {
         STUndefined = 0,
         STInsert = 1,
         STUpdate = 2,
         STDelete = 3,
         STSelect = 4
      };


      struct Column
      {
         String sName;
         
         String sString;
         __int64 iInt;

         eColType iType;
      };

	   SQLStatement();
      SQLStatement(eStatementType iType, const String &tableName);
	   virtual ~SQLStatement();

      void AddColumn(const String & sName, const String & sValue);
      void AddColumn(const String & sName, const String & sValue, int iMaxLength);

      void AddColumn(const String & sName, long lValue);
      void AddColumnNULL(const String & sName);
      void AddColumnInt64(const String & sName, __int64 lValue);
      void AddColumnDate(const String & sName, const DateTime &dtValue);
      void AddColumn(const String & sName);
      void AddColumnCommand(const String &column, const String &command);

      void AddWhereClauseColumn(const String &sName, const String &sValue);
      void AddWhereClauseColumn(const String &sName, const AnsiString &sValue);

      void SetAdditionalSQL(const String &additionalSQL) {additional_sql_ = additionalSQL; }

      void SetTable(const String & sName) { table_ = sName; }
      void SetWhereClause(const String & sWhere) { where_ = sWhere; }
      void SetTopRows(int rows);

      void SetStatementType(eStatementType iType) { type_ = iType; }
      void SetIdentityColumn(const String &sIdentityColumn) {identity_column_ = sIdentityColumn; }

      SQLCommand GetCommand() const;
      int GetNoOfCols() const;

      String GenerateFromCommand(const SQLCommand &command);

      static String GetCurrentTimestamp();
      static String GetCurrentTimestampPlusMinutes(int iMinutes);
      static String GetCreateDatabase(std::shared_ptr<DatabaseSettings> pSettings, const String &sDatabaseName);
      static String GetLeftFunction(const String &sParamName, int iLength);
      static String GetTopRows(const String &tableName, int rows);

      static String Escape(const String &input);
      
      static String ConvertWildcardToLike(String input);
      static String ConvertLikeToWildcard(String input);

   private:

      static bool IsParameterNameCharacter_(wchar_t character);

      eStatementType type_;

      String identity_column_;
      String where_;
      String table_;
      String additional_sql_;

      int top_rows_;

      std::vector<Column> vecColumns;
      std::vector<Column> where_clause_columns_;
   };

   /*
      Tests SQLStatement::GenerateFromCommand. GenerateFromCommand is only
      *used* by MySQLConnection and PGConnection, but it is a plain
      string-to-string function that does not look at the configured backend, so
      it can be exercised whichever backend the bench is running. The one part
      that does look at the backend is Escape(), so the values used here contain
      no backslashes and the expectations hold on all four backends.

      Called from ClassTester::DoTests, i.e. reachable from the regression suite
      through Utilities.RunTestSuite. Signals failure with "throw 0" the way the
      rest of the in-process testers do, which RunTestSuite turns into a COM
      error - never into a terminate.
   */
   class SQLStatementTester
   {
   public:
      void Test();

   private:

      void TestNumberedParameterCollision_();
      void TestExistingPrefixCollisions_();
      void TestParameterNameInsideValue_();
      void TestTypeDispatch_();
      void TestUnknownTokensAreLeftAlone_();
      void TestRepeatedAndCaseSensitiveTokens_();
      void TestGeneratedNamesDoNotCollide_();

      static void AssertQuery_(const SQLCommand &command, const String &expected);
   };
}
