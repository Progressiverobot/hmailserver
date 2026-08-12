// Copyright (c) 2005 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Created 2005-07-21

#pragma once

namespace HM
{
   class Task
   {
   public:
      Task(const String &name);
      ~Task(void);

      void Run();

      String GetName() const {return name_;  }

      // The name is what identifies this task in the work queue diagnostics, so
      // callers are expected to make it specific ("SMTP-finalization session=42")
      // before handing the task over. The queue copies it at enqueue time and
      // never reads it again, so it must not be changed after that point.
      void SetName(const String &name) {name_ = name;}

      Event& GetIsStartedEvent() {return is_started_;}

   protected:

      void SetIsStarted() {is_started_.Set();}
      virtual void DoWork() = 0;

   private:
      
      Event is_started_;

      String name_;
   };
}