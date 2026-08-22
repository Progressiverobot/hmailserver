// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "StdAfx.h"
#include ".\task.h"
#include <boost/thread/thread.hpp>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   Task::Task(const String &name) :
      name_ (name)
   {

   }

   Task::~Task(void)
   {

   }

   void
   Task::Run()
   {
      try
      {
         DoWork();
      }
      catch (boost::thread_interrupted const&)
      {
         boost::this_thread::disable_interruption disabled;
         return;
      }
   }
}