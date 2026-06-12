// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class MessageData;
   class SURBLServer;

   class SURBL
   {
   public:
      SURBL(void);
      ~SURBL(void);

      bool ExtractUrls(std::shared_ptr<MessageData> pMessageData, std::vector<String> &vecUrls);
      bool Run(std::shared_ptr<SURBLServer> pSURBLServer, std::vector<String> &vecUrls);

   private:

      void CleanURL_(String &sURL) const;
      bool CleanHost_(String &sDomain) const;
   };
}