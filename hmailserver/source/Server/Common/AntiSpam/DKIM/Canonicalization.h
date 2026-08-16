// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

namespace HM
{
   class Message;
   class MessageData;
   class MimeHeader;
   class MimeField;

   class Canonicalization
   {
   public:
      enum CanonicalizeMethod
      {
         Simple = 1,
         Relaxed = 2
      };      

      virtual ~Canonicalization() {};
      virtual AnsiString CanonicalizeBody(AnsiString value) = 0;
      virtual AnsiString CanonicalizeHeader(AnsiString value, const std::pair<AnsiString, AnsiString> &signatureField, const std::vector<AnsiString> &fieldsToInclude, AnsiString &fieldList ) = 0;
      virtual AnsiString CanonicalizeHeaderValue(AnsiString value) = 0;
      virtual AnsiString CanonicalizeHeaderName(AnsiString value) = 0;
      virtual AnsiString CanonicalizeHeaderLine(AnsiString name, AnsiString value) = 0;
      
   protected:
 
      AnsiString GetDKIMWithoutSignature_(AnsiString value);
   };

   class RelaxedCanonicalization : public Canonicalization
   {
   public:

      virtual AnsiString CanonicalizeBody(AnsiString value);
      virtual AnsiString CanonicalizeHeader(AnsiString header, const std::pair<AnsiString, AnsiString> &signatureField, const std::vector<AnsiString> &fieldsToInclude, AnsiString &fieldList);
      virtual AnsiString CanonicalizeHeaderValue(AnsiString value);
      virtual AnsiString CanonicalizeHeaderName(AnsiString value);
      
      virtual AnsiString CanonicalizeHeaderLine(AnsiString name, AnsiString value)
      {
         return CanonicalizeHeaderName(name) + ":" + CanonicalizeHeaderValue(value);
      }

   };

   class SimpleCanonicalization : public Canonicalization
   {
   public:

      virtual AnsiString CanonicalizeBody(AnsiString value);
      virtual AnsiString CanonicalizeHeader(AnsiString header, const std::pair<AnsiString, AnsiString> &signatureField,  const std::vector<AnsiString> &fieldsToInclude, AnsiString &fieldList);
      virtual AnsiString CanonicalizeHeaderLine(AnsiString name, AnsiString value)
      {
         return CanonicalizeHeaderName(name) + ": " + CanonicalizeHeaderValue(value);
      }

      virtual AnsiString CanonicalizeHeaderValue(AnsiString value)
      {
         return value;
      }
      virtual AnsiString CanonicalizeHeaderName(AnsiString value)
      {
         return value;
      }

   };

   // In-process tests for DKIM canonicalization, run from ClassTester::DoTests
   // (the regression suite reaches it through Utilities.RunTestSuite).
   //
   // These belong in process rather than in the C# suite because the two things
   // they pin down - which bytes go into the body hash, and which bytes of the
   // DKIM-Signature field go into the header hash - are not observable from
   // outside: every way of getting them wrong produces the same externally
   // visible outcome, a signature that does not verify.
   class CanonicalizationTester
   {
   public:
      void Test();
   };
}