// Copyright (c) 2010 Martin Knafve / hMailServer.com.
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once


namespace HM
{
   // Scrubs credential material out of a single client command line before it is
   // written to the protocol log.
   //
   // What is hidden:
   //    POP3   PASS <password>
   //           APOP <user> <digest>
   //           AUTH <mechanism> <initial-response>
   //           a bare single-word base64 SASL continuation line
   //    IMAP   <tag> LOGIN <user> <password>   (atom, quoted string or {n} literal)
   //           <tag> AUTHENTICATE <mechanism> <initial-response>
   //    SMTP   AUTH <mechanism> <initial-response>
   //           a bare single-word base64 SASL continuation line
   //
   // What is deliberately kept, because a protocol log with no identity in it cannot
   // be used to work out which account a session belongs to or which account is being
   // brute forced:
   //    the command name and the SASL mechanism name;
   //    the POP3 USER argument and the IMAP LOGIN user name;
   //    the initial response of the SASL LOGIN mechanism, which is the user name (the
   //    password of a LOGIN exchange arrives on the following line and is hidden).
   //
   // This is a stateless, line-at-a-time scrubber. The connection classes additionally
   // mask the continuation lines of the exchanges whose state they track; both layers
   // are wanted, since neither sees everything.
   class PasswordRemover
   {
   public:

      enum PRType
      {
         PRIMAP = 1,
         PRPOP3 = 2,
         PRSMTP = 3
      };

      PasswordRemover();
      virtual ~PasswordRemover();

         static void Remove(PRType prt, String &sClientCommand);
   private:


   };
}

