// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

#include "VariantDateTime.h"

namespace HM
{


   class Time  
   {
   public:
	   Time();
	   virtual ~Time();

      static String GetCurrentDateTime();
      static String GetCurrentDate();

      static String GetTimeStampFromDateTime(const DateTime &dt);
      static DateTime GetDateFromSystemDate(const String &sInDate);

      // IMAP
      static String GetIMAPInternalDate(const String &sInternalDate);
      static String GetInternalDateFromIMAPInternalDate(const String &sMIMEDate);
      static String GetIMAPDateFromMimeHeader(const String &sInDate);
      static DateTime GetDateFromMimeHeader(const String &sInDate);
      static DateTime GetDateTimeFromMimeHeader(const String &sInDate);
      static DateTime GetDateFromIMAP(const String &sInDate);
      
      // Used in Date:-header in email.
      static String GetCurrentMimeDate();

      // Log datetime format
      static String GetCurrentDateTimeWithMilliseconds();

      // System
      static String GetUTCRelation();
      static int GetUTCRelationMinutes();
      static String GetCurrentDayName(int Day);
      static bool SeemsToBeValidYear(const String &sInternalDate);
      static String GetMonthShortName(long lMonth);
      static long GetMonthIndex(const String &sMonth);
      

      static bool IsValidSystemDate(const String &dateString);

      static bool GetTimeAdjustForTimezone(const String &sTimeZone, int &iHours, int &iMinutes);
   };

   class TimeTester
   {
   public :
      TimeTester () {};
      ~TimeTester () {};

      void Test();

   private:

      // Clock-behaviour coverage for RateLimiter's per-minute window. Lives with
      // the time tests because that is what it is testing, and because TimeTester
      // is already reached from ClassTester::DoTests.
      void TestRateLimiterClockRegression_();
   };

}