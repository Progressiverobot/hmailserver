// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "COMError.h"
#include "InterfaceMessageHeader.h"

#include "../Common/Mime/Mime.h"

InterfaceMessageHeader::InterfaceMessageHeader() :
   field_index_(-1),
   deleted_(false)
{

}

void 
InterfaceMessageHeader::AttachItem (std::shared_ptr<HM::MimeHeader> pHeader, HM::MimeField *pField)
{
   header_ = pHeader;
   field_name_ = "";
   field_index_ = -1;
   deleted_ = false;

   if (!pHeader || !pField)
      return;

   field_name_ = pField->GetName();

   // The pointer is used here, while the caller still guarantees it is valid, and is
   // then thrown away: what is kept is where the field was, not where it lived.
   HM::MimeHeader::CFieldList &fields = pHeader->Fields();

   for (size_t i = 0; i < fields.size(); i++)
   {
      if (&fields[i] == pField)
      {
         field_index_ = static_cast<int>(i);
         break;
      }
   }
}

// Returns the field this object refers to, looked up in the collection as it stands
// now, or NULL if it has gone.
//
// This indirection exists because the obvious implementation is memory-unsafe. A
// MimeHeader holds its fields in a std::vector<MimeField> by value, so:
//
//   * adding a header - Message.HeaderValue = ..., or anything else reaching
//     MimeHeader::SetField - appends to that vector, and an append that grows it
//     invalidates every MimeField* in existence;
//   * deleting one erases the element, which move-assigns each following field down
//     one slot and destroys the last slot. The pointer held for a field after the
//     deleted one now addresses a *different* field, and the pointer held for the
//     last one addresses a destroyed MimeField whose strings have freed their
//     buffers.
//
// This class used to cache the raw MimeField* it was handed by
// InterfaceMessageHeaders. A script that deleted one header and then touched another
// header object it was already holding therefore read a destroyed object, or - the
// case that loses data rather than merely reading rubbish - wrote its new value onto
// a header it had never asked for, in a message that is then saved to disk.
HM::MimeField *
InterfaceMessageHeader::ResolveField_()
{
   if (!header_ || deleted_ || field_index_ < 0)
      return nullptr;

   HM::MimeHeader::CFieldList &fields = header_->Fields();

   // The recorded position first. While nothing has been added or removed it is still
   // right, and it is the only thing that tells one of several fields sharing a name -
   // Received, for instance - from another.
   size_t index = static_cast<size_t>(field_index_);

   if (index < fields.size() && field_name_.CompareNoCase(fields[index].GetName()) == 0)
      return &fields[index];

   // The position has moved. Fall back to the first field of that name and record
   // where it is now. Where a name is duplicated this can pick a different one of the
   // duplicates than the caller originally asked for - that is the limit of
   // identifying a field by name - and it is a great deal better than dereferencing a
   // pointer into a vector that has been reshaped underneath it.
   for (size_t i = 0; i < fields.size(); i++)
   {
      if (field_name_.CompareNoCase(fields[i].GetName()) == 0)
      {
         field_index_ = static_cast<int>(i);
         return &fields[i];
      }
   }

   return nullptr;
}

// Refused with an explanation rather than reported through ErrorManager: this is a
// mistake in the caller's script, not a defect in the server, and an ERROR record in
// the log is not a free thing to write - PerformBasicSetup fails if the error log
// exists at all, so one would break every fixture that ran afterwards.
HRESULT
InterfaceMessageHeader::ReportUnavailable_()
{
   if (!header_)
      return COMError::GenerateError("This MessageHeader object is not attached to a message. Obtain one from Message.Headers rather than creating it directly.");

   return COMError::GenerateError("The message header this object referred to no longer exists. Re-read it from Message.Headers after headers have been added or deleted.");
}

// RFC 5322 field-name: one or more printable US-ASCII characters other than the
// colon. Anything else, written into a name, produces a message file that no longer
// parses back into the headers it was given - a colon splits the name in two, and a
// CR or LF splits one header into two.
bool
InterfaceMessageHeader::IsValidFieldName_(const HM::AnsiString &name)
{
   if (name.IsEmpty())
      return false;

   for (int i = 0; i < name.GetLength(); i++)
   {
      unsigned char character = static_cast<unsigned char>(name.GetAt(i));

      if (character <= 32 || character >= 127 || character == ':')
         return false;
   }

   return true;
}

STDMETHODIMP 
InterfaceMessageHeader::Delete()
{
   try
   {
      HM::MimeField *field = ResolveField_();

      if (!field)
         return ReportUnavailable_();

      header_->DeleteField(field);

      // The field is gone. Both the position and the name now identify either nothing
      // or somebody else's field, so this object is finished; saying so is better
      // than silently resolving onto a namesake.
      deleted_ = true;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageHeader::put_Name(BSTR newVal)
{
   try
   {
      HM::MimeField *field = ResolveField_();

      if (!field)
         return ReportUnavailable_();

      HM::AnsiString sName = newVal;

      if (!IsValidFieldName_(sName))
         return COMError::GenerateError("A message header name must be one or more printable US-ASCII characters and must not contain a colon.");

      // The existing value is copied out first, because setting it back is the only
      // way from here to mark the field modified - and it has to be marked.
      // MimeField::SetName does not set that flag, and MimeField::Store writes the
      // field's original raw line verbatim whenever the flag is clear, so before this
      // a rename was visible to the script that made it and silently absent from the
      // message that was saved.
      HM::AnsiString sExistingValue = field->GetValue();

      field->SetName(sName);
      field->SetValue(sExistingValue);

      // The name is what this object resolves by, so it has to follow the rename;
      // otherwise the next call cannot find the field it just renamed.
      field_name_ = sName;

      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageHeader::get_Name(BSTR *pVal)
{
   try
   {
      if (!pVal)
         return E_POINTER;

      *pVal = nullptr;

      HM::MimeField *field = ResolveField_();

      if (!field)
         return ReportUnavailable_();

      HM::String sName = field->GetName();
      *pVal = sName.AllocSysString();
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageHeader::put_Value(BSTR newVal)
{
   try
   {
      HM::MimeField *field = ResolveField_();

      if (!field)
         return ReportUnavailable_();

      HM::AnsiString sValue = newVal;
      field->SetValue(sValue);
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}

STDMETHODIMP InterfaceMessageHeader::get_Value(BSTR *pVal)
{
   try
   {
      if (!pVal)
         return E_POINTER;

      *pVal = nullptr;

      HM::MimeField *field = ResolveField_();

      if (!field)
         return ReportUnavailable_();

      HM::String sValue = field->GetValue();
      *pVal = sValue.AllocSysString();
   
      return S_OK;
   }
   catch (...)
   {
      return COMError::GenerateGenericMessage();
   }
}


