// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "stdafx.h"
#include "PropertySet.h"
#include "Property.h"
#include "../Util/Crypt.h"
#include "../Util/AuditTrail.h"


#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif


using namespace std;


namespace HM
{
   PropertySet::PropertySet(void)
   {
   }

   PropertySet::~PropertySet(void)
   {
      
   }

   void 
   PropertySet::Refresh()
   {
      SQLCommand command("select * from hm_settings");
      std::shared_ptr<DALRecordset> pRS = Application::Instance()->GetDBManager()->OpenRecordset(command);
   
      if (!pRS)
         return;

      std::map<String, std::shared_ptr<Property> > tmpMap;

      while (!pRS->IsEOF())
      {
         String sPropertyName = pRS->GetStringValue("settingname");
         long lPropertyLong = pRS->GetLongValue("settinginteger");
         String sPropertyString = pRS->GetStringValue("settingstring");

         bool bIsCrypted = false;
         if (IsCryptedProperty_(sPropertyName))
         {
            // De-crypt the string after load from DB.
            sPropertyString = Crypt::Instance()->UnprotectSecret(sPropertyString);
            bIsCrypted = true;
         }
      
         std::shared_ptr<Property> oProperty = std::shared_ptr<Property> (new Property(sPropertyName, lPropertyLong, sPropertyString));
         if (bIsCrypted)
            oProperty->SetIsCrypted();

         tmpMap[sPropertyName] = oProperty;

         pRS->MoveNext();
      }



      items_ = tmpMap;

      auto iter = items_.begin();
      auto iterEnd = items_.end();
      for (; iter != iterEnd; iter++)
      {
         // Trigger an change-event for all options.
         OnPropertyChanged_((*iter).second);
      }
   }

   std::shared_ptr<Property>
   PropertySet::GetProperty_(const String & sPropertyName)
   {
      auto iterProperty = items_.find(sPropertyName);
   
      if (iterProperty != items_.end())
         return (*iterProperty).second;

      String sErrorMessage;
      sErrorMessage.Format(_T("The property %s could not be found."), sPropertyName.c_str());
      ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5015, "PropertySet::GetProperty_()", sErrorMessage);

      // The property row is missing from the database. Return a NAMED in-memory
      // property: reads get the default value, and a write persists the row via
      // the insert fallback in Property, self-healing the database. (A nameless
      // property here used to make writes silently update zero rows.)
      std::shared_ptr<Property> oProperty = std::shared_ptr<Property>(new Property(sPropertyName, 0, ""));

      if (IsCryptedProperty_(sPropertyName))
         oProperty->SetIsCrypted();

