// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "FilterHookClient.h"

#include "../Util/FileUtilities.h"

#include <boost/asio.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Reads one JSON value by key. Deliberately a scan rather than a parser: the
      // four fields a decision is made from are scalars at a known depth, and a real
      // JSON parser is a dependency this server does not otherwise carry. Everything
      // else in the reply is ignored rather than rejected - an engine is entitled to
      // return fields nobody here has heard of.
      bool FindValue(const AnsiString &json, const AnsiString &key, AnsiString &value)
      {
         value = "";

         const AnsiString needle = "\"" + key + "\"";

         int keyPosition = json.Find(needle);
         if (keyPosition < 0)
            return false;

         int colon = json.Find(":", keyPosition + needle.GetLength());
         if (colon < 0)
            return false;

         int i = colon + 1;

         while (i < json.GetLength() && (json.GetAt(i) == ' ' || json.GetAt(i) == '\t' ||
                                         json.GetAt(i) == '\r' || json.GetAt(i) == '\n'))
            i++;

         if (i >= json.GetLength())
            return false;

         if (json.GetAt(i) == '"')
         {
            i++;
            int start = i;

            while (i < json.GetLength() && json.GetAt(i) != '"')
            {
               // A backslash escapes the next character, so a quote inside the string
               // does not end it. Nothing here needs the escape sequences decoded -
               // these are action names and a human-readable reason.
               if (json.GetAt(i) == '\\' && i + 1 < json.GetLength())
                  i++;

               i++;
            }

            value = json.Mid(start, i - start);
            return true;
         }

         // A bare token: a number, true, false or null.
         int start = i;

         while (i < json.GetLength())
         {
            char c = json.GetAt(i);

            if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\r' || c == '\n' || c == '\t')
               break;

            i++;
         }

         value = json.Mid(start, i - start);

         return !value.IsEmpty();
      }
   }

   bool
   FilterHookClient::ParseHttpUrl(const AnsiString &url, AnsiString &host, AnsiString &port, AnsiString &path)
   {
      host = "";
      port = "80";
      path = "/";

      AnsiString remainder = url;
      remainder.Trim();

      const AnsiString scheme = "http://";

      if (remainder.GetLength() <= scheme.GetLength())
         return false;

      if (remainder.Mid(0, scheme.GetLength()).ToLower() != scheme)
         return false;

      remainder = remainder.Mid(scheme.GetLength());

      int slash = remainder.Find("/");

      if (slash >= 0)
      {
         path = remainder.Mid(slash);
         remainder = remainder.Mid(0, slash);
      }

      int colon = remainder.Find(":");

      if (colon >= 0)
      {
         port = remainder.Mid(colon + 1);
         remainder = remainder.Mid(0, colon);
      }

      host = remainder;

      return !host.IsEmpty() && !port.IsEmpty();
   }

   FilterHookClient::Verdict
   FilterHookClient::ParseResponse(const AnsiString &body)
   {
      Verdict verdict;

      if (body.IsEmpty())
         return verdict;

      AnsiString value;

      if (FindValue(body, "action", value))
      {
         value.ToLower();
         verdict.action = value;
      }

      if (FindValue(body, "score", value))
         verdict.score = atof(value.c_str());

      // smtp_message is where rspamd puts the text meant for the sender; reason is
      // what a simpler engine would call it. Either will do.
      if (FindValue(body, "smtp_message", value) || FindValue(body, "reason", value))
         verdict.reason = value;

      // An answer with neither an action nor a score is not an answer. Saying so is
      // what lets the caller apply its fail-open or fail-closed policy instead of
      // silently treating an empty reply as approval.
      verdict.answered = !verdict.action.IsEmpty() || verdict.score != 0.0;

      return verdict;
   }

   FilterHookClient::Verdict
   FilterHookClient::Check(const String &url,
                           const String &messageFile,
                           const AnsiString &envelopeFrom,
                           const std::vector<AnsiString> &recipients,
                           const AnsiString &remoteIp,
                           const AnsiString &heloHost,
                           int timeoutSeconds)
   {
      Verdict verdict;

      try
      {
         AnsiString host;
         AnsiString port;
         AnsiString path;

         if (!ParseHttpUrl(AnsiString(url), host, port, path))
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5540, "FilterHookClient::Check",
               "FilterHookUrl is not an http:// URL, so no message can be sent to the filtering engine. "
               "FilterHookUrl: " + url);

            return verdict;
         }

         AnsiString body = FileUtilities::ReadCompleteTextFile(messageFile);

         if (body.IsEmpty())
            return verdict;

         AnsiString request;
         AnsiString contentLength;
         contentLength.Format("%d", body.GetLength());

         request += "POST " + path + " HTTP/1.1\r\n";
         request += "Host: " + host + "\r\n";
         request += "Content-Type: message/rfc822\r\n";
         request += "Content-Length: " + contentLength + "\r\n";
         request += "Connection: close\r\n";

         // The metadata, in the header names rspamd already reads. An engine that is
         // not rspamd sees ordinary request headers and can ignore the ones it does
         // not want.
         if (!remoteIp.IsEmpty())
            request += "Ip: " + remoteIp + "\r\n";
         if (!heloHost.IsEmpty())
            request += "Helo: " + heloHost + "\r\n";
         if (!envelopeFrom.IsEmpty())
            request += "From: " + envelopeFrom + "\r\n";

         for (const AnsiString &recipient : recipients)
            request += "Rcpt: " + recipient + "\r\n";

         request += "\r\n";
         request += body;

         boost::asio::io_context ioContext;
         boost::asio::ip::tcp::socket socket(ioContext);
         boost::asio::ip::tcp::resolver resolver(ioContext);

         boost::system::error_code resolveError;
         auto endpoints = resolver.resolve(host, port, resolveError);

         if (resolveError)
            return verdict;

         // One deadline for the whole exchange - connect, write and read.
         //
         // Read and write timeouts alone would not be enough: a blocking connect to a
         // host that drops packets sits for the operating system's TCP timeout,
         // around twenty seconds here, and this code runs while an SMTP client waits
         // for its response to DATA. The engine being unreachable has to cost what
         // the administrator configured, not what Windows decided.
         const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(timeoutSeconds);

         auto runUntilDeadline = [&ioContext, &deadline]() -> bool
         {
            ioContext.restart();

            const auto remaining = deadline - std::chrono::steady_clock::now();

            if (remaining <= std::chrono::steady_clock::duration::zero())
               return false;

            ioContext.run_for(remaining);

            return ioContext.stopped();
         };

         boost::system::error_code operationError = boost::asio::error::would_block;

         boost::asio::async_connect(socket, endpoints,
            [&operationError](const boost::system::error_code &ec, const boost::asio::ip::tcp::endpoint &)
            {
               operationError = ec;
            });

         if (!runUntilDeadline() || operationError)
         {
            socket.close();
            return verdict;
         }

         operationError = boost::asio::error::would_block;

         boost::asio::async_write(socket, boost::asio::buffer(request.c_str(), request.GetLength()),
            [&operationError](const boost::system::error_code &ec, std::size_t)
            {
               operationError = ec;
            });

         if (!runUntilDeadline() || operationError)
         {
            socket.close();
            return verdict;
         }

         boost::asio::streambuf response;
         operationError = boost::asio::error::would_block;

         // Read to end of stream: Connection: close above means the engine closes
         // when it has finished, so eof is the successful outcome here.
         boost::asio::async_read(socket, response, boost::asio::transfer_all(),
            [&operationError](const boost::system::error_code &ec, std::size_t)
            {
               operationError = ec;
            });

         const bool completed = runUntilDeadline();

         socket.close();

         if (!completed)
            return verdict;

         if (operationError && operationError != boost::asio::error::eof)
            return verdict;

         AnsiString raw(static_cast<const char*>(response.data().data()), (int) response.size());

         int headerEnd = raw.Find("\r\n\r\n");

         if (headerEnd < 0)
            return verdict;

         // Only a 200 carries a verdict. Anything else - an engine that is starting
         // up, a proxy in the way, a wrong path - is "no answer", which the caller
         // handles with its fail policy rather than reading as approval.
         AnsiString statusLine = raw.Mid(0, raw.Find("\r\n"));

         if (statusLine.Find(" 200") < 0)
            return verdict;

         return ParseResponse(raw.Mid(headerEnd + 4));
      }
      catch (...)
      {
         // Never let a filtering engine's misbehaviour become an exception on the
         // delivery path. No answer is a state the caller already knows how to
         // handle; an exception here is a message nobody accepted or refused.
         return verdict;
      }
   }
}
