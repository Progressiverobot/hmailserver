// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "UpdateWindow.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const char *DAY_NAMES[7] = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };
      const TCHAR *DAY_LABELS[7] = { _T("Sun"), _T("Mon"), _T("Tue"), _T("Wed"), _T("Thu"), _T("Fri"), _T("Sat") };

      std::string Lower_(const std::string &text)
      {
         std::string result = text;
         for (size_t i = 0; i < result.size(); i++)
            result[i] = (char) tolower((unsigned char) result[i]);
         return result;
      }

      std::string Trim_(const std::string &text)
      {
         size_t start = text.find_first_not_of(" \t");
         if (start == std::string::npos)
            return "";
         size_t end = text.find_last_not_of(" \t");
         return text.substr(start, end - start + 1);
      }

      // "HH:MM" as minutes after midnight; -1 when it is not one.
      int Minutes_(const std::string &text)
      {
         if (text.size() != 5 || text[2] != ':')
            return -1;
         for (size_t i = 0; i < 5; i++)
            if (i != 2 && !isdigit((unsigned char) text[i]))
               return -1;
         int hours = atoi(text.substr(0, 2).c_str());
         int minutes = atoi(text.substr(3, 2).c_str());
         if (hours > 23 || minutes > 59)
            return -1;
         return hours * 60 + minutes;
      }

      String TimeText_(int minutes)
      {
         String text;
         text.Format(_T("%02d:%02d"), minutes / 60, minutes % 60);
         return text;
      }
   }

   UpdateWindow::UpdateWindow() :
      defined_(false),
      start_minutes_(0),
      end_minutes_(0)
   {
      for (int i = 0; i < 7; i++)
         days_[i] = false;
   }

   bool
   UpdateWindow::Parse(const String &text, String &error)
   {
      defined_ = false;
      for (int i = 0; i < 7; i++)
         days_[i] = true;

      std::string value = Trim_(std::string(AnsiString(text).c_str()));
      if (value.empty())
         return true;

      // "[days ]start[-end]": the time part is what ends with a digit.
      size_t space = value.find_last_of(' ');
      std::string days = space == std::string::npos ? "" : Trim_(value.substr(0, space));
      std::string times = space == std::string::npos ? value : Trim_(value.substr(space + 1));

      if (!days.empty())
      {
         for (int i = 0; i < 7; i++)
            days_[i] = false;

         size_t start = 0;
         while (start <= days.size())
         {
            size_t comma = days.find(',', start);
            std::string day = Lower_(Trim_(days.substr(start, comma == std::string::npos ? std::string::npos : comma - start)));
            bool known = false;
            for (int i = 0; i < 7; i++)
            {
               if (day == DAY_NAMES[i])
               {
                  days_[i] = true;
                  known = true;
               }
            }
            if (!known)
            {
               error = Formatter::Format(_T("UpdateWindow: '{0}' is not a day; use Sun, Mon, Tue, Wed, Thu, Fri or Sat."), String(day.c_str()));
               return false;
            }
            if (comma == std::string::npos)
               break;
            start = comma + 1;
         }
      }

      size_t dash = times.find('-');
      std::string from = dash == std::string::npos ? times : times.substr(0, dash);
      std::string to = dash == std::string::npos ? "" : times.substr(dash + 1);

      start_minutes_ = Minutes_(Trim_(from));
      if (start_minutes_ < 0)
      {
         error = Formatter::Format(_T("UpdateWindow: '{0}' is not a time; use HH:MM."), String(from.c_str()));
         return false;
      }

      if (to.empty())
         end_minutes_ = (start_minutes_ + 60) % (24 * 60);
      else
      {
         end_minutes_ = Minutes_(Trim_(to));
         if (end_minutes_ < 0)
         {
            error = Formatter::Format(_T("UpdateWindow: '{0}' is not a time; use HH:MM."), String(to.c_str()));
            return false;
         }
         if (end_minutes_ == start_minutes_)
         {
            error = _T("UpdateWindow: the window's end is its start; give it some length.");
            return false;
         }
      }

      defined_ = true;
      return true;
   }

   bool
   UpdateWindow::Contains(const SYSTEMTIME &local) const
   {
      if (!defined_)
         return false;

      int minutes = local.wHour * 60 + local.wMinute;
      int day = local.wDayOfWeek;

      if (start_minutes_ < end_minutes_)
         return days_[day] && minutes >= start_minutes_ && minutes < end_minutes_;

      // Crosses midnight: the part before midnight belongs to this day, the part
      // after it to the day before.
      if (minutes >= start_minutes_)
         return days_[day];
      if (minutes < end_minutes_)
         return days_[(day + 6) % 7];
      return false;
   }

   bool
   UpdateWindow::ContainsNow() const
   {
      SYSTEMTIME now;
      GetLocalTime(&now);
      return Contains(now);
   }

   String
   UpdateWindow::Describe() const
   {
      if (!defined_)
         return _T("");

      String days;
      int count = 0;
      for (int i = 0; i < 7; i++)
      {
         if (!days_[i])
            continue;
         if (!days.IsEmpty())
            days += _T(",");
         days += DAY_LABELS[i];
         count++;
      }

      String text = count == 7 ? String(_T("every day ")) : days + _T(" ");
      return text + TimeText_(start_minutes_) + _T("-") + TimeText_(end_minutes_);
   }
}
