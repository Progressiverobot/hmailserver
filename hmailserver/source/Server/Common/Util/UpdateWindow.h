// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include <vector>

namespace HM
{
   // When an update may be applied without anyone asking: UpdateWindow in
   // hMailServer.INI.
   //
   //    UpdateWindow=                  never (the default): applying is a person's click
   //    UpdateWindow=03:00             every day, from 03:00 for an hour
   //    UpdateWindow=Sun 03:00         Sundays, from 03:00 for an hour
   //    UpdateWindow=Sat,Sun 02:00-05:00   those days, between the two times
   //
   // Days are the English three-letter names, in any case and any order; times are
   // local, 24-hour. A range that crosses midnight (23:00-01:00) belongs to the day
   // it starts on. The apply itself is UpdateInstaller; this only answers whether
   // now is inside the window.
   class UpdateWindow
   {
   public:
      UpdateWindow();

      // False, with error, when the text is not one of the forms above. An empty
      // text parses as a window that never opens.
      bool Parse(const String &text, String &error);

      bool IsEmpty() const { return !defined_; }

      // Whether the local time given (Windows SYSTEMTIME) is inside the window.
      bool Contains(const SYSTEMTIME &local) const;
      bool ContainsNow() const;

      // The window as text, normalised: "Sat,Sun 02:00-05:00".
      String Describe() const;

   private:
      bool defined_;
      bool days_[7];          // Sunday first, as SYSTEMTIME counts
      int start_minutes_;     // minutes after midnight
      int end_minutes_;       // exclusive; may be less than start (crosses midnight)
   };
}
