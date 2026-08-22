// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class Language;

   class Languages : public Singleton<Languages>
   {
   public:
      Languages(void);
      ~Languages(void);

      void Load();

      std::shared_ptr<Language> GetLanguage(const String &sLanguage);
      std::shared_ptr<Language> GetLanguage(int index);
      size_t GetCount() { return languages_.size(); }

     
   private:

      bool IsValidLangauge_(const String &sLanguage) const;

      std::map<String, std::shared_ptr<Language> > languages_;


      
   };
}