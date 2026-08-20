// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#pragma once

namespace HM
{
   // Posts a message to an external filtering engine over HTTP and reads back what
   // it decided.
   //
   // The shape is deliberately the one rspamd already speaks: the message itself is
   // the request body, everything the engine needs to know about the envelope and
   // the connection travels in request headers, and the reply is JSON carrying an
   // action and a score. An engine that is not rspamd needs no adapter either -
   // "POST me the message, answer with JSON" is about as small a contract as an
   // integration point can have, and it needs no binary protocol, no shared library
   // and no second process under this server's control, which is what ruled milter
   // out.
   //
   // THIS RUNS WHILE A CLIENT IS WAITING. Everything about it is bounded: the
   // connect, the write and the read all share one deadline, and a message larger
   // than the configured ceiling is never sent at all. An engine that stops
   // answering must cost one timeout per message, not a stalled SMTP session - and
   // by default a failure is not an answer, so the message is accepted.
   class FilterHookClient
   {
   public:

      struct Verdict
      {
         // False when the engine could not be reached or did not answer usefully.
         // The caller decides what that means; it is NOT a verdict of "clean".
         bool answered = false;

         // The engine's own action string, lower-cased ("no action", "add header",
         // "reject", ...). Empty when it named none.
         AnsiString action;

         // Added to the message's spam score.
         double score = 0.0;

         // Something to put in the log and in the spam reason. May be empty.
         AnsiString reason;
      };

      // Sends one message. Returns a Verdict whose answered flag says whether the
      // engine spoke at all.
      static Verdict Check(const String &url,
                           const String &messageFile,
                           const AnsiString &envelopeFrom,
                           const std::vector<AnsiString> &recipients,
                           const AnsiString &remoteIp,
                           const AnsiString &heloHost,
                           int timeoutSeconds);

      // The reply parser, separated from the socket so it can be pinned by vectors.
      // Reads the subset of rspamd's reply that a decision can be made from, and is
      // deliberately tolerant of everything else in it: an engine is entitled to
      // return fields this server has never heard of, and a parser that fell over
      // on them would break every time the engine gained a feature.
      static Verdict ParseResponse(const AnsiString &body);

      // Splits http://host[:port]/path. False for anything else, including https,
      // which this does not do yet.
      static bool ParseHttpUrl(const AnsiString &url, AnsiString &host, AnsiString &port, AnsiString &path);
   };
}
