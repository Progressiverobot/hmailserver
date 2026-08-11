// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "IMAPListLookup.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPListLookup::IMAPListLookup()
   {

   }

   IMAPListLookup::~IMAPListLookup()
   {

   }
   bool
   IMAPListLookup::IsItemInList(const std::vector<String> &vecItems, int item, int maxItem)
   {
      for(const String &sCur : vecItems)
      {
         int lColonPos = sCur.Find(_T(":"));

         if (lColonPos >= 0)
         {
            String sFirstPart = sCur.Mid(0, lColonPos);
            String sSecondPart = sCur.Mid(lColonPos + 1);

            // RFC 3501: "*" is the largest value in use - on EITHER side of the
            // colon - and a range is valid in either order, so "*:1" is the whole
            // mailbox and "3:1" is the same set as "1:3".
            int lower = sFirstPart == _T("*") ? maxItem : _ttoi(sFirstPart);
            int upper = sSecondPart == _T("*") ? maxItem : _ttoi(sSecondPart);

            if (upper < lower)
               std::swap(lower, upper);

            if (item >= lower && item <= upper)
               return true;
         }
         else
         {
            int foundItem = sCur == _T("*") ? maxItem : _ttoi(sCur);

            if (foundItem == item)
               return true;
         }
      }

      return false;

   }


}
