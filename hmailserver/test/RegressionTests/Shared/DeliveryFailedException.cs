// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;

namespace RegressionTests.Shared
{
   public class DeliveryFailedException : Exception
   {
      public DeliveryFailedException(string message) :
         base(message)
      {
      }
   }
}