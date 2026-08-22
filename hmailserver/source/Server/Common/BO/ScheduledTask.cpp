// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "ScheduledTask.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   ScheduledTask::ScheduledTask(void) :
      Task("ScheduledTask"),
      minutes_between_run_(0),
      reoccurance_(RunOnce)
   {

   }

   ScheduledTask::~ScheduledTask(void)
   {
   }

   int
   ScheduledTask::GetMinutesBetweenRun() const
   {
      return minutes_between_run_;
   }

   void 
   ScheduledTask::SetMinutesBetweenRun(int iNewVal)
   {
      minutes_between_run_ = iNewVal;

      SetNextRunTime();
   }

   ScheduledTask::Reoccurance 
   ScheduledTask::GetReoccurance() const
   {
      return reoccurance_;
   }

   void 
   ScheduledTask::SetReoccurance(Reoccurance ro)
   {
      reoccurance_ = ro;
   }

   DateTime 
   ScheduledTask::GetNextRunTime() const
   {
      return next_run_time_;

   }

   void 
   ScheduledTask::SetNextRunTime()
   //---------------------------------------------------------------------------
   // DESCRIPTION:
   // Moves the next run one interval on, measured from the run that was DUE rather
   // than from now.
   //
   // This is called after the task has returned, so "now plus the interval" made the
   // real period interval + however long the task took, and every pass drifted by its
   // own duration. A six-hourly log retention pass taking two minutes lost an hour a
   // fortnight; the message-store consistency check, which can run for a long time on
   // a large store, drifted much faster. Nothing was ever skipped, so nothing ever
   // looked wrong - the runs just wandered away from the times an administrator set.
   //
   // If the due time is already more than one interval in the past - the task
   // overran its own interval, or the process was suspended - the next run is taken
   // from now instead. Catching up by running back-to-back would be worse than
   // resuming the cadence, and the alternative is a task that never stops running.
   //---------------------------------------------------------------------------
   {
      DateTime dtNow = DateTime::GetCurrentTime();

      DateTimeSpan dts;
      dts.SetDateTimeSpan(0, 0, minutes_between_run_, 0);

      // GetStatus() rather than a comparison against a default-constructed DateTime:
      // DateTime has no operator== and its default constructor leaves it invalid.
      if (next_run_time_.GetStatus() != DateTime::valid)
      {
         next_run_time_ = dtNow + dts;
         return;
      }

      const DateTime scheduled = next_run_time_ + dts;

      next_run_time_ = (scheduled > dtNow) ? scheduled : (dtNow + dts);
   }

}