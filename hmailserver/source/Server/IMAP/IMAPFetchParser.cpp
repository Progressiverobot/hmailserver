// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "IMAPFetchParser.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   IMAPFetchParser::BodyPart::BodyPart() : name_(""),
      octet_start_(-1),
      octet_count_(-1),
      show_body_header_fields_(false),
      show_body_header_fields_NOT(false),
      show_body_header_(false),
      show_body_mime_(false),
      show_body_text_(false),
      show_body_full_(false),
      show_body_content_(false)
   {

   }

   IMAPFetchParser::IMAPFetchParser()
   {
      show_envelope_ = false;
      show_rfcsize_ = false;
      show_uid_ = false;
      show_flags_ = false;
      show_internal_date_ = false;
      show_body_structure_ = false;
      set_seen_ = false;
      show_body_structure_NonExtensible = false;
      show_modseq_ = false;
      show_preview_ = false;
      show_savedate_ = false;
      has_changedsince_ = false;
      changedsince_ = 0;
   }

   IMAPFetchParser::~IMAPFetchParser()
   {

   }

   void
   IMAPFetchParser::CleanFetchString_(String &sString)
   {
      if (sString.Left(1) == _T(" "))
         sString = sString.Mid(1);
      
      if (sString.Left(1) == _T("("))
         sString = sString.Mid(1);

      if (sString.Right(1) == _T(")"))
         sString = sString.Mid(0, sString.GetLength() - 1);
      

   }

   IMAPResult
   IMAPFetchParser::ValidateSyntax_(const String &sString)
   {
      long lNoOfLeftPar = sString.NumberOf(_T("("));
      long lNoOfRightPar = sString.NumberOf(_T(")"));

      long lNoOfLeftBrack = sString.NumberOf(_T("["));
      long lNoOfRightBrack = sString.NumberOf(_T("]"));

      // The partial specifier "<start.size>" was checked nowhere at all. An item
      // carrying an opening angle bracket and no closing one - "BODY[]<0.100 UID" -
      // passed this function (the round and square bracket counts balance) and
      // reached ParseString_, which could not find the ">", rewrote the string to
      // itself and continued: the parse loop then spun on the same input forever.
      // That pins an io_service worker thread at 100% CPU for the life of the
      // process, one thread per command, and any authenticated user can send it.
      // See the matching guard in ParseString_, which is what actually guarantees
      // termination; this rejects the input before it gets that far, which is the
      // answer RFC 3501 asks for (BAD for an unparseable data item).
      long lNoOfLeftAngle = sString.NumberOf(_T("<"));
      long lNoOfRightAngle = sString.NumberOf(_T(">"));

      if (lNoOfLeftBrack != lNoOfRightBrack)
         return IMAPResult(IMAPResult::ResultBad, "Brackets are mismatching.");

      if (lNoOfLeftPar != lNoOfRightPar)
         return IMAPResult(IMAPResult::ResultBad, "Parenthesises are mismatching.");

      if (lNoOfLeftAngle != lNoOfRightAngle)
         return IMAPResult(IMAPResult::ResultBad, "Angle brackets are mismatching.");

      return IMAPResult();

   }

   std::vector<String>
   IMAPFetchParser::ParseString_(String &sString)
   {

      std::vector<String> vecResult;

      CleanFetchString_(sString);

      while (!sString.IsEmpty())
      {
         long lFirstLeftBracket = sString.Find(_T("["));
         long lFirstSpace = sString.Find(_T(" "));

         if (lFirstLeftBracket >= 0 && lFirstLeftBracket < lFirstSpace)
         {
            // Find end bracket.
            long lFirstRightBracket = sString.Find(_T("]"), lFirstLeftBracket);

            // Check if we got a <> directly after the []
            if (sString.SafeGetAt(lFirstRightBracket + 1) == '<')
               lFirstRightBracket = sString.Find(_T(">"), lFirstRightBracket);

            // THE LOOP HAD NO WAY TO END.
            //
            // Both Find calls above answer -1 when they fail, and the single test
            // that followed was "lFirstRightBracket <= 0", which handled -1 with
            // "sString = sString.Mid(-1 + 1)" - Mid(0), the same string back - and
            // then "continue"d past the CleanFetchString_ at the bottom of the loop.
            // Nothing had been consumed and nothing had changed, so the next pass
            // computed the same indices and did the same thing, forever.
            //
            // Two inputs reach it, both from one authenticated FETCH:
            //   1 (BODY[]<0.100 UID)   - the "<" is never closed, so the ">" search
            //                            fails. ValidateSyntax_ now refuses this.
            //   1 (]BODY[ UID)         - the "]" sits before the "[", so the "]"
            //                            search from the "[" fails. The bracket
            //                            counts balance, so this one is syntactically
            //                            acceptable as far as validation goes and
            //                            must be terminated here.
            //
            // -1 means there is no closing bracket anywhere in what remains, so
            // there is no further item to extract: stop rather than spin. Position 0
            // keeps its old handling - Mid(1) does consume a character, so that case
            // always made progress.
            if (lFirstRightBracket < 0)
            {
               sString.Empty();
               break;
            }

            // Parse out string between brackets.
            if (lFirstRightBracket == 0)
            {
               sString = sString.Mid(lFirstRightBracket + 1);
               continue;
            }

            // String between brackets.
            String sResString = sString.Mid(0, lFirstRightBracket+1 );
            vecResult.push_back(sResString);

            // Cut away this from the start string.
            sString = sString.Mid(lFirstRightBracket + 1);
         }
         else if (lFirstSpace >= 0)
         {
            // Copy string from here to end.
            String sResString = sString.Mid(0, lFirstSpace );

            vecResult.push_back(sResString);

            sString = sString.Mid(lFirstSpace + 1);

         }
         else
         {
            vecResult.push_back(sString);
            sString.Empty();
         }
         

         CleanFetchString_(sString);


      }

      return vecResult;
   }


   IMAPResult
   IMAPFetchParser::ParseCommand(const String &sCommand)
   {
      String sStringToParse = sCommand;

      // RFC 8970: "PREVIEW (LAZY)" is the only data item that carries a
      // parenthesised modifier list, and this parser tokenizes on whitespace, so
      // the modifier is folded into the plain form before parsing. Treating LAZY
      // as the plain form is permitted: it lets a server answer NIL to avoid
      // expensive generation, it does not oblige it to.
      long lPreviewLazy = sStringToParse.FindNoCase(_T("PREVIEW (LAZY)"));
      if (lPreviewLazy >= 0)
      {
         String sBefore = sStringToParse.Mid(0, lPreviewLazy);
         String sAfter = sStringToParse.Mid(lPreviewLazy + (long) wcslen(_T("PREVIEW (LAZY)")));
         sStringToParse = sBefore + _T("PREVIEW") + sAfter;
      }

      // RFC 7162 (CONDSTORE): an optional "(CHANGEDSINCE <modseq> [VANISHED])" FETCH modifier
      // may accompany the data items. Consume it before normal item parsing and switch on
      // MODSEQ output (CHANGEDSINCE implicitly enables CONDSTORE responses for this command).
      long lChangedSince = sStringToParse.FindNoCase(_T("CHANGEDSINCE"));
      if (lChangedSince >= 0)
      {
         String sRest = sStringToParse.Mid(lChangedSince + 12); // length of "CHANGEDSINCE"
         sRest.TrimLeft();
         changedsince_ = _ttoi64(sRest);
         has_changedsince_ = true;
         show_modseq_ = true;

         // Remove the enclosing "(CHANGEDSINCE ... )" group so it isn't parsed as a data item.
         long lOpen = lChangedSince;
         while (lOpen > 0 && sStringToParse.SafeGetAt(lOpen) != _T('('))
            lOpen--;
         long lClose = sStringToParse.Find(_T(")"), lChangedSince);
         if (lClose > lOpen && sStringToParse.SafeGetAt(lOpen) == _T('('))
         {
            String sBefore = sStringToParse.Mid(0, lOpen);
            String sAfter = sStringToParse.Mid(lClose + 1);
            sStringToParse = sBefore + sAfter;
         }
      }

      IMAPResult result = ValidateSyntax_(sStringToParse);
      if (result.GetResult() != IMAPResult::ResultOK)
         return result;
      
      std::vector<String> vecResult = ParseString_(sStringToParse);
      auto iter = vecResult.begin();
      while (iter != vecResult.end())
      {
         String sPart = (*iter);

         
         ePartType iType = GetPartType_(sPart);

         switch (iType)
         {
            case BODYPEEK:
            {
               IMAPFetchParser::BodyPart oPart = ParseBODY_PEEK(sPart);
               parts_to_look_at_.push_back(oPart);
               break;
            }
            
            case ENVELOPE:
            {
               show_envelope_ = true;
               break;
            }
            
            case RFC822SIZE:
            {
               show_rfcsize_ = true;
               break;
            }

            case UID:
            {
               show_uid_ = true;
               break;
            }

            case FLAGS:
            {
               show_flags_ = true;
               break;
            }

            case MODSEQ:
            {
               show_modseq_ = true;
               break;
            }

            case PREVIEW:
            {
               show_preview_ = true;
               break;
            }

            case SAVEDATE:
            {
               show_savedate_ = true;
               break;
            }

            case INTERNALDATE:
            {
               show_internal_date_ = true;
               break;
            }

            case BODYSTRUCTURE:
            {
               show_body_structure_ = true;
               break;
            }
            
            case BODYSTRUCTURENONEXTENSIBLE:
            {
               show_body_structure_NonExtensible = true;
               break;
            }

            case BODY:
            {
               IMAPFetchParser::BodyPart oPart = ParseBODY_(sPart);
               parts_to_look_at_.push_back(oPart);
               break;
            }
            case RFC822:
            {
               // Same as:
               IMAPFetchParser::BodyPart oPart = ParseBODY_(sPart);
               oPart.SetDescription("RFC822");
               parts_to_look_at_.push_back(oPart);
               break;

            }
            case ALL:
            {
               // ALL
               // Macro equivalent to: (FLAGS INTERNALDATE RFC822.SIZE ENVELOPE)

               show_flags_ = true;
               show_internal_date_ = true;
               show_rfcsize_ = true;
               show_envelope_ = true;
               break;
            }

            case FAST:
            {
               // FAST
               // Macro equivalent to: (FLAGS INTERNALDATE RFC822.SIZE)

               show_flags_ = true;
               show_internal_date_ = true;
               show_rfcsize_ = true;
               break;
            }

            case FULL:
            {
               // FULL
               // Macro equivalent to: (FLAGS INTERNALDATE RFC822.SIZE ENVELOPE BODY)
               show_flags_ = true;
               show_internal_date_ = true;
               show_rfcsize_ = true;
               show_envelope_ = true;
               show_body_structure_ = true;
               break;
            }
            case RFC822HEADER:
            {
               /* 
                  RFC822.HEADER
                  Functionally equivalent to BODY.PEEK[HEADER], differing in the
                  syntax of the resulting untagged FETCH data (RFC822.HEADER is
                  returned).
                  */
            
               IMAPFetchParser::BodyPart oPart = ParseBODY_PEEK("BODY[HEADER]");
               oPart.SetDescription("RFC822.HEADER");
               parts_to_look_at_.push_back(oPart);
               break;
            }
            case RFC822TEXT:
               {
                  /* 
                  Functionally equivalent to BODY[TEXT], differing in the syntax
                  of the resulting untagged FETCH data (RFC822.TEXT is returned).
                  */

                  IMAPFetchParser::BodyPart oPart = ParseBODY_("BODY[TEXT]");
                  oPart.SetDescription("RFC822.TEXT");
                  parts_to_look_at_.push_back(oPart);

                  break;
               }
         }

         iter++;
      }

      return IMAPResult();
   }

   IMAPFetchParser::BodyPart
   IMAPFetchParser::ParseBODY_(const String &sString)
   {
      BodyPart oPart;
      
      // Set the description.

      String sNewName = sString;
      sNewName.ReplaceNoCase(_T("BODY.PEEK["), _T("BODY["));

      oPart.SetDescription(sNewName);

      // Locate the start of the peek-part.
      long lBodyStart = sNewName.Find(_T("[")) + 1;
      
      // Locate the end of the part.
      long lBodyEnd = sNewName.Find(_T("]"), lBodyStart) - 1;

      if (sNewName.Find(_T("<"), lBodyEnd) == lBodyEnd +2)
      {
         int iStart = lBodyEnd+3;
         int iEnd = sNewName.Find(_T(">"), iStart);

         if (iEnd < 0)
         {
            // No closing ">". Every calculation below used to go wrong at once for
            // this: Mid(iStart, negative) gave an empty range string, the range
            // silently became <0.0>, and the description was rebuilt as everything
            // before the "<" plus Mid(0) - the WHOLE original item appended to
            // itself, so the untagged FETCH response carried a part identifier with
            // unbalanced brackets in it. A client parsing that is worse off than one
            // given no data.
            //
            // ParseCommand refuses this input before it reaches here now, so this is
            // a floor rather than a live path: drop the unterminated range, keep the
            // section, and answer as an ordinary non-partial fetch.
            oPart.SetDescription(sNewName.Mid(0, iStart - 1));
         }
         else
         {
            String sPartial = sNewName.Mid(iStart, iEnd - iStart);
            int iDotPos = sPartial.Find(_T("."));

            if (iDotPos >= 0)
            {
               oPart.octet_start_ = _ttoi(sPartial.Mid(0, iDotPos));
               oPart.octet_count_ = _ttoi(sPartial.Mid(iDotPos+1));
            }
            else
            {
               // "<start>" with no ".size". RFC 3501 spells the REQUEST form
               // "<" number "." nz-number ">", so this is not valid on the way in -
               // but it is exactly the form the server uses in its own RESPONSE
               // ("BODY[]<1024> {n}"), and clients do echo that back.
               //
               // It used to parse as start 0 with a count of the whole value,
               // because iDotPos was -1: Mid(0, -1) is the empty string and
               // Mid(-1 + 1) is the whole of it. So a client asking for the message
               // from octet 1024 onwards was handed the FIRST 1024 octets, labelled
               // "<0>" - the wrong region of the message, silently, which is the
               // failure this file has now been reported for three times.
               //
               // Read it as the origin with no length limit. 0x7FFFFFFF rather than
               // a negative sentinel because the normalization just below turns any
               // negative count into 0; GetBytesToSend_ clamps this to whatever
               // actually remains after the origin.
               oPart.octet_start_ = _ttoi(sPartial);
               oPart.octet_count_ = 0x7FFFFFFF;
            }

            // A malformed range (negative start/size, e.g. "<-5.10>" or "<0.-1>")
            // must not reach the byte math as a negative int. Normalize to 0 here;
            // GetBytesToSend_ clamps the rest.
            if (oPart.octet_start_ < 0)
               oPart.octet_start_ = 0;
            if (oPart.octet_count_ < 0)
               oPart.octet_count_ = 0;

            // Remove the octets part from the description.
            String sBefore = sNewName.Mid(0, iStart - 1);
            String sAfter = sNewName.Mid(iEnd + 1);

            String sDescWithoutOctets = sBefore + sAfter;

            oPart.SetDescription(sDescWithoutOctets);
         }
      }

      // Extract the body  part.
      long lBodyLen = lBodyEnd - lBodyStart +1 ;
      String sBody = sNewName.Mid(lBodyStart, lBodyLen);

      if (sBody.IsEmpty())
      {
         oPart.SetShowBodyFull(true);
         set_seen_ = true;
      }
      else
      {
         // Determine what to look at.
         long lTemp  = 0;

         // Should we show all header fields except for...
         lTemp = sBody.FindNoCase(_T("HEADER.FIELDS.NOT"));
         if (lTemp >= 0)
         {
            int lStart = sBody.Find(_T("("), lTemp) + 1;
            int lEnd = sBody.Find(_T(")"), lStart) ;
            int lLength = lEnd - lStart;
            
            String sFields = sBody.Mid(lStart, lLength);
            oPart.GetHeaderFieldsNOT() = StringParser::SplitString(sFields, " ");
            oPart.SetShowBodyHeaderFieldsNOT(true);

            // Strip away the header fields part from the Body.
            // If we don't do this, we will parse the same string
            // as header.fields below.
            String sBefore = sBody.Mid(0, lTemp);
            String sAfter = sBody.Mid(lEnd + 2);
            sBody = sBefore + sAfter;

         }

         // Should we show header fields?
         lTemp = sBody.FindNoCase(_T("HEADER.FIELDS"));
         if (lTemp >= 0)
         {
            int lStart = sBody.Find(_T("("), lTemp) + 1;
            int lEnd = sBody.Find(_T(")"), lStart) ;
            int lLength = lEnd - lStart;
            
            String sFields = sBody.Mid(lStart, lLength);
            oPart.GetHeaderFields() = StringParser::SplitString(sFields, " ");
            oPart.SetShowBodyHeaderFields(true);

            // Strip away the header fields part from the Body.
            String sBefore = sBody.Mid(0, lTemp);
            String sAfter = sBody.Mid(lEnd + 2);
            sBody = sBefore + sAfter;
         }

         lTemp = sBody.FindNoCase(_T("HEADER"));
         if (lTemp >= 0)
         {
            oPart.SetShowBodyHeader(true);
            
            String sBefore = sBody.Mid(0, lTemp);
            String sAfter = sBody.Mid(lTemp + 7);
            sBody = sBefore + sAfter;
         }

         lTemp = sBody.FindNoCase(_T("MIME"));
         if (lTemp >= 0)
         {
            oPart.SetShowBodyMime(true);

            String sBefore = sBody.Mid(0, lTemp);
            String sAfter = sBody.Mid(lTemp + 5);
            sBody = sBefore + sAfter;
         }

         lTemp = sBody.FindNoCase(_T("TEXT"));
         if (lTemp >= 0)
         {
            oPart.SetShowBodyText(true);
            
            String sBefore = sBody.Mid(0, lTemp);
            String sAfter = sBody.Mid(lTemp + 5);
            sBody = sBefore + sAfter;

            set_seen_ = true;
         }

         if (!oPart.GetShowBodyText() &&
             !oPart.GetShowBodyHeader() &&
             !oPart.GetShowBodyHeaderFields() &&
             !oPart.GetShowBodyHeaderFieldsNOT() &&
             !oPart.GetShowBodyMime())
         {
             oPart.SetShowBodyContent(true);
             set_seen_ = true;
         }
         
         sBody = sBody.TrimLeft(_T("."));
         sBody = sBody.TrimRight(_T("."));

         oPart.SetName(sBody);
      }

      return oPart;

   }



   
   IMAPFetchParser::ePartType
   IMAPFetchParser::GetPartType_(const String &sPart)
   {
      if (sPart.FindNoCase(_T("BODY.PEEK")) >= 0)
         return BODYPEEK;

      if (sPart.CompareNoCase(_T("BODYSTRUCTURE")) == 0)
         return BODYSTRUCTURE;      

      if (sPart.CompareNoCase(_T("BODY")) == 0)
         return BODYSTRUCTURENONEXTENSIBLE;

      if (sPart.FindNoCase(_T("BODY")) >= 0)
         return BODY;

      if (sPart.CompareNoCase(_T("ENVELOPE")) == 0)
         return ENVELOPE;      

      if (sPart.CompareNoCase(_T("RFC822.SIZE")) == 0)
         return RFC822SIZE;         

      if (sPart.CompareNoCase(_T("UID")) == 0)
         return UID;  

      if (sPart.CompareNoCase(_T("FLAGS")) == 0)
         return FLAGS;  

      if (sPart.CompareNoCase(_T("MODSEQ")) == 0)
         return MODSEQ;

      if (sPart.CompareNoCase(_T("PREVIEW")) == 0)
         return PREVIEW;

      if (sPart.CompareNoCase(_T("SAVEDATE")) == 0)
         return SAVEDATE;  

      if (sPart.CompareNoCase(_T("INTERNALDATE")) == 0)
         return INTERNALDATE;  

      if (sPart.CompareNoCase(_T("RFC822")) == 0)
         return RFC822;  

      if (sPart.CompareNoCase(_T("ALL")) == 0)
         return ALL;  

      if (sPart.CompareNoCase(_T("FAST")) == 0)
         return FAST;  

      if (sPart.CompareNoCase(_T("FULL")) == 0)
         return FULL;  

      if (sPart.CompareNoCase(_T("RFC822.HEADER")) == 0)
         return RFC822HEADER;  

      if (sPart.CompareNoCase(_T("RFC822.TEXT")) == 0)
         return RFC822TEXT;  

      return PARTUNKNOWN;
   }


   IMAPFetchParser::BodyPart
   IMAPFetchParser::ParseBODY_PEEK(const String &sString)
   {
      // set_seen_ is parser-wide, so a PEEK item must not clear a request for
      // \Seen made by a non-peek body item earlier in the same FETCH: the
      // message would stay unread after the client had actually read it.
      bool set_seen_before = set_seen_;

      BodyPart oPart = ParseBODY_(sString);

      set_seen_ = set_seen_before;

      return oPart;
   }

   bool 
   IMAPFetchParser::IsPartSpecifier_(const String &sString)
   {
      String sTemp = sString;
      sTemp.ToUpper();
      sTemp.Replace(_T("."), _T(""));
      sTemp.Replace(_T("MIME"), _T(""));
      sTemp.Replace(_T("HEADER"), _T(""));
      sTemp.Replace(_T("TEXT"), _T(""));

      if (sTemp.GetLength() == 0)
         return false;

      if (StringParser::IsNumeric(sTemp))      
         return true;

      return false;

      
      
   }
}

