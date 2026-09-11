// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// S/MIME for the webmail: the account's key store, the trust check the page
// cannot make for itself, and the send of a message the page built.
//
// The cryptography is the page's (PortalSmime.js, served as /portal-smime.js
// from PortalSmimeData.cpp): keys are imported, signatures made and checked,
// messages encrypted and decrypted in the browser with the Web Crypto API and
// nothing else. This server keeps what the page hands it, in hm_smimekeys
// (schema 6037): the account's own certificates with their chains and their
// private keys - wrapped in the browser under a key derived from the account
// password, so this server stores them and cannot open them - and the
// certificates of correspondents, taken from the signed messages they sent.
// GET /api/v1/me/smime lists both; PUT /api/v1/me/smime/own and
// PUT /api/v1/me/smime/recipients add or replace one by its fingerprint;
// DELETE .../own/{fingerprint} and .../recipients/{fingerprint} remove one.
//
// The one question a page cannot answer is whether a certificate chain ends at
// a root this machine trusts: POST /api/v1/me/smime/chain hands the chain to
// OpenSSL against the system's roots - the Windows ROOT and CA stores here,
// the distribution's bundle on Linux - for the S/MIME signing or encryption
// purpose, and answers what it said.
//
// A signed or an encrypted message must reach the recipient with its body
// bytes exactly as signed, so POST /api/v1/me/messages takes "mime": the MIME
// entity the page built (its Content-Type headers, a blank line, its body),
// which WriteMimeEntity_ writes under this server's own RFC 5322 headers, the
// way every other send is addressed and dated.
#include "StdAfx.h"

#include "RestApiServer.h"
#include "HttpServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "FileUtilities.h"
#include "GUIDCreator.h"
#include "Time.h"
#include "Encoding/Base64.h"
#include "../Application/Application.h"
#include "../BO/Account.h"
#include "../Mime/Mime.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DALRecordset.h"
#include "../Util/Parsing/StringParser.h"

#include <openssl/x509.h>
#include <openssl/x509v3.h>
#include <openssl/x509_vfy.h>
#include <openssl/err.h>

#include <cstring>
#include <ctime>
#include <mutex>
#include <string>
#include <vector>

#ifdef HM_PLATFORM_POSIX
namespace
{
   // _mkgmtime is Microsoft's name for the inverse of gmtime: a struct tm read
   // as UTC, where mktime would read it as local time. POSIX spells the same
   // function timegm; given the Microsoft name once, here, as AcmeClient.cpp
   // does, so the call below reads the way it does on Windows.
   inline time_t _mkgmtime(struct tm *parts)
   {
      return ::timegm(parts);
   }
}
#endif

