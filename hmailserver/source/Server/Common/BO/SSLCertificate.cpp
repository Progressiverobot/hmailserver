// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "SSLCertificate.h"

#include "../Util/Crypt.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   SSLCertificate::SSLCertificate(void)
   {
      
   }

   SSLCertificate::~SSLCertificate(void)
   {
   }

   bool 
   SSLCertificate::XMLStore(XNode *pParentNode, int iOptions)
   {
      XNode *pNode = pParentNode->AppendChild(_T("SSLCertificate"));

      pNode->AppendAttr(_T("Name"), name_);
      pNode->AppendAttr(_T("CertificateFile"), certificate_file_);
      pNode->AppendAttr(_T("PrivateKeyFile"), private_key_file_);

      // Blowfish rather than plaintext, and Blowfish rather than DPAPI, both
      // deliberately - and both exactly what FetchAccount::XMLStore does with its
      // password. Plaintext would put the passphrase legibly into every backup
      // file. DPAPI would bind the backup to this machine, and a backup that
      // cannot be restored onto replacement hardware is not a backup. Blowfish
      // with the built-in key is obfuscation, not protection - anyone holding the
      // backup AND the hMailServer source can decode it - so a backup file must
      // still be treated as containing the passphrase.
      pNode->AppendAttr(_T("PrivateKeyPassword"), Crypt::Instance()->EnCrypt(private_key_password_, Crypt::ETBlowFish));

      return true;
   }

   bool 
   SSLCertificate::XMLLoad(XNode *pNode, int iOptions)
   {
      name_ = pNode->GetAttrValue(_T("Name"));
      certificate_file_ = pNode->GetAttrValue(_T("CertificateFile"));
      private_key_file_ = pNode->GetAttrValue(_T("PrivateKeyFile"));

      // Backups taken before the attribute existed simply have no
      // PrivateKeyPassword attribute; GetAttrValue returns an empty string for
      // those, DeCrypt of an empty string is an empty string, and the restored
      // certificate behaves exactly as it did when it was backed up.
      private_key_password_ = Crypt::Instance()->DeCrypt(pNode->GetAttrValue(_T("PrivateKeyPassword")), Crypt::ETBlowFish);

      return true;
   }
}