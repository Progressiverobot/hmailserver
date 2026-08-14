// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class SSLCertificate : public BusinessObject<SSLCertificate>
   {
   public:
      SSLCertificate(void);
      ~SSLCertificate(void);

      String GetName() const {return name_; }
      void SetName(const String &sName) {name_ = sName; }

      String GetCertificateFile() const {return certificate_file_; }
      void SetCertificateFile(const String &sName) {certificate_file_ = sName; }

      String GetPrivateKeyFile() const {return private_key_file_; }
      void SetPrivateKeyFile(const String &sName) {private_key_file_ = sName; }

      // The passphrase for an encrypted private key file, empty for the ordinary
      // unencrypted key. Held per certificate rather than globally because
      // different certificates can have different passphrases. In memory this is
      // the plaintext - it has to be, since OpenSSL needs the actual bytes during
      // the handshake-context setup - and it is protected at rest on the way in
      // and out of the database (see PersistentSSLCertificate) and of the XML
      // backup (see XMLStore below).
      String GetPrivateKeyPassword() const {return private_key_password_; }
      void SetPrivateKeyPassword(const String &sPassword) {private_key_password_ = sPassword; }

      bool XMLStore(XNode *pNode, int iOptions);
      bool XMLLoad(XNode *pNode, int iOptions);
      bool XMLLoadSubItems (XNode *pNode, int iOptions) {return true;};

   private:

      String name_;
      String certificate_file_;
      String private_key_file_;
      String private_key_password_;


   };
}