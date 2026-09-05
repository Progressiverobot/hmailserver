// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "SQLStatement.h"
#include "SQLCommand.h"

#include "DatabaseSettings.h"
#include "../Util/VariantDateTime.h"
#include "../Util/Time.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SQLStatement::SQLStatement() :
      type_(STUndefined),
      top_rows_(-1)
   {

   }

   SQLStatement::SQLStatement(eStatementType iType, const String &tableName) :
      type_(iType),
      top_rows_(-1),
      table_(tableName)
   {

   }


   SQLStatement::~SQLStatement()
   {

   }

   void 
   SQLStatement::AddColumn(const String &sName, const String &sValue)
   {
      Column p;

      p.sName = sName;
      p.iType = ColTypeString;
      p.sString = sValue;

      vecColumns.push_back(p);
   }

   void 
   SQLStatement::AddColumn(const String &sName, const String &sValue, int iMaxLength)
   {
      Column p;

      String sCopyOfValue = sValue;

      if (sCopyOfValue.GetLength() > iMaxLength)
         sCopyOfValue = sCopyOfValue.Mid(0, iMaxLength);

      p.sName = sName;
      p.iType = ColTypeString;
      p.sString = sCopyOfValue;

      vecColumns.push_back(p);
   }

   void 
   SQLStatement::AddColumnDate(const String &sName, const DateTime & dtValue)
   {
      String value = Time::GetTimeStampFromDateTime(dtValue);

      Column p;

      p.sName = sName;

      // If the date is older than 1800, don't store it. This is to solve
      // limitations in SQL Server.
      if (dtValue.GetStatus() == DateTime::invalid || dtValue.GetYear() < 1800)
      {
         p.iType = ColTypeRaw;
         p.sString = "NULL";
      }
      else
      {
         p.iType = ColTypeString;
         p.sString = value;
      }

      vecColumns.push_back(p);
   }

   void 
   SQLStatement::AddColumnNULL(const String &sName)
   {
      Column p;

      p.sName = sName;
      p.iType = ColTypeRaw;
      p.sString = "NULL";

      vecColumns.push_back(p);
   }



   void 
   SQLStatement::AddColumn(const String &sName, long lValue)
   {
      Column p;
      p.sName = sName;
      p.iType = ColTypeValue;
      p.iInt = lValue;

      vecColumns.push_back(p);
   }

   void 
   SQLStatement::AddColumnInt64(const String &sName, __int64 lValue)
   {
      Column p;
      p.sName = sName;
      p.iType = ColTypeValue;
      p.iInt = lValue;

      vecColumns.push_back(p);
   }

   void 
   SQLStatement::AddColumn(const String & sName)
   {
      Column p;
      p.sName = sName;
      p.iType = ColTypeUnknown;
   
      vecColumns.push_back(p);
   }

   void
   SQLStatement::AddColumnCommand(const String &column, const String &command)
   {
      Column p;
      p.sName = column;
      p.iType = ColTypeRaw;
      p.sString = command;

      vecColumns.push_back(p);
   }

   SQLCommand
   SQLStatement::GetCommand() const
   {
      DatabaseSettings::SQLDBType dbType = IniFileSettings::Instance()->GetDatabaseType();
      if (dbType == DatabaseSettings::TypeUnknown)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5407, "SQLStatement::GetCommand()", Formatter::Format("Unknown database type: {0}", dbType));
         SQLCommand emtpy;
         return emtpy;
      }

      String sSQL;
      SQLCommand command;

      int parameterValue = 1;
      if (type_ == SQLStatement::STInsert)
      {
         sSQL.append(_T("INSERT INTO "));
         sSQL.append(table_);
         sSQL.append(_T(" "));

         // First add columns
         sSQL.append(_T("("));
         bool first = true;
         for(Column c : vecColumns)
         {
            if (!first)
               sSQL.append(_T(", "));
            else
               first = false;

            sSQL.append(c.sName);
         }
         sSQL.append(_T(") VALUES ("));

         // Now append values
         first = true;
         for(Column c : vecColumns)
         {
            if (!first)
               sSQL += ", ";
            else
               first = false;

            parameterValue++;

            // The underscore is what keeps the name unique. Without it a column
            // called x numbered 12 and a column called x1 numbered 2 both come out
            // as @x12, and GenerateFromCommand - which can only key on the name -
            // then substitutes the first one's value for both, writing a row with
            // the wrong contents and reporting nothing. No pair of columns in the
            // schema collides today; adding one column would be enough.
            String parameterName = "@" + c.sName + "_" + StringParser::IntToString(parameterValue);

            switch (c.iType)
            {
            case ColTypeValue:
               sSQL.append(parameterName);
               command.AddParameter(parameterName, c.iInt);
               break;
            case ColTypeString:
               sSQL.append(parameterName);
               command.AddParameter(parameterName, c.sString);
               break;
            case ColTypeRaw:
               sSQL.append(Escape(c.sString));
               break;
            }
         }

         sSQL.append(_T(")"));

         if (dbType == DatabaseSettings::TypePGServer && !identity_column_.IsEmpty())
         {
            sSQL += " RETURNING " + identity_column_;
         }

      }
      else if (type_ == SQLStatement::STUpdate)
      {
         sSQL.append(_T("UPDATE "));
         sSQL.append(table_);
         sSQL.append(_T(" SET "));

         // First add columns
         bool first = true;
         for(Column c : vecColumns)
         {
            if (!first)
               sSQL.append(_T(", "));
            else
               first = false;

            parameterValue++;
            // Underscore-separated, for the reason given in the insert branch above.
            String parameterName = "@" + c.sName + "_" + StringParser::IntToString(parameterValue);

            sSQL.append(c.sName);
            sSQL.append(_T("="));

            switch (c.iType)
            {
            case ColTypeValue:
               sSQL.append(parameterName);
               command.AddParameter(parameterName, c.iInt);
               break;
            case ColTypeString:
               sSQL.append(parameterName);
               command.AddParameter(parameterName, c.sString);
               break;
            case ColTypeRaw:
               sSQL.append(Escape(c.sString));
               break;
            }
         }
      }
      else if (type_ == SQLStatement::STDelete)
      {
         sSQL = "DELETE FROM ";
         sSQL.append(table_);
      }
      else if (type_ == SQLStatement::STSelect)
      {
         sSQL = "SELECT ";

         if (top_rows_ > -1)
         {
            String value;

            switch (dbType)
            {
            case DatabaseSettings::TypeMSSQLServer:
               // SQL Server 2000 does not support ( and ) around the value.
               value.Format(_T(" TOP %d "), top_rows_);
               sSQL.append(value);
               break;
            case DatabaseSettings::TypeMSSQLCompactEdition:
               // SQL Server Compact Edition 3.5 requires () around the value.
               value.Format(_T(" TOP (%d) "), top_rows_);
               sSQL.append(value);
               break;
            }
         }

         if (vecColumns.size() == 0)
         {
            sSQL += " * ";
         }
         else
         {
            std::vector<Column>::const_iterator it = vecColumns.begin();
            std::vector<Column>::const_iterator itEnd = vecColumns.end();

            bool first = true;

            for(Column c : vecColumns)
            {
               if (!first)
                  sSQL.append(_T(", "));
               else
                  first = false;
               
               sSQL.append(c.sName);
            }
         }

         sSQL.append(_T(" FROM "));
         sSQL.append(table_);

         if (!additional_sql_.IsEmpty())
            sSQL.append(_T(" ") + additional_sql_);
      }

      if (where_clause_columns_.size() != 0)
      {
         sSQL.append(_T(" WHERE "));
         bool first = true;
         for(Column col : where_clause_columns_)
         {
            if (!first)
               sSQL.append(_T(" AND "));
            else
               first = false;

            parameterValue++;
            // Underscore-separated, for the reason given in the insert branch above.
            String parameterName = "@" + col.sName + "_" + StringParser::IntToString(parameterValue);

            if (col.iType == ColTypeString)
            {
               switch (dbType)
               {
               case DatabaseSettings::TypeMYSQLServer:
               case DatabaseSettings::TypeMSSQLServer:
               case DatabaseSettings::TypeMSSQLCompactEdition:
                  sSQL += col.sName + " = " + parameterName;
                  break;
               case DatabaseSettings::TypePGServer:
                  sSQL += "lower(" + col.sName + ") = lower(" + parameterName + ")";
                  break;
               }

               command.AddParameter(parameterName, col.sString);
            }
            else if (col.iType == ColTypeValue)
            {
               sSQL += col.sName + " = " + parameterName;
               command.AddParameter(parameterName, col.iInt);
            }
            else if (col.iType == ColTypeRaw)
            {
               sSQL += col.sName + " = " + Escape(col.sString);
            }
         }
      }
      else if (!where_.IsEmpty())
      {
         sSQL.append(_T(" WHERE "));
         sSQL.append(where_);
      }

      if (type_ == SQLStatement::STSelect)
      {
         if (top_rows_ > -1)
         {
            HM::DatabaseSettings::SQLDBType DBType = IniFileSettings::Instance()->GetDatabaseType();
            String value;
            switch (DBType)
            {
            case DatabaseSettings::TypePGServer:
               value.Format(_T(" LIMIT %d "), top_rows_);
               sSQL.append(value);
               break;
            case DatabaseSettings::TypeMYSQLServer:
               value.Format(_T(" LIMIT 0, %d "), top_rows_);
               sSQL.append(value);
               break;
            }
         }
      }
   
      command.SetQueryString(sSQL);
      return command;
   }

   void 
   SQLStatement::AddWhereClauseColumn(const String &sName, const String &sValue)
   {
      Column col;
      col.iType = ColTypeString;
      col.sName = sName;
      col.sString = sValue;

      where_clause_columns_.push_back(col);
   }

   void
   SQLStatement::SetTopRows(int rowCount)
   {
      top_rows_ = rowCount;
   }

   String 
   SQLStatement::GetCreateDatabase(std::shared_ptr<DatabaseSettings> pSettings, const String &sDatabaseName)
   {
      HM::DatabaseSettings::SQLDBType DBType = pSettings->GetType();
      String sSQL;
      switch (DBType)
      {
      case DatabaseSettings::TypeMSSQLServer:
         sSQL.Format(_T("create database %s"), sDatabaseName.c_str());
         break;
      case DatabaseSettings::TypeMYSQLServer:
         sSQL.Format(_T("create database %s character set 'utf8'"), sDatabaseName.c_str());
         break;
      case DatabaseSettings::TypePGServer:
         sSQL.Format(_T("create database \"%s\" ENCODING = 'UTF8'"), sDatabaseName.c_str());
         break;
      default:
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5407, "SQLStatement::GetCreateDatabase()", Formatter::Format("Unknown database type: {0}", DBType));
         break;
      }

      return sSQL;
   }

   int 
   SQLStatement::GetNoOfCols() const
   { 
      return (int) vecColumns.size(); 
   }

   String 
   SQLStatement::GetCurrentTimestamp()
   {
      HM::DatabaseSettings::SQLDBType DBType = IniFileSettings::Instance()->GetDatabaseType();
      
      switch (DBType)
      {
      case DatabaseSettings::TypeMSSQLServer:
      case DatabaseSettings::TypeMSSQLCompactEdition:
         return "GETDATE()";
      case DatabaseSettings::TypePGServer:
         return "current_timestamp";
      case DatabaseSettings::TypeMYSQLServer:
         return "NOW()";
      default:
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5407, "SQLStatement::GetCurrentTimestamp()", Formatter::Format("Unknown database type: {0}", DBType));   
      }

      HM_ASSERT(0);
      return "";
   }

   String 
   SQLStatement::GetLeftFunction(const String &sParamName, int iLength)
   {
      HM::DatabaseSettings::SQLDBType DBType = IniFileSettings::Instance()->GetDatabaseType();
      String sRetVal;

      switch (DBType)
      {
      case DatabaseSettings::TypeMSSQLServer:
      case DatabaseSettings::TypeMSSQLCompactEdition:
         /*
            Use SUBSTRING instead of the normal LEFT. LEFT doesn't work
            with MSSQL Compact Edition while SUBSTRING works with both.
         */
         sRetVal.Format(_T("SUBSTRING(%s, 1, %d)"), sParamName.c_str(), iLength);
         break;
      case DatabaseSettings::TypePGServer:
         sRetVal.Format(_T("SUBSTRING(%s FROM 1 FOR %d)"), sParamName.c_str(), iLength);
         break;
      case DatabaseSettings::TypeMYSQLServer:
         sRetVal.Format(_T("LEFT(%s, %d)"), sParamName.c_str(), iLength);
         break;
      default:
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5407, "SQLStatement::GetLeftFunction()", Formatter::Format("Unknown database type: {0}", DBType));
         break;
      }

      return sRetVal;
   }

   String 
   SQLStatement::GetTopRows(const String &tableName, int rows)
   {
      HM::DatabaseSettings::SQLDBType DBType = IniFileSettings::Instance()->GetDatabaseType();
      String sRetVal;

      switch (DBType)
      {
      case DatabaseSettings::TypeMSSQLServer:
      case DatabaseSettings::TypeMSSQLCompactEdition:
         /*
         Use SUBSTRING instead of the normal LEFT. LEFT doesn't work
         with MSSQL Compact Edition while SUBSTRING works with both.
         */
         sRetVal.Format(_T("SELECT TOP %d FROM %s"), rows, tableName.c_str());
         break;
      case DatabaseSettings::TypePGServer:
         sRetVal.Format(_T("SELECT * FROM %s LIMIT %d"), tableName.c_str(), rows);
         break;
      case DatabaseSettings::TypeMYSQLServer:
         sRetVal.Format(_T("SELECT * FROM %s LIMIT 0, %d"), tableName.c_str(), rows);
         break;
      default:
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5407, "SQLStatement::GetTopRows()", Formatter::Format("Unknown database type: {0}", DBType));
         break;
      }

      return sRetVal;
   }

   String 
   SQLStatement::GetCurrentTimestampPlusMinutes(int iMinutes)
   {
      HM::DatabaseSettings::SQLDBType DBType = IniFileSettings::Instance()->GetDatabaseType();

      String sRetVal;

      switch (DBType)
      {
      case DatabaseSettings::TypeMYSQLServer:
         sRetVal.Format(_T("DATE_ADD(CONCAT(CURDATE(), ' ', CURTIME()), INTERVAL %d MINUTE)"), iMinutes);
         break;
      case DatabaseSettings::TypeMSSQLServer:
      case DatabaseSettings::TypeMSSQLCompactEdition:
         sRetVal.Format(_T("DATEADD(mi, %d, GETDATE())"), iMinutes);
         break;
      case DatabaseSettings::TypePGServer:
         sRetVal.Format(_T("current_timestamp + INTERVAL '%d minutes'"), iMinutes);
         break;
      default:
         ErrorManager::Instance()->ReportError(ErrorManager::Critical, 5407, "SQLStatement::GetCurrentTimestampPlusMinutes()", Formatter::Format("Unknown database type: {0}", DBType));
         break;

      }

      return sRetVal;
   }

   /*
      Escapes a value so that it can be placed inside a single-quoted SQL string
      literal. Two callers, and they differ in reach. The statement builders above
      come here only on the backends that do not support real command parameters
      (MySQL and PostgreSQL), because the ADO-based backends bind values instead.
      SqlLogDevice comes here on all four - it inlines its values deliberately,
      for the reason written above BuildValuesTuple_ - so this function is on the
      path for every backend and not only for two.

      Doubling the quote is correct on every backend. Doubling the backslash is
      correct on MySQL in its default sql_mode, and on PostgreSQL only when
      standard_conforming_strings is off. Neither doubling can end a string
      literal early, so this is safe against injection in all four combinations;
      what it is not is byte-faithful. On a PostgreSQL server with
      standard_conforming_strings on (the default since 9.1) and on a MySQL
      server running with NO_BACKSLASH_ESCAPES, a backslash is an ordinary
      character and the value is stored with the backslash doubled. Making that
      right means asking the connection what it does rather than the ini file
      (PQparameterStatus / mysql_real_escape_string), which is a change to the
      DALConnection interface and is deliberately not done here.
   */
   String
   SQLStatement::Escape(const String &input)
   {
      String sRetVal = input;

      sRetVal.Replace(_T("'"), _T("''"));

      HM::DatabaseSettings::SQLDBType iType = IniFileSettings::Instance()->GetDatabaseType();

      if (iType == DatabaseSettings::TypeMYSQLServer || iType == DatabaseSettings::TypePGServer)
         sRetVal.Replace(_T("\\"), _T("\\\\"));

      return sRetVal;
   }

   /*
      Returns true if the character can be part of a parameter name. Names are
      either generated by GetCommand above as "@" + column name + ordinal, or
      written by hand as "@ACCOUNTID"; every name in the tree consists of ASCII
      letters, digits and underscores. '@' is deliberately not a name character,
      so that "@@IDENTITY" is not mistaken for a parameter called "@IDENTITY".
   */
   bool
   SQLStatement::IsParameterNameCharacter_(wchar_t character)
   {
      return (character >= _T('a') && character <= _T('z')) ||
             (character >= _T('A') && character <= _T('Z')) ||
             (character >= _T('0') && character <= _T('9')) ||
             character == _T('_');
   }

   /*
      Substitutes the parameter values into the query text, for the backends that
      do not support real command parameters - MySQLConnection and PGConnection,
      see DALConnection::Execute and DALRecordset::Open.

      This is a single left-to-right pass. The query is scanned for parameter
      tokens and each token found is looked up in the parameter list; the
      substituted text is appended to the result and never looked at again.

      The obvious alternative - walking the parameter list and running
      String::Replace over the whole query once per parameter - is wrong in two
      ways, and both of them yield a silently incorrect query rather than an
      error:

      1) A name which is a prefix of another name eats it. With @T1 substituted
         before @T10, "@T10" becomes "<value of T1>0". Two such pairs exist among
         the hand-written names in the tree today - @UID against @UIDFAID and
         against @UIDID - and none of the three ever occur in the same command.
         Generated names carry an underscore before the ordinal, so a command that
         numbers ten or more parameters no longer collides with itself, but a table
         holding both a column x and a column x_2 would still produce @x_2 as a
         prefix of @x_2_5.

      2) A parameter value which happens to contain the name of a parameter
         substituted later has that name replaced inside it. The replacement for
         a string parameter carries its own surrounding quotes, so that ends the
         string literal early and splices the other parameter's value into the
         statement as SQL. Escape() cannot defend against it, because the value
         is escaped before it is put at risk. hm_message_metadata is written
         with four attacker-supplied header fields in a single INSERT, so this
         was reachable by sending a mail whose From header contained the text
         "@metadata_cc9".

      Substituting the longest name first fixes (1) and not (2). Requiring a word
      boundary after the name fixes (1) and not (2). Only refusing to re-examine
      substituted text fixes both, which is why it is done this way. It costs an
      up-front lookup table for the parameters, and it costs the scanner having
      to know which characters a parameter name is made of - a name using a
      character that IsParameterNameCharacter_ does not accept would silently
      stop being substituted, so that function and SQLStatement::GetCommand have
      to agree.

      Names are matched longest-first at each token, so a query holding both
      @UID and @UIDFAID resolves each of them correctly, and an unknown token is
      left in the query exactly as before.
   */
   String
   SQLStatement::GenerateFromCommand(const SQLCommand &command)
   {
      const String queryString = command.GetQueryString();

      struct Substitution
      {
         String text;
         bool used;
      };

      // The text each parameter token expands to. Escape() and the integer
      // formatting are done once per parameter, here, rather than per
      // occurrence.
      std::map<String, Substitution> substitutions;
      int longestName = 0;

      for (const SQLParameter &parameter : command.GetParameters())
      {
         const String name = parameter.GetName();

         if (name.GetLength() < 2 || name.GetAt(0) != _T('@'))
         {
            // No caller does this today. If one ever does, the query would run
            // with the token still in it, which MySQL silently reads as a NULL
            // user variable, so say so rather than guess.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5840, "SQLStatement::GenerateFromCommand", Formatter::Format("The parameter name {0} is not on the form @name and can not be substituted.", name));
            continue;
         }

         Substitution substitution;
         substitution.used = false;

         switch (parameter.GetType())
         {
         case SQLParameter::ParamTypeInt32:
            substitution.text = StringParser::IntToString(parameter.GetInt32Value());
            break;
         case SQLParameter::ParamTypeInt64:
            substitution.text = StringParser::IntToString(parameter.GetInt64Value());
            break;
         case SQLParameter::ParamTypeUnsignedInt32:
            substitution.text = StringParser::IntToString(parameter.GetUnsignedInt32Value());
            break;
         case SQLParameter::ParamTypeString:
            substitution.text = "'" + Escape(parameter.GetStringValue()) + "'";
            break;
         default:
            // Unreachable: SQLParameter has no other type. Leaving the token in
            // the query is what the previous implementation did, so keep doing
            // that, but do not do it quietly.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5841, "SQLStatement::GenerateFromCommand", Formatter::Format("The parameter {0} is of unknown type {1} and can not be substituted.", name, parameter.GetType()));
            continue;
         }

         // If a command carries the same name twice the first one wins, which is
         // what the Replace-based code did as well.
         std::map<String, Substitution>::const_iterator existing = substitutions.find(name);

         if (existing == substitutions.end())
         {
            substitutions[name] = substitution;

            if (name.GetLength() > longestName)
               longestName = name.GetLength();
         }
         else if (existing->second.text.Compare(substitution.text) != 0)
         {
            // Two parameters share a name but not a value, so every occurrence of
            // that name gets the first value and the second is silently dropped -
            // an INSERT that writes one column's value into another. GetCommand can
            // no longer generate this, because it separates the column name from the
            // ordinal with an underscore, but a hand-written pair of names still can.
            //
            // Reported only when the values differ: the same name added twice with
            // the same value substitutes to the same text either way, and is not
            // worth an entry in the error log.
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5842, "SQLStatement::GenerateFromCommand", Formatter::Format("The parameter name {0} is used twice in one command with different values. The first value is substituted and the second is lost.", name));
         }
      }

      if (substitutions.empty())
         return queryString;

      const int queryLength = queryString.GetLength();

      String result;
      result.reserve(queryLength);

      int position = 0;

      while (position < queryLength)
      {
         const wchar_t character = queryString.GetAt(position);

         if (character != _T('@'))
         {
            result += character;
            position++;
            continue;
         }

         // Measure the run of name characters after the '@', capped at the
         // longest name we actually have to look for.
         int tokenEnd = position + 1;
         const int maximumEnd = position + longestName;

         while (tokenEnd < queryLength && tokenEnd < maximumEnd && IsParameterNameCharacter_(queryString.GetAt(tokenEnd)))
            tokenEnd++;

         // Longest match first, so that @UIDFAID is not read as @UID followed by
         // the letters FAID.
         bool substituted = false;

         for (int tokenLength = tokenEnd - position; tokenLength >= 2; tokenLength--)
         {
            std::map<String, Substitution>::iterator found = substitutions.find(queryString.Mid(position, tokenLength));

            if (found == substitutions.end())
               continue;

            result += found->second.text;
            found->second.used = true;

            position += tokenLength;
            substituted = true;
            break;
         }

         if (!substituted)
         {
            // Not a parameter of ours. Copy the '@' and carry on; the characters
            // after it are copied by the following iterations.
            result += character;
            position++;
         }
      }

      // A parameter that was never found in the query is a sign of exactly the
      // kind of name mismatch this function used to create. Debug level only -
      // it is a developer aid, not a condition an installation can provoke.
      for (std::map<String, Substitution>::const_iterator it = substitutions.begin(); it != substitutions.end(); it++)
      {
         if (!it->second.used)
         {
            LOG_DEBUG("SQLStatement::GenerateFromCommand - the parameter " + it->first + " does not occur in the query and has not been substituted.");
         }
      }

      return result;
   }

   String
   SQLStatement::ConvertWildcardToLike(String input)
   {
      input.Replace(_T("/"), _T("//"));
      input.Replace(_T("%"), _T("/%"));
      input.Replace(_T("_"), _T("/_"));
      input.Replace(_T("?"), _T("_"));
      input.Replace(_T("*"), _T("%"));
      return input;
   }

   String
   SQLStatement::ConvertLikeToWildcard(String input)
   {
      input.Replace(_T("//"), _T("/"));
      input.Replace(_T("/%"), _T("¤¤¤ESCAPED¤¤¤PERCENTAGE¤¤¤"));
      input.Replace(_T("/_"), _T("¤¤¤ESCAPED¤¤¤UNDERSCORE¤¤¤"));
      input.Replace(_T("_"), _T("?"));
      input.Replace(_T("%"), _T("*"));
      input.Replace(_T("¤¤¤ESCAPED¤¤¤PERCENTAGE¤¤¤"), _T("%"));
      input.Replace(_T("¤¤¤ESCAPED¤¤¤UNDERSCORE¤¤¤"), _T("_"));

      return input;
   }

   void
   SQLStatementTester::Test()
   {
      TestNumberedParameterCollision_();
      TestExistingPrefixCollisions_();
      TestParameterNameInsideValue_();
      TestTypeDispatch_();
      TestUnknownTokensAreLeftAlone_();
      TestRepeatedAndCaseSensitiveTokens_();
      TestGeneratedNamesDoNotCollide_();
   }

   /*
      Two different columns must not generate the same parameter name. A column
      called x1 numbered 2 and a column called x numbered 12 both used to come out
      as @x12, and since GenerateFromCommand can only key on the name, the first
      value was substituted for both: the INSERT wrote 111 into the column that
      should have held 999, and said nothing.

      Unlike the tests above this one goes through SQLStatement::GetCommand rather
      than a hand-written SQLCommand, because the names GetCommand generates are
      what is under test. Ordinals start at 2 and count columns, so x1 first and x
      eleventh is the collision.
   */
   void
   SQLStatementTester::TestGeneratedNamesDoNotCollide_()
   {
      SQLStatement statement(SQLStatement::STInsert, _T("t"));

      statement.AddColumn(_T("x1"), 111L);

      for (int i = 0; i < 9; i++)
         statement.AddColumn(Formatter::Format(_T("filler{0}"), i), 0L);

      statement.AddColumn(_T("x"), 999L);

      String actual = statement.GenerateFromCommand(statement.GetCommand());

      // 999 is the one that used to disappear. 111 is here so that a query which
      // substituted nothing at all cannot pass.
      if (actual.Find(_T("111")) >= 0 && actual.Find(_T("999")) >= 0)
         return;

      OutputDebugString(_T("hMailServer: SQLStatement generated colliding parameter names.\n"));
      OutputDebugString(_T("   Query: "));
      OutputDebugString(actual.c_str());
      OutputDebugString(_T("\n"));

      throw 0;
   }

   void
   SQLStatementTester::AssertQuery_(const SQLCommand &command, const String &expected)
   {
      SQLStatement statement;
      String actual = statement.GenerateFromCommand(command);

      if (actual.Compare(expected) == 0)
         return;

      OutputDebugString(_T("hMailServer: SQLStatement parameter substitution failed.\n"));
      OutputDebugString(_T("   Expected: "));
      OutputDebugString(expected.c_str());
      OutputDebugString(_T("\n   Actual:   "));
      OutputDebugString(actual.c_str());
      OutputDebugString(_T("\n"));

      throw 0;
   }

   /*
      A parameter name which is a prefix of another parameter name must not be
      substituted inside it. The names here are hand-written, which is where the
      case survives: GetCommand separates the ordinal with an underscore, so it no
      longer numbers its way into a collision on its own.
   */
   void
   SQLStatementTester::TestNumberedParameterCollision_()
   {
      // The minimal case. Substituting @T1 first turns "@T10" into "50".
      SQLCommand three(_T("select * from t where a = @T1 and b = @T10 and c = @T2"));
      three.AddParameter("@T1", 5);
      three.AddParameter("@T10", 7);
      three.AddParameter("@T2", 6);

      AssertQuery_(three, _T("select * from t where a = 5 and b = 7 and c = 6"));

      // Twelve numbered parameters, the shape a multi-row insert has by its
      // nature. @P1 eats @P10, @P11 and @P12; @P2 does not eat anything.
      SQLCommand twelve(_T("insert into t (c) values (@P1), (@P2), (@P3), (@P4), (@P5), (@P6), ")
                        _T("(@P7), (@P8), (@P9), (@P10), (@P11), (@P12)"));
      twelve.AddParameter("@P1", 100);
      twelve.AddParameter("@P2", 200);
      twelve.AddParameter("@P3", 300);
      twelve.AddParameter("@P4", 400);
      twelve.AddParameter("@P5", 500);
      twelve.AddParameter("@P6", 600);
      twelve.AddParameter("@P7", 700);
      twelve.AddParameter("@P8", 800);
      twelve.AddParameter("@P9", 900);
      twelve.AddParameter("@P10", 1000);
      twelve.AddParameter("@P11", 1100);
      twelve.AddParameter("@P12", 1200);

      AssertQuery_(twelve, _T("insert into t (c) values (100), (200), (300), (400), (500), (600), ")
                           _T("(700), (800), (900), (1000), (1100), (1200)"));
   }

   /*
      The two prefix collisions that exist among the parameter names in the tree
      today - @UID against @UIDFAID and against @UIDID. No command uses more than
      one of them at the moment, so this is the case that keeps it that way.
   */
   void
   SQLStatementTester::TestExistingPrefixCollisions_()
   {
      SQLCommand command(_T("select * from hm_fetchaccounts_uids where uidfaid = @UIDFAID and uidid = @UIDID and uidvalue = @UID"));
      command.AddParameter("@UID", String(_T("abc")));
      command.AddParameter("@UIDFAID", 12);
      command.AddParameter("@UIDID", 34);

      AssertQuery_(command, _T("select * from hm_fetchaccounts_uids where uidfaid = 12 and uidid = 34 and uidvalue = 'abc'"));
   }

   /*
      A parameter value which contains the name of a parameter substituted later
      must be left alone. Replacing inside an already substituted value ends its
      string literal early and splices the later value into the statement as SQL.

      The names and the column order here are the ones
      PersistentMessageMetaData::SaveObject generates, where every one of the four
      string values is a header field taken straight off an incoming message.
   */
   void
   SQLStatementTester::TestParameterNameInsideValue_()
   {
      SQLCommand command(_T("insert into hm_message_metadata (metadata_from, metadata_cc) values (@metadata_from_6, @metadata_cc_9)"));
      command.AddParameter("@metadata_from_6", String(_T("evil@metadata_cc_9")));
      command.AddParameter("@metadata_cc_9", String(_T("x, 1); delete from hm_messages; --")));

      AssertQuery_(command, _T("insert into hm_message_metadata (metadata_from, metadata_cc) values ('evil@metadata_cc_9', 'x, 1); delete from hm_messages; --')"));
   }

   /*
      Every parameter type renders as it did before. The unsigned value is above
      the signed 32 bit range on purpose: it would come out negative if it were
      formatted as an int.
   */
   void
   SQLStatementTester::TestTypeDispatch_()
   {
      SQLCommand command(_T("select @A2, @B3, @C4, @D5"));
      command.AddParameter("@A2", -17);
      command.AddParameter("@B3", (__int64) -9007199254740993);
      command.AddParameter("@C4", (unsigned int) 4000000000);
      command.AddParameter("@D5", String(_T("O'Brien")));

      AssertQuery_(command, _T("select -17, -9007199254740993, 4000000000, 'O''Brien'"));
   }

   /*
      Text that looks like a parameter but is not one of the command's parameters
      stays in the query untouched, which is what the previous implementation did.
      "@@IDENTITY" is a real example - ADOConnection and SQLCEConnection both
      issue it.
   */
   void
   SQLStatementTester::TestUnknownTokensAreLeftAlone_()
   {
      SQLCommand command(_T("select @@IDENTITY, @NOTAPARAMETER, @KNOWN2 from t"));
      command.AddParameter("@KNOWN2", 1);

      AssertQuery_(command, _T("select @@IDENTITY, @NOTAPARAMETER, 1 from t"));

      // A command with no parameters at all is returned as it stands.
      SQLCommand noParameters(_T("select @@IDENTITY as ident"));

      AssertQuery_(noParameters, _T("select @@IDENTITY as ident"));
   }

   /*
      A parameter used twice in one query is substituted at both places, and the
      match is case sensitive - both as before.
   */
   void
   SQLStatementTester::TestRepeatedAndCaseSensitiveTokens_()
   {
      SQLCommand command(_T("select * from t where a = @X2 or b = @X2 or c = @x2"));
      command.AddParameter("@X2", 9);

      AssertQuery_(command, _T("select * from t where a = 9 or b = 9 or c = @x2"));
   }
}
