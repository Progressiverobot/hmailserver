// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The headers the webmail's page and script are served with. The page itself
// is Portal.html, its script Portal.js, its manifest Portal.webmanifest and
// its service worker PortalServiceWorker.js, beside this file: real files,
// edited as such and run as such by build/portal-script-test.js, and carried
// into the binary by build/generate-portal-page.py as PortalPageData.cpp - the
// same arrangement as the S/MIME module (PortalSmime.js, PortalSmimeData.cpp),
// and build/check-portal-script.py runs the page and the script as files.
//
// Still in the binary, deliberately: the portal answers on a listener that may
// be the only thing an installation has, and a page that depends on a file
// beside the binary is a page that is missing where somebody most needs it.
// The headers stay a literal here, in the shape check-portal-script.py reads:
// a name, "=", and one string literal per line until the semicolon.

#include "StdAfx.h"

namespace HM
{
   const char *PortalHeaders =
      "Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'unsafe-inline'; img-src data: blob:; connect-src 'self'; frame-src 'self' blob:; manifest-src 'self'; worker-src 'self'; form-action 'none'; frame-ancestors 'none'; base-uri 'none'\r\n"
      "X-Content-Type-Options: nosniff\r\n"
      "Referrer-Policy: no-referrer\r\n"
      "Cache-Control: no-store\r\n";
}