      return oProperty;
   }

   long
   PropertySet::GetLong(const String &sPropertyName)
   {
      return GetProperty_(sPropertyName)->GetLongValue();
   }

   bool
   PropertySet::GetBool(const String &sPropertyName)
   {
      return GetProperty_(sPropertyName)->GetBoolValue();
   }

   String
   PropertySet::GetString(const String &sPropertyName)
   {
      return GetProperty_(sPropertyName)->GetStringValue();
   }

   /*
      The audit trail's one chokepoint for settings. Every server setting, from
      every interface, is written through the three functions below, and this is
      the only place where the value the setting HAD is still readable - which is
      why hm_settings is not one of the tables AuditTrail records from the
      statement chokepoint. A setting whose value is a secret is recorded as
      changed and never as a value; AuditTrail::IsSecretName decides, by name.

      Recorded only when the value really changed, which is the same condition
      OnPropertyChanged_ already uses: the Control Panel's settings pages write
      every field on the page when one of them is edited, and an audit trail that
      recorded forty unchanged settings per save would bury the one that moved.
   */
   void 
   PropertySet::SetLong(const String &sPropertyName, long lValue)
   {
      std::shared_ptr<Property> pProperty = GetProperty_(sPropertyName);
      long previousValue = pProperty->GetLongValue();
      bool bChanged = lValue != previousValue;
      pProperty->SetLongValue(lValue);

      if (bChanged)
      {
         AuditTrail::Instance()->RecordSettingChange(sPropertyName,
            StringParser::IntToString((int) previousValue), StringParser::IntToString((int) lValue));

         OnPropertyChanged_(pProperty);
      }
   }

   void 
   PropertySet::SetBool(const String &sPropertyName, bool bValue)
   {
      std::shared_ptr<Property> pProperty = GetProperty_(sPropertyName);
      bool previousValue = pProperty->GetBoolValue();
      bool bChanged = bValue != previousValue;
      pProperty->SetBoolValue(bValue);

      if (bChanged)
      {
         AuditTrail::Instance()->RecordSettingChange(sPropertyName,
            previousValue ? _T("true") : _T("false"), bValue ? _T("true") : _T("false"));

         OnPropertyChanged_(pProperty);
      }
   }

   void 
   PropertySet::SetString(const String &sPropertyName, const String &sValue)
   {
      std::shared_ptr<Property> pProperty = GetProperty_(sPropertyName);
      String previousValue = pProperty->GetStringValue();
      bool bChanged = sValue != previousValue;
      pProperty->SetStringValue(sValue);

      if (bChanged)
      {
         AuditTrail::Instance()->RecordSettingChange(sPropertyName, previousValue, sValue);

         OnPropertyChanged_(pProperty);
      }
   }

   bool
   PropertySet::Contains(const String &sPropertyName) const
   {
      return items_.find(sPropertyName) != items_.end();
   }

   void
   PropertySet::EnsureLong(const String &sPropertyName, long lValue)
   {
      if (Contains(sPropertyName))
         return;

      std::shared_ptr<Property> oProperty = std::shared_ptr<Property>(new Property(sPropertyName, lValue, ""));

      // Writes the row. Property inserts when the row is missing, which it is -
      // that is the whole reason this function was reached.
      oProperty->SetLongValue(lValue);

      items_[sPropertyName] = oProperty;

      // No change event: the value that has just been stored is the default, so
      // nothing about the server's behaviour has changed and nothing needs to be
      // told. Refresh() raises one for every property that WAS in the database.
   }

   void
   PropertySet::EnsureString(const String &sPropertyName, const String &sValue)
   {
      if (Contains(sPropertyName))
         return;

      std::shared_ptr<Property> oProperty = std::shared_ptr<Property>(new Property(sPropertyName, 0, sValue));

      if (IsCryptedProperty_(sPropertyName))
         oProperty->SetIsCrypted();

      oProperty->SetStringValue(sValue);

      items_[sPropertyName] = oProperty;
   }

   void
   PropertySet::OnPropertyChanged_(std::shared_ptr<Property> pProperty)
   {
      // Notify configuration that a setting has changed.
      Configuration::Instance()->OnPropertyChanged(pProperty);
   }

   bool 
   PropertySet::IsCryptedProperty_(const String &sPropertyName)
   {
      if (sPropertyName == PROPERTY_SMTPRELAYER_PASSWORD)
         return true;

      return false;
   }

   bool
   PropertySet::XMLStore(XNode *pBackupNode)
   {
      XNode *pPropertiesNode = pBackupNode->AppendChild(_T("Properties"));
      auto iterProperty = items_.begin();

      while (iterProperty != items_.end())
      {
         std::shared_ptr<Property> oProperty = (*iterProperty).second;

         XNode *pNode = pPropertiesNode->AppendChild(String(oProperty->GetName()));

         pNode->AppendAttr(_T("LongValue"), StringParser::IntToString(oProperty->GetLongValue()));
         pNode->AppendAttr(_T("StringValue"), oProperty->GetStringValue());

         iterProperty++;
      }      

      return true;
   }

   bool
   PropertySet::XMLLoad(XNode *pBackupNode)
   {
      XNode *pPropertiesNode = pBackupNode->GetChild(_T("Properties"));
      if (!pPropertiesNode)
         return true;

      for (int i = 0; i < pPropertiesNode->GetChildCount(); i++)
      {
         XNode *pPropertyNode = pPropertiesNode->GetChild(i);
         String sName = pPropertyNode->name;
         String sStringValue = pPropertyNode->GetAttrValue(_T("StringValue"));
         int iLongValue = _ttoi(pPropertyNode->GetAttrValue(_T("LongValue")));

         std::shared_ptr<Property> pProperty = GetProperty_(sName);
         if (pProperty)
         {
            pProperty->SetStringValue(sStringValue);
            pProperty->SetLongValue(iLongValue);
         }
      }

      return true;
   }
}
