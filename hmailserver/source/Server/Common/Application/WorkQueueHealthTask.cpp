// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

#include "StdAfx.h"
#include "WorkQueueHealthTask.h"

#include "Application.h"
#include "../Threading/WorkQueue.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   WorkQueueHealthTask::WorkQueueHealthTask(void)
   {

   }

   WorkQueueHealthTask::~WorkQueueHealthTask(void)
   {

   }

   void
   WorkQueueHealthTask::DoWork()
   {
      std::shared_ptr<WorkQueue> asyncQueue = Application::Instance()->GetAsyncWorkQueue();

      if (!asyncQueue)
         return;

      asyncQueue->ReportStalledTasks();
   }
}
