// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd


namespace HM
{
   class Message;

   class SMTPVacationMessageCreator : public Singleton<SMTPVacationMessageCreator> 
   {
   public:
	   
      SMTPVacationMessageCreator();
	   virtual ~SMTPVacationMessageCreator();

      void CreateVacationMessage(std::shared_ptr<const Account> recipientAccount,
                                  const String &sToAddress, 
                                  const String &sVacationSubject, 
                                  const String &sVacationMessage,
                                  const std::shared_ptr<Message> pOriginalMessage);

      

      void VacationMessageTurnedOff(const String &sUserAddress);

   private:
      
      bool CanSendVacationMessage_(const String &sFrom, const String &sTo);

      std::multimap<String, String> mapVacationMessageRecipients;
      boost::recursive_mutex mutex_;
   };
}
