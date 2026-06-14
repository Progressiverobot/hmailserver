// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "ClassTester.h"
#include "../Application/BackupManager.h"
#include "../Application/TimeoutCalculator.h"
#include "../BO/MessageData.h"
#include "../BO/Message.h"
#include "../Mime/MimeTester.h"
#include "../Util/Utilities.h"
#include "../Util/MessageUtilities.h"
#include "../Util/Charset.h"
#include "../Util/RegularExpression.h"
#include "../TCPIP/LocalIPAddresses.h"
#include "../TCPIP/IPAddress.h"
#include "Time.h"
#include "Utilities.h"
#include "Parsing\AddresslistParser.h"
#include "../../IMAP/IMAPSimpleCommandParser.h"
#include "BlowFish.h"
#include "Crypt.h"
#include "DataProtector.h"
#include "SRS.h"
#include "RateLimiter.h"
#include "../Persistence/PersistentMessage.h"
#include "../../SMTP/SPF/SPF.h"
#include "../../SMTP/BLCheck.h"
#include "../Application/BackupManager.h"
#include "../Util/Encoding/Base64.h"
#include "../Util/Encoding/ModifiedUTF7.h"
#include "../Util/Hashing/HashCreator.h"
#include "../Util/OAuth2TokenValidator.h"
#include "../Util/EventTester.h"
#include <boost/pool/object_pool.hpp>

#ifdef _DEBUG
   #define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
   #define new DEBUG_NEW
#endif

namespace HM
{

   ClassTester::ClassTester()
   {

   }

   ClassTester::~ClassTester()
   {

   }

