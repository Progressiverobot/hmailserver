// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include ".\PersistentTCPIPPort.h"
#include "..\BO\TCPIPPort.h"
#include "..\SQL\SQLStatement.h"
#include "../SQL/IPAddressSQLHelper.h"

#include "../Persistence/PersistenceMode.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   PersistentTCPIPPort::PersistentTCPIPPort(void)
   {
   }

   PersistentTCPIPPort::~PersistentTCPIPPort(void)
   {
   }

   bool
   PersistentTCPIPPort::DeleteObject(std::shared_ptr<TCPIPPort> pObject)
   {
      SQLCommand command("delete from hm_tcpipports where portid = @PORTID");
      command.AddParameter("@PORTID", pObject->GetID());

      return Application::Instance()->GetDBManager()->Execute(command);
   }

   bool 
   PersistentTCPIPPort::ReadObject(std::shared_ptr<TCPIPPort> pObject, std::shared_ptr<DALRecordset> pRS)
   {
      IPAddressSQLHelper helper;

      pObject->SetID (pRS->GetLongValue("portid"));
      pObject->SetProtocol((SessionType) pRS->GetLongValue("portprotocol"));
      pObject->SetPortNumber(pRS->GetLongValue("portnumber"));
      pObject->SetConnectionSecurity((ConnectionSecurity) pRS->GetLongValue("portconnectionsecurity"));
      pObject->SetAddress(helper.Construct(pRS, "portaddress1", "portaddress2"));
      pObject->SetSSLCertificateID(pRS->GetLongValue("portsslcertificateid"));
      pObject->SetClientCertificatePolicy((ClientCertificatePolicy) pRS->GetLongValue("portclientcertificatepolicy"));
      pObject->SetClientCertificateCAFile(pRS->GetStringValue("portclientcertificatecafile"));

      return true;
   }

   bool 
   PersistentTCPIPPort::SaveObject(std::shared_ptr<TCPIPPort> pObject, String &errorMessage, PersistenceMode mode)
   {
      if (mode == PersistenceModeNormal)
      {
         if (pObject->GetSSLCertificateID() == 0 &&
             (pObject->GetConnectionSecurity() == CSSSL || pObject->GetConnectionSecurity() == CSSTARTTLSOptional || pObject->GetConnectionSecurity() == CSSTARTTLSRequired))
         {
            errorMessage = "Certificate must be specified.";
            return false;
         }

         if (pObject->GetClientCertificatePolicy() != CCPOff)
         {
            // A client certificate is only ever exchanged during a TLS handshake, so
            // on a plaintext port the policy could never run: the administrator would
            // believe client certificates gate the port while every session walks
            // straight past. Refuse the combination here, where the mistake is one
            // sentence to fix, instead of at runtime where it is an outage.
            if (pObject->GetConnectionSecurity() == CSNone)
            {
               errorMessage = "Client certificates can only be requested or required on a port that uses SSL/TLS or STARTTLS.";
               return false;
            }

            // Without trust anchors, "require" rejects every client (nothing can
            // chain to an empty CA set) and "request" verifies nothing while looking
            // configured. Both are misconfigurations an administrator should hear
            // about now, not discover from the error log after a restart.
            if (pObject->GetClientCertificateCAFile().Trim().IsEmpty())
            {
               errorMessage = "A CA certificate file must be specified when client certificates are requested or required.";
               return false;
            }

            // On a port where STARTTLS is optional, a client that simply never
            // issues STARTTLS is never asked for a certificate at all, so "require"
            // would be a lock on a door standing open. The combinations that do
            // enforce it are SSL/TLS and required STARTTLS.
            if (pObject->GetClientCertificatePolicy() == CCPRequire &&
                pObject->GetConnectionSecurity() == CSSTARTTLSOptional)
            {
               errorMessage = "Requiring a client certificate cannot be combined with optional STARTTLS, since a client that never starts TLS would bypass the requirement. Use SSL/TLS or required STARTTLS.";
               return false;
            }
         }
      }

      SQLStatement oStatement;
      oStatement.SetTable("hm_tcpipports");

      if (pObject->GetID() == 0)
      {
         oStatement.SetStatementType(SQLStatement::STInsert);
         oStatement.SetIdentityColumn("portid");
      }
      else
      {
         oStatement.SetStatementType(SQLStatement::STUpdate);
         String sWhere;
         sWhere.Format(_T("portid = %I64d"), pObject->GetID());
         oStatement.SetWhereClause(sWhere);
         
      }

      __int64 iAddress = 0;

      IPAddressSQLHelper helper;
      

      oStatement.AddColumn("portprotocol", pObject->GetProtocol());
      oStatement.AddColumn("portnumber", pObject->GetPortNumber());
      oStatement.AddColumnInt64("portsslcertificateid", pObject->GetSSLCertificateID());
      helper.AppendStatement(oStatement, pObject->GetAddress(), "portaddress1", "portaddress2");
      oStatement.AddColumn("portconnectionsecurity", pObject->GetConnectionSecurity());
      oStatement.AddColumn("portclientcertificatepolicy", pObject->GetClientCertificatePolicy());
      oStatement.AddColumn("portclientcertificatecafile", pObject->GetClientCertificateCAFile());
      
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