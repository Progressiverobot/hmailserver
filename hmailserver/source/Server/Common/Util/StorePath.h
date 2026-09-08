// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // The case policy for the directories the message store names after a domain
   // and a mailbox.
   //
   // An address is compared without regard to case everywhere the server reads
   // one, and Windows compared the directories the same way for free: NTFS finds
   // "Test.com\Bob" when asked for "test.com\bob". A POSIX filesystem does not,
   // so an account created as Bob@Test.com and a message addressed to
   // bob@test.com would have named two different directories, the second of
   // them empty. Every place that turns an address into a directory name goes
   // through this function, and on POSIX it answers in lower case, so that one
   // address is one directory however it was spelled.
   //
   // On Windows it answers with the component unchanged, on purpose: a store
   // that already holds "Test.com" must keep finding it under that spelling,
   // and NTFS needs no help. The policy is therefore "the lower-cased address
   // on a case-sensitive filesystem, and whatever the filesystem already
   // considers equal on a case-insensitive one".
   inline String StoreDirectoryName(const String &addressComponent)
   {
#ifdef HM_PLATFORM_POSIX
      String lowered = addressComponent;
      lowered.ToLower();
      return lowered;
#else
      return addressComponent;
#endif
   }
}
