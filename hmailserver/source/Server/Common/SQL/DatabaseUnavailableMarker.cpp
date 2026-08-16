// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

#include "StdAfx.h"
#include "DatabaseUnavailableMarker.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   thread_local bool DatabaseUnavailableMarker::unavailable_ = false;

   void
   DatabaseUnavailableMarker::Mark()
   {
      unavailable_ = true;
   }

   bool
   DatabaseUnavailableMarker::IsMarked()
   {
      return unavailable_;
   }

   void
   DatabaseUnavailableMarker::Clear()
   {
      unavailable_ = false;
   }
}
