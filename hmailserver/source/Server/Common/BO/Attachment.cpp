// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"

#include "Attachment.h"

#include "../Mime/Mime.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   Attachment::Attachment(std::shared_ptr<MimeBody> pMessage, std::shared_ptr<MimeBody> pAttachment) :
      message_(pMessage),
      attachment_(pAttachment)
   {

   }

   Attachment::~Attachment()
   {

   }

   void
   Attachment::SaveAs(const String &sSaveTo) const
   {
      // Reached from COM (IInterfaceAttachment::SaveAs), where the caller is a script
      // or an administration tool that has just been told the save worked. The result
      // was discarded and the method is void, so a failed write - a full disk, a path
      // that cannot be created, a body that could not be read back - looked exactly
      // like a successful one and the script carried on to the next attachment.
      if (!attachment_->WriteToFile(sSaveTo))
      {
         ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6003, "Attachment::SaveAs",
            Formatter::Format("The attachment could not be written to {0}. The caller has not been told, because this method cannot report it; the file is either missing or incomplete.", sSaveTo));
      }
   }

   String 
   Attachment::GetFileName()
   {
      return attachment_->GetUnicodeFilename();
   }

   void
   Attachment::SetFileName(const String &file_name)
   {
      attachment_->SetFileName(file_name);
   }

   void 
   Attachment::SetContent(const String &content)
   {
      attachment_->SetUnicodeText(content);
   }

   int
   Attachment::GetSize()
   {
      return attachment_->GetContentLength();
   }

   void
   Attachment::Delete()
   {
      // Remove this attachment from the parent message.
      message_->RemoveAttachment(attachment_);
   }
}
