// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// The webmail's catalogues, embedded: PortalLanguagesData.cpp is generated
// from hmailserver/source/Server/Common/Util/PortalLanguages/<code>.json by
// build/generate-portal-languages.py, and build/check-portal-languages.py
// refuses a unit that is not what the generator writes. Each catalogue is
// its JSON in pieces of at most four thousand characters, joined by the
// server on first use (RestApiLanguages.cpp).

#pragma once

namespace HM
{
   struct PortalLanguage
   {
      const char *code;
      const char *const *pieces;   // ends with nullptr
   };

   extern const PortalLanguage PortalLanguages[];   // ends with { nullptr, nullptr }
}
