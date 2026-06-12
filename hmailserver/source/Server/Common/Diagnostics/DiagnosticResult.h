// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{

   class DiagnosticResult
   {
   public:
      
      DiagnosticResult(String name, String description, bool Success, String Details);
      DiagnosticResult();

      void SetName(const String &name) {name_ = name;}
      void SetDescription(const String &description) {description_ = description;}
      void SetDetails(const String &details) {details_ = details;}
      void SetSuccess(bool success) {success_ = success;}

      String GetName() const 
      {
         return name_;
      }

      String GetDescription() const
      {
         return description_;
      }

      String GetDetails() const
      {
         return details_;
      }

      bool GetSuccess() const
      {
         return success_;
      }

   private:
      
      String name_;
      String description_;
      bool success_;
      String details_;

   };


}
