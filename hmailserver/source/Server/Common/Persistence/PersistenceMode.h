// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

// This file used to carry the MFC leak-tracking boilerplate that 441 .cpp files in
// this tree carry:
//
//    #ifdef _DEBUG
//    #define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
//    #define new DEBUG_NEW
//    #endif
//
// In a .cpp that is harmless, because it is placed after every #include. In a
// *header* it is not, and this is the only header in the tree that had it. Once any
// translation unit includes this file, `new` is a macro for the rest of that unit -
// including every #include that comes afterwards. Third-party headers that declare
// their own operator new are then rewritten into nonsense.
//
// That is why the whole server has never built in Debug on this machine: it reaches
// boost through PersistentMessage.h, which includes this file, and
//
//    static void* operator new(std::size_t class_size, std::size_t extra_size)
//
// in boost/filesystem/directory.hpp becomes
//
//    static void* operator new(_NORMAL_BLOCK, __FILE__, __LINE__)(std::size_t ...)
//
// which produces exactly the C2059 / C2091 / C2802 trio the Debug build reported,
// pointing at a boost header that has nothing wrong with it. BackupExecuter.cpp and
// RuleGuard.cpp were the two casualties, both because they include a Persistent*
// header before their boost include - an ordering no rule anywhere required them to
// get right, and which no amount of reading either file could explain.
//
// Deleted rather than moved to the bottom of the file: a header has no "after every
// include" to be placed after.

namespace HM
{
   enum PersistenceMode
   {
      PersistenceModeNormal = 0,
      PersistenceModeRestore = 1,
      PersistenceModeRename = 2,
   };
   
}