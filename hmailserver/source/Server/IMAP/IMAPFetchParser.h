// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class IMAPFetchParser  
   {
   public:
      
      IMAPFetchParser();
      virtual ~IMAPFetchParser();

      enum ePartType
      {
         PARTUNKNOWN = 0,
         BODYPEEK = 201,
         ENVELOPE = 202,
         RFC822SIZE = 203,
         UID = 204,
         FLAGS = 205,
         INTERNALDATE = 206,
         BODYSTRUCTURE = 207,
         BODY = 208,
         RFC822 = 209,
         ALL = 210,
         FAST = 211,
         FULL = 212,
         RFC822HEADER = 213,
         RFC822TEXT = 214,
         BODYSTRUCTURENONEXTENSIBLE = 215,
         MODSEQ = 216,
         PREVIEW = 217,
         SAVEDATE = 218,
         EMAILID = 219,
         THREADID = 220,
         BINARYITEM = 221,
         BINARYPEEK = 222,
         BINARYSIZE = 223

      };

      class BodyPart
      {
      public:
         BodyPart();

         
         int octet_start_;
         int octet_count_;

         const String &GetName() const {return name_; }
         void SetName(const String &sName) {name_ = sName; }

         bool GetShowBodyHeaderFields() { return show_body_header_fields_; }
         bool GetShowBodyHeaderFieldsNOT() { return show_body_header_fields_NOT; }
         bool GetShowBodyHeader() { return show_body_header_; }

         // BODY[X.MIME] — the MIME entity headers of part X itself (e.g. Content-Type:
         // message/rfc822). Does NOT load any encapsulated message; returns the outer
         // part's own headers, not those of the message it contains.
         bool GetShowBodyMime() { return show_body_mime_; }

         // BODY[X.TEXT] — explicitly requested text body. For message/rfc822 parts,
         // loads the encapsulated message and returns its body without RFC2822 headers.
         bool GetShowBodyText() { return show_body_text_; }

         // BODY[] — no section specifier; returns the entire raw message from disk,
         // headers and body included.
         bool GetShowBodyFull() { return show_body_full_; }

         // BODY[X] — numeric-only specifier with no sub-keyword. For message/rfc822
         // parts, returns the full inner RFC2822 message (inner headers + body).
         // For all other parts, returns the body content without the MIME entity header.
         // Differs from GetShowBodyText() in that inner headers are included for
         // message/rfc822, and from GetShowBodyFull() in that it navigates to a
         // specific part rather than reading the whole file.
         bool GetShowBodyContent() { return show_body_content_; }

         void SetShowBodyHeaderFields(bool bValue) {show_body_header_fields_ = bValue; }
         void SetShowBodyHeaderFieldsNOT(bool bValue) {show_body_header_fields_NOT = bValue; }
         void SetShowBodyHeader(bool bValue) {show_body_header_ = bValue; }
         void SetShowBodyMime(bool bValue) {show_body_mime_ = bValue; }
         void SetShowBodyText(bool bValue) {show_body_text_ = bValue; }
         void SetShowBodyFull(bool bValue) {show_body_full_ = bValue; }
         void SetShowBodyContent(bool bValue) {show_body_content_ = bValue; }

         std::vector<String> &GetHeaderFields() { return header_fields_; }
         std::vector<String> &GetHeaderFieldsNOT() { return header_fields_NOT; }

         void SetDescription(const String &sDescription ) {description_ = sDescription; }
         String &GetDescription() {return description_; }

         // RFC 3516 (BINARY): the section's content with its transfer encoding
         // decoded, or just the decoded size.
         bool GetShowBinaryContent() { return show_binary_content_; }
         bool GetShowBinarySize() { return show_binary_size_; }
         void SetShowBinaryContent(bool bValue) {show_binary_content_ = bValue; }
         void SetShowBinarySize(bool bValue) {show_binary_size_ = bValue; }

         bool GetBodyTextNeeded()
         {
            // Returns true if we need to load the entire body part, false otherwise.
            return show_body_text_ || show_body_full_ || show_body_content_ ||
                   show_binary_content_ || show_binary_size_;
         }

      private:

         String name_;

         bool show_body_header_fields_;
         bool show_body_header_fields_NOT;
         bool show_body_header_;
         bool show_body_mime_;
         bool show_body_text_;
         bool show_body_full_;
         bool show_body_content_;
         bool show_binary_content_ = false;
         bool show_binary_size_ = false;

         std::vector<String> header_fields_;
         std::vector<String> header_fields_NOT;

         String description_;

      };

      IMAPResult ParseCommand(const String &sCommand);

      bool GetShowEnvelope() { return show_envelope_; }
      bool GetShowRFCSize() { return show_rfcsize_; }
      bool GetShowUID() { return show_uid_; }
      bool GetShowFlags() { return show_flags_; }
      bool GetShowInternalDate() { return show_internal_date_; }
      
      bool GetShowBodyStructure() { return show_body_structure_; }
      bool GetShowBodyStructureNonExtensible() { return show_body_structure_NonExtensible; }
      
      // RFC 7162 (CONDSTORE/QRESYNC): the MODSEQ FETCH data item.
      bool GetShowModSeq() { return show_modseq_; }

      // RFC 8970: the PREVIEW FETCH data item - a short server-generated snippet
      // of the message body. The LAZY modifier is accepted and treated as the
      // plain form, which the RFC permits: LAZY lets a server answer NIL to
      // avoid expensive generation, it does not oblige it to.
      bool GetShowPreview() { return show_preview_; }

      // RFC 8514: the SAVEDATE FETCH data item - when the message was saved
      // into this mailbox.
      bool GetShowSaveDate() { return show_savedate_; }

      // RFC 8474 (OBJECTID): the EMAILID and THREADID FETCH data items. This
      // server has no thread ids, so THREADID is answered NIL - which the RFC
      // provides for exactly this case.
      bool GetShowEmailId() { return show_emailid_; }
      bool GetShowThreadId() { return show_threadid_; }

      // RFC 7162 (CONDSTORE): the "(CHANGEDSINCE <modseq>)" FETCH modifier. When present,
      // only messages whose mod-sequence is greater than this value are returned.
      bool GetHasChangedSince() { return has_changedsince_; }
      __int64 GetChangedSince() { return changedsince_; }

      bool GetSetSeenFlag() { return set_seen_; }

      std::vector<BodyPart> GetPartsToLookAt() { return parts_to_look_at_; }
      
   private:

      ePartType GetPartType_(const String &sPart);
      bool IsPartSpecifier_(const String &sString);
      
      void CleanFetchString_(String &sString);
      std::vector<String> ParseString_(String &sString);
      IMAPResult ValidateSyntax_(const String &sString);
      
      // Additional parsing of commands that create more complex 
      // structure than just single words.
      BodyPart ParseBODY_(const String &sString);
      BodyPart ParseBODY_PEEK(const String &sString);
      BodyPart ParseBINARY_(const String &sString, bool isPeek, bool isSize);

      bool show_envelope_;
      bool show_rfcsize_;
      bool show_uid_;
      bool show_flags_;
      bool show_internal_date_;
      bool show_body_structure_;
      bool show_body_structure_NonExtensible;
      bool show_modseq_;
      bool show_preview_;
      bool show_savedate_;
      bool show_emailid_;
      bool show_threadid_;

      bool has_changedsince_;
      __int64 changedsince_;

      bool set_seen_;
      
      std::vector<BodyPart> parts_to_look_at_;
   };

}
