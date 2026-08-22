// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "property.h"
#include "../Util/Crypt.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   Property::Property(void)
   {
      long_value_ = 0;
      string_value_ = "";
      save_crypted_ = false;
   }

   Property::Property(const String & sName, long lValue, const String & sValue, bool SaveCrypted)
   {
      name_ = sName;
      long_value_ = lValue;
      string_value_ = sValue;
      
      save_crypted_ = SaveCrypted;
   }

   Property::~Property(void)
   {

   }

   void
   Property::SetLongValue(long NewVal)
   {
      long_value_ = NewVal;
      WriteLongSetting_(NewVal);
   }

   void
   Property::SetStringValue(const String &NewVal)
   {
      string_value_ = NewVal;
      WriteStringSetting_(NewVal);
   }

   void
   Property::SetBoolValue(bool NewVal)
   {
      long_value_ = NewVal ? 1 : 0;
      WriteLongSetting_(long_value_);
   }

   bool
   Property::SettingRowExists_() const
   {
      SQLCommand command("select count(*) as settingcount from hm_settings where settingname = @SETTINGNAME");
      command.AddParameter("@SETTINGNAME", name_);

      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
      if (!pRS)
         return false;

      return pRS->GetLongValue("settingcount") > 0;
   }

   bool
   Property::WriteBoolSetting_(bool bValue)
   {
      return WriteLongSetting_(bValue ? 1 : 0);
   }

   bool
   Property::WriteLongSetting_(long lValue)
   {
      // The row can be missing on a database that skipped an upgrade step (for
      // example after a partially-committed upgrade). A bare UPDATE would then
      // affect zero rows and silently drop the setting, so insert it instead.
      if (!SettingRowExists_())
      {
         SQLCommand command("insert into hm_settings (settingname, settingstring, settinginteger) values (@SETTINGNAME, '', @SETTINGINTEGER)");
         command.AddParameter("@SETTINGNAME", name_);
         command.AddParameter("@SETTINGINTEGER", lValue);

         return Application::Instance()->GetDBManager()->Execute(command);
      }

      SQLCommand command("update hm_settings set settinginteger = @SETTINGINTEGER where settingname = @SETTINGNAME");
      command.AddParameter("@SETTINGINTEGER", lValue);
      command.AddParameter("@SETTINGNAME", name_);

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool
   Property::WriteStringSetting_(const String & sValue)
   {
      String sTemp;
      if (save_crypted_)
         sTemp = Crypt::Instance()->ProtectSecret(sValue);
      else
         sTemp = sValue;

      // See WriteLongSetting_: insert the row when it is missing.
      if (!SettingRowExists_())
      {
         SQLStatement statement(SQLStatement::STInsert, "hm_settings");
         statement.AddColumn("settingname", name_);
         statement.AddColumn("settingstring", sTemp);
         statement.AddColumn("settinginteger", (long) 0);

         return Application::Instance()->GetDBManager()->Execute(statement);
      }

      SQLStatement statement(SQLStatement::STUpdate, "hm_settings");
      statement.AddColumn("settingstring", sTemp);
      statement.SetWhereClause(Formatter::Format("settingname = '{0}'", name_));

      return Application::Instance()->GetDBManager()->Execute(statement);
   }
}