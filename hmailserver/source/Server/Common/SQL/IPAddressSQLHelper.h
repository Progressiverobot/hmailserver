// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class IPAddress;

   class  IPAddressSQLHelper
   {
   public:
	   IPAddressSQLHelper();

      AnsiString GetAddress1String(const IPAddress &address) const;
      AnsiString GetAddress2String(const IPAddress &address) const;

      AnsiString GetAddress1Equals(const IPAddress &address) const;
      AnsiString GetAddress2Equals(const IPAddress &address) const;

      IPAddress Construct(std::shared_ptr<DALRecordset> recordset, const AnsiString &address1Column, const AnsiString &address2Column) const;
      void AppendStatement(SQLStatement &statement, const IPAddress &address, const AnsiString &address1Column, const AnsiString &address2Column) const;
   };
}
