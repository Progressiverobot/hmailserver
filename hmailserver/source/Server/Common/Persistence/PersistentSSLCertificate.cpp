// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"

#include ".\PersistentSSLCertificate.h"
#include "..\BO\SSLCertificate.h"
#include "..\SQL\SQLStatement.h"
#include "..\Util\Crypt.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentSSLCertificate::PersistentSSLCertificate(void)
   {
   }

   PersistentSSLCertificate::~PersistentSSLCertificate(void)
   {
   }

   bool
   PersistentSSLCertificate::DeleteObject(std::shared_ptr<SSLCertificate> pObject)
   {
      SQLCommand command(_T("delete from hm_sslcertificates where sslcertificateid = @CERTIFICATEID"));
      command.AddParameter("@CERTIFICATEID", pObject->GetID());
      
      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool 
   PersistentSSLCertificate::ReadObject(std::shared_ptr<SSLCertificate> pObject, std::shared_ptr<DALRecordset> pRS)
   {
      pObject->SetID (pRS->GetLongValue("sslcertificateid"));
      pObject->SetName(pRS->GetStringValue("sslcertificatename"));
      pObject->SetCertificateFile(pRS->GetStringValue("sslcertificatefile"));
      pObject->SetPrivateKeyFile(pRS->GetStringValue("sslprivatekeyfile"));

      // Requires database version 6009 (adds hm_sslcertificates.sslprivatekeypassword).
      // UnprotectSecret reverses whatever ProtectSecret produced in SaveObject below:
      // a DPAPI envelope on the machine that wrote it, or the legacy Blowfish value.
      // A DPAPI envelope read on a DIFFERENT machine (database moved without a proper
      // backup/restore) fails to decrypt and comes back empty - the certificate then
      // behaves as if no passphrase were configured and error 6170 tells the
      // administrator to re-enter it, which is the intended property: the database
      // alone, off-box, does not surrender the passphrase.
      pObject->SetPrivateKeyPassword(Crypt::Instance()->UnprotectSecret(pRS->GetStringValue("sslprivatekeypassword")));

      return true;
   }

   bool 
   PersistentSSLCertificate::SaveObject(std::shared_ptr<SSLCertificate> pObject, String &errorMessage,  PersistenceMode mode)
   {
      // errorMessage - not supported yet.
      return SaveObject(pObject);
   }

   bool 
   PersistentSSLCertificate::SaveObject(std::shared_ptr<SSLCertificate> pObject)
   {
      SQLStatement oStatement;
      oStatement.SetTable("hm_sslcertificates");
      
      if (pObject->GetID() == 0)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("sslcertificateid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);
         String sWhere;
         sWhere.Format(_T("sslcertificateid = %I64d"), pObject->GetID());
         oStatement.SetWhereClause(sWhere);
         
      }

      oStatement.AddColumn(_T("sslcertificatename"), pObject->GetName());
      oStatement.AddColumn(_T("sslcertificatefile"), pObject->GetCertificateFile());
      oStatement.AddColumn(_T("sslprivatekeyfile"), pObject->GetPrivateKeyFile());

      // Protected at rest exactly like the other reversible secrets in this
      // database (fetch-account and route passwords): a machine-bound DPAPI
      // envelope when ProtectStoredSecretsWithDPAPI=1 (the default), legacy
      // Blowfish otherwise. What that buys, stated plainly: an attacker holding a
      // copy of the database (or a dump, or the ini file) but not code execution
      // on this machine cannot recover the passphrase - DPAPI decryption only
      // works on the machine that encrypted it. An attacker who CAN run code on
      // this machine can unprotect it, but such an attacker can also read the key
      // file itself, so nothing new is conceded. With DPAPI turned off the stored
      // value is Blowfish under a key that ships in the source: obfuscation only,
      // and the database must then be treated as containing the passphrase.
      oStatement.AddColumn(_T("sslprivatekeypassword"), Crypt::Instance()->ProtectSecret(pObject->GetPrivateKeyPassword()));

      bool bNewObject = pObject->GetID() == 0;

      // Save and fetch ID
      __int64 iDBID = 0;
      bool bRetVal = Application::Instance()->GetDBManager()->Execute(oStatement, bNewObject ? &iDBID : 0);
      if (bRetVal && bNewObject)
         pObject->SetID((int) iDBID);


      // bRetVal, not true. This computed whether the statement had succeeded and then
      // reported success regardless, so every caller that checked was checking a
      // constant - including the ones hardened earlier this week, whose checks were
      // therefore correct code that could never fire. Found by the database fault
      // injector on its first run, which is the entire argument for having built it.
      return bRetVal;
   }
}