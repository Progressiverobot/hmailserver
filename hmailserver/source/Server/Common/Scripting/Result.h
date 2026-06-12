// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once


namespace HM
{
   class Result 
   {
   public:
      Result(void);
      ~Result(void);

      void SetValue(long lNewVal){value_ = lNewVal; }
      long GetValue() const; 

      void SetParameter(int lNewVal){parameter_ = lNewVal; }
      int GetParameter() const; 


      void SetMessage(const String& sValue){message_ = sValue; }
      String GetMessage() const;

   private:
      long value_;
      int parameter_;

      String message_;
   };
}