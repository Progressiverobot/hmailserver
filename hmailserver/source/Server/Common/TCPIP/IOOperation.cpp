// Copyright (c) 2008 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2008-08-12
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "IOOperation.h"
#include "../Util/ByteBuffer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IOOperation::IOOperation(OperationType type, std::shared_ptr<ByteBuffer> buffer) :
      type_(type),
      buffer_(buffer)
   {
      
   }

   IOOperation::IOOperation(OperationType type, const AnsiString &string) :
      type_(type),
      string_(string)
   {

   }

   IOOperation::IOOperation(OperationType type, int delaySeconds) :
      type_(type),
      delay_seconds_(delaySeconds)
   {

   }

   IOOperation::~IOOperation(void)
   {

   }

   
}