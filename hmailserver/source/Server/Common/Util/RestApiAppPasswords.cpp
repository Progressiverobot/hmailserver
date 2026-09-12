// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The signed-in account's app passwords: GET and POST /api/v1/me/app-passwords,
// DELETE /api/v1/me/app-passwords/{id}. The same store the Control Panel and
// COM administer (hm_apppasswords, AppPassword::SetPassword, the account's
// preferred hash), reached by the account itself for its own mailbox: a
// password for a phone or a program that is not the account's own, revocable
// on its own. The clear text exists in the answer to the POST and nowhere else.
//
// Minting a credential is proven by the person, not by the request: the POST
// carries the account's own password (and the one-time code when a second
// factor is enrolled), checked here against the account and never against
// an app password - and neither the POST nor the DELETE is reachable on a
// request that was itself authenticated with an app password, or by a
// session one started. Otherwise a leaked phone password could mint its own
// replacement and revoke the others before the owner did, and a stolen
// cookie would turn a session with a ceiling into a mailbox credential
// without one. Both are logged with the caller's address.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "HttpServer.h"
#include "Time.h"
#include "Totp.h"
#include "PasswordValidator.h"
#include "../Application/Logger.h"
#include "../BO/Account.h"
#include "../BO/AppPassword.h"
#include "../BO/AppPasswords.h"
#include "../Persistence/PersistentAppPassword.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const int AppPasswordsPerAccount = 20;

      AnsiString Int64Text(__int64 value)
      {
         AnsiString text;
         text.Format("%I64d", value);
         return text;
      }

   }

   AnsiString
   RestApiServer::AppPasswordJson_(std::shared_ptr<AppPassword> password, const String &clearText)
   {
      AnsiString json = "{\"id\":" + Int64Text(password->GetID()) +
         ",\"name\":\"" + JsonEscape_(Utf8_(password->GetName())) +
         "\",\"created\":\"" + JsonEscape_(Utf8_(password->GetCreatedTime())) +
         "\",\"last_used\":\"" + JsonEscape_(Utf8_(password->GetLastUsedTime())) +
         "\",\"active\":" + (password->GetActive() ? "true" : "false");
      if (!clearText.IsEmpty())
         json += ",\"password\":\"" + JsonEscape_(Utf8_(clearText)) + "\"";
      json += "}";
      return json;
   }

   HttpResponse
   RestApiServer::HandleMeAppPasswords_(const Caller &caller)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      AppPasswords list;
      list.Refresh(account->GetID());

      AnsiString json = "{\"app_passwords\":[";
      const std::vector<std::shared_ptr<AppPassword> > &items = list.GetVector();
      for (size_t i = 0; i < items.size(); i++)
      {
         if (i > 0)
            json += ",";
         json += AppPasswordJson_(items[i], String());
      }
      json += "]}";
      return BuildResponse_(200, json);
   }

   bool
   RestApiServer::RefuseAppPasswordCaller_(const Caller &caller, const char *what, HttpResponse &failure)
   {
      if (!caller.via_app_password)
         return false;

      LOG_APPLICATION("REST API: " + String(what) + " for " + (caller.account ? caller.account->GetAddress() : String()) +
         " was refused from " + String(caller.peer.ToString()) + " - the request was authenticated with an app password.");
      failure = BuildResponse_(403, "{\"error\":\"not for an app password: sign in with the account's own password\"}");
      return true;
   }

   bool
   RestApiServer::ConfirmAccountPassword_(const Caller &caller, const AnsiString &request, HttpResponse &failure)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
      {
         failure = BuildResponse_(500, "{\"error\":\"internal error\"}");
         return false;
      }

      if (RefuseAppPasswordCaller_(caller, "making an app password", failure))
         return false;

      const String password = JsonUtf8Value_(GetRequestBody_(request), "password");
      if (password.IsEmpty())
      {
         failure = BuildResponse_(400, "{\"error\":\"password is required: the account's own password, to prove it is the account holder asking\"}");
         return false;
      }

      // The account's own password, whatever authenticated the request: a
      // session cookie proves the browser, and an app password would prove
      // only that a program has one. Never an app password here.
      if (!PasswordValidator::ValidateAccountPasswordOnly(account, password))
      {
         LOG_APPLICATION("REST API: making an app password for " + account->GetAddress() + " was refused from " +
            String(caller.peer.ToString()) + " - the password did not match.");
         RegisterAuthenticationFailure_(caller.peer);
         failure = BuildResponse_(403, "{\"error\":\"the password is not correct\"}");
         return false;
      }

      // An account with a second factor proves it as HandleMePassword_ asks:
      // the code travels in X-hMailServer-OTP, and the 401 names the header.
      const String secret = account->GetTotpSecret();
      if (!secret.IsEmpty())
      {
         const AnsiString code = GetHeader_(request, "x-hmailserver-otp");
         if (code.IsEmpty() || !Totp::VerifyCode(AnsiString(secret), code))
         {
            failure = BuildUnauthorizedResponse_(true, IsPageScriptRequest_(request));
            return false;
         }
      }

      return true;
   }

   HttpResponse
   RestApiServer::HandleMeAppPasswordCreate_(const Caller &caller, const AnsiString &request)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      HttpResponse refusal;
      if (!ConfirmAccountPassword_(caller, request, refusal))
         return refusal;

      const AnsiString requestBody = GetRequestBody_(request);
      String name = JsonUtf8Value_(requestBody, "name");
      name.TrimLeft();
      name.TrimRight();
      if (name.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"name is required: what the password is for\"}");
      if (name.GetLength() > 255)
         return BuildResponse_(400, "{\"error\":\"name is at most 255 characters\"}");

      AppPasswords existing;
      existing.Refresh(account->GetID());
      if (existing.GetCount() >= AppPasswordsPerAccount)
         return BuildResponse_(400, "{\"error\":\"at most 20 app passwords; remove one first\"}");

      String clearText = AppPassword::GenerateSecret();
      if (clearText.IsEmpty())
         return BuildResponse_(500, "{\"error\":\"the random number generator failed, so no password was made\"}");

      std::shared_ptr<AppPassword> password = std::shared_ptr<AppPassword>(new AppPassword());
      password->SetAccountID(account->GetID());
      password->SetName(name);
      password->SetPassword(clearText);
      password->SetActive(true);

      String result;
      if (!PersistentAppPassword::SaveObject(password, result, PersistenceModeNormal))
      {
         AnsiString body;
         body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(result.IsEmpty() ? String(_T("the app password could not be saved")) : result)).c_str());
         return BuildResponse_(500, body);
      }
      PersistentAppPassword::InvalidateExistenceCache();

      LOG_APPLICATION("REST API: app password \"" + name + "\" made for " + account->GetAddress() + " from " +
         String(caller.peer.ToString()) + ".");

      return BuildResponse_(201, AppPasswordJson_(password, clearText));
   }

   HttpResponse
   RestApiServer::HandleMeAppPasswordDelete_(const Caller &caller, __int64 id)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      HttpResponse refusal;
      if (RefuseAppPasswordCaller_(caller, "removing an app password", refusal))
         return refusal;

      AppPasswords list;
      list.Refresh(account->GetID());
      std::shared_ptr<AppPassword> password = list.GetItemByDBID((unsigned __int64) id);
      if (!password || password->GetAccountID() != account->GetID())
         return BuildResponse_(404, "{\"error\":\"no such app password\"}");

      if (!PersistentAppPassword::DeleteObject(password))
         return BuildResponse_(500, "{\"error\":\"the app password could not be removed\"}");
      PersistentAppPassword::InvalidateExistenceCache();

      LOG_APPLICATION("REST API: app password \"" + password->GetName() + "\" removed for " + account->GetAddress() + " from " +
         String(caller.peer.ToString()) + ".");

      return BuildResponse_(200, "{\"deleted\":true}");
   }
}
