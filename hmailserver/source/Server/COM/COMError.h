// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

class COMError
{
public:
   COMError(void);
   ~COMError(void);

   static HRESULT GenerateGenericMessage();
   static HRESULT GenerateError(HM::String sDescription);
};
