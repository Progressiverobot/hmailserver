// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Two answers a reader gives a message without writing one: a read receipt,
// for a message that asked for one (POST /api/v1/me/messages/{id}/receipt - an
// RFC 8098 disposition notification, queued as the account sends anything),
// and an unsubscribe, by the method the list itself named (POST
// /api/v1/me/messages/{id}/unsubscribe - the RFC 8058 one-click HTTPS POST when
// the list offers it, else a message to its mailto). Both go out under the
// account's own address and the same recipient checks a send makes.

#include "StdAfx.h"
#include "RestApiServer.h"
#include "HttpServer.h"
#include "HttpsClient.h"
#include "Time.h"
#include "GUIDCreator.h"
#include "FileUtilities.h"
#include "Unicode.h"
#include "../BO/Account.h"
#include "../BO/Message.h"
#include "../BO/MessageRecipients.h"
#include "../BO/IMAPFolder.h"
#include "../Mime/Mime.h"
#include "../Persistence/PersistentMessage.h"
#include "../Application/Application.h"
#include "../../SMTP/RecipientParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const char *CRLF = "\r\n";

      AnsiString HeaderValue(MimeHeader &header, const char *name)
      {
         const char *value = header.GetRawFieldValue(name);
         AnsiString text = value ? value : "";
         text.TrimLeft();
         text.TrimRight();
         return text;
      }

      // The first address in a header: "<a@b>", "Name <a@b>" or "a@b, c@d".
      AnsiString FirstAddressIn(const AnsiString &header)
      {
         AnsiString text = header;
         int open = text.Find("<");
         int close = text.Find(">");
         if (open >= 0 && close > open)
            text = text.Mid(open + 1, close - open - 1);
         else
         {
            int comma = text.Find(",");
            if (comma >= 0)
               text = text.Mid(0, comma);
         }
         text.TrimLeft();
         text.TrimRight();
         text.ToLower();
         return text;
      }

      // The first <...> entry of a List-Unsubscribe header that starts with the
      // scheme, without the brackets.
      bool FindAngleUrl(const AnsiString &header, const AnsiString &scheme, AnsiString &url)
      {
         int at = 0;
         while (true)
         {
            int open = header.Find("<", at);
            if (open < 0)
               return false;
            int close = header.Find(">", open);
            if (close < 0)
               return false;
            AnsiString candidate = header.Mid(open + 1, close - open - 1);
            candidate.TrimLeft();
            candidate.TrimRight();
            AnsiString lower = candidate;
            lower.ToLower();
            if (lower.Find(scheme) == 0)
            {
               url = candidate;
               return true;
            }
            at = close + 1;
         }
      }

      AnsiString PercentDecode(const AnsiString &text)
      {
         AnsiString out;
         for (int i = 0; i < text.GetLength(); i++)
         {
            char c = text[i];
            if (c == '+')
               out += ' ';
            else if (c == '%' && i + 2 < text.GetLength() && isxdigit((unsigned char) text[i + 1]) && isxdigit((unsigned char) text[i + 2]))
            {
               AnsiString hex = text.Mid(i + 1, 2);
               out += (char) strtol(hex.c_str(), nullptr, 16);
               i += 2;
            }
            else
               out += c;
         }
         return out;
      }

      bool IsAscii(const AnsiString &text)
      {
         for (int i = 0; i < text.GetLength(); i++)
            if ((unsigned char) text[i] > 126 || (unsigned char) text[i] < 32)
               return false;
         return true;
      }

      // A header line that is safe as one line: no CR or LF from the message
      // it came from can start a header of its own.
      // A CR, LF, NUL or any other control character: in an address that is
      // written into a header it would end the line and begin another.
      bool HasControlCharacters(const AnsiString &text)
      {
         const char *at = text.c_str();
         for (size_t i = 0; i < text.size(); i++)
         {
            const unsigned char c = (unsigned char) at[i];
            if (c < 0x20 || c == 0x7f)
               return true;
         }
         return false;
      }

      AnsiString OneLine(const AnsiString &text)
      {
         AnsiString out = text;
         out.Replace('\r', ' ');
         out.Replace('\n', ' ');
         return out;
      }
   }

   // A message the account sends, written as given: the recipient put through
   // the checks a send makes, the file written as given, the row queued.
   // 0 when queued; else an HTTP status with problem holding the body.
   int
   RestApiServer::QueueRawMessage_(std::shared_ptr<const Account> account, const String &toAddress, const AnsiString &rawText, AnsiString &problem)
   {
      if (!StringParser::IsValidEmailAddress(toAddress))
      {
         problem = "{\"error\":\"" + JsonEscape_(Utf8_(toAddress)) + ": not an e-mail address\"}";
         return 400;
      }

      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      message->SetFromAddress(account->GetAddress());

      RecipientParser parser;
      String reason;
      bool treatSecurityAsLocal = false;
      RecipientParser::DeliveryPossibility possibility =
         parser.CheckDeliveryPossibility(true, account->GetAddress(), toAddress, reason, treatSecurityAsLocal, 0, true);
      if (possibility != RecipientParser::DP_Possible)
      {
         if (reason.IsEmpty())
            reason = possibility == RecipientParser::DP_RecipientUnknown ? _T("unknown recipient") : _T("delivery is not permitted");
         problem = "{\"error\":\"" + JsonEscape_(Utf8_(toAddress)) + ": " + JsonEscape_(Utf8_(reason)) + "\"}";
         return 400;
      }

      bool recipientOK = false;
      parser.CreateMessageRecipientList(toAddress, message->GetRecipients(), recipientOK);
      if (!recipientOK || message->GetRecipients()->GetCount() == 0)
      {
         problem = "{\"error\":\"" + JsonEscape_(Utf8_(toAddress)) + ": unknown recipient\"}";
         return 400;
      }

      const String fileName = PersistentMessage::GetFileName(message);
      if (!FileUtilities::WriteToFile(fileName, rawText))
      {
         problem = "{\"error\":\"the message could not be written\"}";
         return 500;
      }

      message->SetSize((int) FileUtilities::FileSize(fileName));
      message->SetState(Message::Delivering);
      if (!PersistentMessage::SaveObject(message))
      {
         FileUtilities::DeleteFile(fileName);
         problem = "{\"error\":\"the message could not be queued\"}";
         return 500;
      }

      Application::Instance()->SubmitPendingEmail();
      return 0;
   }

   HttpResponse
   RestApiServer::HandleMeMessageReceipt_(const Caller &caller, __int64 messageId)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      if (!PersistentMessage::ReadObject(message, messageId) || message->GetID() == 0)
         return BuildResponse_(404, "{\"error\":\"message not found\"}");
      std::shared_ptr<IMAPFolder> folder = FindReadableFolder_(account, message->GetFolderID());
      if (!folder || folder->GetAccountID() != message->GetAccountID())
         return BuildResponse_(404, "{\"error\":\"message not found\"}");

      AnsiString header = PersistentMessage::LoadHeader(MessageFile_(message), false);
      MimeHeader mimeHeader;
      mimeHeader.Load(header.c_str(), header.GetLength(), true);

      AnsiString wanted = HeaderValue(mimeHeader, "Disposition-Notification-To");
      if (wanted.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"the message did not ask for a receipt\"}");
      AnsiString to = FirstAddressIn(wanted);
      if (HasControlCharacters(to))
         return BuildResponse_(400, "{\"error\":\"the address asking for the receipt carries control characters and is not written into a header\"}");

      String subject = mimeHeader.GetUnicodeFieldValue("Subject");
      AnsiString subjectUtf8 = Utf8_(subject);
      AnsiString date = OneLine(HeaderValue(mimeHeader, "Date"));
      AnsiString originalId = OneLine(HeaderValue(mimeHeader, "Message-ID"));
      AnsiString me = Utf8_(account->GetAddress());
      AnsiString domain = me.Find("@") >= 0 ? me.Mid(me.Find("@") + 1) : me;
      AnsiString newId = "<" + Utf8_(GUIDCreator::GetGUID()) + "@" + domain + ">";
      AnsiString boundary = "hm-mdn-" + Utf8_(GUIDCreator::GetGUID());
      AnsiString subjectLine = IsAscii(subjectUtf8) ? "Read: " + OneLine(subjectUtf8) : AnsiString("Read: your message");

      AnsiString text;
      text += "From: " + OneLine(Utf8_(FromHeader_(account))) + CRLF;
      text += "To: <" + to + ">" + CRLF;
      text += "Subject: " + subjectLine + CRLF;
      text += "Date: " + Utf8_(Time::GetCurrentMimeDate()) + CRLF;
      text += "Message-ID: " + newId + CRLF;
      text += "MIME-Version: 1.0" + AnsiString(CRLF);
      text += "Auto-Submitted: auto-replied" + AnsiString(CRLF);
      text += "Content-Type: multipart/report; report-type=disposition-notification; boundary=\"" + boundary + "\"" + CRLF + CRLF;
      text += "--" + boundary + CRLF;
      text += "Content-Type: text/plain; charset=utf-8" + AnsiString(CRLF) + CRLF;
      text += "The message sent on " + date + " to " + me + " with the subject \"" + subjectUtf8 + "\" has been displayed. This is no guarantee that it has been read or understood." + CRLF + CRLF;
      text += "--" + boundary + CRLF;
      text += "Content-Type: message/disposition-notification" + AnsiString(CRLF) + CRLF;
      text += "Reporting-UA: hMailServer webmail" + AnsiString(CRLF);
      text += "Original-Recipient: rfc822;" + me + CRLF;
      text += "Final-Recipient: rfc822;" + me + CRLF;
      if (!originalId.IsEmpty())
         text += "Original-Message-ID: " + originalId + CRLF;
      text += "Disposition: manual-action/MDN-sent-manually; displayed" + AnsiString(CRLF) + CRLF;
      text += "--" + boundary + "--" + CRLF;

      AnsiString problem;
      int refused = QueueRawMessage_(account, String(to.c_str()), text, problem);
      if (refused != 0)
         return BuildResponse_(refused, problem);

      return BuildResponse_(201, "{\"queued\":true,\"to\":\"" + JsonEscape_(to) + "\"}");
   }

   HttpResponse
   RestApiServer::HandleMeMessageUnsubscribe_(const Caller &caller, __int64 messageId)
   {
      std::shared_ptr<const Account> account = caller.account;
      if (!account)
         return BuildResponse_(500, "{\"error\":\"internal error\"}");

      std::shared_ptr<Message> message = std::shared_ptr<Message>(new Message());
      if (!PersistentMessage::ReadObject(message, messageId) || message->GetID() == 0)
         return BuildResponse_(404, "{\"error\":\"message not found\"}");
      std::shared_ptr<IMAPFolder> folder = FindReadableFolder_(account, message->GetFolderID());
      if (!folder || folder->GetAccountID() != message->GetAccountID())
         return BuildResponse_(404, "{\"error\":\"message not found\"}");

      AnsiString header = PersistentMessage::LoadHeader(MessageFile_(message), false);
      MimeHeader mimeHeader;
      mimeHeader.Load(header.c_str(), header.GetLength(), true);

      AnsiString list = HeaderValue(mimeHeader, "List-Unsubscribe");
      AnsiString post = HeaderValue(mimeHeader, "List-Unsubscribe-Post");
      post.ToLower();
      if (list.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"the message names no way to unsubscribe\"}");

      // RFC 8058: one POST to the https address, with the one body the RFC
      // names, and nothing else - no redirect followed, no page shown.
      AnsiString https;
      if (post.Find("list-unsubscribe=one-click") >= 0 && FindAngleUrl(list, "https://", https))
      {
         bool isHttps = false;
         AnsiString host, port, path;
         if (HttpsClient::ParseUrl(https, isHttps, host, port, path) && isHttps && !HttpsClient::IsLoopbackHost(host))
         {
            // An address an outsider chose, so it is reached only if it is
            // public - judged by the addresses the name resolves to, and
            // connected to those very addresses (HttpsClient::RequestPublic)
            // - and with a short deadline and a small cap, since this runs
            // on a REST worker: a slow list must not hold one for long.
            HttpsClient::Response response;
            String error;
            bool ok = HttpsClient::RequestPublic("POST", https, std::vector<AnsiString>(), "application/x-www-form-urlencoded",
                                                 "List-Unsubscribe=One-Click", response, error, 10, 32 * 1024);
            AnsiString json;
            json.Format("{\"method\":\"one-click\",\"ok\":%hs,\"status\":%d,\"error\":\"%hs\"}",
               (ok && response.status_code >= 200 && response.status_code < 300) ? "true" : "false",
               ok ? response.status_code : 0,
               JsonEscape_(Utf8_(error)).c_str());
            return BuildResponse_(ok ? 200 : 502, json);
         }
      }

      AnsiString mailto;
      if (FindAngleUrl(list, "mailto:", mailto))
      {
         AnsiString rest = mailto.Mid(7);
         AnsiString address = rest;
         AnsiString subject = "Unsubscribe";
         int question = rest.Find("?");
         if (question >= 0)
         {
            address = rest.Mid(0, question);
            AnsiString query = rest.Mid(question + 1);
            std::vector<AnsiString> parts = StringParser::SplitString(query, "&");
            for (size_t i = 0; i < parts.size(); i++)
            {
               AnsiString key = parts[i].Mid(0, parts[i].Find("=") >= 0 ? parts[i].Find("=") : parts[i].GetLength());
               key.ToLower();
               if (key == "subject" && parts[i].Find("=") >= 0)
                  subject = PercentDecode(parts[i].Mid(parts[i].Find("=") + 1));
            }
         }
         address = PercentDecode(address);
         address.TrimLeft();
         address.TrimRight();
         // Percent-decoding can put a CR or LF into the address, and a quoted
         // local part carries one past IsValidEmailAddress; written into To:
         // it would end the header and begin another. Refused whole.
         if (HasControlCharacters(address))
            return BuildResponse_(400, "{\"error\":\"the list's mailto address carries control characters and is not used\"}");
         if (!IsAscii(subject))
            subject = "Unsubscribe";

         AnsiString me = Utf8_(account->GetAddress());
         AnsiString domain = me.Find("@") >= 0 ? me.Mid(me.Find("@") + 1) : me;
         AnsiString text;
         text += "From: " + OneLine(Utf8_(FromHeader_(account))) + CRLF;
         text += "To: <" + address + ">" + CRLF;
         text += "Subject: " + OneLine(subject) + CRLF;
         text += "Date: " + Utf8_(Time::GetCurrentMimeDate()) + CRLF;
         text += "Message-ID: <" + Utf8_(GUIDCreator::GetGUID()) + "@" + domain + ">" + CRLF;
         text += "MIME-Version: 1.0" + AnsiString(CRLF);
         text += "Auto-Submitted: auto-generated" + AnsiString(CRLF);
         text += "Content-Type: text/plain; charset=utf-8" + AnsiString(CRLF) + CRLF;
         text += "Unsubscribe" + AnsiString(CRLF);

         AnsiString problem;
         int refused = QueueRawMessage_(account, String(address.c_str()), text, problem);
         if (refused != 0)
            return BuildResponse_(refused, problem);

         return BuildResponse_(201, "{\"method\":\"mail\",\"queued\":true,\"to\":\"" + JsonEscape_(address) + "\"}");
      }

      return BuildResponse_(400, "{\"error\":\"the message names no unsubscribe method this server can use: no one-click https address and no mailto\"}");
   }
}
