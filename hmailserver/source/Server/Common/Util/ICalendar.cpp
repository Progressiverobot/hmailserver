// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// See ICalendar.h. Four parts: the text (content lines, folding, escaping,
// the component tree), the calendar (civil dates as days and seconds, with no
// dependence on the C library's local time), the zones (a VTIMEZONE's
// observances or a built-in rule, and the local-to-UTC step that has to deal
// with the hour that happens twice and the hour that never happens), and the
// recurrence (RRULE read into a rule, the rule expanded period by period in
// the DTSTART's wall-clock time, then EXDATE, RDATE and the overrides).

#include "StdAfx.h"

#include <algorithm>
#include <map>

#include "ICalendar.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const __int64 ICalendar::Forever = 253402300799LL;   // 9999-12-31T23:59:59Z
   const size_t ICalendar::MaxInstances = 10000;

   namespace
   {
      const int FoldWidth = 75;

      // Ceilings on the tree a text may describe, and on an expansion.
      const int MaxDepth = 8;
      const size_t MaxComponents = 2000;
      const size_t MaxProperties = 50000;
      const size_t MaxParametersPerProperty = 100;

      // Periods (days, weeks, months or years) one rule may be walked
      // through, and periods in a row that may yield nothing before the rule
      // is taken as exhausted - which is what RFC 5545 says of a rule whose
      // parts never agree, such as BYMONTH=2;BYMONTHDAY=30.
      const int MaxPeriods = 100000;
      const int MaxEmptyPeriods = 1000;

      const __int64 SecondsPerDay = 86400;

      //------------------------------------------------------------------------
      // Strings
      //------------------------------------------------------------------------

      bool IsSpaceOrTab(char c)
      {
         return c == ' ' || c == '\t';
      }

      AnsiString Trimmed(const AnsiString &value)
      {
         AnsiString text = value;
         text.TrimLeft();
         text.TrimRight();
         return text;
      }

      AnsiString Upper(const AnsiString &value)
      {
         AnsiString text = value;
         text.ToUpper();
         return text;
      }

      bool AllDigits(const AnsiString &text, int from, int count)
      {
         if (from + count > text.GetLength())
            return false;
         for (int i = from; i < from + count; i++)
         {
            if (text[i] < '0' || text[i] > '9')
               return false;
         }
         return true;
      }

      int Digits(const AnsiString &text, int from, int count)
      {
         int value = 0;
         for (int i = from; i < from + count; i++)
            value = value * 10 + (text[i] - '0');
         return value;
      }

      // A signed integer, or false. Bounded to what a rule part can mean.
      bool SmallInt(const AnsiString &text, int &value)
      {
         AnsiString t = Trimmed(text);
         if (t.IsEmpty() || t.GetLength() > 7)
            return false;
         int i = 0;
         bool negative = false;
         if (t[0] == '+' || t[0] == '-')
         {
            negative = t[0] == '-';
            i = 1;
         }
         if (i >= t.GetLength())
            return false;
         int v = 0;
         for (; i < t.GetLength(); i++)
         {
            if (t[i] < '0' || t[i] > '9')
               return false;
            v = v * 10 + (t[i] - '0');
         }
         value = negative ? -v : v;
         return true;
      }

      std::vector<AnsiString> SplitOn(const AnsiString &value, char separator)
      {
         std::vector<AnsiString> parts;
         AnsiString current;
         for (int i = 0; i < value.GetLength(); i++)
         {
            if (value[i] == separator)
            {
               parts.push_back(current);
               current = "";
            }
            else
               current += value[i];
         }
         parts.push_back(current);
         return parts;
      }

      //------------------------------------------------------------------------
      // Civil time. Days are counted from 1970-01-01; seconds from midnight
      // of that day. Weekdays are 0 = Sunday .. 6 = Saturday, which is how
      // BYDAY and WKST are read below.
      //------------------------------------------------------------------------

      bool IsLeap(int y)
      {
         return (y % 4 == 0 && y % 100 != 0) || y % 400 == 0;
      }

      int DaysInMonth(int y, int m)
      {
         static const int lengths[] = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
         if (m < 1 || m > 12)
            return 0;
         return m == 2 && IsLeap(y) ? 29 : lengths[m - 1];
      }

      int DaysInYear(int y)
      {
         return IsLeap(y) ? 366 : 365;
      }

      __int64 DaysFromCivil(int y, int m, int d)
      {
         y -= m <= 2 ? 1 : 0;
         const __int64 era = (y >= 0 ? y : y - 399) / 400;
         const unsigned yoe = static_cast<unsigned>(y - era * 400);
         const unsigned doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
         const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
         return era * 146097 + static_cast<__int64>(doe) - 719468;
      }

      void CivilFromDays(__int64 z, int &y, int &m, int &d)
      {
         z += 719468;
         const __int64 era = (z >= 0 ? z : z - 146096) / 146097;
         const unsigned doe = static_cast<unsigned>(z - era * 146097);
         const unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
         const __int64 yy = static_cast<__int64>(yoe) + era * 400;
         const unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
         const unsigned mp = (5 * doy + 2) / 153;
         d = static_cast<int>(doy - (153 * mp + 2) / 5 + 1);
         m = static_cast<int>(mp < 10 ? mp + 3 : mp - 9);
         y = static_cast<int>(yy + (m <= 2 ? 1 : 0));
      }

      int Weekday(__int64 days)
      {
         return static_cast<int>(((days % 7) + 11) % 7);
      }

      __int64 FloorDiv(__int64 a, __int64 b)
      {
         __int64 q = a / b;
         if ((a % b != 0) && ((a < 0) != (b < 0)))
            q--;
         return q;
      }

      __int64 DayOf(__int64 seconds)
      {
         return FloorDiv(seconds, SecondsPerDay);
      }

      // The two-letter weekday of BYDAY, or -1.
      int WeekdayOf(const AnsiString &text)
      {
         static const char *names[] = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };
         for (int i = 0; i < 7; i++)
         {
            if (text == names[i])
               return i;
         }
         return -1;
      }

      // The day of the month of the nth weekday (1..5 from the start, -1..-5
      // from the end), or 0 when the month has no such day.
      int NthWeekdayOfMonth(int y, int m, int weekday, int ordinal)
      {
         int length = DaysInMonth(y, m);
         if (length == 0 || ordinal == 0)
            return 0;
         __int64 first = DaysFromCivil(y, m, 1);
         if (ordinal > 0)
         {
            int day = 1 + ((weekday - Weekday(first) + 7) % 7) + 7 * (ordinal - 1);
            return day <= length ? day : 0;
         }
         int lastWeekday = Weekday(first + length - 1);
         int day = length - ((lastWeekday - weekday + 7) % 7) - 7 * (-ordinal - 1);
         return day >= 1 ? day : 0;
      }

      //------------------------------------------------------------------------
      // The text
      //------------------------------------------------------------------------

      // The logical lines of the text: physical lines joined where one begins
      // with a space or a tab, that character dropped (RFC 5545 section 3.1).
      void UnfoldLines(const AnsiString &text, std::vector<AnsiString> &lines)
      {
         AnsiString current;
         bool haveCurrent = false;
         int length = text.GetLength();
         int position = 0;

         while (position < length)
         {
            int lineEnd = position;
            while (lineEnd < length && text[lineEnd] != '\n')
               lineEnd++;

            AnsiString line = text.Mid(position, lineEnd - position);
            if (!line.IsEmpty() && line[line.GetLength() - 1] == '\r')
               line = line.Mid(0, line.GetLength() - 1);
            position = lineEnd + 1;

            if (haveCurrent && !line.IsEmpty() && IsSpaceOrTab(line[0]))
            {
               current += line.Mid(1);
               continue;
            }

            if (haveCurrent)
               lines.push_back(current);
            current = line;
            haveCurrent = true;
         }

         if (haveCurrent)
            lines.push_back(current);
      }

      // NAME[;parameter[=value]]*:value. False without a name or a colon
      // outside quotes.
      bool ParseContentLine(const AnsiString &line, ICalProperty &property)
      {
         bool quoted = false;
         int colon = -1;
         for (int i = 0; i < line.GetLength(); i++)
         {
            char c = line[i];
            if (c == '"')
               quoted = !quoted;
            else if (c == ':' && !quoted)
            {
               colon = i;
               break;
            }
         }

         if (colon <= 0)
            return false;

         AnsiString head = line.Mid(0, colon);
         property.value = line.Mid(colon + 1);

         std::vector<AnsiString> parts;
         AnsiString part;
         quoted = false;
         for (int i = 0; i < head.GetLength(); i++)
         {
            char c = head[i];
            if (c == '"')
            {
               quoted = !quoted;
               part += c;
            }
            else if (c == ';' && !quoted)
            {
               parts.push_back(part);
               part = "";
            }
            else
               part += c;
         }
         parts.push_back(part);

         AnsiString name = Upper(Trimmed(parts[0]));
         if (name.IsEmpty())
            return false;
         for (int i = 0; i < name.GetLength(); i++)
         {
            char c = name[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-';
            if (!ok)
               return false;
         }
         property.name = name;

         for (size_t i = 1; i < parts.size() && i <= MaxParametersPerProperty; i++)
         {
            AnsiString parameter = Trimmed(parts[i]);
            if (parameter.IsEmpty())
               continue;

            AnsiString parameterName;
            AnsiString parameterValue;
            int equals = parameter.Find("=");
            if (equals < 0)
            {
               parameterName = parameter;
            }
            else
            {
               parameterName = parameter.Mid(0, equals);
               parameterValue = parameter.Mid(equals + 1);
            }

            parameterName = Upper(Trimmed(parameterName));
            parameterValue = Trimmed(parameterValue);
            parameterValue.Remove('"');
            property.parameters.push_back(std::make_pair(parameterName, parameterValue));
         }

         return true;
      }

      // A parameter value on the way out: quoted when it holds a character
      // that would otherwise end it (RFC 5545 section 3.2), with the one
      // character it may never hold dropped.
      AnsiString ParameterText(const AnsiString &value)
      {
         bool quote = false;
         AnsiString clean;
         for (int i = 0; i < value.GetLength(); i++)
         {
            char c = value[i];
            if (c == '"')
               continue;
            if (c == ';' || c == ':' || c == ',')
               quote = true;
            clean += c;
         }
         return quote ? "\"" + clean + "\"" : clean;
      }

      void SerializeInto(const ICalComponent &component, AnsiString &out, int depth)
      {
         if (depth > MaxDepth)
            return;

         out += "BEGIN:" + Upper(component.name) + "\r\n";
         for (size_t i = 0; i < component.properties.size(); i++)
         {
            const ICalProperty &property = component.properties[i];
            AnsiString line = Upper(property.name);
            for (size_t j = 0; j < property.parameters.size(); j++)
               line += ";" + Upper(property.parameters[j].first) + "=" + ParameterText(property.parameters[j].second);
            line += ":" + property.value;
            out += ICalendar::Fold(line) + "\r\n";
         }
         for (size_t i = 0; i < component.children.size(); i++)
            SerializeInto(component.children[i], out, depth + 1);
         out += "END:" + Upper(component.name) + "\r\n";
      }

      //------------------------------------------------------------------------
      // Values: dates, durations, offsets
      //------------------------------------------------------------------------

      // "YYYYMMDD" or "YYYYMMDDTHHMMSS[Z]" into fields. Ranges checked.
      bool ParseDateFields(const AnsiString &raw, bool &isDate, bool &utc, int &y, int &m, int &d, int &hh, int &mm, int &ss)
      {
         AnsiString value = Trimmed(raw);
         isDate = false;
         utc = false;
         hh = mm = ss = 0;

         if (value.GetLength() == 8 && AllDigits(value, 0, 8))
            isDate = true;
         else if ((value.GetLength() == 15 || value.GetLength() == 16) && AllDigits(value, 0, 8) && value[8] == 'T' && AllDigits(value, 9, 6))
         {
            if (value.GetLength() == 16)
            {
               if (value[15] != 'Z' && value[15] != 'z')
                  return false;
               utc = true;
            }
            hh = Digits(value, 9, 2);
            mm = Digits(value, 11, 2);
            ss = Digits(value, 13, 2);
         }
         else
            return false;

         y = Digits(value, 0, 4);
         m = Digits(value, 4, 2);
         d = Digits(value, 6, 2);

         if (y < 1 || m < 1 || m > 12 || d < 1 || d > DaysInMonth(y, m))
            return false;
         if (hh > 23 || mm > 59 || ss > 60)
            return false;
         if (ss == 60)
            ss = 59;
         return true;
      }

      __int64 SecondsOf(int y, int m, int d, int hh, int mm, int ss)
      {
         return DaysFromCivil(y, m, d) * SecondsPerDay + hh * 3600 + mm * 60 + ss;
      }

      // "+0100", "-0530", "+013000" into seconds east of UTC.
      bool ParseOffset(const AnsiString &raw, int &seconds)
      {
         AnsiString value = Trimmed(raw);
         if (value.GetLength() != 5 && value.GetLength() != 7)
            return false;
         if (value[0] != '+' && value[0] != '-')
            return false;
         if (!AllDigits(value, 1, value.GetLength() - 1))
            return false;
         int hours = Digits(value, 1, 2);
         int minutes = Digits(value, 3, 2);
         int secs = value.GetLength() == 7 ? Digits(value, 5, 2) : 0;
         if (hours > 23 || minutes > 59 || secs > 59)
            return false;
         seconds = hours * 3600 + minutes * 60 + secs;
         if (value[0] == '-')
            seconds = -seconds;
         return true;
      }

      // RFC 5545 section 3.3.6: [+|-]P[nW | nD[TnH[nM[nS]]] | TnH[nM[nS]] | TnM[nS] | TnS].
      bool ParseDuration(const AnsiString &raw, __int64 &seconds)
      {
         AnsiString value = Upper(Trimmed(raw));
         seconds = 0;
         int i = 0;
         bool negative = false;
         if (i < value.GetLength() && (value[i] == '+' || value[i] == '-'))
         {
            negative = value[i] == '-';
            i++;
         }
         if (i >= value.GetLength() || value[i] != 'P')
            return false;
         i++;

         bool inTime = false;
         bool any = false;
         while (i < value.GetLength())
         {
            char c = value[i];
            if (c == 'T')
            {
               inTime = true;
               i++;
               continue;
            }
            int start = i;
            while (i < value.GetLength() && value[i] >= '0' && value[i] <= '9')
               i++;
            if (i == start || i >= value.GetLength() || i - start > 9)
               return false;
            __int64 number = Digits(value, start, i - start);
            char unit = value[i];
            i++;
            if (!inTime && unit == 'W')
               seconds += number * 7 * SecondsPerDay;
            else if (!inTime && unit == 'D')
               seconds += number * SecondsPerDay;
            else if (inTime && unit == 'H')
               seconds += number * 3600;
            else if (inTime && unit == 'M')
               seconds += number * 60;
            else if (inTime && unit == 'S')
               seconds += number;
            else
               return false;
            any = true;
         }

         if (!any)
            return false;
         if (negative)
            seconds = -seconds;
         return true;
      }

      //------------------------------------------------------------------------
      // Zones
      //------------------------------------------------------------------------

      // One STANDARD or DAYLIGHT observance: the offsets on either side of its
      // onset, and when the onset falls - once (its DTSTART), yearly by a rule
      // (nth weekday or a day of a month), and on any RDATEs.
      struct Observance
      {
         Observance() : offsetFrom(0), offsetTo(0), startYear(1970), startMonth(1), startDay(1), startSeconds(0),
                        yearly(false), ruleMonth(0), ruleWeekday(-1), ruleOrdinal(0), ruleMonthDay(0), ruleDayOffset(0),
                        untilInstant(0) { }

         int offsetFrom;
         int offsetTo;
         int startYear, startMonth, startDay;
         int startSeconds;       // time of day of the onset, wall clock in offsetFrom
         bool yearly;
         int ruleMonth;          // BYMONTH; 0 = the DTSTART's month
         int ruleWeekday;        // BYDAY's weekday, or -1
         int ruleOrdinal;        // BYDAY's ordinal, or 0
         int ruleMonthDay;       // BYMONTHDAY, or 0
         int ruleDayOffset;      // days added to the rule's day (the built-in Israel rule)
         __int64 untilInstant;   // UNTIL as an instant, or 0
         std::vector<__int64> rdates;   // onsets as wall-clock seconds in offsetFrom
      };

      struct Transition
      {
         __int64 utc;
         int offsetFrom;
         int offsetTo;

         bool operator<(const Transition &other) const { return utc < other.utc; }
      };

      struct Zone
      {
         Zone() : valid(false), fromYear(0), toYear(-1) { }

         bool valid;
         std::vector<Observance> observances;

         // Transitions computed for the years [fromYear, toYear], extended on
         // demand.
         std::vector<Transition> transitions;
         int fromYear;
         int toYear;
      };

      void OnsetsOf(const Observance &observance, int fromYear, int toYear, std::vector<Transition> &out)
      {
         Transition t;
         t.offsetFrom = observance.offsetFrom;
         t.offsetTo = observance.offsetTo;

         if (observance.yearly)
         {
            int month = observance.ruleMonth > 0 ? observance.ruleMonth : observance.startMonth;
            for (int y = std::max(fromYear, observance.startYear); y <= toYear; y++)
            {
               int day;
               if (observance.ruleMonthDay != 0)
               {
                  int length = DaysInMonth(y, month);
                  day = observance.ruleMonthDay > 0 ? observance.ruleMonthDay : length + 1 + observance.ruleMonthDay;
                  if (day < 1 || day > length)
                     continue;
               }
               else if (observance.ruleWeekday >= 0)
               {
                  day = NthWeekdayOfMonth(y, month, observance.ruleWeekday, observance.ruleOrdinal);
                  if (day == 0)
                     continue;
               }
               else
               {
                  day = observance.startDay;
                  if (day > DaysInMonth(y, month))
                     continue;
               }

               __int64 local = (DaysFromCivil(y, month, day) + observance.ruleDayOffset) * SecondsPerDay + observance.startSeconds;
               t.utc = local - observance.offsetFrom;
               if (observance.untilInstant != 0 && t.utc > observance.untilInstant)
                  continue;
               out.push_back(t);
            }
         }
         else
         {
            // A single onset at the DTSTART, whenever it was: it is the state
            // before any rule-made transition, so it always counts.
            t.utc = SecondsOf(observance.startYear, observance.startMonth, observance.startDay, 0, 0, 0) + observance.startSeconds - observance.offsetFrom;
            out.push_back(t);
         }

         for (size_t i = 0; i < observance.rdates.size(); i++)
         {
            t.utc = observance.rdates[i] - observance.offsetFrom;
            out.push_back(t);
         }
      }

      void EnsureTransitions(Zone &zone, int year)
      {
         if (year >= zone.fromYear && year <= zone.toYear)
            return;

         int from = std::min(year, zone.fromYear == 0 ? year : zone.fromYear) - 5;
         int to = std::max(year, zone.toYear < 0 ? year : zone.toYear) + 5;
         if (to - from > 400)
         {
            from = year - 5;
            to = year + 5;
         }

         zone.transitions.clear();
         for (size_t i = 0; i < zone.observances.size(); i++)
            OnsetsOf(zone.observances[i], from, to, zone.transitions);
         std::sort(zone.transitions.begin(), zone.transitions.end());
         zone.fromYear = from;
         zone.toYear = to;
      }

      int YearOfInstant(__int64 instant)
      {
         int y, m, d;
         CivilFromDays(DayOf(instant), y, m, d);
         return y;
      }

      // The offset in effect at an instant.
      int OffsetAt(Zone &zone, __int64 utc)
      {
         EnsureTransitions(zone, YearOfInstant(utc));

         const std::vector<Transition> &t = zone.transitions;
         if (t.empty())
            return zone.observances.empty() ? 0 : zone.observances[0].offsetTo;

         // The last transition at or before the instant.
         size_t lo = 0, hi = t.size();
         while (lo < hi)
         {
            size_t mid = (lo + hi) / 2;
            if (t[mid].utc <= utc)
               lo = mid + 1;
            else
               hi = mid;
         }
         if (lo == 0)
            return t[0].offsetFrom;
         return t[lo - 1].offsetTo;
      }

      // A wall-clock time in the zone to an instant. In the hour that happens
      // twice (the autumn fall-back) the first is taken; in the hour that never
      // happens (the spring gap) the offset from before the gap, so 02:30 on
      // the morning the clocks went from 02:00 to 03:00 is read as 03:30.
      __int64 LocalToUtc(Zone &zone, __int64 local)
      {
         EnsureTransitions(zone, YearOfInstant(local));

         std::vector<int> offsets;
         for (size_t i = 0; i < zone.observances.size(); i++)
         {
            int a = zone.observances[i].offsetTo;
            int b = zone.observances[i].offsetFrom;
            if (std::find(offsets.begin(), offsets.end(), a) == offsets.end())
               offsets.push_back(a);
            if (std::find(offsets.begin(), offsets.end(), b) == offsets.end())
               offsets.push_back(b);
         }
         if (offsets.empty())
            return local;

         bool found = false;
         __int64 best = 0;
         for (size_t i = 0; i < offsets.size(); i++)
         {
            __int64 candidate = local - offsets[i];
            if (OffsetAt(zone, candidate) != offsets[i])
               continue;
            if (!found || candidate < best)
            {
               best = candidate;
               found = true;
            }
         }
         if (found)
            return best;

         // A gap: the transition whose old offset puts the time after it and
         // whose new offset puts it before.
         const std::vector<Transition> &t = zone.transitions;
         for (size_t i = 0; i < t.size(); i++)
         {
            if (local - t[i].offsetFrom >= t[i].utc && local - t[i].offsetTo < t[i].utc)
               return local - t[i].offsetFrom;
         }

         return local - offsets[0];
      }

      // The built-in zones: each name, its standard offset, its daylight
      // offset, and the family of rule that moves between them. The rules
      // are the ones in force in 2026.
      enum RuleFamily
      {
         RuleNone,      // no daylight time
         RuleEU,        // last Sunday of March 01:00 UTC to last Sunday of October 01:00 UTC
         RuleUS,        // second Sunday of March 02:00 to first Sunday of November 02:00, local
         RuleAU,        // first Sunday of October 02:00 to first Sunday of April 03:00, local (southern)
         RuleNZ,        // last Sunday of September 02:00 to first Sunday of April 03:00, local
         RuleCL,        // first Sunday of September 00:00 to first Sunday of April 00:00, local
         RuleEG,        // last Friday of April 00:00 to last Thursday of October 24:00, local
         RuleLB,        // last Sunday of March 00:00 to last Sunday of October 00:00, local
         RuleCU,        // second Sunday of March 00:00 to first Sunday of November 01:00, local
         RuleMD,        // last Sunday of March 02:00 to last Sunday of October 03:00, local
         RuleIL         // the Friday before the last Sunday of March 02:00 to the last Sunday of October 02:00, local
      };

      struct BuiltInZone
      {
         const char *name;
         int standardMinutes;
         int daylightMinutes;   // == standardMinutes when the family is RuleNone
         RuleFamily family;
      };

      const BuiltInZone BuiltInZones[] =
      {
         { "UTC", 0, 0, RuleNone }, { "Etc/UTC", 0, 0, RuleNone }, { "GMT", 0, 0, RuleNone }, { "Etc/GMT", 0, 0, RuleNone },
         { "Z", 0, 0, RuleNone }, { "Zulu", 0, 0, RuleNone }, { "Etc/Zulu", 0, 0, RuleNone }, { "Universal", 0, 0, RuleNone },
         { "Etc/Universal", 0, 0, RuleNone }, { "Greenwich", 0, 0, RuleNone }, { "Etc/Greenwich", 0, 0, RuleNone },
         { "UCT", 0, 0, RuleNone }, { "Etc/UCT", 0, 0, RuleNone },

         { "Europe/London", 0, 60, RuleEU }, { "Europe/Belfast", 0, 60, RuleEU }, { "Europe/Guernsey", 0, 60, RuleEU },
         { "Europe/Jersey", 0, 60, RuleEU }, { "Europe/Isle_of_Man", 0, 60, RuleEU }, { "Europe/Dublin", 0, 60, RuleEU },
         { "Europe/Lisbon", 0, 60, RuleEU }, { "Atlantic/Canary", 0, 60, RuleEU }, { "Atlantic/Madeira", 0, 60, RuleEU },
         { "Atlantic/Faroe", 0, 60, RuleEU }, { "Atlantic/Faeroe", 0, 60, RuleEU }, { "WET", 0, 60, RuleEU },
         { "GB", 0, 60, RuleEU }, { "GB-Eire", 0, 60, RuleEU }, { "Eire", 0, 60, RuleEU }, { "Portugal", 0, 60, RuleEU },
         { "Atlantic/Reykjavik", 0, 0, RuleNone }, { "Iceland", 0, 0, RuleNone }, { "Africa/Abidjan", 0, 0, RuleNone },
         { "Africa/Accra", 0, 0, RuleNone }, { "Africa/Dakar", 0, 0, RuleNone }, { "Africa/Bamako", 0, 0, RuleNone },
         { "Africa/Monrovia", 0, 0, RuleNone }, { "Africa/Sao_Tome", 0, 0, RuleNone }, { "Atlantic/St_Helena", 0, 0, RuleNone },

         { "Europe/Berlin", 60, 120, RuleEU }, { "Europe/Paris", 60, 120, RuleEU }, { "Europe/Madrid", 60, 120, RuleEU },
         { "Europe/Rome", 60, 120, RuleEU }, { "Europe/Amsterdam", 60, 120, RuleEU }, { "Europe/Brussels", 60, 120, RuleEU },
         { "Europe/Vienna", 60, 120, RuleEU }, { "Europe/Zurich", 60, 120, RuleEU }, { "Europe/Stockholm", 60, 120, RuleEU },
         { "Europe/Oslo", 60, 120, RuleEU }, { "Europe/Copenhagen", 60, 120, RuleEU }, { "Europe/Prague", 60, 120, RuleEU },
         { "Europe/Warsaw", 60, 120, RuleEU }, { "Europe/Budapest", 60, 120, RuleEU }, { "Europe/Belgrade", 60, 120, RuleEU },
         { "Europe/Zagreb", 60, 120, RuleEU }, { "Europe/Ljubljana", 60, 120, RuleEU }, { "Europe/Bratislava", 60, 120, RuleEU },
         { "Europe/Luxembourg", 60, 120, RuleEU }, { "Europe/Monaco", 60, 120, RuleEU }, { "Europe/Malta", 60, 120, RuleEU },
         { "Europe/Andorra", 60, 120, RuleEU }, { "Europe/Gibraltar", 60, 120, RuleEU }, { "Europe/Tirane", 60, 120, RuleEU },
         { "Europe/Sarajevo", 60, 120, RuleEU }, { "Europe/Skopje", 60, 120, RuleEU }, { "Europe/Podgorica", 60, 120, RuleEU },
         { "Europe/Vaduz", 60, 120, RuleEU }, { "Europe/San_Marino", 60, 120, RuleEU }, { "Europe/Vatican", 60, 120, RuleEU },
         { "Europe/Busingen", 60, 120, RuleEU }, { "Africa/Ceuta", 60, 120, RuleEU }, { "Arctic/Longyearbyen", 60, 120, RuleEU },
         { "CET", 60, 120, RuleEU }, { "MET", 60, 120, RuleEU }, { "Poland", 60, 120, RuleEU },
         { "Africa/Lagos", 60, 60, RuleNone }, { "Africa/Algiers", 60, 60, RuleNone }, { "Africa/Tunis", 60, 60, RuleNone },
         { "Africa/Kinshasa", 60, 60, RuleNone }, { "Africa/Luanda", 60, 60, RuleNone }, { "Africa/Douala", 60, 60, RuleNone },
         { "Africa/Brazzaville", 60, 60, RuleNone }, { "Africa/Libreville", 60, 60, RuleNone }, { "Africa/Malabo", 60, 60, RuleNone },
         { "Africa/Ndjamena", 60, 60, RuleNone }, { "Africa/Niamey", 60, 60, RuleNone }, { "Africa/Porto-Novo", 60, 60, RuleNone },
         { "Africa/Bangui", 60, 60, RuleNone },

         { "Europe/Helsinki", 120, 180, RuleEU }, { "Europe/Athens", 120, 180, RuleEU }, { "Europe/Kyiv", 120, 180, RuleEU },
         { "Europe/Kiev", 120, 180, RuleEU }, { "Europe/Bucharest", 120, 180, RuleEU }, { "Europe/Sofia", 120, 180, RuleEU },
         { "Europe/Riga", 120, 180, RuleEU }, { "Europe/Tallinn", 120, 180, RuleEU }, { "Europe/Vilnius", 120, 180, RuleEU },
         { "Europe/Mariehamn", 120, 180, RuleEU }, { "Asia/Nicosia", 120, 180, RuleEU }, { "Europe/Nicosia", 120, 180, RuleEU },
         { "Asia/Famagusta", 120, 180, RuleEU }, { "EET", 120, 180, RuleEU }, { "Europe/Chisinau", 120, 180, RuleMD },
         { "Africa/Cairo", 120, 180, RuleEG }, { "Egypt", 120, 180, RuleEG }, { "Asia/Beirut", 120, 180, RuleLB },
         { "Asia/Jerusalem", 120, 180, RuleIL }, { "Asia/Tel_Aviv", 120, 180, RuleIL }, { "Israel", 120, 180, RuleIL },
         { "Africa/Johannesburg", 120, 120, RuleNone }, { "Africa/Maputo", 120, 120, RuleNone }, { "Africa/Harare", 120, 120, RuleNone },
         { "Africa/Lusaka", 120, 120, RuleNone }, { "Africa/Gaborone", 120, 120, RuleNone }, { "Africa/Windhoek", 120, 120, RuleNone },
         { "Africa/Tripoli", 120, 120, RuleNone }, { "Africa/Khartoum", 120, 120, RuleNone }, { "Africa/Juba", 120, 120, RuleNone },
         { "Africa/Kigali", 120, 120, RuleNone }, { "Africa/Lubumbashi", 120, 120, RuleNone }, { "Africa/Blantyre", 120, 120, RuleNone },
         { "Europe/Kaliningrad", 120, 120, RuleNone }, { "Libya", 120, 120, RuleNone },

         { "Europe/Moscow", 180, 180, RuleNone }, { "W-SU", 180, 180, RuleNone }, { "Europe/Istanbul", 180, 180, RuleNone },
         { "Asia/Istanbul", 180, 180, RuleNone }, { "Turkey", 180, 180, RuleNone }, { "Europe/Minsk", 180, 180, RuleNone },
         { "Asia/Riyadh", 180, 180, RuleNone }, { "Asia/Baghdad", 180, 180, RuleNone }, { "Asia/Kuwait", 180, 180, RuleNone },
         { "Asia/Qatar", 180, 180, RuleNone }, { "Asia/Bahrain", 180, 180, RuleNone }, { "Asia/Aden", 180, 180, RuleNone },
         { "Asia/Amman", 180, 180, RuleNone }, { "Asia/Damascus", 180, 180, RuleNone }, { "Africa/Nairobi", 180, 180, RuleNone },
         { "Africa/Addis_Ababa", 180, 180, RuleNone }, { "Africa/Dar_es_Salaam", 180, 180, RuleNone }, { "Africa/Kampala", 180, 180, RuleNone },
         { "Africa/Mogadishu", 180, 180, RuleNone }, { "Africa/Djibouti", 180, 180, RuleNone }, { "Indian/Antananarivo", 180, 180, RuleNone },
         { "Europe/Kirov", 180, 180, RuleNone }, { "Europe/Volgograd", 180, 180, RuleNone }, { "Europe/Simferopol", 180, 180, RuleNone },
         { "Asia/Tehran", 210, 210, RuleNone }, { "Iran", 210, 210, RuleNone },
         { "Asia/Dubai", 240, 240, RuleNone }, { "Asia/Muscat", 240, 240, RuleNone }, { "Asia/Baku", 240, 240, RuleNone },
         { "Asia/Tbilisi", 240, 240, RuleNone }, { "Asia/Yerevan", 240, 240, RuleNone }, { "Europe/Samara", 240, 240, RuleNone },
         { "Europe/Astrakhan", 240, 240, RuleNone }, { "Europe/Saratov", 240, 240, RuleNone }, { "Europe/Ulyanovsk", 240, 240, RuleNone },
         { "Indian/Mauritius", 240, 240, RuleNone }, { "Indian/Reunion", 240, 240, RuleNone }, { "Indian/Mahe", 240, 240, RuleNone },
         { "Asia/Kabul", 270, 270, RuleNone },
         { "Asia/Karachi", 300, 300, RuleNone }, { "Asia/Tashkent", 300, 300, RuleNone }, { "Asia/Samarkand", 300, 300, RuleNone },
         { "Asia/Yekaterinburg", 300, 300, RuleNone }, { "Asia/Dushanbe", 300, 300, RuleNone }, { "Asia/Ashgabat", 300, 300, RuleNone },
         { "Asia/Aqtobe", 300, 300, RuleNone }, { "Asia/Aqtau", 300, 300, RuleNone }, { "Asia/Almaty", 300, 300, RuleNone },
         { "Asia/Qyzylorda", 300, 300, RuleNone }, { "Asia/Atyrau", 300, 300, RuleNone }, { "Asia/Oral", 300, 300, RuleNone },
         { "Indian/Maldives", 300, 300, RuleNone }, { "Indian/Kerguelen", 300, 300, RuleNone },
         { "Asia/Kolkata", 330, 330, RuleNone }, { "Asia/Calcutta", 330, 330, RuleNone }, { "Asia/Colombo", 330, 330, RuleNone },
         { "Asia/Kathmandu", 345, 345, RuleNone }, { "Asia/Katmandu", 345, 345, RuleNone },
         { "Asia/Dhaka", 360, 360, RuleNone }, { "Asia/Dacca", 360, 360, RuleNone }, { "Asia/Thimphu", 360, 360, RuleNone },
         { "Asia/Omsk", 360, 360, RuleNone }, { "Asia/Bishkek", 360, 360, RuleNone }, { "Asia/Urumqi", 360, 360, RuleNone },
         { "Indian/Chagos", 360, 360, RuleNone },
         { "Asia/Yangon", 390, 390, RuleNone }, { "Asia/Rangoon", 390, 390, RuleNone }, { "Indian/Cocos", 390, 390, RuleNone },
         { "Asia/Bangkok", 420, 420, RuleNone }, { "Asia/Jakarta", 420, 420, RuleNone }, { "Asia/Ho_Chi_Minh", 420, 420, RuleNone },
         { "Asia/Saigon", 420, 420, RuleNone }, { "Asia/Phnom_Penh", 420, 420, RuleNone }, { "Asia/Vientiane", 420, 420, RuleNone },
         { "Asia/Novosibirsk", 420, 420, RuleNone }, { "Asia/Krasnoyarsk", 420, 420, RuleNone }, { "Asia/Hovd", 420, 420, RuleNone },
         { "Asia/Pontianak", 420, 420, RuleNone }, { "Asia/Barnaul", 420, 420, RuleNone }, { "Asia/Tomsk", 420, 420, RuleNone },
         { "Asia/Novokuznetsk", 420, 420, RuleNone }, { "Indian/Christmas", 420, 420, RuleNone },
         { "Asia/Shanghai", 480, 480, RuleNone }, { "Asia/Chongqing", 480, 480, RuleNone }, { "Asia/Chungking", 480, 480, RuleNone },
         { "Asia/Harbin", 480, 480, RuleNone }, { "Asia/Hong_Kong", 480, 480, RuleNone }, { "Asia/Macau", 480, 480, RuleNone },
         { "Asia/Macao", 480, 480, RuleNone }, { "Asia/Taipei", 480, 480, RuleNone }, { "Asia/Singapore", 480, 480, RuleNone },
         { "Asia/Kuala_Lumpur", 480, 480, RuleNone }, { "Asia/Kuching", 480, 480, RuleNone }, { "Asia/Manila", 480, 480, RuleNone },
         { "Asia/Makassar", 480, 480, RuleNone }, { "Asia/Brunei", 480, 480, RuleNone }, { "Asia/Irkutsk", 480, 480, RuleNone },
         { "Asia/Ulaanbaatar", 480, 480, RuleNone }, { "Asia/Ulan_Bator", 480, 480, RuleNone }, { "Australia/Perth", 480, 480, RuleNone },
         { "Australia/West", 480, 480, RuleNone }, { "PRC", 480, 480, RuleNone }, { "Hongkong", 480, 480, RuleNone },
         { "Singapore", 480, 480, RuleNone }, { "ROC", 480, 480, RuleNone },
         { "Asia/Tokyo", 540, 540, RuleNone }, { "Asia/Seoul", 540, 540, RuleNone }, { "Asia/Pyongyang", 540, 540, RuleNone },
         { "Asia/Jayapura", 540, 540, RuleNone }, { "Asia/Yakutsk", 540, 540, RuleNone }, { "Asia/Chita", 540, 540, RuleNone },
         { "Asia/Khandyga", 540, 540, RuleNone }, { "Asia/Dili", 540, 540, RuleNone }, { "Pacific/Palau", 540, 540, RuleNone },
         { "Japan", 540, 540, RuleNone }, { "ROK", 540, 540, RuleNone },
         { "Australia/Darwin", 570, 570, RuleNone }, { "Australia/North", 570, 570, RuleNone },
         { "Australia/Adelaide", 570, 630, RuleAU }, { "Australia/Broken_Hill", 570, 630, RuleAU }, { "Australia/South", 570, 630, RuleAU },
         { "Australia/Yancowinna", 570, 630, RuleAU },
         { "Australia/Brisbane", 600, 600, RuleNone }, { "Australia/Lindeman", 600, 600, RuleNone }, { "Australia/Queensland", 600, 600, RuleNone },
         { "Pacific/Guam", 600, 600, RuleNone }, { "Pacific/Saipan", 600, 600, RuleNone }, { "Pacific/Port_Moresby", 600, 600, RuleNone },
         { "Pacific/Chuuk", 600, 600, RuleNone }, { "Asia/Vladivostok", 600, 600, RuleNone }, { "Asia/Ust-Nera", 600, 600, RuleNone },
         { "Australia/Sydney", 600, 660, RuleAU }, { "Australia/Melbourne", 600, 660, RuleAU }, { "Australia/Hobart", 600, 660, RuleAU },
         { "Australia/Canberra", 600, 660, RuleAU }, { "Australia/ACT", 600, 660, RuleAU }, { "Australia/NSW", 600, 660, RuleAU },
         { "Australia/Victoria", 600, 660, RuleAU }, { "Australia/Tasmania", 600, 660, RuleAU }, { "Australia/Currie", 600, 660, RuleAU },
         { "Antarctica/Macquarie", 600, 660, RuleAU },
         { "Pacific/Noumea", 660, 660, RuleNone }, { "Pacific/Guadalcanal", 660, 660, RuleNone }, { "Asia/Magadan", 660, 660, RuleNone },
         { "Asia/Sakhalin", 660, 660, RuleNone }, { "Asia/Srednekolymsk", 660, 660, RuleNone }, { "Pacific/Efate", 660, 660, RuleNone },
         { "Pacific/Bougainville", 660, 660, RuleNone }, { "Pacific/Kosrae", 660, 660, RuleNone }, { "Pacific/Pohnpei", 660, 660, RuleNone },
         { "Pacific/Auckland", 720, 780, RuleNZ }, { "NZ", 720, 780, RuleNZ }, { "Antarctica/McMurdo", 720, 780, RuleNZ },
         { "Antarctica/South_Pole", 720, 780, RuleNZ },
         { "Pacific/Fiji", 720, 720, RuleNone }, { "Asia/Kamchatka", 720, 720, RuleNone }, { "Asia/Anadyr", 720, 720, RuleNone },
         { "Pacific/Tarawa", 720, 720, RuleNone }, { "Pacific/Majuro", 720, 720, RuleNone }, { "Pacific/Kwajalein", 720, 720, RuleNone },
         { "Pacific/Funafuti", 720, 720, RuleNone }, { "Pacific/Wallis", 720, 720, RuleNone }, { "Pacific/Nauru", 720, 720, RuleNone },
         { "Pacific/Wake", 720, 720, RuleNone }, { "Pacific/Norfolk", 660, 720, RuleAU },
         { "Pacific/Tongatapu", 780, 780, RuleNone }, { "Pacific/Apia", 780, 780, RuleNone }, { "Pacific/Fakaofo", 780, 780, RuleNone },
         { "Pacific/Kanton", 780, 780, RuleNone }, { "Pacific/Enderbury", 780, 780, RuleNone },
         { "Pacific/Kiritimati", 840, 840, RuleNone },

         { "Atlantic/Azores", -60, 0, RuleEU }, { "Atlantic/Cape_Verde", -60, -60, RuleNone },
         { "America/Noronha", -120, -120, RuleNone }, { "Atlantic/South_Georgia", -120, -120, RuleNone },
         { "America/Sao_Paulo", -180, -180, RuleNone }, { "Brazil/East", -180, -180, RuleNone }, { "America/Argentina/Buenos_Aires", -180, -180, RuleNone },
         { "America/Buenos_Aires", -180, -180, RuleNone }, { "America/Argentina/Cordoba", -180, -180, RuleNone }, { "America/Cordoba", -180, -180, RuleNone },
         { "America/Argentina/Mendoza", -180, -180, RuleNone }, { "America/Montevideo", -180, -180, RuleNone }, { "America/Cayenne", -180, -180, RuleNone },
         { "America/Paramaribo", -180, -180, RuleNone }, { "America/Fortaleza", -180, -180, RuleNone }, { "America/Recife", -180, -180, RuleNone },
         { "America/Bahia", -180, -180, RuleNone }, { "America/Belem", -180, -180, RuleNone }, { "America/Maceio", -180, -180, RuleNone },
         { "America/Araguaina", -180, -180, RuleNone }, { "America/Santarem", -180, -180, RuleNone }, { "America/Punta_Arenas", -180, -180, RuleNone },
         { "America/Asuncion", -180, -180, RuleNone }, { "Atlantic/Stanley", -180, -180, RuleNone }, { "Antarctica/Palmer", -180, -180, RuleNone },
         { "Antarctica/Rothera", -180, -180, RuleNone },
         { "America/St_Johns", -210, -150, RuleUS }, { "Canada/Newfoundland", -210, -150, RuleUS },
         { "America/Halifax", -240, -180, RuleUS }, { "America/Glace_Bay", -240, -180, RuleUS }, { "America/Moncton", -240, -180, RuleUS },
         { "America/Goose_Bay", -240, -180, RuleUS }, { "Atlantic/Bermuda", -240, -180, RuleUS }, { "America/Thule", -240, -180, RuleUS },
         { "Canada/Atlantic", -240, -180, RuleUS },
         { "America/Santiago", -240, -180, RuleCL }, { "Chile/Continental", -240, -180, RuleCL },
         { "America/Caracas", -240, -240, RuleNone }, { "America/La_Paz", -240, -240, RuleNone }, { "America/Manaus", -240, -240, RuleNone },
         { "Brazil/West", -240, -240, RuleNone }, { "America/Puerto_Rico", -240, -240, RuleNone }, { "America/Santo_Domingo", -240, -240, RuleNone },
         { "America/Barbados", -240, -240, RuleNone }, { "America/Martinique", -240, -240, RuleNone }, { "America/Port_of_Spain", -240, -240, RuleNone },
         { "America/Guyana", -240, -240, RuleNone }, { "America/Campo_Grande", -240, -240, RuleNone }, { "America/Cuiaba", -240, -240, RuleNone },
         { "America/Porto_Velho", -240, -240, RuleNone }, { "America/Boa_Vista", -240, -240, RuleNone }, { "America/Anguilla", -240, -240, RuleNone },
         { "America/Antigua", -240, -240, RuleNone }, { "America/Aruba", -240, -240, RuleNone }, { "America/Curacao", -240, -240, RuleNone },
         { "America/Dominica", -240, -240, RuleNone }, { "America/Grenada", -240, -240, RuleNone }, { "America/Guadeloupe", -240, -240, RuleNone },
         { "America/St_Lucia", -240, -240, RuleNone }, { "America/St_Thomas", -240, -240, RuleNone }, { "America/St_Vincent", -240, -240, RuleNone },
         { "America/Tortola", -240, -240, RuleNone }, { "America/Blanc-Sablon", -240, -240, RuleNone },
         { "America/New_York", -300, -240, RuleUS }, { "America/Toronto", -300, -240, RuleUS }, { "America/Detroit", -300, -240, RuleUS },
         { "America/Montreal", -300, -240, RuleUS }, { "America/Nassau", -300, -240, RuleUS }, { "America/Indiana/Indianapolis", -300, -240, RuleUS },
         { "America/Indianapolis", -300, -240, RuleUS }, { "America/Fort_Wayne", -300, -240, RuleUS }, { "America/Kentucky/Louisville", -300, -240, RuleUS },
         { "America/Louisville", -300, -240, RuleUS }, { "America/Kentucky/Monticello", -300, -240, RuleUS }, { "America/Iqaluit", -300, -240, RuleUS },
         { "America/Port-au-Prince", -300, -240, RuleUS }, { "America/Grand_Turk", -300, -240, RuleUS }, { "America/Nipigon", -300, -240, RuleUS },
         { "America/Thunder_Bay", -300, -240, RuleUS }, { "America/Pangnirtung", -300, -240, RuleUS }, { "US/Eastern", -300, -240, RuleUS },
         { "US/Michigan", -300, -240, RuleUS }, { "US/East-Indiana", -300, -240, RuleUS }, { "Canada/Eastern", -300, -240, RuleUS },
         { "EST5EDT", -300, -240, RuleUS },
         { "America/Havana", -300, -240, RuleCU }, { "Cuba", -300, -240, RuleCU },
         { "America/Bogota", -300, -300, RuleNone }, { "America/Lima", -300, -300, RuleNone }, { "America/Guayaquil", -300, -300, RuleNone },
         { "America/Panama", -300, -300, RuleNone }, { "America/Jamaica", -300, -300, RuleNone }, { "Jamaica", -300, -300, RuleNone },
         { "America/Cancun", -300, -300, RuleNone }, { "America/Cayman", -300, -300, RuleNone }, { "America/Rio_Branco", -300, -300, RuleNone },
         { "America/Eirunepe", -300, -300, RuleNone }, { "America/Atikokan", -300, -300, RuleNone }, { "America/Coral_Harbour", -300, -300, RuleNone },
         { "Brazil/Acre", -300, -300, RuleNone }, { "EST", -300, -300, RuleNone },
         { "America/Chicago", -360, -300, RuleUS }, { "America/Winnipeg", -360, -300, RuleUS }, { "America/Menominee", -360, -300, RuleUS },
         { "America/Indiana/Knox", -360, -300, RuleUS }, { "America/Knox_IN", -360, -300, RuleUS }, { "America/Indiana/Tell_City", -360, -300, RuleUS },
         { "America/North_Dakota/Center", -360, -300, RuleUS }, { "America/North_Dakota/New_Salem", -360, -300, RuleUS },
         { "America/North_Dakota/Beulah", -360, -300, RuleUS }, { "America/Matamoros", -360, -300, RuleUS }, { "America/Ojinaga", -360, -300, RuleUS },
         { "America/Rainy_River", -360, -300, RuleUS }, { "America/Rankin_Inlet", -360, -300, RuleUS }, { "America/Resolute", -360, -300, RuleUS },
         { "US/Central", -360, -300, RuleUS }, { "US/Indiana-Starke", -360, -300, RuleUS }, { "Canada/Central", -360, -300, RuleUS },
         { "CST6CDT", -360, -300, RuleUS },
         { "America/Mexico_City", -360, -360, RuleNone }, { "Mexico/General", -360, -360, RuleNone }, { "America/Guatemala", -360, -360, RuleNone },
         { "America/Belize", -360, -360, RuleNone }, { "America/El_Salvador", -360, -360, RuleNone }, { "America/Tegucigalpa", -360, -360, RuleNone },
         { "America/Managua", -360, -360, RuleNone }, { "America/Costa_Rica", -360, -360, RuleNone }, { "America/Regina", -360, -360, RuleNone },
         { "America/Swift_Current", -360, -360, RuleNone }, { "America/Merida", -360, -360, RuleNone }, { "America/Monterrey", -360, -360, RuleNone },
         { "America/Bahia_Banderas", -360, -360, RuleNone }, { "America/Chihuahua", -360, -360, RuleNone }, { "Pacific/Galapagos", -360, -360, RuleNone },
         { "Canada/Saskatchewan", -360, -360, RuleNone },
         { "America/Denver", -420, -360, RuleUS }, { "America/Edmonton", -420, -360, RuleUS }, { "America/Boise", -420, -360, RuleUS },
         { "America/Yellowknife", -420, -360, RuleUS }, { "America/Inuvik", -420, -360, RuleUS }, { "America/Cambridge_Bay", -420, -360, RuleUS },
         { "America/Ciudad_Juarez", -420, -360, RuleUS }, { "America/Shiprock", -420, -360, RuleUS }, { "US/Mountain", -420, -360, RuleUS },
         { "Canada/Mountain", -420, -360, RuleUS }, { "MST7MDT", -420, -360, RuleUS }, { "Navajo", -420, -360, RuleUS },
         { "America/Phoenix", -420, -420, RuleNone }, { "America/Hermosillo", -420, -420, RuleNone }, { "America/Mazatlan", -420, -420, RuleNone },
         { "Mexico/BajaSur", -420, -420, RuleNone }, { "America/Creston", -420, -420, RuleNone }, { "America/Dawson_Creek", -420, -420, RuleNone },
         { "America/Fort_Nelson", -420, -420, RuleNone }, { "America/Whitehorse", -420, -420, RuleNone }, { "America/Dawson", -420, -420, RuleNone },
         { "Canada/Yukon", -420, -420, RuleNone }, { "US/Arizona", -420, -420, RuleNone }, { "MST", -420, -420, RuleNone },
         { "America/Los_Angeles", -480, -420, RuleUS }, { "America/Vancouver", -480, -420, RuleUS }, { "America/Tijuana", -480, -420, RuleUS },
         { "America/Ensenada", -480, -420, RuleUS }, { "Mexico/BajaNorte", -480, -420, RuleUS }, { "US/Pacific", -480, -420, RuleUS },
         { "Canada/Pacific", -480, -420, RuleUS }, { "PST8PDT", -480, -420, RuleUS },
         { "Pacific/Pitcairn", -480, -480, RuleNone },
         { "America/Anchorage", -540, -480, RuleUS }, { "America/Juneau", -540, -480, RuleUS }, { "America/Sitka", -540, -480, RuleUS },
         { "America/Nome", -540, -480, RuleUS }, { "America/Yakutat", -540, -480, RuleUS }, { "America/Metlakatla", -540, -480, RuleUS },
         { "US/Alaska", -540, -480, RuleUS },
         { "Pacific/Gambier", -540, -540, RuleNone }, { "Pacific/Marquesas", -570, -570, RuleNone },
         { "America/Adak", -600, -540, RuleUS }, { "America/Atka", -600, -540, RuleUS }, { "US/Aleutian", -600, -540, RuleUS },
         { "Pacific/Honolulu", -600, -600, RuleNone }, { "Pacific/Johnston", -600, -600, RuleNone }, { "Pacific/Tahiti", -600, -600, RuleNone },
         { "Pacific/Rarotonga", -600, -600, RuleNone }, { "US/Hawaii", -600, -600, RuleNone }, { "HST", -600, -600, RuleNone },
         { "Pacific/Pago_Pago", -660, -660, RuleNone }, { "Pacific/Samoa", -660, -660, RuleNone }, { "Pacific/Midway", -660, -660, RuleNone },
         { "Pacific/Niue", -660, -660, RuleNone }, { "US/Samoa", -660, -660, RuleNone },

         // The names Windows and Outlook write.
         { "GMT Standard Time", 0, 60, RuleEU }, { "Greenwich Standard Time", 0, 0, RuleNone }, { "W. Europe Standard Time", 60, 120, RuleEU },
         { "Romance Standard Time", 60, 120, RuleEU }, { "Central Europe Standard Time", 60, 120, RuleEU },
         { "Central European Standard Time", 60, 120, RuleEU }, { "W. Central Africa Standard Time", 60, 60, RuleNone },
         { "E. Europe Standard Time", 120, 180, RuleMD }, { "FLE Standard Time", 120, 180, RuleEU }, { "GTB Standard Time", 120, 180, RuleEU },
         { "Egypt Standard Time", 120, 180, RuleEG }, { "Israel Standard Time", 120, 180, RuleIL }, { "Middle East Standard Time", 120, 180, RuleLB },
         { "South Africa Standard Time", 120, 120, RuleNone }, { "Kaliningrad Standard Time", 120, 120, RuleNone }, { "Libya Standard Time", 120, 120, RuleNone },
         { "Jordan Standard Time", 180, 180, RuleNone }, { "Syria Standard Time", 180, 180, RuleNone },
         { "Russian Standard Time", 180, 180, RuleNone }, { "Turkey Standard Time", 180, 180, RuleNone }, { "Belarus Standard Time", 180, 180, RuleNone },
         { "Arab Standard Time", 180, 180, RuleNone }, { "Arabic Standard Time", 180, 180, RuleNone }, { "E. Africa Standard Time", 180, 180, RuleNone },
         { "Iran Standard Time", 210, 210, RuleNone }, { "Arabian Standard Time", 240, 240, RuleNone }, { "Azerbaijan Standard Time", 240, 240, RuleNone },
         { "Georgian Standard Time", 240, 240, RuleNone }, { "Caucasus Standard Time", 240, 240, RuleNone }, { "Mauritius Standard Time", 240, 240, RuleNone },
         { "Russia Time Zone 3", 240, 240, RuleNone }, { "Afghanistan Standard Time", 270, 270, RuleNone },
         { "Pakistan Standard Time", 300, 300, RuleNone }, { "West Asia Standard Time", 300, 300, RuleNone }, { "Ekaterinburg Standard Time", 300, 300, RuleNone },
         { "Central Asia Standard Time", 300, 300, RuleNone },
         { "India Standard Time", 330, 330, RuleNone }, { "Sri Lanka Standard Time", 330, 330, RuleNone }, { "Nepal Standard Time", 345, 345, RuleNone },
         { "Bangladesh Standard Time", 360, 360, RuleNone }, { "Omsk Standard Time", 360, 360, RuleNone }, { "Myanmar Standard Time", 390, 390, RuleNone },
         { "SE Asia Standard Time", 420, 420, RuleNone }, { "N. Central Asia Standard Time", 420, 420, RuleNone }, { "North Asia Standard Time", 420, 420, RuleNone },
         { "China Standard Time", 480, 480, RuleNone }, { "Singapore Standard Time", 480, 480, RuleNone }, { "Taipei Standard Time", 480, 480, RuleNone },
         { "W. Australia Standard Time", 480, 480, RuleNone }, { "North Asia East Standard Time", 480, 480, RuleNone }, { "Ulaanbaatar Standard Time", 480, 480, RuleNone },
         { "Tokyo Standard Time", 540, 540, RuleNone }, { "Korea Standard Time", 540, 540, RuleNone }, { "Yakutsk Standard Time", 540, 540, RuleNone },
         { "AUS Central Standard Time", 570, 570, RuleNone }, { "Cen. Australia Standard Time", 570, 630, RuleAU },
         { "E. Australia Standard Time", 600, 600, RuleNone }, { "AUS Eastern Standard Time", 600, 660, RuleAU }, { "Tasmania Standard Time", 600, 660, RuleAU },
         { "West Pacific Standard Time", 600, 600, RuleNone }, { "Vladivostok Standard Time", 600, 600, RuleNone },
         { "Central Pacific Standard Time", 660, 660, RuleNone }, { "Magadan Standard Time", 660, 660, RuleNone },
         { "New Zealand Standard Time", 720, 780, RuleNZ }, { "Fiji Standard Time", 720, 720, RuleNone }, { "UTC+12", 720, 720, RuleNone },
         { "Tonga Standard Time", 780, 780, RuleNone }, { "Samoa Standard Time", 780, 780, RuleNone }, { "Line Islands Standard Time", 840, 840, RuleNone },
         { "Azores Standard Time", -60, 0, RuleEU }, { "Cape Verde Standard Time", -60, -60, RuleNone }, { "UTC-02", -120, -120, RuleNone },
         { "E. South America Standard Time", -180, -180, RuleNone }, { "Argentina Standard Time", -180, -180, RuleNone },
         { "SA Eastern Standard Time", -180, -180, RuleNone }, { "Montevideo Standard Time", -180, -180, RuleNone }, { "Paraguay Standard Time", -180, -180, RuleNone },
         { "Newfoundland Standard Time", -210, -150, RuleUS }, { "Atlantic Standard Time", -240, -180, RuleUS },
         { "Pacific SA Standard Time", -240, -180, RuleCL }, { "SA Western Standard Time", -240, -240, RuleNone }, { "Venezuela Standard Time", -240, -240, RuleNone },
         { "Central Brazilian Standard Time", -240, -240, RuleNone }, { "Eastern Standard Time", -300, -240, RuleUS },
         { "US Eastern Standard Time", -300, -240, RuleUS }, { "Haiti Standard Time", -300, -240, RuleUS }, { "Cuba Standard Time", -300, -240, RuleCU },
         { "SA Pacific Standard Time", -300, -300, RuleNone }, { "Eastern Standard Time (Mexico)", -300, -300, RuleNone },
         { "Central Standard Time", -360, -300, RuleUS }, { "Canada Central Standard Time", -360, -360, RuleNone },
         { "Central Standard Time (Mexico)", -360, -360, RuleNone }, { "Central America Standard Time", -360, -360, RuleNone },
         { "Mountain Standard Time", -420, -360, RuleUS }, { "US Mountain Standard Time", -420, -420, RuleNone },
         { "Mountain Standard Time (Mexico)", -420, -420, RuleNone }, { "Yukon Standard Time", -420, -420, RuleNone },
         { "Pacific Standard Time", -480, -420, RuleUS }, { "Pacific Standard Time (Mexico)", -480, -420, RuleUS },
         { "Alaskan Standard Time", -540, -480, RuleUS }, { "Aleutian Standard Time", -600, -540, RuleUS }, { "Hawaiian Standard Time", -600, -600, RuleNone },
         { "UTC-11", -660, -660, RuleNone }, { "Dateline Standard Time", -720, -720, RuleNone },
         { "UTC-09", -540, -540, RuleNone }, { "UTC-08", -480, -480, RuleNone }, { "UTC+13", 780, 780, RuleNone },
         { "Coordinated Universal Time", 0, 0, RuleNone },
      };

      // A yearly observance from a rule family's half: the month, the weekday
      // and ordinal, the wall-clock time, and the days added.
      Observance RuleHalf(int offsetFrom, int offsetTo, int month, int weekday, int ordinal, int seconds, int dayOffset)
      {
         Observance o;
         o.offsetFrom = offsetFrom;
         o.offsetTo = offsetTo;
         o.startYear = 1970;
         o.startMonth = month;
         o.startDay = 1;
         o.startSeconds = seconds;
         o.yearly = true;
         o.ruleMonth = month;
         o.ruleWeekday = weekday;
         o.ruleOrdinal = ordinal;
         o.ruleDayOffset = dayOffset;
         return o;
      }

      void BuiltInToZone(const BuiltInZone &entry, Zone &zone)
      {
         zone = Zone();
         int standard = entry.standardMinutes * 60;
         int daylight = entry.daylightMinutes * 60;

         if (entry.family == RuleNone || standard == daylight)
         {
            Observance fixed;
            fixed.offsetFrom = standard;
            fixed.offsetTo = standard;
            zone.observances.push_back(fixed);
            zone.valid = true;
            return;
         }

         const int SU = 0, TH = 4, FR = 5;
         switch (entry.family)
         {
         case RuleEU:
            // 01:00 UTC both ways: as a wall-clock time, 01:00 + the offset in force.
            zone.observances.push_back(RuleHalf(standard, daylight, 3, SU, -1, 3600 + standard, 0));
            zone.observances.push_back(RuleHalf(daylight, standard, 10, SU, -1, 3600 + daylight, 0));
            break;
         case RuleUS:
            zone.observances.push_back(RuleHalf(standard, daylight, 3, SU, 2, 2 * 3600, 0));
            zone.observances.push_back(RuleHalf(daylight, standard, 11, SU, 1, 2 * 3600, 0));
            break;
         case RuleAU:
            zone.observances.push_back(RuleHalf(standard, daylight, 10, SU, 1, 2 * 3600, 0));
            zone.observances.push_back(RuleHalf(daylight, standard, 4, SU, 1, 3 * 3600, 0));
            break;
         case RuleNZ:
            zone.observances.push_back(RuleHalf(standard, daylight, 9, SU, -1, 2 * 3600, 0));
            zone.observances.push_back(RuleHalf(daylight, standard, 4, SU, 1, 3 * 3600, 0));
            break;
         case RuleCL:
            zone.observances.push_back(RuleHalf(standard, daylight, 9, SU, 1, 0, 0));
            zone.observances.push_back(RuleHalf(daylight, standard, 4, SU, 1, 0, 0));
            break;
         case RuleEG:
            zone.observances.push_back(RuleHalf(standard, daylight, 4, FR, -1, 0, 0));
            zone.observances.push_back(RuleHalf(daylight, standard, 10, TH, -1, 24 * 3600, 0));
            break;
         case RuleLB:
            zone.observances.push_back(RuleHalf(standard, daylight, 3, SU, -1, 0, 0));
            zone.observances.push_back(RuleHalf(daylight, standard, 10, SU, -1, 0, 0));
            break;
         case RuleCU:
            zone.observances.push_back(RuleHalf(standard, daylight, 3, SU, 2, 0, 0));
            zone.observances.push_back(RuleHalf(daylight, standard, 11, SU, 1, 3600, 0));
            break;
         case RuleMD:
            zone.observances.push_back(RuleHalf(standard, daylight, 3, SU, -1, 2 * 3600, 0));
            zone.observances.push_back(RuleHalf(daylight, standard, 10, SU, -1, 3 * 3600, 0));
            break;
         case RuleIL:
            zone.observances.push_back(RuleHalf(standard, daylight, 3, SU, -1, 2 * 3600, -2));
            zone.observances.push_back(RuleHalf(daylight, standard, 10, SU, -1, 2 * 3600, 0));
            break;
         default:
            break;
         }
         zone.valid = true;
      }

      // "Etc/GMT+5" is five hours WEST of Greenwich (the POSIX sign), "Etc/GMT-3" three east.
      bool EtcOffset(const AnsiString &name, int &minutes)
      {
         AnsiString rest;
         if (name.StartsWith("Etc/GMT"))
            rest = name.Mid(7);
         else if (name.StartsWith("GMT") && name.GetLength() > 3 && (name[3] == '+' || name[3] == '-'))
            rest = name.Mid(3);
         else
            return false;
         if (rest.GetLength() < 2 || rest.GetLength() > 3 || (rest[0] != '+' && rest[0] != '-'))
            return false;
         int hours = 0;
         if (!SmallInt(rest.Mid(1), hours) || hours < 0 || hours > 14)
            return false;
         minutes = (rest[0] == '+' ? -hours : hours) * 60;
         return true;
      }

      bool BuiltIn(const AnsiString &tzid, Zone &zone)
      {
         AnsiString name = Trimmed(tzid);
         for (size_t i = 0; i < sizeof(BuiltInZones) / sizeof(BuiltInZones[0]); i++)
         {
            if (name.CompareNoCase(BuiltInZones[i].name) == 0)
            {
               BuiltInToZone(BuiltInZones[i], zone);
               return true;
            }
         }

         int minutes = 0;
         if (EtcOffset(name, minutes))
         {
            BuiltInZone fixed = { "", minutes, minutes, RuleNone };
            BuiltInToZone(fixed, zone);
            return true;
         }

         // "/mozilla.org/20070129_1/Europe/London", "/freeassociation.sourceforge.net/Europe/London":
         // the last two segments name the zone.
         if (name.StartsWith("/"))
         {
            std::vector<AnsiString> segments = SplitOn(name, '/');
            if (segments.size() >= 3)
            {
               AnsiString tail = segments[segments.size() - 2] + "/" + segments[segments.size() - 1];
               for (size_t i = 0; i < sizeof(BuiltInZones) / sizeof(BuiltInZones[0]); i++)
               {
                  if (tail.CompareNoCase(BuiltInZones[i].name) == 0)
                  {
                     BuiltInToZone(BuiltInZones[i], zone);
                     return true;
                  }
               }
            }
         }

         return false;
      }

      // A VTIMEZONE component into a zone. False, with the reason, for an
      // observance this server cannot follow.
      bool ZoneFromComponent(const ICalComponent &vtimezone, Zone &zone, AnsiString &problem)
      {
         zone = Zone();
         for (size_t i = 0; i < vtimezone.children.size(); i++)
         {
            const ICalComponent &child = vtimezone.children[i];
            if (child.name != "STANDARD" && child.name != "DAYLIGHT")
               continue;

            Observance o;
            const ICalProperty *from = child.Property("TZOFFSETFROM");
            const ICalProperty *to = child.Property("TZOFFSETTO");
            const ICalProperty *start = child.Property("DTSTART");
            if (!from || !to || !start || !ParseOffset(from->value, o.offsetFrom) || !ParseOffset(to->value, o.offsetTo))
            {
               problem = "a " + child.name + " observance of VTIMEZONE " + vtimezone.Value("TZID") + " lacks TZOFFSETFROM, TZOFFSETTO or DTSTART";
               return false;
            }

            bool isDate, utc;
            int hh, mm, ss;
            if (!ParseDateFields(start->value, isDate, utc, o.startYear, o.startMonth, o.startDay, hh, mm, ss))
            {
               problem = "the DTSTART of a " + child.name + " observance is not a date-time";
               return false;
            }
            o.startSeconds = hh * 3600 + mm * 60 + ss;

            if (const ICalProperty *rrule = child.Property("RRULE"))
            {
               std::vector<AnsiString> parts = SplitOn(Upper(rrule->value), ';');
               bool yearly = false;
               for (size_t p = 0; p < parts.size(); p++)
               {
                  int equals = parts[p].Find("=");
                  if (equals < 0)
                     continue;
                  AnsiString key = Trimmed(parts[p].Mid(0, equals));
                  AnsiString value = Trimmed(parts[p].Mid(equals + 1));
                  if (key == "FREQ")
                     yearly = value == "YEARLY";
                  else if (key == "BYMONTH")
                  {
                     if (!SmallInt(value, o.ruleMonth) || o.ruleMonth < 1 || o.ruleMonth > 12)
                     {
                        problem = "a VTIMEZONE rule has a BYMONTH this server cannot read";
                        return false;
                     }
                  }
                  else if (key == "BYDAY")
                  {
                     if (value.GetLength() < 3)
                     {
                        problem = "a VTIMEZONE rule has a BYDAY this server cannot read";
                        return false;
                     }
                     AnsiString dayName = value.Mid(value.GetLength() - 2);
                     o.ruleWeekday = WeekdayOf(dayName);
                     if (o.ruleWeekday < 0 || !SmallInt(value.Mid(0, value.GetLength() - 2), o.ruleOrdinal) || o.ruleOrdinal == 0 || o.ruleOrdinal > 5 || o.ruleOrdinal < -5)
                     {
                        problem = "a VTIMEZONE rule has a BYDAY this server cannot read";
                        return false;
                     }
                  }
                  else if (key == "BYMONTHDAY")
                  {
                     // A single day; a list (the "the Sunday on or after the 8th"
                     // form) is not followed.
                     if (!SmallInt(value, o.ruleMonthDay) || o.ruleMonthDay == 0 || o.ruleMonthDay > 31 || o.ruleMonthDay < -31)
                     {
                        problem = "a VTIMEZONE rule has a BYMONTHDAY this server cannot read";
                        return false;
                     }
                  }
                  else if (key == "UNTIL")
                  {
                     bool uDate, uUtc;
                     int uy, um, ud, uh, umi, us;
                     if (!ParseDateFields(value, uDate, uUtc, uy, um, ud, uh, umi, us))
                     {
                        problem = "a VTIMEZONE rule has an UNTIL this server cannot read";
                        return false;
                     }
                     o.untilInstant = SecondsOf(uy, um, ud, uh, umi, us);
                     if (!uUtc)
                        o.untilInstant -= o.offsetFrom;
                  }
                  else if (key == "INTERVAL")
                  {
                     int interval = 1;
                     if (!SmallInt(value, interval) || interval != 1)
                     {
                        problem = "a VTIMEZONE rule with an INTERVAL other than 1 is not followed";
                        return false;
                     }
                  }
                  else if (key == "WKST" || key == "COUNT")
                  {
                     // Neither changes a yearly onset.
                  }
                  else
                  {
                     problem = "a VTIMEZONE rule part this server does not follow: " + key;
                     return false;
                  }
               }
               if (!yearly)
               {
                  problem = "a VTIMEZONE observance recurs other than yearly, which this server does not follow";
                  return false;
               }
               if (o.ruleWeekday >= 0 && o.ruleMonthDay != 0)
               {
                  problem = "a VTIMEZONE rule names both BYDAY and BYMONTHDAY, which this server does not follow";
                  return false;
               }
               o.yearly = true;
            }

            for (size_t p = 0; p < child.properties.size(); p++)
            {
               const ICalProperty &property = child.properties[p];
               if (property.name != "RDATE")
                  continue;
               std::vector<AnsiString> values = SplitOn(property.value, ',');
               for (size_t v = 0; v < values.size() && o.rdates.size() < 500; v++)
               {
                  bool rDate, rUtc;
                  int ry, rm, rd, rh, rmi, rs;
                  if (!ParseDateFields(values[v], rDate, rUtc, ry, rm, rd, rh, rmi, rs))
                     continue;
                  __int64 local = SecondsOf(ry, rm, rd, rh, rmi, rs);
                  if (rUtc)
                     local += o.offsetFrom;
                  o.rdates.push_back(local);
               }
            }

            zone.observances.push_back(o);
            if (zone.observances.size() > 64)
            {
               problem = "a VTIMEZONE with more than 64 observances";
               return false;
            }
         }

         if (zone.observances.empty())
         {
            problem = "VTIMEZONE " + vtimezone.Value("TZID") + " has no STANDARD or DAYLIGHT observance";
            return false;
         }

         zone.valid = true;
         return true;
      }

      // The zones one expansion resolves, each once: the calendar's own
      // VTIMEZONEs first, then the built-in table.
      class ZoneCache
      {
      public:
         explicit ZoneCache(const ICalComponent *calendar) : calendar_(calendar) { }

         Zone *Find(const AnsiString &tzid, AnsiString &problem)
         {
            std::map<AnsiString, Zone>::iterator found = zones_.find(tzid);
            if (found != zones_.end())
               return found->second.valid ? &found->second : nullptr;

            Zone &zone = zones_[tzid];
            if (calendar_)
            {
               for (size_t i = 0; i < calendar_->children.size(); i++)
               {
                  const ICalComponent &child = calendar_->children[i];
                  if (child.name == "VTIMEZONE" && child.Value("TZID") == tzid)
                  {
                     if (!ZoneFromComponent(child, zone, problem))
                     {
                        zone.valid = false;
                        return nullptr;
                     }
                     return &zone;
                  }
               }
            }

            if (BuiltIn(tzid, zone))
               return &zone;

            problem = "the time zone " + tzid + " is neither in the object as a VTIMEZONE nor one this server knows";
            zone.valid = false;
            return nullptr;
         }

      private:
         const ICalComponent *calendar_;
         std::map<AnsiString, Zone> zones_;
      };

      // A DATE or DATE-TIME property to an ICalTime through the cache.
      bool ResolveTime(const ICalProperty &property, const AnsiString &rawValue, ZoneCache &zones, ICalTime &time, AnsiString &problem)
      {
         time = ICalTime();
         if (!ParseDateFields(rawValue, time.isDate, time.utc, time.year, time.month, time.day, time.hour, time.minute, time.second))
         {
            problem = property.name + " is not a DATE or DATE-TIME: " + Trimmed(rawValue);
            return false;
         }
         if (Upper(property.Parameter("VALUE")) == "DATE" && !time.isDate)
         {
            problem = property.name + " says VALUE=DATE but carries a time";
            return false;
         }

         __int64 local = SecondsOf(time.year, time.month, time.day, time.hour, time.minute, time.second);
         time.tzid = property.Parameter("TZID");

         if (time.isDate || time.utc)
         {
            time.instant = local;
            return true;
         }

         if (time.tzid.IsEmpty())
         {
            time.floating = true;
            time.instant = local;
            return true;
         }

         Zone *zone = zones.Find(time.tzid, problem);
         if (!zone)
            return false;
         time.instant = LocalToUtc(*zone, local);
         return true;
      }

      //------------------------------------------------------------------------
      // Recurrence
      //------------------------------------------------------------------------

      enum Frequency
      {
         FreqNone,
         FreqDaily,
         FreqWeekly,
         FreqMonthly,
         FreqYearly
      };

      struct ByDay
      {
         int weekday;
         int ordinal;   // 0 = every such weekday
      };

      struct Rule
      {
         Rule() : freq(FreqNone), interval(1), count(0), hasUntil(false), untilIsDate(false), untilUtc(false), untilLocal(0), untilInstant(0), wkst(1) { }

         Frequency freq;
         int interval;
         int count;
         bool hasUntil;
         bool untilIsDate;
         bool untilUtc;
         __int64 untilLocal;     // a floating or DATE UNTIL, as wall-clock seconds (DATE: the day's end)
         __int64 untilInstant;   // a UTC UNTIL
         std::vector<int> byMonth, byMonthDay, byYearDay, byWeekNo, bySetPos;
         std::vector<ByDay> byDay;
         int wkst;
      };

      bool ReadIntList(const AnsiString &value, int low, int high, bool allowNegative, std::vector<int> &out, size_t limit)
      {
         std::vector<AnsiString> items = SplitOn(value, ',');
         if (items.size() > limit)
            return false;
         for (size_t i = 0; i < items.size(); i++)
         {
            int v = 0;
            if (!SmallInt(items[i], v) || (v == 0 && low > 0))
               return false;
            if (v < 0 && !allowNegative)
               return false;
            int magnitude = v < 0 ? -v : v;
            if (magnitude < low || magnitude > high)
               return false;
            out.push_back(v);
         }
         return true;
      }

      bool ParseRule(const AnsiString &text, Rule &rule, AnsiString &problem)
      {
         rule = Rule();
         std::vector<AnsiString> parts = SplitOn(Upper(Trimmed(text)), ';');
         if (parts.size() > 20)
         {
            problem = "an RRULE with more than twenty parts";
            return false;
         }
         for (size_t i = 0; i < parts.size(); i++)
         {
            if (Trimmed(parts[i]).IsEmpty())
               continue;
            int equals = parts[i].Find("=");
            if (equals <= 0)
            {
               problem = "an RRULE part without a value: " + parts[i];
               return false;
            }
            AnsiString key = Trimmed(parts[i].Mid(0, equals));
            AnsiString value = Trimmed(parts[i].Mid(equals + 1));

            if (key == "FREQ")
            {
               if (value == "DAILY") rule.freq = FreqDaily;
               else if (value == "WEEKLY") rule.freq = FreqWeekly;
               else if (value == "MONTHLY") rule.freq = FreqMonthly;
               else if (value == "YEARLY") rule.freq = FreqYearly;
               else
               {
                  problem = "an RRULE with FREQ=" + value + ", which this server does not expand; DAILY, WEEKLY, MONTHLY and YEARLY are expanded";
                  return false;
               }
            }
            else if (key == "INTERVAL")
            {
               if (!SmallInt(value, rule.interval) || rule.interval < 1 || rule.interval > 1000)
               {
                  problem = "an RRULE INTERVAL outside 1..1000";
                  return false;
               }
            }
            else if (key == "COUNT")
            {
               if (!SmallInt(value, rule.count) || rule.count < 1)
               {
                  problem = "an RRULE COUNT that is not a positive number";
                  return false;
               }
            }
            else if (key == "UNTIL")
            {
               int y, m, d, hh, mm, ss;
               if (!ParseDateFields(value, rule.untilIsDate, rule.untilUtc, y, m, d, hh, mm, ss))
               {
                  problem = "an RRULE UNTIL that is not a date";
                  return false;
               }
               rule.hasUntil = true;
               __int64 seconds = SecondsOf(y, m, d, hh, mm, ss);
               rule.untilLocal = rule.untilIsDate ? seconds + SecondsPerDay - 1 : seconds;
               rule.untilInstant = seconds;
            }
            else if (key == "BYMONTH")
            {
               if (!ReadIntList(value, 1, 12, false, rule.byMonth, 12))
               {
                  problem = "an RRULE BYMONTH outside 1..12";
                  return false;
               }
            }
            else if (key == "BYMONTHDAY")
            {
               if (!ReadIntList(value, 1, 31, true, rule.byMonthDay, 31))
               {
                  problem = "an RRULE BYMONTHDAY outside 1..31 or -31..-1";
                  return false;
               }
            }
            else if (key == "BYYEARDAY")
            {
               if (!ReadIntList(value, 1, 366, true, rule.byYearDay, 366))
               {
                  problem = "an RRULE BYYEARDAY outside 1..366 or -366..-1";
                  return false;
               }
            }
            else if (key == "BYWEEKNO")
            {
               if (!ReadIntList(value, 1, 53, true, rule.byWeekNo, 53))
               {
                  problem = "an RRULE BYWEEKNO outside 1..53 or -53..-1";
                  return false;
               }
            }
            else if (key == "BYSETPOS")
            {
               if (!ReadIntList(value, 1, 366, true, rule.bySetPos, 366))
               {
                  problem = "an RRULE BYSETPOS outside 1..366 or -366..-1";
                  return false;
               }
            }
            else if (key == "BYDAY")
            {
               std::vector<AnsiString> items = SplitOn(value, ',');
               if (items.size() > 35)
               {
                  problem = "an RRULE BYDAY with more than 35 entries";
                  return false;
               }
               for (size_t j = 0; j < items.size(); j++)
               {
                  AnsiString item = Trimmed(items[j]);
                  if (item.GetLength() < 2)
                  {
                     problem = "an RRULE BYDAY entry that is not a weekday: " + item;
                     return false;
                  }
                  ByDay day;
                  day.weekday = WeekdayOf(item.Mid(item.GetLength() - 2));
                  day.ordinal = 0;
                  if (day.weekday < 0)
                  {
                     problem = "an RRULE BYDAY entry that is not a weekday: " + item;
                     return false;
                  }
                  if (item.GetLength() > 2)
                  {
                     if (!SmallInt(item.Mid(0, item.GetLength() - 2), day.ordinal) || day.ordinal == 0 || day.ordinal > 53 || day.ordinal < -53)
                     {
                        problem = "an RRULE BYDAY ordinal outside 1..53 or -53..-1: " + item;
                        return false;
                     }
                  }
                  rule.byDay.push_back(day);
               }
            }
            else if (key == "WKST")
            {
               rule.wkst = WeekdayOf(value);
               if (rule.wkst < 0)
               {
                  problem = "an RRULE WKST that is not a weekday";
                  return false;
               }
            }
            else if (key == "BYHOUR" || key == "BYMINUTE" || key == "BYSECOND")
            {
               problem = "an RRULE with " + key + ", which this server does not expand";
               return false;
            }
            else
            {
               problem = "an RRULE part this server does not know: " + key;
               return false;
            }
         }

         if (rule.freq == FreqNone)
         {
            problem = "an RRULE without a FREQ";
            return false;
         }
         if (rule.count > 0 && rule.hasUntil)
         {
            problem = "an RRULE with both COUNT and UNTIL";
            return false;
         }
         if (rule.freq != FreqYearly && !rule.byWeekNo.empty())
         {
            problem = "an RRULE with BYWEEKNO other than yearly";
            return false;
         }
         if ((rule.freq == FreqWeekly || rule.freq == FreqDaily) && !rule.byYearDay.empty())
         {
            problem = "an RRULE with BYYEARDAY on a daily or weekly rule";
            return false;
         }
         if (rule.freq == FreqWeekly && !rule.byMonthDay.empty())
         {
            problem = "an RRULE with BYMONTHDAY on a weekly rule";
            return false;
         }
         return true;
      }

      // The ISO-style week number of a day for a week starting on wkst: week
      // 1 is the week with at least four days in the year (RFC 5545 section
      // 3.3.10). Answers the week and the year it belongs to.
      void WeekNumber(__int64 day, int wkst, int &week, int &weekYear)
      {
         int y, m, d;
         CivilFromDays(day, y, m, d);

         for (int candidateYear = y + 1; candidateYear >= y - 1; candidateYear--)
         {
            // The first day of week 1 of candidateYear: the week (starting on
            // wkst) that holds January 4th.
            __int64 jan4 = DaysFromCivil(candidateYear, 1, 4);
            __int64 week1 = jan4 - ((Weekday(jan4) - wkst + 7) % 7);
            if (day >= week1)
            {
               week = static_cast<int>((day - week1) / 7) + 1;
               weekYear = candidateYear;
               return;
            }
         }
         week = 1;
         weekYear = y - 1;
      }

      int WeeksInYear(int year, int wkst)
      {
         int week, weekYear;
         WeekNumber(DaysFromCivil(year, 12, 28), wkst, week, weekYear);
         return weekYear == year ? week : 52;
      }

      // Whether a day of the period passes every BY part of the rule; the
      // defaults (the DTSTART's month, day or weekday) already folded in.
      bool DayPasses(const Rule &rule, __int64 day, int y, int m, int d, int periodYear, int periodMonth, const std::vector<int> &byMonthDayDefault)
      {
         if (!rule.byMonth.empty() && std::find(rule.byMonth.begin(), rule.byMonth.end(), m) == rule.byMonth.end())
            return false;

         if (!rule.byWeekNo.empty())
         {
            int week, weekYear;
            WeekNumber(day, rule.wkst, week, weekYear);
            if (weekYear != periodYear)
               return false;
            int weeks = WeeksInYear(periodYear, rule.wkst);
            bool matched = false;
            for (size_t i = 0; !matched && i < rule.byWeekNo.size(); i++)
            {
               int wanted = rule.byWeekNo[i] > 0 ? rule.byWeekNo[i] : weeks + 1 + rule.byWeekNo[i];
               matched = wanted == week;
            }
            if (!matched)
               return false;
         }

         if (!rule.byYearDay.empty())
         {
            int yearDay = static_cast<int>(day - DaysFromCivil(y, 1, 1)) + 1;
            int length = DaysInYear(y);
            bool matched = false;
            for (size_t i = 0; !matched && i < rule.byYearDay.size(); i++)
            {
               int wanted = rule.byYearDay[i] > 0 ? rule.byYearDay[i] : length + 1 + rule.byYearDay[i];
               matched = wanted == yearDay;
            }
            if (!matched)
               return false;
         }

         const std::vector<int> &monthDays = rule.byMonthDay.empty() ? byMonthDayDefault : rule.byMonthDay;
         if (!monthDays.empty())
         {
            int length = DaysInMonth(y, m);
            bool matched = false;
            for (size_t i = 0; !matched && i < monthDays.size(); i++)
            {
               int wanted = monthDays[i] > 0 ? monthDays[i] : length + 1 + monthDays[i];
               matched = wanted == d;
            }
            if (!matched)
               return false;
         }

         if (!rule.byDay.empty())
         {
            int weekday = Weekday(day);
            bool matched = false;
            for (size_t i = 0; !matched && i < rule.byDay.size(); i++)
            {
               const ByDay &entry = rule.byDay[i];
               if (entry.weekday != weekday)
                  continue;
               if (entry.ordinal == 0 || rule.freq == FreqWeekly || rule.freq == FreqDaily)
               {
                  matched = true;
                  continue;
               }

               // The nth such weekday of the month (monthly, or yearly with
               // BYMONTH) or of the year (yearly without).
               bool withinMonth = rule.freq == FreqMonthly || !rule.byMonth.empty();
               __int64 first = withinMonth ? DaysFromCivil(y, m, 1) : DaysFromCivil(y, 1, 1);
               int length = withinMonth ? DaysInMonth(y, m) : DaysInYear(y);
               int index = static_cast<int>(day - first);   // 0-based within the span
               if (entry.ordinal > 0)
                  matched = index / 7 + 1 == entry.ordinal;
               else
                  matched = (length - 1 - index) / 7 + 1 == -entry.ordinal;
            }
            if (!matched)
               return false;
         }

         (void) periodMonth;
         return true;
      }

      struct Expansion
      {
         Expansion() : truncated(false), emptyPeriods(0) { }

         std::vector<__int64> locals;   // wall-clock seconds of each instance, in order
         bool truncated;
         int emptyPeriods;
      };

      // Every instance of the rule from the DTSTART that is not before the
      // window's start, as wall-clock seconds, until the rule ends, the
      // window's end passes (in local terms, with a day's slack for the zone
      // either side), or a ceiling is reached. The instances before the
      // window are walked - COUNT and UNTIL are counted from the DTSTART -
      // but not kept, so a daily rule that has run for thirty years is
      // answered for this month without its history filling the ceiling.
      void ExpandRule(const Rule &rule, __int64 startLocal, __int64 localWindowStart, __int64 localWindowEnd, size_t maxInstances, Expansion &out)
      {
         int sy, sm, sd;
         __int64 startDay = DayOf(startLocal);
         __int64 timeOfDay = startLocal - startDay * SecondsPerDay;
         CivilFromDays(startDay, sy, sm, sd);

         // The defaults RFC 5545 implies when a rule names no day part.
         std::vector<int> byMonthDayDefault;
         Rule effective = rule;
         bool noDayPart = rule.byMonthDay.empty() && rule.byDay.empty() && rule.byYearDay.empty() && rule.byWeekNo.empty();
         if (rule.freq == FreqYearly)
         {
            if (rule.byMonth.empty() && noDayPart)
               effective.byMonth.push_back(sm);
            if (noDayPart)
               byMonthDayDefault.push_back(sd);
         }
         else if (rule.freq == FreqMonthly)
         {
            if (noDayPart)
               byMonthDayDefault.push_back(sd);
         }
         else if (rule.freq == FreqWeekly)
         {
            if (rule.byDay.empty())
            {
               ByDay own;
               own.weekday = Weekday(startDay);
               own.ordinal = 0;
               effective.byDay.push_back(own);
            }
         }

         int emitted = 0;
         __int64 weekStart = startDay - ((Weekday(startDay) - rule.wkst + 7) % 7);

         for (int period = 0; period < MaxPeriods; period++)
         {
            __int64 firstDay, lastDay;
            int periodYear = sy, periodMonth = sm;
            switch (rule.freq)
            {
            case FreqDaily:
               firstDay = lastDay = startDay + static_cast<__int64>(period) * rule.interval;
               break;
            case FreqWeekly:
               firstDay = weekStart + static_cast<__int64>(period) * rule.interval * 7;
               lastDay = firstDay + 6;
               break;
            case FreqMonthly:
            {
               __int64 months = static_cast<__int64>(sy) * 12 + (sm - 1) + static_cast<__int64>(period) * rule.interval;
               periodYear = static_cast<int>(FloorDiv(months, 12));
               periodMonth = static_cast<int>(months - static_cast<__int64>(periodYear) * 12) + 1;
               if (periodYear > 9999)
                  return;
               firstDay = DaysFromCivil(periodYear, periodMonth, 1);
               lastDay = firstDay + DaysInMonth(periodYear, periodMonth) - 1;
               break;
            }
            default:
               periodYear = sy + period * rule.interval;
               if (periodYear > 9999)
                  return;
               firstDay = DaysFromCivil(periodYear, 1, 1);
               lastDay = DaysFromCivil(periodYear, 12, 31);
               break;
            }

            if (firstDay * SecondsPerDay > localWindowEnd + SecondsPerDay)
               return;   // nothing later can be inside the window
            if (rule.hasUntil && firstDay * SecondsPerDay > (rule.untilUtc ? rule.untilInstant + SecondsPerDay : rule.untilLocal) + SecondsPerDay)
               return;

            // The days of the period that pass the rule, in order.
            std::vector<__int64> days;
            for (__int64 day = firstDay; day <= lastDay; day++)
            {
               int y, m, d;
               CivilFromDays(day, y, m, d);
               if (rule.freq == FreqMonthly && (y != periodYear || m != periodMonth))
                  continue;
               if (DayPasses(effective, day, y, m, d, periodYear, periodMonth, byMonthDayDefault))
                  days.push_back(day);
            }

            if (!rule.bySetPos.empty() && !days.empty())
            {
               std::vector<__int64> chosen;
               for (size_t i = 0; i < rule.bySetPos.size(); i++)
               {
                  int pos = rule.bySetPos[i];
                  int index = pos > 0 ? pos - 1 : static_cast<int>(days.size()) + pos;
                  if (index >= 0 && index < static_cast<int>(days.size()))
                     chosen.push_back(days[index]);
               }
               std::sort(chosen.begin(), chosen.end());
               chosen.erase(std::unique(chosen.begin(), chosen.end()), chosen.end());
               days.swap(chosen);
            }

            bool any = false;
            for (size_t i = 0; i < days.size(); i++)
            {
               __int64 local = days[i] * SecondsPerDay + timeOfDay;
               if (local < startLocal)
                  continue;

               if (rule.hasUntil)
               {
                  // A UTC UNTIL is compared as an instant by the caller; here
                  // the local form is enough to stop a rule that has run past
                  // it by more than a day.
                  if (rule.untilUtc ? local > rule.untilInstant + SecondsPerDay : local > rule.untilLocal)
                  {
                     out.emptyPeriods = 0;
                     return;
                  }
               }

               any = true;
               emitted++;
               if (rule.count > 0 && emitted > rule.count)
                  return;

               if (local >= localWindowStart)
               {
                  out.locals.push_back(local);
                  if (out.locals.size() >= maxInstances)
                  {
                     out.truncated = true;
                     return;
                  }
               }
               if (local > localWindowEnd + SecondsPerDay)
                  return;
            }

            if (any)
               out.emptyPeriods = 0;
            else if (++out.emptyPeriods >= MaxEmptyPeriods)
               return;
         }

         out.truncated = true;
      }

      //------------------------------------------------------------------------
      // The object: its master and override components, and their times
      //------------------------------------------------------------------------

      struct ComponentTimes
      {
         ComponentTimes() : hasStart(false), hasEnd(false), hasDue(false), hasDuration(false), duration(0), allDay(false), index(0) { }

         bool hasStart;
         ICalTime start;
         bool hasEnd;        // DTEND
         ICalTime end;
         bool hasDue;        // DUE (VTODO)
         ICalTime due;
         bool hasDuration;
         __int64 duration;   // seconds; for an all-day component whole days
         bool allDay;
         size_t index;
      };

      bool ReadTimes(const ICalComponent &component, size_t index, ZoneCache &zones, ComponentTimes &times, AnsiString &problem)
      {
         times = ComponentTimes();
         times.index = index;

         if (const ICalProperty *start = component.Property("DTSTART"))
         {
            if (!ResolveTime(*start, start->value, zones, times.start, problem))
               return false;
            times.hasStart = true;
            times.allDay = times.start.isDate;
         }
         if (const ICalProperty *end = component.Property("DTEND"))
         {
            if (!ResolveTime(*end, end->value, zones, times.end, problem))
               return false;
            times.hasEnd = true;
         }
         if (const ICalProperty *due = component.Property("DUE"))
         {
            if (!ResolveTime(*due, due->value, zones, times.due, problem))
               return false;
            times.hasDue = true;
         }
         if (const ICalProperty *duration = component.Property("DURATION"))
         {
            if (!ParseDuration(duration->value, times.duration))
            {
               problem = "a DURATION this server cannot read: " + Trimmed(duration->value);
               return false;
            }
            times.hasDuration = true;
         }

         if (component.name == "VEVENT" && !times.hasStart)
         {
            problem = "a VEVENT without a DTSTART";
            return false;
         }
         if (times.hasEnd && times.hasDuration)
         {
            problem = "a component with both DTEND and DURATION";
            return false;
         }
         if (times.hasDue && times.hasDuration)
         {
            problem = "a VTODO with both DUE and DURATION";
            return false;
         }
         if (times.hasStart && times.hasEnd && times.end.instant < times.start.instant)
         {
            problem = "a DTEND before its DTSTART";
            return false;
         }
         if (times.hasStart && times.hasDue && times.due.instant < times.start.instant)
         {
            problem = "a DUE before its DTSTART";
            return false;
         }
         return true;
      }

      // The length of a component's occurrence, in seconds from its start:
      // DTEND or DUE less DTSTART, the DURATION, a day for an all-day
      // component with neither, else nothing.
      __int64 DurationOf(const ComponentTimes &times)
      {
         if (times.hasStart && times.hasEnd)
            return times.end.instant - times.start.instant;
         if (times.hasStart && times.hasDue)
            return times.due.instant - times.start.instant;
         if (times.hasDuration)
            return times.duration < 0 ? 0 : times.duration;
         if (times.allDay)
            return SecondsPerDay;
         return 0;
      }

      bool IsCalendarComponent(const AnsiString &name)
      {
         return name == "VEVENT" || name == "VTODO";
      }

      // The instant a wall-clock time of the master's kind means: through
      // the master's zone for a zoned DTSTART, as it stands for UTC, floating
      // and DATE.
      __int64 InstantOfLocal(const ICalTime &start, Zone *zone, __int64 local)
      {
         if (start.isDate || start.utc || start.floating || !zone)
            return local;
         return LocalToUtc(*zone, local);
      }

      // The core of Expand: the instances of every master in the calendar,
      // the overrides put in place, filtered to the window.
      bool ExpandCalendar(const ICalComponent &calendar, __int64 windowStart, __int64 windowEnd, size_t maxInstances,
                          std::vector<ICalInstance> &instances, bool &truncated, AnsiString &problem)
      {
         instances.clear();
         truncated = false;
         if (maxInstances == 0 || maxInstances > ICalendar::MaxInstances)
            maxInstances = ICalendar::MaxInstances;

         ZoneCache zones(&calendar);

         struct Override
         {
            __int64 recurrenceId;
            ComponentTimes times;
         };
         std::vector<Override> overrides;
         std::vector<ComponentTimes> masters;

         for (size_t i = 0; i < calendar.children.size(); i++)
         {
            const ICalComponent &component = calendar.children[i];
            if (!IsCalendarComponent(component.name))
               continue;

            ComponentTimes times;
            if (!ReadTimes(component, i, zones, times, problem))
               return false;

            if (const ICalProperty *rid = component.Property("RECURRENCE-ID"))
            {
               ICalTime when;
               if (!ResolveTime(*rid, rid->value, zones, when, problem))
                  return false;
               Override o;
               o.recurrenceId = when.instant;
               o.times = times;
               overrides.push_back(o);
            }
            else
               masters.push_back(times);
         }

         if (masters.size() > 1)
         {
            problem = "more than one component without a RECURRENCE-ID; an object is one master and its overrides";
            return false;
         }

         // Every generated instance that touches the window, keyed by the
         // start the recurrence gave it.
         std::vector<ICalInstance> generated;

         if (!masters.empty())
         {
            const ComponentTimes &master = masters[0];
            const ICalComponent &component = calendar.children[master.index];
            __int64 duration = DurationOf(master);

            Zone *zone = nullptr;
            if (master.hasStart && !master.start.isDate && !master.start.utc && !master.start.floating)
            {
               zone = zones.Find(master.start.tzid, problem);
               if (!zone)
                  return false;
            }

            std::vector<__int64> starts;   // instants
            std::vector<__int64> locals;   // the wall-clock seconds behind each, for EXDATE by day

            if (!master.hasStart)
            {
               // A VTODO with no DTSTART: one instance at its DUE, or at no
               // time at all.
               starts.push_back(master.hasDue ? master.due.instant : 0);
               locals.push_back(starts.back());
            }
            else
            {
               __int64 startLocal = SecondsOf(master.start.year, master.start.month, master.start.day, master.start.hour, master.start.minute, master.start.second);
               const ICalProperty *rruleProperty = component.Property("RRULE");
               if (!rruleProperty)
               {
                  starts.push_back(master.start.instant);
                  locals.push_back(startLocal);
               }
               else
               {
                  Rule rule;
                  if (!ParseRule(rruleProperty->value, rule, problem))
                     return false;
                  if (master.start.isDate && rule.hasUntil && !rule.untilIsDate)
                  {
                     // Tolerated: the day of the UNTIL is what counts.
                     rule.untilLocal = DayOf(rule.untilInstant) * SecondsPerDay + SecondsPerDay - 1;
                     rule.untilUtc = false;
                     rule.untilIsDate = true;
                  }

                  // The window in wall-clock terms, with a day's slack either
                  // side for the zone's offset.
                  __int64 localWindowStart = windowStart - SecondsPerDay;
                  __int64 localWindowEnd = windowEnd >= ICalendar::Forever - SecondsPerDay ? ICalendar::Forever : windowEnd + SecondsPerDay;

                  Expansion expansion;
                  ExpandRule(rule, startLocal, localWindowStart, localWindowEnd, maxInstances, expansion);
                  truncated = expansion.truncated;

                  for (size_t i = 0; i < expansion.locals.size(); i++)
                  {
                     __int64 instant = InstantOfLocal(master.start, zone, expansion.locals[i]);
                     if (rule.hasUntil && rule.untilUtc && instant > rule.untilInstant)
                        break;
                     starts.push_back(instant);
                     locals.push_back(expansion.locals[i]);
                  }
               }
            }

            // EXDATE: by instant, or by day for a DATE-valued EXDATE.
            std::vector<__int64> excludedInstants;
            std::vector<__int64> excludedDays;
            for (size_t p = 0; p < component.properties.size(); p++)
            {
               const ICalProperty &property = component.properties[p];
               if (property.name != "EXDATE")
                  continue;
               std::vector<AnsiString> values = SplitOn(property.value, ',');
               for (size_t v = 0; v < values.size(); v++)
               {
                  ICalTime when;
                  if (!ResolveTime(property, values[v], zones, when, problem))
                     return false;
                  if (when.isDate && !master.start.isDate)
                     excludedDays.push_back(DaysFromCivil(when.year, when.month, when.day));
                  else
                     excludedInstants.push_back(when.instant);
               }
            }

            for (size_t i = 0; i < starts.size(); i++)
            {
               __int64 start = starts[i];
               if (std::find(excludedInstants.begin(), excludedInstants.end(), start) != excludedInstants.end())
                  continue;
               if (!excludedDays.empty() && std::find(excludedDays.begin(), excludedDays.end(), DayOf(locals[i])) != excludedDays.end())
                  continue;

               ICalInstance instance;
               instance.start = start;
               instance.end = start + duration;
               instance.allDay = master.allDay;
               instance.recurrenceId = start;
               instance.component = master.index;
               generated.push_back(instance);
            }

            // RDATE: DATE, DATE-TIME or PERIOD values.
            for (size_t p = 0; p < component.properties.size(); p++)
            {
               const ICalProperty &property = component.properties[p];
               if (property.name != "RDATE")
                  continue;
               std::vector<AnsiString> values = SplitOn(property.value, ',');
               for (size_t v = 0; v < values.size(); v++)
               {
                  if (generated.size() >= maxInstances)
                  {
                     truncated = true;
                     break;
                  }
                  AnsiString value = Trimmed(values[v]);
                  ICalInstance instance;
                  instance.allDay = master.allDay;
                  instance.component = master.index;

                  int slash = value.Find("/");
                  if (slash > 0)
                  {
                     ICalTime from;
                     if (!ResolveTime(property, value.Mid(0, slash), zones, from, problem))
                        return false;
                     AnsiString rest = Trimmed(value.Mid(slash + 1));
                     __int64 length = 0;
                     if (!rest.IsEmpty() && rest[0] == 'P')
                     {
                        if (!ParseDuration(rest, length))
                        {
                           problem = "an RDATE period with a duration this server cannot read";
                           return false;
                        }
                     }
                     else
                     {
                        ICalTime to;
                        if (!ResolveTime(property, rest, zones, to, problem))
                           return false;
                        length = to.instant - from.instant;
                     }
                     instance.start = from.instant;
                     instance.end = from.instant + (length < 0 ? 0 : length);
                  }
                  else
                  {
                     ICalTime when;
                     if (!ResolveTime(property, value, zones, when, problem))
                        return false;
                     instance.start = when.instant;
                     instance.end = when.instant + duration;
                  }
                  instance.recurrenceId = instance.start;
                  if (std::find(excludedInstants.begin(), excludedInstants.end(), instance.start) != excludedInstants.end())
                     continue;
                  generated.push_back(instance);
               }
            }
         }

         // The overrides: each replaces the instance it names, and stands in
         // its own right whether or not one was generated.
         for (size_t i = 0; i < overrides.size(); i++)
         {
            const Override &o = overrides[i];
            for (size_t g = 0; g < generated.size(); g++)
            {
               if (generated[g].recurrenceId == o.recurrenceId && !generated[g].overridden)
               {
                  generated.erase(generated.begin() + g);
                  break;
               }
            }

            ICalInstance instance;
            instance.start = o.times.hasStart ? o.times.start.instant : (o.times.hasDue ? o.times.due.instant : o.recurrenceId);
            instance.end = instance.start + DurationOf(o.times);
            instance.allDay = o.times.allDay;
            instance.recurrenceId = o.recurrenceId;
            instance.overridden = true;
            instance.component = o.times.index;
            generated.push_back(instance);
         }

         for (size_t i = 0; i < generated.size(); i++)
         {
            const ICalInstance &instance = generated[i];
            bool inWindow = instance.start < windowEnd && (instance.end > windowStart || instance.start >= windowStart);
            if (inWindow)
               instances.push_back(instance);
         }

         std::stable_sort(instances.begin(), instances.end(), [](const ICalInstance &a, const ICalInstance &b) { return a.start < b.start; });
         if (instances.size() > maxInstances)
         {
            instances.resize(maxInstances);
            truncated = true;
         }
         return true;
      }
   }

   //---------------------------------------------------------------------------
   // ICalProperty, ICalComponent
   //---------------------------------------------------------------------------

   AnsiString
   ICalProperty::Parameter(const char *parameterName) const
   {
      for (size_t i = 0; i < parameters.size(); i++)
      {
         if (parameters[i].first.CompareNoCase(parameterName) == 0)
            return parameters[i].second;
      }
      return "";
   }

   bool
   ICalProperty::HasParameter(const char *parameterName, const char *parameterValue) const
   {
      for (size_t i = 0; i < parameters.size(); i++)
      {
         if (parameters[i].first.CompareNoCase(parameterName) != 0)
            continue;
         std::vector<AnsiString> items = SplitOn(parameters[i].second, ',');
         for (size_t j = 0; j < items.size(); j++)
         {
            if (Trimmed(items[j]).CompareNoCase(parameterValue) == 0)
               return true;
         }
      }
      return false;
   }

   const ICalProperty *
   ICalComponent::Property(const char *propertyName) const
   {
      for (size_t i = 0; i < properties.size(); i++)
      {
         if (properties[i].name == propertyName)
            return &properties[i];
      }
      return nullptr;
   }

   AnsiString
   ICalComponent::Value(const char *propertyName) const
   {
      const ICalProperty *property = Property(propertyName);
      return property ? Trimmed(ICalendar::Unescape(property->value)) : AnsiString("");
   }

   const ICalComponent *
   ICalComponent::Child(const char *componentName) const
   {
      for (size_t i = 0; i < children.size(); i++)
      {
         if (children[i].name == componentName)
            return &children[i];
      }
      return nullptr;
   }

   //---------------------------------------------------------------------------
   // ICalendar: the text
   //---------------------------------------------------------------------------

   bool
   ICalendar::Parse(const AnsiString &text, ICalComponent &root, AnsiString &problem)
   {
      root = ICalComponent();
      problem = "";

      std::vector<AnsiString> lines;
      UnfoldLines(text, lines);

      std::vector<ICalComponent *> stack;
      size_t components = 0;
      size_t propertyCount = 0;
      bool closed = false;

      for (size_t i = 0; i < lines.size(); i++)
      {
         if (Trimmed(lines[i]).IsEmpty())
            continue;

         ICalProperty property;
         if (!ParseContentLine(lines[i], property))
         {
            problem = "a content line has no property name before its colon";
            return false;
         }

         AnsiString upperValue = Upper(Trimmed(property.value));

         if (property.name == "BEGIN")
         {
            if (closed)
            {
               problem = "there is content after END:VCALENDAR; a resource holds one calendar object";
               return false;
            }
            if (upperValue.IsEmpty())
            {
               problem = "a BEGIN without a component name";
               return false;
            }
            if (stack.empty())
            {
               if (upperValue != "VCALENDAR")
               {
                  problem = "the object does not begin with BEGIN:VCALENDAR";
                  return false;
               }
               root.name = "VCALENDAR";
               stack.push_back(&root);
               continue;
            }
            if (stack.size() >= static_cast<size_t>(MaxDepth))
            {
               problem = "components nested too deeply";
               return false;
            }
            if (++components > MaxComponents)
            {
               problem = "too many components";
               return false;
            }
            ICalComponent child;
            child.name = upperValue;
            stack.back()->children.push_back(child);
            stack.push_back(&stack.back()->children.back());
            continue;
         }

         if (stack.empty())
         {
            problem = "the object does not begin with BEGIN:VCALENDAR";
            return false;
         }

         if (property.name == "END")
         {
            if (upperValue != stack.back()->name)
            {
               problem = "END:" + upperValue + " does not close BEGIN:" + stack.back()->name;
               return false;
            }
            stack.pop_back();
            if (stack.empty())
               closed = true;
            continue;
         }

         if (++propertyCount > MaxProperties)
         {
            problem = "too many properties";
            return false;
         }
         stack.back()->properties.push_back(property);
      }

      if (root.name.IsEmpty())
      {
         problem = "the object does not begin with BEGIN:VCALENDAR";
         return false;
      }
      if (!closed)
      {
         problem = stack.empty() ? AnsiString("the object does not end with END:VCALENDAR") : "BEGIN:" + stack.back()->name + " is never closed";
         return false;
      }

      return true;
   }

   AnsiString
   ICalendar::Serialize(const ICalComponent &component)
   {
      AnsiString out;
      SerializeInto(component, out, 0);
      return out;
   }

   AnsiString
   ICalendar::Unescape(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength());

      for (int i = 0; i < value.GetLength(); i++)
      {
         char c = value[i];
         if (c == '\\' && i + 1 < value.GetLength())
         {
            char next = value[i + 1];
            if (next == 'n' || next == 'N')
            {
               result += '\n';
               i++;
               continue;
            }
            if (next == ',' || next == ';' || next == '\\')
            {
               result += next;
               i++;
               continue;
            }
         }
         result += c;
      }

      return result;
   }

   AnsiString
   ICalendar::Escape(const AnsiString &value)
   {
      AnsiString result;
      result.reserve(value.GetLength() + 8);

      for (int i = 0; i < value.GetLength(); i++)
      {
         char c = value[i];
         switch (c)
         {
         case '\\': result += "\\\\"; break;
         case '\n': result += "\\n"; break;
         case '\r': break;
         case ',':  result += "\\,"; break;
         case ';':  result += "\\;"; break;
         default:   result += c; break;
         }
      }

      return result;
   }

   AnsiString
   ICalendar::Fold(const AnsiString &line)
   {
      if (line.GetLength() <= FoldWidth)
         return line;

      AnsiString result;
      int position = 0;
      int width = FoldWidth;
      while (position < line.GetLength())
      {
         int take = std::min(width, line.GetLength() - position);

         // Never split a UTF-8 sequence: back off to before a continuation byte.
         while (take > 1 && position + take < line.GetLength() &&
                (static_cast<unsigned char>(line[position + take]) & 0xc0) == 0x80)
            take--;

         if (position > 0)
            result += "\r\n ";
         result += line.Mid(position, take);
         position += take;
         width = FoldWidth - 1;   // the continuation line's leading space counts
      }

      return result;
   }

   //---------------------------------------------------------------------------
   // ICalendar: values
   //---------------------------------------------------------------------------

   bool
   ICalendar::ParseTime(const ICalProperty &property, const ICalComponent *calendar, ICalTime &time, AnsiString &problem)
   {
      ZoneCache zones(calendar);
      return ResolveTime(property, property.value, zones, time, problem);
   }

   bool
   ICalendar::ParseUtc(const AnsiString &value, __int64 &instant)
   {
      bool isDate, utc;
      int y, m, d, hh, mm, ss;
      if (!ParseDateFields(value, isDate, utc, y, m, d, hh, mm, ss))
         return false;
      if (!isDate && !utc)
         return false;
      instant = SecondsOf(y, m, d, hh, mm, ss);
      return true;
   }

   AnsiString
   ICalendar::FormatUtc(__int64 instant)
   {
      if (instant < 0)
         instant = 0;
      if (instant > Forever)
         instant = Forever;
      __int64 day = DayOf(instant);
      __int64 rest = instant - day * SecondsPerDay;
      int y, m, d;
      CivilFromDays(day, y, m, d);
      AnsiString text;
      text.Format("%04d%02d%02dT%02d%02d%02dZ", y, m, d, static_cast<int>(rest / 3600), static_cast<int>((rest / 60) % 60), static_cast<int>(rest % 60));
      return text;
   }

   bool
   ICalendar::KnowsTimeZone(const AnsiString &tzid)
   {
      Zone zone;
      return BuiltIn(tzid, zone);
   }

   //---------------------------------------------------------------------------
   // ICalendar: the object
   //---------------------------------------------------------------------------

   bool
   ICalendar::Summarize(const ICalComponent &calendar, Summary &summary, AnsiString &problem)
   {
      summary = Summary();
      problem = "";

      if (calendar.name != "VCALENDAR")
      {
         problem = "the object is not a VCALENDAR";
         return false;
      }

      size_t masters = 0;
      size_t masterIndex = 0;
      bool rrule = false;
      bool rdate = false;
      size_t overrides = 0;

      for (size_t i = 0; i < calendar.children.size(); i++)
      {
         const ICalComponent &component = calendar.children[i];
         if (component.name == "VTIMEZONE")
            continue;
         if (component.name == "VJOURNAL" || component.name == "VFREEBUSY")
         {
            problem = "this calendar stores VEVENT and VTODO; the object is a " + component.name;
            return false;
         }
         if (!IsCalendarComponent(component.name))
         {
            problem = "this calendar stores VEVENT and VTODO; the object holds a " + component.name + " at the top level";
            return false;
         }

         if (summary.component.IsEmpty())
            summary.component = component.name;
         else if (summary.component != component.name)
         {
            problem = "the object mixes " + summary.component + " and " + component.name + " components; an object is one kind";
            return false;
         }

         AnsiString uid = component.Value("UID");
         if (uid.IsEmpty())
         {
            problem = "a " + component.name + " without a UID";
            return false;
         }
         if (uid.GetLength() > 255)
         {
            problem = "a UID longer than 255 bytes";
            return false;
         }
         for (int c = 0; c < uid.GetLength(); c++)
         {
            if (static_cast<unsigned char>(uid[c]) < 0x20 || uid[c] == 0x7f)
            {
               problem = "a UID with a control character";
               return false;
            }
         }
         if (summary.uid.IsEmpty())
            summary.uid = uid;
         else if (summary.uid != uid)
         {
            problem = "the object's components carry different UIDs (" + summary.uid + " and " + uid + "); an object is one UID";
            return false;
         }

         if (component.Property("RECURRENCE-ID"))
         {
            overrides++;
            if (component.Property("RRULE"))
            {
               problem = "a RECURRENCE-ID component with an RRULE of its own";
               return false;
            }
            const ICalProperty *rid = component.Property("RECURRENCE-ID");
            if (Upper(rid->Parameter("RANGE")) == "THISANDFUTURE")
            {
               problem = "a RECURRENCE-ID with RANGE=THISANDFUTURE, which this server does not apply";
               return false;
            }
            continue;
         }

         masters++;
         masterIndex = i;
         if (component.Property("RRULE"))
            rrule = true;
         if (component.Property("RDATE"))
            rdate = true;
      }

      if (summary.component.IsEmpty())
      {
         problem = "the object holds no VEVENT or VTODO";
         return false;
      }
      if (masters > 1)
      {
         problem = "more than one component without a RECURRENCE-ID; an object is one master and its overrides";
         return false;
      }

      summary.recurring = rrule || rdate || overrides > 0;

      // The master's own times, and the rule if any, checked by reading them.
      ZoneCache zones(&calendar);
      bool endless = false;
      if (masters == 1)
      {
         const ICalComponent &master = calendar.children[masterIndex];
         ComponentTimes times;
         if (!ReadTimes(master, masterIndex, zones, times, problem))
            return false;

         summary.start = times.hasStart ? times.start.instant : (times.hasDue ? times.due.instant : 0);
         summary.end = summary.start + DurationOf(times);
         if (!times.hasStart && times.hasDue)
            summary.end = times.due.instant;

         if (const ICalProperty *rruleProperty = master.Property("RRULE"))
         {
            Rule rule;
            if (!ParseRule(rruleProperty->value, rule, problem))
               return false;
            endless = rule.count == 0 && !rule.hasUntil;
         }
      }

      // Every override read too, so a zone or a date it cannot place is
      // refused now rather than at the first query.
      for (size_t i = 0; i < calendar.children.size(); i++)
      {
         const ICalComponent &component = calendar.children[i];
         if (!IsCalendarComponent(component.name) || !component.Property("RECURRENCE-ID"))
            continue;
         ComponentTimes times;
         if (!ReadTimes(component, i, zones, times, problem))
            return false;
         ICalTime when;
         const ICalProperty *rid = component.Property("RECURRENCE-ID");
         if (!ResolveTime(*rid, rid->value, zones, when, problem))
            return false;
      }

      // The span: the first start and the last end over the instances, from
      // an expansion bounded like a query's. An endless rule is Forever at
      // the far end and only its first few instances are generated (enough
      // that an RDATE or an override before the DTSTART is still seen); a
      // finite one that outruns the ceiling is Forever too, which errs
      // towards answering a query.
      std::vector<ICalInstance> instances;
      bool truncated = false;
      if (!ExpandCalendar(calendar, 0, Forever, endless ? 64 : MaxInstances, instances, truncated, problem))
         return false;

      summary.first = summary.start;
      summary.last = summary.end;
      for (size_t i = 0; i < instances.size(); i++)
      {
         if (i == 0 || instances[i].start < summary.first)
            summary.first = instances[i].start;
         if (i == 0 || instances[i].end > summary.last)
            summary.last = instances[i].end;
      }
      if (endless || truncated)
         summary.last = Forever;
      if (summary.last < summary.first)
         summary.last = summary.first;

      return true;
   }

   bool
   ICalendar::Expand(const ICalComponent &calendar, __int64 windowStart, __int64 windowEnd, size_t maxInstances,
                     std::vector<ICalInstance> &instances, bool &truncated, AnsiString &problem)
   {
      problem = "";
      if (windowEnd < windowStart)
         windowEnd = windowStart;
      return ExpandCalendar(calendar, windowStart, windowEnd, maxInstances, instances, truncated, problem);
   }

   bool
   ICalendar::OverlapsTimeRange(const ICalComponent &calendar, __int64 rangeStart, __int64 rangeEnd)
   {
      // The master's kind and shape decide which row of the table applies.
      const ICalComponent *master = nullptr;
      for (size_t i = 0; i < calendar.children.size(); i++)
      {
         const ICalComponent &component = calendar.children[i];
         if (IsCalendarComponent(component.name) && !component.Property("RECURRENCE-ID"))
         {
            master = &component;
            break;
         }
      }

      std::vector<ICalInstance> instances;
      bool truncated = false;
      AnsiString problem;

      if (!master || master->name == "VEVENT")
      {
         // Expand's window test is the VEVENT row: start < end-of-range and
         // end > start-of-range, an instant counted when it lies inside.
         if (!ExpandCalendar(calendar, rangeStart, rangeEnd, MaxInstances, instances, truncated, problem))
            return false;
         return !instances.empty();
      }

      // VTODO. The rows without a DTSTART or DUE are decided from the
      // master's COMPLETED and CREATED alone.
      ZoneCache zones(&calendar);
      ComponentTimes times;
      if (!ReadTimes(*master, 0, zones, times, problem))
         return false;

      if (!times.hasStart && !times.hasDue)
      {
         ICalTime completed, created;
         bool hasCompleted = false, hasCreated = false;
         if (const ICalProperty *p = master->Property("COMPLETED"))
            hasCompleted = ResolveTime(*p, p->value, zones, completed, problem);
         if (const ICalProperty *p = master->Property("CREATED"))
            hasCreated = ResolveTime(*p, p->value, zones, created, problem);

         if (hasCompleted && hasCreated)
            return (rangeStart <= created.instant || rangeStart <= completed.instant) && (rangeEnd >= created.instant || rangeEnd >= completed.instant);
         if (hasCompleted)
            return rangeStart <= completed.instant && rangeEnd >= completed.instant;
         if (hasCreated)
            return rangeEnd > created.instant;
         return true;
      }

      // The rows with a DTSTART or DUE, applied to each instance with the
      // window widened by a day so the table's own comparisons decide.
      if (!ExpandCalendar(calendar, rangeStart - SecondsPerDay, rangeEnd + SecondsPerDay, MaxInstances, instances, truncated, problem))
         return false;

      for (size_t i = 0; i < instances.size(); i++)
      {
         __int64 start = instances[i].start;
         __int64 end = instances[i].end;
         bool matched;
         if (times.hasStart && times.hasDuration)
            matched = rangeStart <= end && (rangeEnd > start || rangeEnd >= end);
         else if (times.hasStart && times.hasDue)
            matched = (rangeStart < end || rangeStart <= start) && (rangeEnd > start || rangeEnd >= end);
         else if (times.hasStart)
            matched = rangeStart <= start && rangeEnd > start;
         else
            matched = rangeStart < end && rangeEnd >= end;
         if (matched)
            return true;
      }
      return false;
   }
}