#ifdef _WIN32
#pragma comment(lib, "crypt32.lib")
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   extern const char *PortalHeaders;
   extern const char *const PortalSmimeScriptPieces[];

   namespace
   {
      const int OwnKind = 1;
      const int RecipientKind = 2;
      const int OwnPerAccount = 20;
      const int RecipientsPerAccount = 500;
      const int CertificateMaximum = 16 * 1024;   // base64 characters
      const int ChainMaximum = 8;
      const int WrappedKeyMaximum = 16 * 1024;
      const int EntityMaximum = 16 * 1024 * 1024;

      const char *Columns = "smimeid, smimeaccountid, smimekind, smimeaddress, smimename, smimefingerprint, smimecertificate, smimechain, smimekey, smimenotafter, smimecreated";

      struct KeyRow
      {
         __int64 id;
         int kind;
         String address;
         String name;
         AnsiString fingerprint;
         AnsiString certificate;
         AnsiString chain;
         AnsiString key;
         __int64 notAfter;
         __int64 created;
      };

      __int64 Now()
      {
         return (__int64) time(nullptr);
      }

      AnsiString Escape(const AnsiString &value)
      {
         AnsiString out;
         for (int i = 0; i < value.GetLength(); i++)
         {
            unsigned char c = (unsigned char) value[i];
            if (c == '"') out += "\\\"";
            else if (c == '\\') out += "\\\\";
            else if (c == '\n') out += "\\n";
            else if (c == '\r') out += "\\r";
            else if (c == '\t') out += "\\t";
            else if (c < 0x20) { char buffer[8]; sprintf_s(buffer, sizeof(buffer), "\\u%04x", c); out += buffer; }
            else out += (char) c;
         }
         return out;
      }

      AnsiString Utf8(const String &value)
      {
         AnsiString out;
         Unicode::WideToMultiByte(value, out);
         return out;
      }

      // What a helper answers; the route hands it to the server's response builder.
      struct Answer
      {
         int status;
         AnsiString body;
         Answer(int s, const AnsiString &b) : status(s), body(b) {}
      };

      AnsiString Int64Text(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

      bool IsBase64(const std::string &text)
      {
         if (text.empty() || text.size() % 4 != 0)
            return false;
         for (size_t i = 0; i < text.size(); i++)
         {
            char c = text[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=';
            if (!ok)
               return false;
         }
         return true;
      }

      bool IsFingerprint(const AnsiString &text)
      {
         if (text.GetLength() != 64)
            return false;
         for (int i = 0; i < 64; i++)
         {
            char c = text[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
               return false;
         }
         return true;
      }

      void ReadRow(std::shared_ptr<DALRecordset> recordset, KeyRow &row)
      {
         row.id = recordset->GetInt64Value("smimeid");
         row.kind = (int) recordset->GetLongValue("smimekind");
         row.address = recordset->GetStringValue("smimeaddress");
         row.name = recordset->GetStringValue("smimename");
         row.fingerprint = AnsiString(recordset->GetStringValue("smimefingerprint"));
         row.certificate = AnsiString(recordset->GetStringValue("smimecertificate"));
         row.chain = AnsiString(recordset->GetStringValue("smimechain"));
         row.key = AnsiString(recordset->GetStringValue("smimekey"));
         row.notAfter = recordset->GetInt64Value("smimenotafter");
         row.created = recordset->GetInt64Value("smimecreated");
      }

      void ListRows(__int64 accountId, int kind, std::vector<KeyRow> &rows)
      {
         SQLCommand command(String(_T("select ")) + String(Columns) + _T(" from hm_smimekeys where smimeaccountid = @ACCOUNTID and smimekind = @KIND order by smimeid asc"));
         command.AddParameter("@ACCOUNTID", accountId);
         command.AddParameter("@KIND", (int) kind);
         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset)
            return;
         while (!recordset->IsEOF())
         {
            KeyRow row;
            ReadRow(recordset, row);
            rows.push_back(row);
            recordset->MoveNext();
         }
      }

      bool FindRow(__int64 accountId, int kind, const AnsiString &fingerprint, KeyRow &row)
      {
         SQLCommand command(String(_T("select ")) + String(Columns) + _T(" from hm_smimekeys where smimeaccountid = @ACCOUNTID and smimekind = @KIND and smimefingerprint = @FINGERPRINT"));
         command.AddParameter("@ACCOUNTID", accountId);
         command.AddParameter("@KIND", (int) kind);
         command.AddParameter("@FINGERPRINT", String(fingerprint));
         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset || recordset->IsEOF())
            return false;
         ReadRow(recordset, row);
         return true;
      }

      int CountRows(__int64 accountId, int kind)
      {
         SQLCommand command("select count(*) as c from hm_smimekeys where smimeaccountid = @ACCOUNTID and smimekind = @KIND");
         command.AddParameter("@ACCOUNTID", accountId);
         command.AddParameter("@KIND", (int) kind);
         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset || recordset->IsEOF())
            return 0;
         return (int) recordset->GetLongValue("c");
      }

      // The JSON of one row. An own entry carries its chain and its wrapped
      // key, which the page stored as it made them; a recipient's carries
      // the certificate alone.
      AnsiString RowJson(const KeyRow &row)
      {
         AnsiString json;
         json.Format("{\"id\":%I64d,\"address\":\"%hs\",\"name\":\"%hs\",\"fingerprint\":\"%hs\",\"certificate\":\"%hs\",\"not_after\":%I64d,\"created\":%I64d",
            row.id,
            Escape(Utf8(row.address)).c_str(),
            Escape(Utf8(row.name)).c_str(),
            row.fingerprint.c_str(),
            row.certificate.c_str(),
            row.notAfter,
            row.created);
         if (row.kind == OwnKind)
         {
            json += ",\"chain\":[";
            std::vector<AnsiString> parts = StringParser::SplitString(row.chain, "\n");
            bool first = true;
            for (size_t i = 0; i < parts.size(); i++)
            {
               if (parts[i].IsEmpty())
                  continue;
               if (!first)
                  json += ",";
               first = false;
               json += "\"" + parts[i] + "\"";
            }
            json += "],\"key\":" + (row.key.IsEmpty() ? AnsiString("null") : row.key);
         }
         json += "}";
         return json;
      }

      // The wrapped key as the page made it: the fields it needs to open it
      // again, checked and written back in one shape.
      bool WrappedKeyJson(const JsonValue *key, AnsiString &out, AnsiString &problem)
      {
         if (!key || !key->IsObject())
         {
            problem = "{\"error\":\"key: an object with kdf, iterations, salt, iv and data, as the page wraps a private key\"}";
            return false;
         }
         const JsonValue *kdf = key->Get("kdf");
         const JsonValue *iterations = key->Get("iterations");
         const JsonValue *salt = key->Get("salt");
         const JsonValue *iv = key->Get("iv");
         const JsonValue *data = key->Get("data");
         if (!kdf || !kdf->IsString() || kdf->AsString().size() > 32 || !iterations || !iterations->IsNumber() || !salt || !salt->IsString() || !iv || !iv->IsString() || !data || !data->IsString())
         {
            problem = "{\"error\":\"key: kdf (string), iterations (number), salt, iv and data (base64) are required\"}";
            return false;
         }
         double count = iterations->AsNumber();
         if (count < 1 || count > 10000000 || !IsBase64(salt->AsString()) || !IsBase64(iv->AsString()) || !IsBase64(data->AsString()) || data->AsString().size() > (size_t) WrappedKeyMaximum)
         {
            problem = "{\"error\":\"key: iterations is 1 to 10,000,000; salt, iv and data are base64, data at most 16 KB\"}";
            return false;
         }
         out.Format("{\"kdf\":\"%hs\",\"iterations\":%d,\"salt\":\"%hs\",\"iv\":\"%hs\",\"data\":\"%hs\"}",
            Escape(AnsiString(kdf->AsString().c_str())).c_str(), (int) count,
            salt->AsString().c_str(), iv->AsString().c_str(), data->AsString().c_str());
         return true;
      }

      // What every entry, own or a recipient's, must carry.
      bool CommonFields(const JsonValue &document, String &address, String &name, AnsiString &fingerprint, AnsiString &certificate, __int64 &notAfter, AnsiString &problem)
      {
         const JsonValue *addressValue = document.Get("address");
         const JsonValue *nameValue = document.Get("name");
         const JsonValue *fingerprintValue = document.Get("fingerprint");
         const JsonValue *certificateValue = document.Get("certificate");
         const JsonValue *notAfterValue = document.Get("not_after");
         if (!addressValue || !addressValue->IsString() || !fingerprintValue || !fingerprintValue->IsString() || !certificateValue || !certificateValue->IsString())
         {
            problem = "{\"error\":\"address, fingerprint and certificate are required\"}";
            return false;
         }
         Unicode::MultiByteToWide(AnsiString(addressValue->AsString().c_str()), address);
         address.ToLower();
         address.Trim();
         if (!StringParser::IsValidEmailAddress(address) || address.GetLength() > 255)
         {
            problem = "{\"error\":\"address: not an e-mail address\"}";
            return false;
         }
         if (nameValue && nameValue->IsString())
            Unicode::MultiByteToWide(AnsiString(nameValue->AsString().c_str()), name);
         if (name.GetLength() > 255)
            name = name.Mid(0, 255);
         fingerprint = AnsiString(fingerprintValue->AsString().c_str());
         if (!IsFingerprint(fingerprint))
         {
            problem = "{\"error\":\"fingerprint: the certificate's SHA-256, 64 lowercase hex digits\"}";
            return false;
         }
         if (!IsBase64(certificateValue->AsString()) || certificateValue->AsString().size() > (size_t) CertificateMaximum)
         {
            problem = "{\"error\":\"certificate: base64 DER, at most 16 KB\"}";
            return false;
         }
         certificate = AnsiString(certificateValue->AsString().c_str());
         notAfter = 0;
         if (notAfterValue && notAfterValue->IsNumber())
            notAfter = (__int64) notAfterValue->AsNumber();
         return true;
      }

      // Adds or replaces the row for (account, kind, fingerprint). Answers
      // 201 when it is new, 200 when it replaced one.
      Answer Upsert(std::shared_ptr<const Account> account, int kind, const String &address, const String &name, const AnsiString &fingerprint, const AnsiString &certificate, const AnsiString &chain, const AnsiString &key, __int64 notAfter)
      {
         KeyRow existing;
         bool present = FindRow(account->GetID(), kind, fingerprint, existing);
         if (!present)
         {
            int limit = kind == OwnKind ? OwnPerAccount : RecipientsPerAccount;
            if (CountRows(account->GetID(), kind) >= limit)
            {
               AnsiString json;
               json.Format("{\"error\":\"at most %d %hs\"}", limit, kind == OwnKind ? "own certificates" : "recipients' certificates");
               return Answer(400, json);
            }
         }

         SQLStatement statement;
         statement.SetTable("hm_smimekeys");
         if (present)
         {
            statement.SetStatementType(SQLStatement::STUpdate);
            statement.SetWhereClause(String(_T("smimeid = ")) + String(Int64Text(existing.id)));
         }
         else
         {
            statement.SetStatementType(SQLStatement::STInsert);
            statement.SetIdentityColumn("smimeid");
            statement.AddColumnInt64("smimeaccountid", account->GetID());
            statement.AddColumn("smimekind", (long) kind);
            statement.AddColumnInt64("smimecreated", Now());
         }
         statement.AddColumn("smimeaddress", address);
         statement.AddColumn("smimename", name);
         statement.AddColumn("smimefingerprint", String(fingerprint));
         statement.AddColumn("smimecertificate", String(certificate));
         statement.AddColumn("smimechain", String(chain));
         statement.AddColumn("smimekey", String(key));
         statement.AddColumnInt64("smimenotafter", notAfter);

         __int64 id = existing.id;
         if (present)
         {
            if (!Application::Instance()->GetDBManager()->Execute(statement))
               return Answer(500, "{\"error\":\"the certificate could not be stored\"}");
         }
         else if (!Application::Instance()->GetDBManager()->Execute(statement, &id) || id == 0)
            return Answer(500, "{\"error\":\"the certificate could not be stored\"}");

         KeyRow row;
         if (!FindRow(account->GetID(), kind, fingerprint, row))
            return Answer(500, "{\"error\":\"the certificate could not be read back\"}");
         return Answer(present ? 200 : 201, RowJson(row));
      }

      Answer Remove(std::shared_ptr<const Account> account, int kind, const AnsiString &fingerprint)
      {
         if (!IsFingerprint(fingerprint))
            return Answer(404, "{\"error\":\"no such certificate\"}");
         KeyRow row;
         if (!FindRow(account->GetID(), kind, fingerprint, row))
            return Answer(404, "{\"error\":\"no such certificate\"}");
         SQLCommand command("delete from hm_smimekeys where smimeid = @ID");
         command.AddParameter("@ID", row.id);
         if (!Application::Instance()->GetDBManager()->Execute(command))
            return Answer(500, "{\"error\":\"the certificate could not be removed\"}");
         return Answer(200, "{\"removed\":true}");
      }

      // ---- the trust check ------------------------------------------------

      // The roots this machine trusts, into an OpenSSL store: on Windows the
      // ROOT and CA system stores (the ones a browser here consults), else
      // the platform's default paths. Answers how many certificates were
      // added, -1 when the default paths were taken and no count is known.
      int LoadSystemRoots(X509_STORE *store)
      {
#ifdef _WIN32
         int loaded = 0;
         const wchar_t *names[] = { L"ROOT", L"CA" };
         for (size_t n = 0; n < 2; n++)
         {
            HCERTSTORE systemStore = CertOpenSystemStoreW(0, names[n]);
            if (!systemStore)
               continue;
            PCCERT_CONTEXT context = nullptr;
            while ((context = CertEnumCertificatesInStore(systemStore, context)) != nullptr)
            {
               const unsigned char *data = context->pbCertEncoded;
               X509 *certificate = d2i_X509(nullptr, &data, (long) context->cbCertEncoded);
               if (!certificate)
                  continue;
               if (X509_STORE_add_cert(store, certificate) == 1)
                  loaded++;
               X509_free(certificate);
            }
            CertCloseStore(systemStore, 0);
         }
         ERR_clear_error();
         return loaded;
#else
         X509_STORE_set_default_paths(store);
         ERR_clear_error();
         return -1;
#endif
      }

      struct ChainVerdict
      {
         bool trusted;
         AnsiString error;
         int depth;
         int roots;
         AnsiString subject;
         AnsiString issuer;
         __int64 notAfter;
      };

      AnsiString NameText(X509 *certificate, bool subject)
      {
         char buffer[512];
         buffer[0] = 0;
         X509_NAME_oneline(subject ? X509_get_subject_name(certificate) : X509_get_issuer_name(certificate), buffer, sizeof(buffer));
         return AnsiString(buffer);
      }

      // The leaf first, its chain after; the verdict OpenSSL gives for the
      // S/MIME purpose against the system's roots.
      ChainVerdict VerifyChain(const std::vector<std::string> &ders, bool forEncryption)
      {
         ChainVerdict verdict = { false, "", 0, 0, "", "", 0 };
         X509 *leaf = nullptr;
         STACK_OF(X509) *untrusted = sk_X509_new_null();
         for (size_t i = 0; i < ders.size(); i++)
         {
            const unsigned char *data = (const unsigned char *) ders[i].data();
            X509 *certificate = d2i_X509(nullptr, &data, (long) ders[i].size());
            if (!certificate)
            {
               verdict.error.Format("certificate %d is not X.509 DER", (int) i + 1);
               if (leaf)
                  X509_free(leaf);
               sk_X509_pop_free(untrusted, X509_free);
               ERR_clear_error();
               return verdict;
            }
            if (!leaf)
               leaf = certificate;
            else
               sk_X509_push(untrusted, certificate);
         }
         if (!leaf)
         {
            verdict.error = "no certificate";
            sk_X509_pop_free(untrusted, X509_free);
            return verdict;
         }

         verdict.subject = NameText(leaf, true);
         verdict.issuer = NameText(leaf, false);
         {
            struct tm expiry;
            memset(&expiry, 0, sizeof(expiry));
            if (ASN1_TIME_to_tm(X509_get0_notAfter(leaf), &expiry) == 1)
            {
#ifdef _WIN32
               verdict.notAfter = (__int64) _mkgmtime(&expiry);
#else
               verdict.notAfter = (__int64) timegm(&expiry);
#endif
            }
         }

         X509_STORE *store = X509_STORE_new();
         verdict.roots = LoadSystemRoots(store);
         X509_STORE_CTX *context = X509_STORE_CTX_new();
         if (X509_STORE_CTX_init(context, store, leaf, untrusted) == 1)
         {
            X509_STORE_CTX_set_purpose(context, forEncryption ? X509_PURPOSE_SMIME_ENCRYPT : X509_PURPOSE_SMIME_SIGN);
            if (X509_verify_cert(context) == 1)
            {
               verdict.trusted = true;
               STACK_OF(X509) *chain = X509_STORE_CTX_get0_chain(context);
               verdict.depth = chain ? sk_X509_num(chain) : 1;
            }
            else
            {
               verdict.error = X509_verify_cert_error_string(X509_STORE_CTX_get_error(context));
               verdict.depth = X509_STORE_CTX_get_error_depth(context);
            }
         }
         else
            verdict.error = "the verification could not be set up";

         X509_STORE_CTX_free(context);
         X509_STORE_free(store);
         sk_X509_pop_free(untrusted, X509_free);
         X509_free(leaf);
         ERR_clear_error();
         return verdict;
      }
   }

   // ---- routes --------------------------------------------------------------

   HttpResponse
   RestApiServer::HandleMeSmime_(const Caller &caller)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      AnsiString json = "{\"own\":[";
      std::vector<KeyRow> own;
      ListRows(account->GetID(), OwnKind, own);
      for (size_t i = 0; i < own.size(); i++)
      {
         if (i > 0)
            json += ",";
         json += RowJson(own[i]);
      }
      json += "],\"recipients\":[";
      std::vector<KeyRow> recipients;
      ListRows(account->GetID(), RecipientKind, recipients);
      for (size_t i = 0; i < recipients.size(); i++)
      {
         if (i > 0)
            json += ",";
         json += RowJson(recipients[i]);
      }
      json += "],\"limits\":{\"own\":" + Int64Text(OwnPerAccount) + ",\"recipients\":" + Int64Text(RecipientsPerAccount) + "}}";
      return BuildResponse_(200, json);
   }

   HttpResponse
   RestApiServer::HandleMeSmimeOwnPut_(const Caller &caller, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      JsonValue document;
      std::string parseError;
      if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.size()), document, parseError) || !document.IsObject())
         return BuildResponse_(400, "{\"error\":\"a JSON object is expected\"}");

      String address, name;
      AnsiString fingerprint, certificate, problem;
      __int64 notAfter = 0;
      if (!CommonFields(document, address, name, fingerprint, certificate, notAfter, problem))
         return BuildResponse_(400, problem);

      AnsiString chain;
      const JsonValue *chainValue = document.Get("chain");
      if (chainValue && !chainValue->IsNull())
      {
         if (!chainValue->IsArray() || chainValue->Size() > (size_t) ChainMaximum)
            return BuildResponse_(400, "{\"error\":\"chain: an array of at most eight base64 DER certificates\"}");
         for (size_t i = 0; i < chainValue->Size(); i++)
         {
            const JsonValue *item = chainValue->At(i);
            if (!item || !item->IsString() || !IsBase64(item->AsString()) || item->AsString().size() > (size_t) CertificateMaximum)
               return BuildResponse_(400, "{\"error\":\"chain: an array of at most eight base64 DER certificates, 16 KB each\"}");
            if (!chain.IsEmpty())
               chain += "\n";
            chain += AnsiString(item->AsString().c_str());
         }
      }

      AnsiString key;
      if (!WrappedKeyJson(document.Get("key"), key, problem))
         return BuildResponse_(400, problem);

      Answer answer = Upsert(account, OwnKind, address, name, fingerprint, certificate, chain, key, notAfter);
      return BuildResponse_(answer.status, answer.body);
   }

   HttpResponse
   RestApiServer::HandleMeSmimeOwnDelete_(const Caller &caller, const AnsiString &fingerprint)
   {
      if (!caller.account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");
      Answer answer = Remove(caller.account, OwnKind, fingerprint);
      return BuildResponse_(answer.status, answer.body);
   }

   HttpResponse
   RestApiServer::HandleMeSmimeRecipientPut_(const Caller &caller, const AnsiString &requestBody)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      JsonValue document;
      std::string parseError;
      if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.size()), document, parseError) || !document.IsObject())
         return BuildResponse_(400, "{\"error\":\"a JSON object is expected\"}");

      String address, name;
      AnsiString fingerprint, certificate, problem;
      __int64 notAfter = 0;
      if (!CommonFields(document, address, name, fingerprint, certificate, notAfter, problem))
         return BuildResponse_(400, problem);

      Answer answer = Upsert(account, RecipientKind, address, name, fingerprint, certificate, "", "", notAfter);
      return BuildResponse_(answer.status, answer.body);
   }

   HttpResponse
   RestApiServer::HandleMeSmimeRecipientDelete_(const Caller &caller, const AnsiString &fingerprint)
   {
      if (!caller.account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");
      Answer answer = Remove(caller.account, RecipientKind, fingerprint);
      return BuildResponse_(answer.status, answer.body);
   }

   // POST /api/v1/me/smime/chain: {"certificates":[leaf, ...chain], "purpose":"sign"|"encrypt"}.
   HttpResponse
   RestApiServer::HandleMeSmimeChain_(const AnsiString &requestBody)
   {
      JsonValue document;
      std::string parseError;
      if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.size()), document, parseError) || !document.IsObject())
         return BuildResponse_(400, "{\"error\":\"a JSON object is expected\"}");

      const JsonValue *list = document.Get("certificates");
      if (!list || !list->IsArray() || list->Size() == 0 || list->Size() > (size_t) ChainMaximum + 1)
         return BuildResponse_(400, "{\"error\":\"certificates: the leaf first, then its chain - base64 DER each, at most nine\"}");

      std::vector<std::string> ders;
      for (size_t i = 0; i < list->Size(); i++)
      {
         const JsonValue *item = list->At(i);
         if (!item || !item->IsString() || !IsBase64(item->AsString()) || item->AsString().size() > (size_t) CertificateMaximum)
         {
            AnsiString json;
            json.Format("{\"error\":\"certificate %d is not base64 DER of at most 16 KB\"}", (int) i + 1);
            return BuildResponse_(400, json);
         }
         AnsiString decoded = Base64::Decode(item->AsString().c_str(), (int) item->AsString().size());
         ders.push_back(std::string(decoded.c_str(), decoded.size()));
      }

      const JsonValue *purpose = document.Get("purpose");
      bool forEncryption = purpose && purpose->IsString() && purpose->AsString() == "encrypt";

      ChainVerdict verdict = VerifyChain(ders, forEncryption);
      AnsiString json;
      json.Format("{\"trusted\":%hs,\"error\":\"%hs\",\"depth\":%d,\"roots\":%d,\"subject\":\"%hs\",\"issuer\":\"%hs\",\"not_after\":%I64d}",
         verdict.trusted ? "true" : "false",
         JsonEscape_(verdict.error).c_str(),
         verdict.depth,
         verdict.roots,
         JsonEscape_(verdict.subject).c_str(),
         JsonEscape_(verdict.issuer).c_str(),
         verdict.notAfter);
      return BuildResponse_(200, json);
   }

   // The page's S/MIME module, joined from its pieces on first use.
   HttpResponse
   RestApiServer::HandlePortalSmimeScript_()
   {
      static AnsiString script;
      static std::once_flag joined;
      std::call_once(joined, []()
      {
         for (const char *const *piece = PortalSmimeScriptPieces; *piece; piece++)
            script += *piece;
      });

      HttpResponse response;
      response.content_type = "text/javascript; charset=utf-8";
      response.body = script;
      response.extra_headers = PortalHeaders;
      return response;
   }

   // Writes the message file for a send whose body the page built: this
   // server's RFC 5322 headers, then the entity as given - its own headers
   // continue the block, its body follows the blank line it carries. The
   // entity is ASCII (the page writes quoted-printable and base64), so its
   // bytes are what was signed. 0 when written; else an HTTP status with
   // problem holding the body.
   int
   RestApiServer::WriteMimeEntity_(std::shared_ptr<const Account> account, const String &fromHeader, const String &toHeader, const String &ccHeader, const AnsiString &requestBody, const String &entityText, const String &fileName, AnsiString &problem)
   {
      if (entityText.GetLength() > EntityMaximum)
      {
         problem = "{\"error\":\"mime: larger than 16 MB\"}";
         return 413;
      }
      for (int i = 0; i < entityText.GetLength(); i++)
      {
         if ((unsigned) entityText[i] > 127)
         {
            problem = "{\"error\":\"mime: must be 7-bit (quoted-printable or base64 parts); a byte above 127 was found\"}";
            return 400;
         }
      }
      AnsiString entity = Utf8_(entityText);
      AnsiString head = entity.Mid(0, 13);
      head.ToLower();
      if (head != "content-type:" || entity.Find("\r\n\r\n") < 0)
      {
         problem = "{\"error\":\"mime: the entity begins with its Content-Type header and carries a blank line before its body\"}";
         return 400;
      }

      MimeHeader header;
      header.SetUnicodeFieldValue("From", fromHeader, "utf-8");
      header.SetUnicodeFieldValue("To", toHeader, "utf-8");
      if (!ccHeader.IsEmpty())
         header.SetUnicodeFieldValue("Cc", ccHeader, "utf-8");
      header.SetUnicodeFieldValue("Subject", JsonUtf8Value_(requestBody, "subject"), "utf-8");
      header.SetRawFieldValue("Date", Utf8_(Time::GetCurrentMimeDate()), "");
      AnsiString me = Utf8_(account->GetAddress());
      AnsiString domain = me.Find("@") >= 0 ? me.Mid(me.Find("@") + 1) : me;
      header.SetRawFieldValue("Message-ID", "<" + Utf8_(GUIDCreator::GetGUID()) + "@" + domain + ">", "");
      String inReplyTo = JsonUtf8Value_(requestBody, "in_reply_to");
      String references = JsonUtf8Value_(requestBody, "references");
      if (!inReplyTo.IsEmpty())
         header.SetUnicodeFieldValue("In-Reply-To", inReplyTo, "");
      if (!references.IsEmpty())
         header.SetUnicodeFieldValue("References", references, "");
      if (GetJsonBoolValue_(requestBody, "receipt", false))
         header.SetUnicodeFieldValue("Disposition-Notification-To", fromHeader, "utf-8");
      header.SetRawFieldValue("MIME-Version", "1.0", "");

      AnsiString block;
      header.Store(block);
      // Store ends the block with its blank line; the entity's headers carry on.
      if (block.EndsWith("\r\n\r\n"))
         block = block.Mid(0, block.GetLength() - 2);

      if (!FileUtilities::WriteToFile(fileName, block + entity))
      {
         problem = "{\"error\":\"the message could not be written\"}";
         return 500;
      }
      return 0;
   }
}
