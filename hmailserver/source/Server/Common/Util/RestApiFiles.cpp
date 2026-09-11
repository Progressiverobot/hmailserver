// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Large attachments as links. Above a size the domain sets, the webmail does
// not attach a file to the message: it sends the file here in chunks (POST
// /api/v1/me/files makes the record, PUT /api/v1/me/files/{id}/content?offset=
// appends the bytes) and the message carries a link to /files/{token}, which
// anyone can fetch - with a password first, when the sender set one. The
// sender sees every file with its downloads and expiry (GET /api/v1/me/files),
// re-times or protects it (PUT /api/v1/me/files/{id}) and removes it (DELETE);
// the scheduled task removes what has expired and what was never finished.
// The bytes live under <data directory>/Files/<token>; the record, in
// hm_files. The policy - the size above which a file becomes a link, the
// days a link lives, the most a file may be, the most an account may keep -
// is in the [Settings] store, server-wide with a ".<domain>" twin, set by an
// administrator (GET/PUT /api/v1/portal/files).

#include "StdAfx.h"
#include "RestApiServer.h"
#include "HttpServer.h"
#include "JsonDocument.h"
#include "Unicode.h"
#include "FileUtilities.h"
#include "FileInfo.h"
#include "Hashing/HashCreator.h"
#include "../Application/IniFileSettings.h"
#include "../Application/Application.h"
#include "../Application/Logger.h"
#include "../BO/Account.h"
#include "../SQL/SQLCommand.h"
#include "../SQL/SQLStatement.h"
#include "../SQL/DALRecordset.h"
#include <openssl/rand.h>
#include <openssl/crypto.h>
#include <algorithm>
#include <cstdlib>
#include <ctime>
#include <fstream>
#include <iterator>
#include <map>
#include <mutex>
#include <set>
#include <string>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int TokenBytes = 32;
      const int NameMaximum = 200;
      const int PasswordMaximum = 100;
      const int DaysMaximum = 90;
      const int FilesPerAccount = 200;
      const int HardMaximumMB = 500;
      const int DefaultAboveKB = 8192;
      const int DefaultDays = 14;
      const int DefaultMaximumMB = 100;
      const int DefaultQuotaMB = 1024;
      const __int64 UnfinishedSeconds = 24 * 60 * 60;
      const int WrongPasswordsBeforePause = 10;
      const ULONGLONG PauseMilliseconds = 15 * 60 * 1000;

      const char *Columns = "fileid, fileaccountid, filetoken, filename, filetype, filesize, filestored, filecomplete, filecreated, fileexpires, filepasswordhash, filedownloads";

      // One chunk is appended at a time, whatever two requests race for.
      std::mutex files_mutex;

      // Wrong passwords per file: after ten, the file answers nothing but a
      // pause for a quarter of an hour.
      struct Guesses
      {
         int wrong = 0;
         ULONGLONG since = 0;
      };
      std::mutex guesses_mutex;
      std::map<AnsiString, Guesses> guesses;

      __int64 Now()
      {
         return (__int64) time(nullptr);
      }

      String FilesDirectory()
      {
         return FileUtilities::Combine(IniFileSettings::Instance()->GetDataDirectory(), _T("Files"));
      }

      String PathOf(const AnsiString &token)
      {
         return FileUtilities::Combine(FilesDirectory(), String(token));
      }

      AnsiString LowerHex(const unsigned char *data, int length)
      {
         static const char *digits = "0123456789abcdef";
         AnsiString result;
         for (int i = 0; i < length; i++)
         {
            result += digits[data[i] >> 4];
            result += digits[data[i] & 15];
         }
         return result;
      }

      bool IsToken(const AnsiString &value)
      {
         if (value.GetLength() != TokenBytes * 2)
            return false;
         for (int i = 0; i < value.GetLength(); i++)
         {
            char c = value[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok)
               return false;
         }
         return true;
      }

      AnsiString HashOf(const AnsiString &token, const String &password)
      {
         AnsiString utf8;
         Unicode::WideToMultiByte(password, utf8);
         HashCreator hasher(HashCreator::SHA256);
         return hasher.GenerateHashNoSalt(token + ":" + utf8, HashCreator::hex);
      }

      bool SameBytes(const AnsiString &left, const AnsiString &right)
      {
         if (left.GetLength() != right.GetLength() || left.IsEmpty())
            return false;
         return CRYPTO_memcmp(left.c_str(), right.c_str(), (size_t) left.GetLength()) == 0;
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

      AnsiString HtmlEscape(const AnsiString &value)
      {
         AnsiString out;
         for (int i = 0; i < value.GetLength(); i++)
         {
            char c = value[i];
            if (c == '&') out += "&amp;";
            else if (c == '<') out += "&lt;";
            else if (c == '>') out += "&gt;";
            else if (c == '"') out += "&quot;";
            else if (c == '\'') out += "&#39;";
            else out += c;
         }
         return out;
      }

      AnsiString Utf8(const String &value)
      {
         AnsiString out;
         Unicode::WideToMultiByte(value, out);
         return out;
      }

      String DomainOf(const String &address)
      {
         int at = address.Find(_T("@"));
         String domain = at >= 0 ? address.Mid(at + 1) : String();
         domain.ToLower();
         return domain;
      }

      bool ValidDomain(const String &domain)
      {
         if (domain.IsEmpty() || domain.GetLength() > 253)
            return false;
         for (int i = 0; i < domain.GetLength(); i++)
         {
            wchar_t c = domain[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.';
            if (!ok)
               return false;
         }
         return true;
      }

      // A file name that is only a name: no path, no control characters, no
      // quotes, at most two hundred characters, never empty.
      String SafeName(const String &given)
      {
         String name;
         for (int i = 0; i < given.GetLength(); i++)
         {
            wchar_t c = given[i];
            if (c < 0x20 || c == 0x7F || c == '/' || c == '\\' || c == '"' || c == ':' || c == '*' || c == '?' || c == '<' || c == '>' || c == '|')
               continue;
            name += c;
         }
         name.TrimLeft();
         name.TrimRight();
         while (!name.IsEmpty() && name[0] == '.')
            name = name.Mid(1);
         if (name.GetLength() > NameMaximum)
            name = name.Mid(0, NameMaximum);
         if (name.IsEmpty())
            name = _T("file");
         return name;
      }

      // The declared type, if it looks like one; and never a type a browser
      // would render or run.
      AnsiString SafeType(const AnsiString &declared)
      {
         AnsiString type = declared;
         type.TrimLeft();
         type.TrimRight();
         type.ToLower();
         if (type.IsEmpty() || type.GetLength() > 100 || type.Find("/") < 0)
            return "application/octet-stream";
         for (int i = 0; i < type.GetLength(); i++)
         {
            char c = type[i];
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '/' || c == '-' || c == '+' || c == '.';
            if (!ok)
               return "application/octet-stream";
         }
         if (type == "text/html" || type == "application/xhtml+xml" || type == "image/svg+xml" ||
             type == "text/xml" || type == "application/xml" || type.EndsWith("+xml") ||
             type == "text/javascript" || type == "application/javascript")
            return "application/octet-stream";
         return type;
      }

      AnsiString AsciiName(const String &value)
      {
         AnsiString ascii;
         for (int i = 0; i < value.GetLength(); i++)
         {
            wchar_t c = value[i];
            bool safe = c >= 0x20 && c < 0x7F && c != '"' && c != '\\';
            ascii += safe ? (char) c : '_';
         }
         if (ascii.IsEmpty())
            ascii = "file";
         return ascii;
      }

      AnsiString PercentEncode(const String &value)
      {
         AnsiString utf8 = Utf8(value);
         static const char *hex = "0123456789ABCDEF";
         AnsiString encoded;
         for (int i = 0; i < utf8.GetLength(); i++)
         {
            unsigned char c = (unsigned char) utf8[i];
            bool plain = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_' || c == '~';
            if (plain)
            {
               encoded += (char) c;
               continue;
            }
            encoded += '%';
            encoded += hex[c >> 4];
            encoded += hex[c & 15];
         }
         return encoded;
      }

      AnsiString Int64Text(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

      // ---- the policy -----------------------------------------------------

      struct Policy
      {
         int above_kb;
         int days;
         int maximum_mb;
         int quota_mb;
      };

      int PolicyNumber(const String &key, const String &domain, int fallback, int low, int high)
      {
         String value;
         if (!domain.IsEmpty())
            value = IniFileSettings::Instance()->GetSettingsValue(key + _T(".") + domain);
         if (value.IsEmpty())
            value = IniFileSettings::Instance()->GetSettingsValue(key);
         if (value.IsEmpty())
            return fallback;
         int number = _ttoi(value.c_str());
         if (number < low)
            return low;
         if (number > high)
            return high;
         return number;
      }

      Policy PolicyFor(const String &domain)
      {
         Policy policy;
         policy.above_kb = PolicyNumber(_T("WebmailLinkAboveKB"), domain, DefaultAboveKB, 0, 1024 * 1024);
         policy.days = PolicyNumber(_T("WebmailLinkDays"), domain, DefaultDays, 1, DaysMaximum);
         policy.maximum_mb = PolicyNumber(_T("WebmailLinkMaxMB"), domain, DefaultMaximumMB, 1, HardMaximumMB);
         policy.quota_mb = PolicyNumber(_T("WebmailLinkQuotaMB"), domain, DefaultQuotaMB, 1, 100 * 1024);
         return policy;
      }

      AnsiString PolicyJson(const Policy &policy, const String &domain)
      {
         AnsiString json;
         json.Format("{\"link_above_kb\":%d,\"days\":%d,\"max_days\":%d,\"max_mb\":%d,\"quota_mb\":%d,\"domain\":\"%hs\"}",
            policy.above_kb, policy.days, DaysMaximum, policy.maximum_mb, policy.quota_mb, Escape(Utf8(domain)).c_str());
         return json;
      }

      // ---- the rows -------------------------------------------------------

      struct FileRow
      {
         __int64 id = 0;
         __int64 accountId = 0;
         AnsiString token;
         String name;
         AnsiString type;
         __int64 size = 0;
         __int64 stored = 0;
         bool complete = false;
         __int64 created = 0;
         __int64 expires = 0;
         AnsiString passwordHash;
         int downloads = 0;
      };

      void ReadRow(std::shared_ptr<DALRecordset> recordset, FileRow &row)
      {
         row.id = recordset->GetInt64Value("fileid");
         row.accountId = recordset->GetInt64Value("fileaccountid");
         row.token = AnsiString(recordset->GetStringValue("filetoken"));
         row.name = recordset->GetStringValue("filename");
         row.type = AnsiString(recordset->GetStringValue("filetype"));
         row.size = recordset->GetInt64Value("filesize");
         row.stored = recordset->GetInt64Value("filestored");
         row.complete = recordset->GetLongValue("filecomplete") != 0;
         row.created = recordset->GetInt64Value("filecreated");
         row.expires = recordset->GetInt64Value("fileexpires");
         row.passwordHash = AnsiString(recordset->GetStringValue("filepasswordhash"));
         row.downloads = (int) recordset->GetLongValue("filedownloads");
      }

      bool FindById(__int64 accountId, __int64 id, FileRow &row)
      {
         SQLCommand command(String(_T("select ")) + String(Columns) + _T(" from hm_files where fileid = @ID and fileaccountid = @ACCOUNTID"));
         command.AddParameter("@ID", id);
         command.AddParameter("@ACCOUNTID", accountId);
         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset || recordset->IsEOF())
            return false;
         ReadRow(recordset, row);
         return true;
      }

      bool FindByToken(const AnsiString &token, FileRow &row)
      {
         SQLCommand command(String(_T("select ")) + String(Columns) + _T(" from hm_files where filetoken = @TOKEN"));
         command.AddParameter("@TOKEN", String(token));
         std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
         if (!recordset || recordset->IsEOF())
            return false;
         ReadRow(recordset, row);
         return true;
      }

      void RemoveRow(__int64 id)
      {
         SQLCommand command("delete from hm_files where fileid = @ID");
         command.AddParameter("@ID", id);
         Application::Instance()->GetDBManager()->Execute(command);
      }

      AnsiString RowJson(const FileRow &row, __int64 now)
      {
         AnsiString json;
         json.Format("{\"id\":%I64d,\"token\":\"%hs\",\"name\":\"%hs\",\"type\":\"%hs\",\"size\":%I64d,\"stored\":%I64d,\"complete\":%hs,\"created\":%I64d,\"expires\":%I64d,\"expired\":%hs,\"downloads\":%d,\"protected\":%hs,\"link\":\"/files/%hs\"}",
            row.id, row.token.c_str(), Escape(Utf8(row.name)).c_str(), Escape(row.type).c_str(), row.size, row.stored,
            row.complete ? "true" : "false", row.created, row.expires, row.expires <= now ? "true" : "false",
            row.downloads, row.passwordHash.IsEmpty() ? "false" : "true", row.token.c_str());
         return json;
      }

      // ---- the public page ------------------------------------------------

      HttpResponse Page(int status, const AnsiString &title, const AnsiString &inner)
      {
         HttpResponse response;
         response.status = status;
         response.content_type = "text/html; charset=utf-8";
         response.body =
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>" + HtmlEscape(title) + "</title>"
            "<style>body{font-family:system-ui,sans-serif;background:#0f1419;color:#e6edf3;display:flex;justify-content:center;padding:48px 16px}main{max-width:420px;width:100%;background:#161b22;border:1px solid #30363d;border-radius:12px;padding:24px}h1{font-size:18px;margin:0 0 12px}p{margin:8px 0;color:#9aa4ae;font-size:14px}input{width:100%;box-sizing:border-box;padding:10px;border-radius:8px;border:1px solid #30363d;background:#0f1419;color:#e6edf3;font-size:15px;margin:8px 0}button{padding:10px 16px;border-radius:8px;border:0;background:#36c2ff;color:#04121c;font-weight:600;font-size:15px;cursor:pointer}.err{color:#ff7b72}</style></head>"
            "<body><main>" + inner + "</main></body></html>";
         response.extra_headers =
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'\r\n"
            "X-Frame-Options: DENY\r\n"
            "X-Content-Type-Options: nosniff\r\n"
            "Referrer-Policy: no-referrer\r\n"
            "X-Robots-Tag: noindex\r\n"
            "Cache-Control: no-store\r\n";
         return response;
      }

      HttpResponse PasswordPage(const AnsiString &token, const FileRow &row, int status, bool wrong)
      {
         AnsiString name = HtmlEscape(Utf8(row.name));
         AnsiString size = Int64Text(row.size);
         return Page(status, "A file was shared with you",
            "<h1>A file was shared with you</h1><p>" + name + " (" + size + " bytes). The sender set a password on it.</p>"
            + (wrong ? AnsiString("<p class=\"err\">That password is not right.</p>") : AnsiString())
            + "<form method=\"post\" action=\"/files/" + token + "\"><input type=\"password\" name=\"password\" autocomplete=\"off\" autofocus required><button type=\"submit\">Download</button></form>");
      }

      bool Paused(const AnsiString &token)
      {
         std::lock_guard<std::mutex> guard(guesses_mutex);
         std::map<AnsiString, Guesses>::iterator it = guesses.find(token);
         if (it == guesses.end())
            return false;
         const ULONGLONG now = GetTickCount64();
         if (now - it->second.since > PauseMilliseconds)
         {
            guesses.erase(it);
            return false;
         }
         return it->second.wrong >= WrongPasswordsBeforePause;
      }

      void Wrong(const AnsiString &token)
      {
         std::lock_guard<std::mutex> guard(guesses_mutex);
         Guesses &g = guesses[token];
         const ULONGLONG now = GetTickCount64();
         if (g.wrong == 0 || now - g.since > PauseMilliseconds)
         {
            g.wrong = 0;
            g.since = now;
         }
         g.wrong++;
      }

      void Right(const AnsiString &token)
      {
         std::lock_guard<std::mutex> guard(guesses_mutex);
         guesses.erase(token);
      }
   }

   // The account's files: the policy it lives under, what it keeps, and how
   // much of its allowance that is.
   HttpResponse
   RestApiServer::HandleMeFiles_(const Caller &caller)
   {
      if (!caller.account)
         return BuildResponse_(403, "{\"error\":\"an account's route\"}");
      const String domain = DomainOf(caller.account->GetAddress());
      const Policy policy = PolicyFor(domain);
      const __int64 now = Now();

      SQLCommand command(String(_T("select ")) + String(Columns) + _T(" from hm_files where fileaccountid = @ACCOUNTID order by filecreated desc, fileid desc"));
      command.AddParameter("@ACCOUNTID", caller.account->GetID());
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      AnsiString files;
      __int64 used = 0;
      int count = 0;
      while (recordset && !recordset->IsEOF())
      {
         FileRow row;
         ReadRow(recordset, row);
         used += row.stored;
         if (!files.IsEmpty())
            files += ",";
         files += RowJson(row, now);
         count++;
         recordset->MoveNext();
      }

      AnsiString json;
      json.Format("{\"policy\":%hs,\"used_bytes\":%I64d,\"count\":%d,\"files\":[%hs]}", PolicyJson(policy, domain).c_str(), used, count, files.c_str());
      return BuildResponse_(200, json);
   }

   // The record of a file to come: name, type, size, and how long the link
   // lives (days, the domain's default when absent, 0 for a link that is dead
   // at once) and a password when the sender wants one. The bytes follow in
   // chunks. Refused when the file is larger than the domain allows or the
   // account's allowance is used up.
   HttpResponse
   RestApiServer::HandleMeFileCreate_(const Caller &caller, const AnsiString &requestBody)
   {
      if (!caller.account)
         return BuildResponse_(403, "{\"error\":\"an account's route\"}");
      JsonValue document;
      std::string parseError;
      if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.size()), document, parseError) || !document.IsObject())
         return BuildResponse_(400, "{\"error\":\"the body must be a JSON object: name, type, size, and days or password when wanted\"}");

      const String domain = DomainOf(caller.account->GetAddress());
      const Policy policy = PolicyFor(domain);

      String name;
      Unicode::MultiByteToWide(AnsiString(document.GetString("name").c_str()), name);
      name = SafeName(name);
      const AnsiString type = SafeType(AnsiString(document.GetString("type").c_str()));

      const JsonValue *sizeValue = document.Get("size");
      if (!sizeValue || !sizeValue->IsNumber() || sizeValue->AsNumber() < 1)
         return BuildResponse_(400, "{\"error\":\"size is the file's length in bytes\"}");
      const __int64 size = (__int64) sizeValue->AsNumber();
      if (size > (__int64) policy.maximum_mb * 1024 * 1024)
      {
         AnsiString problem;
         problem.Format("{\"error\":\"a file sent as a link is at most %d MB here\"}", policy.maximum_mb);
         return BuildResponse_(413, problem);
      }

      int days = policy.days;
      const JsonValue *daysValue = document.Get("days");
      if (daysValue && !daysValue->IsNull())
      {
         if (!daysValue->IsNumber() || daysValue->AsNumber() < 0 || daysValue->AsNumber() > DaysMaximum)
         {
            AnsiString problem;
            problem.Format("{\"error\":\"days is 0 to %d\"}", DaysMaximum);
            return BuildResponse_(400, problem);
         }
         days = (int) daysValue->AsNumber();
      }

      String password;
      Unicode::MultiByteToWide(AnsiString(document.GetString("password").c_str()), password);
      if (password.GetLength() > PasswordMaximum)
         return BuildResponse_(400, "{\"error\":\"the password is at most a hundred characters\"}");

      // What the account already keeps, finished or not, counts against it.
      SQLCommand sums("select filesize from hm_files where fileaccountid = @ACCOUNTID");
      sums.AddParameter("@ACCOUNTID", caller.account->GetID());
      std::shared_ptr<DALRecordset> kept = Application::Instance()->GetDBManager()->OpenRecordset(sums);
      __int64 used = 0;
      int count = 0;
      while (kept && !kept->IsEOF())
      {
         used += kept->GetInt64Value("filesize");
         count++;
         kept->MoveNext();
      }
      if (count >= FilesPerAccount)
      {
         AnsiString problem;
         problem.Format("{\"error\":\"at most %d files at a time; remove some first\"}", FilesPerAccount);
         return BuildResponse_(400, problem);
      }
      if (used + size > (__int64) policy.quota_mb * 1024 * 1024)
      {
         AnsiString problem;
         problem.Format("{\"error\":\"the account's %d MB for files sent as links is used up; remove some first\"}", policy.quota_mb);
         return BuildResponse_(413, problem);
      }

      unsigned char secret[TokenBytes];
      if (RAND_bytes(secret, sizeof(secret)) != 1)
         return BuildResponse_(500, "{\"error\":\"no entropy for a link\"}");
      const AnsiString token = LowerHex(secret, TokenBytes);

      const String directory = FilesDirectory();
      if (!FileUtilities::DirectoryExists(directory) && !FileUtilities::CreateDirectory(directory))
         return BuildResponse_(500, "{\"error\":\"the files directory could not be made\"}");

      const __int64 now = Now();
      SQLStatement statement;
      statement.SetTable("hm_files");
      statement.SetStatementType(SQLStatement::STInsert);
      statement.SetIdentityColumn("fileid");
      statement.AddColumnInt64("fileaccountid", caller.account->GetID());
      statement.AddColumn("filetoken", String(token));
      statement.AddColumn("filename", name);
      statement.AddColumn("filetype", String(type));
      statement.AddColumnInt64("filesize", size);
      statement.AddColumnInt64("filestored", 0);
      statement.AddColumn("filecomplete", (long) 0);
      statement.AddColumnInt64("filecreated", now);
      statement.AddColumnInt64("fileexpires", now + (__int64) days * 24 * 60 * 60);
      statement.AddColumn("filepasswordhash", password.IsEmpty() ? String() : String(HashOf(token, password)));
      statement.AddColumn("filedownloads", (long) 0);
      __int64 id = 0;
      if (!Application::Instance()->GetDBManager()->Execute(statement, &id) || id == 0)
         return BuildResponse_(500, "{\"error\":\"the file could not be recorded\"}");

      FileRow row;
      if (!FindById(caller.account->GetID(), id, row))
         return BuildResponse_(500, "{\"error\":\"the file could not be read back\"}");
      return BuildResponse_(201, RowJson(row, now));
   }

   // One chunk of the bytes, raw, appended at the offset the record has
   // reached - so a chunk lost on the way is sent again and a chunk sent
   // twice is refused, and the record is complete when the size declared is
   // reached.
   HttpResponse
   RestApiServer::HandleMeFileContent_(const Caller &caller, __int64 id, const AnsiString &query, const AnsiString &body)
   {
      if (!caller.account)
         return BuildResponse_(403, "{\"error\":\"an account's route\"}");
      if (body.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"the body is the chunk's bytes\"}");
      const AnsiString offsetText = QueryParameter_(query, "offset");
      const __int64 offset = offsetText.IsEmpty() ? 0 : (__int64) strtoll(offsetText.c_str(), nullptr, 10);

      std::lock_guard<std::mutex> guard(files_mutex);
      FileRow row;
      if (!FindById(caller.account->GetID(), id, row))
         return BuildResponse_(404, "{\"error\":\"no such file of this account\"}");
      if (row.complete)
         return BuildResponse_(409, "{\"error\":\"the file is complete\"}");
      if (offset != row.stored)
      {
         AnsiString problem;
         problem.Format("{\"error\":\"the next chunk starts at %I64d\",\"stored\":%I64d}", row.stored, row.stored);
         return BuildResponse_(409, problem);
      }
      if (row.stored + body.GetLength() > row.size)
         return BuildResponse_(413, "{\"error\":\"more bytes than the size that was declared\"}");

      const String path = PathOf(row.token);
      {
         std::ofstream out(path.c_str(), std::ios::binary | std::ios::app);
         if (!out)
            return BuildResponse_(500, "{\"error\":\"the file could not be written\"}");
         out.write(body.c_str(), body.GetLength());
         out.close();
         if (out.fail())
            return BuildResponse_(500, "{\"error\":\"the file could not be written\"}");
      }

      const __int64 stored = row.stored + body.GetLength();
      const bool complete = stored == row.size;
      SQLStatement update;
      update.SetTable("hm_files");
      update.SetStatementType(SQLStatement::STUpdate);
      update.AddColumnInt64("filestored", stored);
      update.AddColumn("filecomplete", (long) (complete ? 1 : 0));
      update.SetWhereClause(String(_T("fileid = ")) + String(Int64Text(row.id)));
      if (!Application::Instance()->GetDBManager()->Execute(update))
         return BuildResponse_(500, "{\"error\":\"the file could not be recorded\"}");

      AnsiString json;
      json.Format("{\"id\":%I64d,\"stored\":%I64d,\"size\":%I64d,\"complete\":%hs,\"link\":\"/files/%hs\"}", row.id, stored, row.size, complete ? "true" : "false", row.token.c_str());
      return BuildResponse_(200, json);
   }

   // A new life for the link (days from now; 0 ends it) and a password set,
   // changed or removed (an empty one).
   HttpResponse
   RestApiServer::HandleMeFileUpdate_(const Caller &caller, __int64 id, const AnsiString &requestBody)
   {
      if (!caller.account)
         return BuildResponse_(403, "{\"error\":\"an account's route\"}");
      JsonValue document;
      std::string parseError;
      if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.size()), document, parseError) || !document.IsObject())
         return BuildResponse_(400, "{\"error\":\"the body must be a JSON object: days and/or password\"}");
      FileRow row;
      if (!FindById(caller.account->GetID(), id, row))
         return BuildResponse_(404, "{\"error\":\"no such file of this account\"}");

      SQLStatement update;
      update.SetTable("hm_files");
      update.SetStatementType(SQLStatement::STUpdate);
      bool changed = false;
      const __int64 now = Now();

      const JsonValue *daysValue = document.Get("days");
      if (daysValue && !daysValue->IsNull())
      {
         if (!daysValue->IsNumber() || daysValue->AsNumber() < 0 || daysValue->AsNumber() > DaysMaximum)
         {
            AnsiString problem;
            problem.Format("{\"error\":\"days is 0 to %d\"}", DaysMaximum);
            return BuildResponse_(400, problem);
         }
         row.expires = now + (__int64) daysValue->AsNumber() * 24 * 60 * 60;
         update.AddColumnInt64("fileexpires", row.expires);
         changed = true;
      }

      const JsonValue *passwordValue = document.Get("password");
      if (passwordValue && !passwordValue->IsNull())
      {
         if (!passwordValue->IsString())
            return BuildResponse_(400, "{\"error\":\"password is a string; empty removes it\"}");
         String password;
         Unicode::MultiByteToWide(AnsiString(passwordValue->AsString().c_str()), password);
         if (password.GetLength() > PasswordMaximum)
            return BuildResponse_(400, "{\"error\":\"the password is at most a hundred characters\"}");
         row.passwordHash = password.IsEmpty() ? AnsiString() : HashOf(row.token, password);
         update.AddColumn("filepasswordhash", String(row.passwordHash));
         changed = true;
      }

      if (changed)
      {
         update.SetWhereClause(String(_T("fileid = ")) + String(Int64Text(row.id)));
         if (!Application::Instance()->GetDBManager()->Execute(update))
            return BuildResponse_(500, "{\"error\":\"the file could not be recorded\"}");
      }
      return BuildResponse_(200, RowJson(row, now));
   }

   HttpResponse
   RestApiServer::HandleMeFileDelete_(const Caller &caller, __int64 id)
   {
      if (!caller.account)
         return BuildResponse_(403, "{\"error\":\"an account's route\"}");
      FileRow row;
      if (!FindById(caller.account->GetID(), id, row))
         return BuildResponse_(404, "{\"error\":\"no such file of this account\"}");
      std::lock_guard<std::mutex> guard(files_mutex);
      const String path = PathOf(row.token);
      if (FileUtilities::Exists(path))
         FileUtilities::DeleteFile(path);
      RemoveRow(row.id);
      return BuildResponse_(200, "{\"removed\":true}");
   }

   // The link itself: anyone who has it. A password, when one was set, is
   // asked for on a page of its own and checked in constant time, ten wrong
   // ones pausing the file for a quarter of an hour. The bytes go out as a
   // download under a type a browser will not render, with no caching, and
   // the download is counted for the sender.
   HttpResponse
   RestApiServer::HandlePublicFile_(const AnsiString &method, const AnsiString &token, const AnsiString &body)
   {
      if (!IsToken(token))
         return Page(404, "No such file", "<h1>No such file</h1><p>There is no file at this address, or it has been removed.</p>");
      FileRow row;
      if (!FindByToken(token, row) || !row.complete)
         return Page(404, "No such file", "<h1>No such file</h1><p>There is no file at this address, or it has been removed.</p>");
      if (row.expires <= Now())
         return Page(410, "This link has expired", "<h1>This link has expired</h1><p>The sender can send the file again.</p>");

      if (!row.passwordHash.IsEmpty())
      {
         if (method != "POST")
            return PasswordPage(token, row, 200, false);
         if (Paused(token))
            return Page(429, "Too many tries", "<h1>Too many tries</h1><p>Wait a quarter of an hour and try again.</p>");
         String password;
         Unicode::MultiByteToWide(QueryParameter_(body, "password"), password);
         if (!SameBytes(HashOf(token, password), row.passwordHash))
         {
            Wrong(token);
            return PasswordPage(token, row, 403, true);
         }
         Right(token);
      }
      else if (method != "GET")
      {
         return BuildResponse_(405, "{\"error\":\"GET fetches the file\"}");
      }

      std::ifstream in(PathOf(token).c_str(), std::ios::binary);
      if (!in)
         return Page(404, "No such file", "<h1>No such file</h1><p>There is no file at this address, or it has been removed.</p>");
      std::string bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());

      SQLCommand counted("update hm_files set filedownloads = filedownloads + 1 where fileid = @ID");
      counted.AddParameter("@ID", row.id);
      Application::Instance()->GetDBManager()->Execute(counted);

      HttpResponse response;
      response.status = 200;
      response.content_type = SafeType(row.type);
      response.body = AnsiString(bytes);
      response.extra_headers =
         "Content-Disposition: attachment; filename=\"" + AsciiName(row.name) + "\"; filename*=UTF-8''" + PercentEncode(row.name) + "\r\n"
         "X-Content-Type-Options: nosniff\r\n"
         "Content-Security-Policy: sandbox\r\n"
         "X-Robots-Tag: noindex\r\n"
         "Cache-Control: no-store\r\n";
      return response;
   }

   HttpResponse
   RestApiServer::HandlePortalFilesPolicy_(const AnsiString &query)
   {
      String domain;
      Unicode::MultiByteToWide(QueryParameter_(query, "domain"), domain);
      domain.ToLower();
      if (!domain.IsEmpty() && !ValidDomain(domain))
         return BuildResponse_(400, "{\"error\":\"domain is a domain name\"}");
      return BuildResponse_(200, PolicyJson(PolicyFor(domain), domain));
   }

   // Each of link_above_kb, days, max_mb and quota_mb: a number writes it,
   // null removes it (the server's value then applies to the domain, the
   // built-in default to the server), absent leaves it. With domain, the
   // domain's own; without, the server's.
   HttpResponse
   RestApiServer::HandlePortalFilesPolicyPut_(const AnsiString &requestBody)
   {
      JsonValue document;
      std::string parseError;
      if (!JsonValue::Parse(std::string(requestBody.c_str(), requestBody.size()), document, parseError) || !document.IsObject())
         return BuildResponse_(400, "{\"error\":\"the body must be a JSON object: link_above_kb, days, max_mb, quota_mb, and domain for a domain's own\"}");
      String domain;
      Unicode::MultiByteToWide(AnsiString(document.GetString("domain").c_str()), domain);
      domain.TrimLeft();
      domain.TrimRight();
      domain.ToLower();
      if (!domain.IsEmpty() && !ValidDomain(domain))
         return BuildResponse_(400, "{\"error\":\"domain is a domain name\"}");

      struct Field
      {
         const char *member;
         const wchar_t *key;
         int low;
         int high;
      };
      const Field fields[] = {
         { "link_above_kb", L"WebmailLinkAboveKB", 0, 1024 * 1024 },
         { "days", L"WebmailLinkDays", 1, DaysMaximum },
         { "max_mb", L"WebmailLinkMaxMB", 1, HardMaximumMB },
         { "quota_mb", L"WebmailLinkQuotaMB", 1, 100 * 1024 },
      };
      for (size_t i = 0; i < sizeof(fields) / sizeof(fields[0]); i++)
      {
         const JsonValue *value = document.Get(fields[i].member);
         if (!value)
            continue;
         String key = String(fields[i].key) + (domain.IsEmpty() ? String() : _T(".") + domain);
         if (value->IsNull())
         {
            IniFileSettings::Instance()->RemoveSettingsValue(key);
            continue;
         }
         if (!value->IsNumber() || value->AsNumber() < fields[i].low || value->AsNumber() > fields[i].high)
         {
            AnsiString problem;
            problem.Format("{\"error\":\"%hs is a number from %d to %d, or null to remove it\"}", fields[i].member, fields[i].low, fields[i].high);
            return BuildResponse_(400, problem);
         }
         String text;
         text.Format(_T("%d"), (int) value->AsNumber());
         if (!IniFileSettings::Instance()->WriteSettingsValue(key, text))
            return BuildResponse_(500, "{\"error\":\"the setting could not be stored\"}");
      }
      return BuildResponse_(200, PolicyJson(PolicyFor(domain), domain));
   }

   // Every minute, from the scheduled task: what has expired goes, what was
   // never finished goes after a day, and bytes whose record is gone (an
   // account deleted takes its rows with it, not its files) go too.
   int
   RestApiServer::SweepExpiredFiles()
   {
      const __int64 now = Now();
      std::vector<std::pair<__int64, AnsiString> > gone;
      std::set<AnsiString> live;

      SQLCommand command("select fileid, filetoken, filecomplete, filecreated, fileexpires from hm_files");
      std::shared_ptr<DALRecordset> recordset = Application::Instance()->GetDBManager()->OpenRecordset(command);
      while (recordset && !recordset->IsEOF())
      {
         const __int64 id = recordset->GetInt64Value("fileid");
         const AnsiString token = AnsiString(recordset->GetStringValue("filetoken"));
         const bool complete = recordset->GetLongValue("filecomplete") != 0;
         const __int64 created = recordset->GetInt64Value("filecreated");
         const __int64 expires = recordset->GetInt64Value("fileexpires");
         if (expires <= now || (!complete && created + UnfinishedSeconds <= now))
            gone.push_back(std::make_pair(id, token));
         else
            live.insert(token);
         recordset->MoveNext();
      }

      int removed = 0;
      {
         std::lock_guard<std::mutex> guard(files_mutex);
         for (size_t i = 0; i < gone.size(); i++)
         {
            const String path = PathOf(gone[i].second);
            if (FileUtilities::Exists(path))
               FileUtilities::DeleteFile(path);
            RemoveRow(gone[i].first);
            removed++;
         }

         const String directory = FilesDirectory();
         if (FileUtilities::DirectoryExists(directory))
         {
            std::vector<FileInfo> files = FileUtilities::GetFilesInDirectory(directory, _T(""));
            for (size_t i = 0; i < files.size(); i++)
            {
               const AnsiString name = AnsiString(files[i].GetName());
               if (IsToken(name) && live.find(name) == live.end() && std::find_if(gone.begin(), gone.end(), [&](const std::pair<__int64, AnsiString> &g) { return g.second == name; }) == gone.end())
               {
                  FileUtilities::DeleteFile(FileUtilities::Combine(directory, String(name)));
                  removed++;
               }
            }
         }
      }

      if (removed > 0)
      {
         String message;
         message.Format(_T("Files sent as links: %d removed (expired, unfinished, or without a record)."), removed);
         LOG_APPLICATION(message);
      }
      return removed;
   }
}
