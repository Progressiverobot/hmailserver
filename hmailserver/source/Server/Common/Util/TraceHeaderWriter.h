// Copyright (c) 2008 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2008-12-23
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Message;

   class TraceHeaderWriter
   {
   public:
      TraceHeaderWriter();
      ~TraceHeaderWriter();

      bool Write(const String &messageFileName, std::shared_ptr<Message> message, const std::vector<std::pair<AnsiString, AnsiString> > &headerFields);
   };
}
