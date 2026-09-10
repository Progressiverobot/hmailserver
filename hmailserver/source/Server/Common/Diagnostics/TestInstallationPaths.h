// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "DiagnosticResult.h"

namespace HM
{
   // Lists every directory and file the running server was configured with,
   // where it read each one from, and whether it exists - the inventory an
   // administrator otherwise assembles by hand before moving an installation
   // (docs/RelocatingAnInstallation.md, issue #158). It changes nothing.
   //
   // Two lines of the report differ by platform, and both differences are in
   // PerformTest with the reason beside them: the Languages directory counts
   // towards the result on Windows and is reported as unused on POSIX, because
   // the only consumer of the catalogues is the COM API the Control Panel talks
   // to; and the Control Deck page is reported on both and counts on neither,
   // because a server without it still serves /api/v1/.
   class TestInstallationPaths
   {
   public:
      TestInstallationPaths();
      virtual ~TestInstallationPaths();

      DiagnosticResult PerformTest();

   private:

      // One line of the report: "<label>: <path>   [exists | MISSING | not set]".
      // A configured path that does not exist fails the test; an empty one is
      // reported as not set and does not, because most of these are optional.
      void AppendPath_(String &report, bool &all_present, const String &label, const String &path, bool is_directory);
   };
}
