// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Property
   {
   public:
      Property(void);
      ~Property(void);

      Property(const String &sName, long lValue, const String &sValue, bool SaveCrypted = false);
   

      AnsiString GetName() const {return name_;}
      void SetName(const AnsiString &NewVal) {name_ = NewVal;}

      int GetLongValue() const {return long_value_; }
      void SetLongValue(long NewVal);

      String GetStringValue() const {return string_value_; }
      void SetStringValue(const String &NewVal);

      bool GetBoolValue() const {return long_value_ ? true : false; }
      void SetBoolValue(bool NewVal);

      void SetIsCrypted() {save_crypted_ = true; }
   private:

  
      bool WriteBoolSetting_(bool bValue);
      bool WriteLongSetting_(long lValue);
      bool WriteStringSetting_(const String &sValue);
      bool SettingRowExists_() const;

      long long_value_;
      String string_value_;
      AnsiString name_;

      bool save_crypted_;

   };
}
