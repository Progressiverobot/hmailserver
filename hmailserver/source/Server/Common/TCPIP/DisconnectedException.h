// Copyright (c) 2014 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class DisconnectedException : public std::exception
   {
   public:

      // std::exception::what() is declared not to throw, so an override of it
      // has to promise the same. MSVC accepts the promise on a base that does
      // not spell it out; a conforming compiler refuses an override whose
      // exception specification is the laxer of the two. Saying noexcept here
      // is true of this body either way - it returns a string literal.
      virtual const char* what() const noexcept
      {
         return "The client has been disconnected.";
      }


   private:

   };
}