   void
   ClassTester::DoTests()
   {
      EventTester *pEventTester = new EventTester;
      pEventTester->Test();
      delete pEventTester;

      OutputDebugString(_T("hMailServer: Testing mime parser\n"));
	   MimeTester *pMimeTester = new MimeTester;
      pMimeTester->Test();
	   delete pMimeTester;

      OutputDebugString(_T("hMailServer: Testing StringParser\n"));
      StringParserTester *pParser = new StringParserTester();
      pParser->Test();
      delete pParser;
  
      OutputDebugString(_T("hMailServer: Testing FileUtilities\n"));
      FileUtilitiesTester fileUtilitiesTester;
      fileUtilitiesTester.Test();

      OutputDebugString(_T("hMailServer: Testing IPAddress\n"));
      IPAddressTester ipAddressTester;
      ipAddressTester.Test();

      OutputDebugString(_T("hMailServer: Testing MessageUtilities\n"));
      MessageUtilitiesTester messageUtilitiesTester;
      messageUtilitiesTester.Test();

      OutputDebugString(_T("hMailServer: Testing Formatter\n"));
      FormatterTester formatterTester;
      formatterTester.Test();

      OutputDebugString(_T("hMailServer: Testing TimeoutCalculator\n"));
      TimeoutCalculatorTester timeoutCalculatortester;
      timeoutCalculatortester.Test();

      OutputDebugString(_T("hMailServer: Test DateTime\n"));
      DateTimeTests tests;
      tests.Test();

      OutputDebugString(_T("hMailServer: Testing Utilities\n"));
      UtilitiesTester *pTestUtilities = new UtilitiesTester();
      pTestUtilities->Test();
      delete pTestUtilities;

      OutputDebugString(_T("hMailServer: Test BLChecktester\n"));
      BLCheckTester blchecktester;
      blchecktester.Test();

      OutputDebugString(_T("hMailServer: Testing SPF\n"));
      SPFTester *pSPF = new SPFTester();
      pSPF->Test();
      delete pSPF;

      OutputDebugString(_T("hMailServer: Testing SHA256\n"));
      HashCreatorTester tester;
      tester.Test();

      OutputDebugString(_T("hMailServer: Testing password hash dispatch (Crypt)\n"));
      {
         // Validate the exact server-side login dispatch (EnCrypt -> GetHashType ->
         // Validate) for the strong password KDFs, including Argon2id.
         const String testPwd = _T("Corr3ct H0rse Battery Staple!");

         String argonHash = Crypt::Instance()->EnCrypt(testPwd, Crypt::ETArgon2id);
         if (Crypt::Instance()->GetHashType(argonHash) != Crypt::ETArgon2id)
            throw 0;
         if (!Crypt::Instance()->Validate(testPwd, argonHash, Crypt::ETArgon2id))
            throw 0;
         if (Crypt::Instance()->Validate(_T("the wrong password"), argonHash, Crypt::ETArgon2id))
            throw 0;

         String pbkdf2Hash = Crypt::Instance()->EnCrypt(testPwd, Crypt::ETPBKDF2);
         if (Crypt::Instance()->GetHashType(pbkdf2Hash) != Crypt::ETPBKDF2)
            throw 0;
         if (!Crypt::Instance()->Validate(testPwd, pbkdf2Hash, Crypt::ETPBKDF2))
            throw 0;
      }

      OutputDebugString(_T("hMailServer: Testing OAuth2TokenValidator\n"));
      {
         OAuth2TokenValidatorTester oauth2Tester;
         oauth2Tester.Test();
      }

      OutputDebugString(_T("hMailServer: Testing SRS\n"));
      {
         // Sender Rewriting Scheme: sign/verify round-trip + tamper detection.
         const String secret = _T("srs-test-secret-key");
         const String original = _T("alice@external-sender.com");
         const String forwarder = _T("relay.example.com");

         String srs = SRS::Forward(original, forwarder, secret);

         if (srs.IsEmpty())
            throw 0;
         if (!SRS::IsSRS0(srs))
            throw 0;
         if (srs.Find(_T("@relay.example.com")) < 0)
            throw 0;

         // A valid SRS0 address reverses to the original sender.
         String reversed;
         if (!SRS::Reverse(srs, secret, reversed))
            throw 0;
         if (reversed.CompareNoCase(original) != 0)
            throw 0;

         // Tamper detection: a flipped character in the signed local-part fails.
         wchar_t hashChar = srs.GetAt(5);
         String tampered = srs.Mid(0, 5) + ((hashChar == L'0') ? _T("1") : _T("0")) + srs.Mid(6);
         if (SRS::Reverse(tampered, secret, reversed))
            throw 0;

         // A different secret must not validate.
         if (SRS::Reverse(srs, _T("a-different-secret"), reversed))
            throw 0;

         // A plain address is neither detected as SRS nor reversible.
         if (SRS::IsSRS0(_T("bob@example.com")))
            throw 0;
         if (SRS::Reverse(_T("bob@example.com"), secret, reversed))
            throw 0;

         // An original local-part containing '=' and '+' survives the round trip.
         const String original2 = _T("a=b+tag@ext.example.org");
         String srs2 = SRS::Forward(original2, forwarder, secret);
         if (srs2.IsEmpty())
            throw 0;
         String reversed2;
         if (!SRS::Reverse(srs2, secret, reversed2))
            throw 0;
         if (reversed2.CompareNoCase(original2) != 0)
            throw 0;
      }

      OutputDebugString(_T("hMailServer: Testing RateLimiter\n"));
      {
         // Sliding-window rate limiter: limit is enforced per key, 0 = unlimited.
         RateLimiter::Instance()->Clear();

         // A maxPerMinute of 0 never throttles.
         for (int i = 0; i < 1000; i++)
         {
            if (!RateLimiter::Instance()->TryConsume(_T("unlimited"), 0))
               throw 0;
         }

         // With a limit of 3, the first three succeed and the fourth fails.
         if (!RateLimiter::Instance()->TryConsume(_T("keyA"), 3))
            throw 0;
         if (!RateLimiter::Instance()->TryConsume(_T("keyA"), 3))
            throw 0;
         if (!RateLimiter::Instance()->TryConsume(_T("keyA"), 3))
            throw 0;
         if (RateLimiter::Instance()->TryConsume(_T("keyA"), 3))
            throw 0;

         // A different key has an independent budget.
         if (!RateLimiter::Instance()->TryConsume(_T("keyB"), 3))
            throw 0;

         RateLimiter::Instance()->Clear();
      }

      OutputDebugString(_T("hMailServer: Testing DataProtector (DPAPI)\n"));
      {
         DataProtectorTester dpapiTester;
         dpapiTester.Test();

         // Also exercise the Crypt secret envelope used for stored secrets.
         const String secret = _T("route-relay-p@ssw0rd");
         String envelope = Crypt::Instance()->ProtectSecret(secret);
         if (envelope.IsEmpty())
            throw 0;
         if (Crypt::Instance()->UnprotectSecret(envelope) != secret)
            throw 0;
         // A legacy Blowfish value (no DPAPI prefix) must still round-trip.
         String legacy = Crypt::Instance()->EnCrypt(secret, Crypt::ETBlowFish);
         if (Crypt::Instance()->UnprotectSecret(legacy) != secret)
            throw 0;
      }


      OutputDebugString(_T("hMailServer: Testing RegularExpressionTester\n"));
      RegularExpressionTester *pRegExTest = new RegularExpressionTester();
      pRegExTest->Test();
      delete pRegExTest;

      OutputDebugString(_T("hMailServer: Testing Base64\n"));
      Base64Tester base64Tester;
      base64Tester.Test();

      OutputDebugString(_T("hMailServer: Testing Base64\n"));
      ModifiedUTF7Tester modifiedUTF7Tester;
      modifiedUTF7Tester.Test();

      OutputDebugString(_T("hMailServer: Testing AddresslistParser\n"));
      AddresslistParserTester *pTest2 = new AddresslistParserTester();
      pTest2->Test();
      delete pTest2;

      OutputDebugString(_T("hMailServer: Testing charset\n"));
      CharsetTester *pCharsetTester = new CharsetTester;
      pCharsetTester->Test();
      delete pCharsetTester;


      OutputDebugString(_T("hMailServer: Testing LocalIPAddresses\n"));
      LocalIPAddressesTester *pTest4 = new LocalIPAddressesTester();
      pTest4->Test();
      delete pTest4;

      OutputDebugString(_T("hMailServer: Testing MessageData\n"));
      MessageDataTester *pMsgData = new MessageDataTester();
      pMsgData->Test();
      delete pMsgData;

      OutputDebugString(_T("hMailServer: Testing Time\n"));
      TimeTester *pTimeT = new TimeTester();
      pTimeT->Test();
      delete pTimeT;




      OutputDebugString(_T("hMailServer: Testing BlowFishEncryptorTester\n"));
      BlowFishEncryptorTester *pTest3 = new BlowFishEncryptorTester();
      pTest3->Test();
      delete pTest3;

      OutputDebugString(_T("hMailServer: Testing IMAPSimpleCommandParserTester\n"));
      IMAPSimpleCommandParserTester *pTest = new IMAPSimpleCommandParserTester();
      pTest->Test();
      delete pTest;

   }

   void 
   ClassTester::LoadSettings_()
   {
      String sAppPath = Utilities::GetBinDirectory();
      if (sAppPath.Right(1) != _T("\\"))
         sAppPath += _T("\\");

      String sConfigFile = sAppPath + "test_config.xml";
      String sTestSpec = FileUtilities::ReadCompleteTextFile(sConfigFile);

      if (sTestSpec.IsEmpty())
         return;

      XDoc oDoc;
      oDoc.Load(sTestSpec);

      XNode *pBackupNode = oDoc.GetChild(_T("Config"));

      if (!pBackupNode)
         throw;

      mime_data_path_ = pBackupNode->GetChildValue(_T("MimeDataPath"));
   }

   void 
   ClassTester::TestBackup_()
   {
      std::shared_ptr<BackupManager> pBackupManager = Application::Instance()->GetBackupManager();
      std::shared_ptr<Backup> pBackup = pBackupManager->LoadBackup("C:\\Temp\\Backup\\HMBackup 2006-12-10 091555.zip");
      pBackup->SetRestoreOptions(1 | 2 | 4 | 8 | 16 | 32);
      pBackupManager->StartRestore(pBackup);

      Sleep(1000000);
   }
}
