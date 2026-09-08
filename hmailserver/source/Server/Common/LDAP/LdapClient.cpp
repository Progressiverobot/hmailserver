// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "LdapClient.h"
#ifdef HM_PLATFORM_POSIX

// OpenLDAP. This arm and the wldap32 one below share their function NAMES -
// ldap_search_ext_s, ldap_parse_result, ldap_first_entry - and very little else
// that matters here: OpenLDAP takes and returns UTF-8 where wldap32 takes wide
// strings, a session is opened by ldap_initialize from a URI rather than by
// ldap_init from a host and a port, a paged reply is read back through
// ldap_parse_pageresponse_control, and a Negotiate bind does not exist at all. So
// the two arms share the value mapping, the escaping and the template expansion -
// the parts that carry the security consequences and the tests - and nothing that
// touches a handle. Needs libldap and liblber on the link line; the CMakeLists
// finds both.
#include <ldap.h>
#include <lber.h>

// explicit_bzero, which is what SecureZeroMemory is on this platform.
#include <string.h>

// Unicode::WideToMultiByte and MultiByteToWide, the UTF-8 conversions between
// the server's wide String and the char* OpenLDAP takes and returns. The Windows
// arm needs no conversion at all - wldap32 has a wide entry point for everything
// it calls - which is why this include is here and not above the #ifdef.
#include "../Util/Unicode.h"

// The client-side result codes, and the one deliberate renumbering in this file.
//
// Every code the PROTOCOL defines - RFC 4511 appendix A, 0 to 0x50 - has the same
// value in winldap.h and in ldap.h, so LDAP_INVALID_CREDENTIALS is 0x31 on both
// platforms and the classification below needs no help with it. The codes a
// LIBRARY adds for a failure that never reached a server - the socket could not
// be opened, the reply did not arrive, the filter did not parse - are numbered by
// each library for itself: winldap.h counts them upwards from 0x51, ldap.h counts
// them downwards from -1, and by no coincidence in the same order.
//
// last_error_ is shown to administrators, keyed into the report throttle, and
// compared by IsCredentialRejection, IsTransportFailure_ and DescribeLastError,
// and the promise this file makes is that the same failure produces the same
// number on both platforms. So the macros ldap.h defines for these codes are
// captured under their own names, taken off, and re-declared below with the
// winldap values; TranslateNative_ turns what OpenLDAP returns into that
// numbering, ToNative_ turns it back for ldap_err2string, and every result code
// this arm receives from the library goes through the first before anything
// looks at it.
namespace OpenLdapNative
{
   enum
   {
      ServerDown            = LDAP_SERVER_DOWN,
      LocalError            = LDAP_LOCAL_ERROR,
      EncodingError         = LDAP_ENCODING_ERROR,
      DecodingError         = LDAP_DECODING_ERROR,
      Timeout               = LDAP_TIMEOUT,
      AuthUnknown           = LDAP_AUTH_UNKNOWN,
      FilterError           = LDAP_FILTER_ERROR,
      UserCancelled         = LDAP_USER_CANCELLED,
      ParamError            = LDAP_PARAM_ERROR,
      NoMemory              = LDAP_NO_MEMORY,
      ConnectError          = LDAP_CONNECT_ERROR,
      NotSupported          = LDAP_NOT_SUPPORTED,
      ControlNotFound       = LDAP_CONTROL_NOT_FOUND,
      NoResultsReturned     = LDAP_NO_RESULTS_RETURNED,
      MoreResultsToReturn   = LDAP_MORE_RESULTS_TO_RETURN,
      ClientLoop            = LDAP_CLIENT_LOOP,
      ReferralLimitExceeded = LDAP_REFERRAL_LIMIT_EXCEEDED
   };
}

#undef LDAP_SERVER_DOWN
#undef LDAP_LOCAL_ERROR
#undef LDAP_ENCODING_ERROR
#undef LDAP_DECODING_ERROR
#undef LDAP_TIMEOUT
#undef LDAP_AUTH_UNKNOWN
#undef LDAP_FILTER_ERROR
#undef LDAP_USER_CANCELLED
#undef LDAP_PARAM_ERROR
#undef LDAP_NO_MEMORY
#undef LDAP_CONNECT_ERROR
#undef LDAP_NOT_SUPPORTED
#undef LDAP_CONTROL_NOT_FOUND
#undef LDAP_NO_RESULTS_RETURNED
#undef LDAP_MORE_RESULTS_TO_RETURN
#undef LDAP_CLIENT_LOOP
#undef LDAP_REFERRAL_LIMIT_EXCEEDED

enum
{
   // Result code 0x32, which ldap.h spells LDAP_INSUFFICIENT_ACCESS and winldap.h
   // and the shared code below spell LDAP_INSUFFICIENT_RIGHTS.
   LDAP_INSUFFICIENT_RIGHTS     = LDAP_INSUFFICIENT_ACCESS,

   // The winldap.h numbering of the client-side codes; see above.
   LDAP_SERVER_DOWN             = 0x51,
   LDAP_LOCAL_ERROR             = 0x52,
   LDAP_ENCODING_ERROR          = 0x53,
   LDAP_DECODING_ERROR          = 0x54,
   LDAP_TIMEOUT                 = 0x55,
   LDAP_AUTH_UNKNOWN            = 0x56,
   LDAP_FILTER_ERROR            = 0x57,
   LDAP_USER_CANCELLED          = 0x58,
   LDAP_PARAM_ERROR             = 0x59,
   LDAP_NO_MEMORY               = 0x5a,
   LDAP_CONNECT_ERROR           = 0x5b,
   LDAP_NOT_SUPPORTED           = 0x5c,
   LDAP_CONTROL_NOT_FOUND       = 0x5d,
   LDAP_NO_RESULTS_RETURNED     = 0x5e,
   LDAP_MORE_RESULTS_TO_RETURN  = 0x5f,
   LDAP_CLIENT_LOOP             = 0x60,
   LDAP_REFERRAL_LIMIT_EXCEEDED = 0x61
};

#else

// PCCERT_CONTEXT, which winldap.h uses in the VERIFYSERVERCERT signature without
// declaring it. Included before winldap.h for that reason; crypt32.lib is already on
// the link line (DataProtector uses it).
#include <wincrypt.h>

// SEC_WINNT_AUTH_IDENTITY_W, the structure a Negotiate bind carries explicit
// credentials in. SECURITY_WIN32 selects the Win32 flavour of sspi.h; Secur32.lib is
// already on the link line.
#define SECURITY_WIN32
#include <sspi.h>

// The Windows LDAP API. Deliberately used in place of an OpenLDAP dependency: it
// ships with the operating system, it is the client Active Directory is tested
// against, and it is the only one that can do a Negotiate bind without extra
// libraries. Needs wldap32.lib on the link line.
#include <winldap.h>

#endif
#include <atomic>
#include <vector>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
#ifdef HM_PLATFORM_POSIX

   namespace
   {
      // OpenLDAP's own number for a result, as the winldap number the rest of this
      // file is written in. Case by case rather than the arithmetic the two tables
      // happen to share (-n on one side is 0x50 + n on the other), so that each line
      // can be checked against both headers by eye, and so that a code one library
      // defines and the other does not cannot be invented by a subtraction.
      unsigned long TranslateNative_(int nativeCode)
      {
         switch (nativeCode)
         {
            case OpenLdapNative::ServerDown:            return LDAP_SERVER_DOWN;
            case OpenLdapNative::LocalError:            return LDAP_LOCAL_ERROR;
            case OpenLdapNative::EncodingError:         return LDAP_ENCODING_ERROR;
            case OpenLdapNative::DecodingError:         return LDAP_DECODING_ERROR;
            case OpenLdapNative::Timeout:               return LDAP_TIMEOUT;
            case OpenLdapNative::AuthUnknown:           return LDAP_AUTH_UNKNOWN;
            case OpenLdapNative::FilterError:           return LDAP_FILTER_ERROR;
            case OpenLdapNative::UserCancelled:         return LDAP_USER_CANCELLED;
            case OpenLdapNative::ParamError:            return LDAP_PARAM_ERROR;
            case OpenLdapNative::NoMemory:              return LDAP_NO_MEMORY;
            case OpenLdapNative::ConnectError:          return LDAP_CONNECT_ERROR;
            case OpenLdapNative::NotSupported:          return LDAP_NOT_SUPPORTED;
            case OpenLdapNative::ControlNotFound:       return LDAP_CONTROL_NOT_FOUND;
            case OpenLdapNative::NoResultsReturned:     return LDAP_NO_RESULTS_RETURNED;
            case OpenLdapNative::MoreResultsToReturn:   return LDAP_MORE_RESULTS_TO_RETURN;
            case OpenLdapNative::ClientLoop:            return LDAP_CLIENT_LOOP;
            case OpenLdapNative::ReferralLimitExceeded: return LDAP_REFERRAL_LIMIT_EXCEEDED;
            default:
               break;
         }

         // A negative code ldap.h did not define when this was written. It came from
         // the library and not from a server, so it is a local failure and never a
         // credential rejection, and LDAP_LOCAL_ERROR is the honest name for it.
         if (nativeCode < 0)
            return LDAP_LOCAL_ERROR;

         // A protocol code, identical on both platforms.
         return (unsigned long) nativeCode;
      }

      // The inverse, and the one place the arithmetic IS used: it feeds
      // ldap_err2string and nothing else, so a wrong answer costs a wrong sentence
      // rather than a wrong decision.
      int ToNative_(unsigned long code)
      {
         if (code >= LDAP_SERVER_DOWN && code <= LDAP_REFERRAL_LIMIT_EXCEEDED)
            return -(int) (code - 0x50);

         return (int) code;
      }

      // UTF-8, which is what OpenLDAP speaks, from the wide String the rest of the
      // server speaks. False when the value cannot be encoded - a lone surrogate, a
      // code point past U+10FFFF - which is not a question the directory can be
      // asked, so every caller treats such a value as one it will not send.
      bool ToUtf8_(const String &value, AnsiString &utf8)
      {
         if (value.IsEmpty())
         {
            utf8.Empty();
            return true;
         }

         return Unicode::WideToMultiByte(value, utf8);
      }

      // A NUL-terminated, writable UTF-8 copy of a String, for the entry points that
      // take char* rather than const char*. OpenLDAP does not write through them; the
      // copy is so that no pointer into a String's own storage is handed out, which
      // is the same rule the Windows arm's ToMutable_ keeps.
      bool ToMutableUtf8_(const String &value, std::vector<char> &buffer)
      {
         AnsiString utf8;

         if (!ToUtf8_(value, utf8))
            return false;

         buffer.assign(utf8.begin(), utf8.end());
         buffer.push_back('\0');

         return true;
      }

      // The other direction, for what the directory sends back. A null pointer -
      // which is what ldap_get_dn answers for an entry with no DN - is an empty
      // string, and so are bytes that are not valid UTF-8, which RFC 4511 forbids a
      // server to send: empty rather than a guess, because a guessed DN is one that
      // can be bound as.
      String FromUtf8_(const char *value, size_t length)
      {
         String result;

         if (value == nullptr || length == 0)
            return result;

         if (!Unicode::MultiByteToWide(AnsiString(std::string(value, length)), result))
            result.Empty();

         return result;
      }

      String FromUtf8_(const char *value)
      {
         return FromUtf8_(value, value != nullptr ? strlen(value) : 0);
      }

      // The server's diagnosticMessage for the last operation, for the entry points
      // that hand back no result message - ldap_connect, ldap_start_tls_s - and so
      // cannot be parsed for one. The library keeps it on the handle.
      String ReadDiagnostic_(LDAP *session)
      {
         char *text = nullptr;

         if (ldap_get_option(session, LDAP_OPT_DIAGNOSTIC_MESSAGE, &text) != LDAP_OPT_SUCCESS || text == nullptr)
            return String();

         String diagnostic = FromUtf8_(text);
         ldap_memfree(text);

         return diagnostic;
      }

      struct timeval SecondsAsTimeval_(int seconds)
      {
         struct timeval timeout;
         timeout.tv_sec = seconds;
         timeout.tv_usec = 0;

         return timeout;
      }

      // Frees the paging cookie ldap_parse_pageresponse_control allocates, and leaves
      // the berval empty so that it reads as "first page" if it reaches
      // ldap_create_page_control again.
      void ReleaseCookie_(struct berval &cookie)
      {
         if (cookie.bv_val != nullptr)
            ber_memfree(cookie.bv_val);

         cookie.bv_val = nullptr;
         cookie.bv_len = 0;
      }
   }

#else
   namespace
   {
      // A NUL-terminated, writable copy of a String, because every wldap32 entry
      // point takes PWSTR rather than PCWSTR. Casting away const on a CStdStringW's
      // internal buffer would be shorter and would also hand the library a pointer
      // into a string that may be shared; this cannot.
      void ToMutable_(const String &value, std::vector<wchar_t> &buffer)
      {
         buffer.assign(value.begin(), value.end());
         buffer.push_back(L'\0');
      }

      // The certificate callback installed only when VerifyCertificate is turned off.
      //
      // wldap32 validates the server certificate against the machine store by
      // default, and the documented way to stop it doing so is to register a callback
      // that says yes. There is no way to express "validate, but tolerate this one
      // known problem", so this is all or nothing - which is why the setting that
      // installs it is off by default and why enabling it is logged.
      //
      // __cdecl is required by the VERIFYSERVERCERT typedef and is not the project
      // default.
      BOOLEAN __cdecl AcceptAnyServerCertificate_(PLDAP connection, PCCERT_CONTEXT *serverCertificate)
      {
         // Both parameters are referenced so that /W3 /WX does not fail the build on
         // an unreferenced formal parameter, and the check is not merely decorative:
         // a callback invoked with no certificate at all is not something to answer
         // "trusted" to.
         if (connection == nullptr || serverCertificate == nullptr || *serverCertificate == nullptr)
            return FALSE;

         // The certificate context belongs to the LDAP library; freeing it here would
         // be a double free.
         return TRUE;
      }
   }

#endif

   namespace
   {
      // Neither library has a single "the server did not answer" code, so this is
      // the set that all mean the same thing to an administrator. Shared by both
      // arms, and it can be because the numbers are: the client-side codes on this
      // list are the winldap ones on both platforms, by the translation at the top
      // of the file.
      bool IsTransportFailure_(unsigned long ldapError)
      {
         switch (ldapError)
         {
            case LDAP_SERVER_DOWN:
            case LDAP_CONNECT_ERROR:
            case LDAP_TIMEOUT:
            case LDAP_UNAVAILABLE:
            case LDAP_BUSY:
            case LDAP_LOCAL_ERROR:
               return true;
            default:
               return false;
         }
      }
   }

   LdapClient::LdapClient()
   {
      last_error_ = LDAP_SUCCESS;
   }

   LdapClient::~LdapClient()
   {
      // The session is closed here and not only in Disconnect, so that a throw or an
      // early return anywhere above cannot leave a connection to the directory open.
      Disconnect();
   }

   void
   LdapClient::Disconnect()
   {
      if (session_ == nullptr)
         return;

#ifdef HM_PLATFORM_POSIX

      // ldap_unbind_ext_s sends the unbind and frees the session, so the handle must
      // not be touched afterwards - the same call as the wldap32 ldap_unbind_s on the
      // other side of this #ifdef, under the name OpenLDAP has not deprecated.
      ldap_unbind_ext_s(session_, nullptr, nullptr);

      session_ = nullptr;
      transport_protected_ = false;

#else
      // ldap_unbind_s sends the unbind and frees the session, so the handle must not
      // be touched afterwards.
      ldap_unbind_s(session_);

      session_ = nullptr;
      transport_protected_ = false;
#endif
   }

   void
   LdapClient::Abandon_()
   {
      if (session_ == nullptr)
         return;
#ifdef HM_PLATFORM_POSIX

      // ldap_unbind_ext_s is still the right call - it is what frees the session - but
      // the name is misleading here: what matters is that the socket is closed and the
      // handle dropped, because the state of this connection is no longer known.
      ldap_unbind_ext_s(session_, nullptr, nullptr);

      session_ = nullptr;
      transport_protected_ = false;

#else

      // ldap_unbind_s is still the right call - it is what frees the session - but the
      // name is misleading here: what matters is that the socket is closed and the
      // handle dropped, because the state of this connection is no longer known.
      ldap_unbind_s(session_);

      session_ = nullptr;
      transport_protected_ = false;
#endif
   }

   bool
   LdapClient::IsCredentialRejection(unsigned long ldapError)
   {
      switch (ldapError)
      {
         case LDAP_INVALID_CREDENTIALS:
            // The one that actually means "wrong password". Active Directory also
            // reports a disabled, expired or locked-out account this way and puts the
            // difference in the diagnosticMessage - see
            // DescribeActiveDirectorySubStatus.
            return true;

         case LDAP_NO_SUCH_OBJECT:
            // Meaningful as a rejection only on the user bind, where the DN was read
            // out of the directory moments earlier and its disappearance means the
            // account is gone. On a SEARCH the same code means the search base does
            // not exist, which is a configuration fault - and that is why
            // LdapDirectoryAuthenticator treats any non-success from FindUserDn as an
            // infrastructure failure rather than consulting this classification. If
            // that ever changes, a mistyped SearchBase starts telling every user their
            // password is wrong.
            return true;

         default:
            // Everything else, and this is the whole point of the function.
            // LDAP_STRONG_AUTH_REQUIRED (8), LDAP_INAPPROPRIATE_AUTH (48),
            // LDAP_INSUFFICIENT_ACCESS (50), LDAP_UNWILLING_TO_PERFORM (53) and every
            // transport failure are things the administrator has to fix. Reporting
            // them as "wrong password" is what makes a broken directory look like a
            // user error, which is the defect this work exists to remove.
            return false;
      }
   }

   LdapOperationOutcome
   LdapClient::Record_(unsigned long ldapError)
   {
      last_error_ = ldapError;

      if (ldapError == LDAP_SUCCESS)
         return LdapOperationOutcome::OutcomeSuccess;

      return IsCredentialRejection(ldapError)
         ? LdapOperationOutcome::OutcomeRejected
         : LdapOperationOutcome::OutcomeUnavailable;
   }

   String
   LdapClient::DescribeLastError() const
   {
      switch (last_error_)
      {
#ifdef HM_PLATFORM_POSIX
         case LDAP_AUTH_UNKNOWN:
            // What BindNegotiate records on this platform. Named here, and not left
            // to ldap_err2string's "Unknown authentication method", because this text
            // reaches the administrator inside the authenticator's HM5920 report every
            // minute the configuration stays as it is, and it is the one line there
            // that can say what to change.
            return _T("BindMethod=1 (Negotiate) is Windows SSPI and does not exist on this platform. ")
                   _T("Use BindMethod=0 (simple bind) with Security=2 (LDAPS) or Security=1 (StartTLS)");
#endif
         case LDAP_SUCCESS:
            return _T("no error");

         case LDAP_STRONG_AUTH_REQUIRED:
            // Spelled out because ldap_err2string says "Strong authentication
            // required", which is accurate and tells an administrator nothing about
            // what to change. This is the Windows Server 2019-and-later default and
            // the single most likely first failure of a new LDAP configuration.
            return _T("the directory refuses simple binds on an unprotected connection ")
                   _T("(strongAuthRequired). Use Security=2 (LDAPS) or Security=1 (StartTLS), ")
#ifdef HM_PLATFORM_POSIX
                   // No third option here: Negotiate is Windows SSPI, and naming it
                   // would send an administrator to a setting that BindNegotiate
                   // refuses on this platform.
                   _T("which protect the connection before the password is sent");
#else
                   _T("or BindMethod=1 (Negotiate), which satisfies the requirement without TLS");
#endif

         case LDAP_SERVER_DOWN:
         case LDAP_CONNECT_ERROR:
            return _T("the directory server could not be contacted");

         case LDAP_TIMEOUT:
            return _T("the directory server did not answer within the configured timeout");

         case LDAP_FILTER_ERROR:
            return _T("the configured UserSearchFilter is not a valid LDAP filter");

         // LDAP_INSUFFICIENT_RIGHTS, not LDAP_INSUFFICIENT_ACCESS: winldap.h uses the
         // former, and the latter is the OpenLDAP spelling of the same result code (50).
         case LDAP_INSUFFICIENT_RIGHTS:
            return _T("the service credential is not permitted to read the directory");

         case LDAP_INAPPROPRIATE_AUTH:
            return _T("the directory refused the configured bind method for this entry");

         case LDAP_UNWILLING_TO_PERFORM:
            return _T("the directory refused to perform the operation");

         case LDAP_INVALID_CREDENTIALS:
            return _T("the credentials were rejected");

         case LDAP_NO_SUCH_OBJECT:
            return _T("the entry does not exist (check SearchBase, or UserDnTemplate)");

         default:
            break;
      }

#ifdef HM_PLATFORM_POSIX

      // ldap_err2string wants the library's own numbering, so a client-side code goes
      // back through the translation. It returns a pointer into static storage,
      // which must not be freed.
      const char *text = ldap_err2string(ToNative_(last_error_));

      if (text == nullptr)
         return _T("an unrecognised LDAP error");

      return FromUtf8_(text);

#else
      // ldap_err2stringW returns a pointer into library-owned storage; it must not be
      // freed.
      PWSTR text = ldap_err2stringW(last_error_);

      if (text == nullptr)
         return _T("an unrecognised LDAP error");

      return String(text);
#endif
   }

   String
   LdapClient::DescribeActiveDirectorySubStatus(const String &diagnostic)
   {
      // "80090308: LdapErr: DSID-0C0903A9, comment: AcceptSecurityContext error,
      //  data 52e, v4563"
      //
      // The four-character hexadecimal value after "data " is the only place the real
      // reason appears. Without it every one of these is LDAP_INVALID_CREDENTIALS and
      // a locked-out account is indistinguishable from a typo - which is precisely the
      // failure the LogonUser path has today.
      int position = diagnostic.Find(_T("data "));

      if (position < 0)
         return _T("");

      String code = diagnostic.Mid(position + 5);

      int end = code.Find(_T(","));
      if (end >= 0)
         code = code.Mid(0, end);

      code.Trim();
      code.MakeLower();

      if (code == _T("525"))
         return _T("the user does not exist in the directory");
      if (code == _T("52e"))
         return _T("the password is not correct");
      if (code == _T("530"))
         return _T("logon is not permitted at this time of day");
      if (code == _T("531"))
         return _T("logon from this computer is not permitted");
      if (code == _T("532"))
         return _T("the password has expired");
      if (code == _T("533"))
         return _T("the account is disabled");
      if (code == _T("568"))
         return _T("too many context IDs (the directory is out of resources)");
      if (code == _T("701"))
         return _T("the account has expired");
      if (code == _T("773"))
         return _T("the password must be changed before the next logon");
      if (code == _T("775"))
         return _T("the account is locked out");

      return _T("");
   }

   String
   LdapClient::EscapeFilterValue(const String &value)
   {
      // RFC 4515 section 3. The five that MUST be escaped are ( ) * \ and NUL.
      //
      // This is a security boundary, not tidiness. The username reaching here came
      // from a logon prompt, and the filter it is substituted into has boolean
      // structure: an unescaped ")(|(objectClass=*" turns a filter that matches one
      // account into one that matches every object in the directory, and the code
      // above then binds as whichever entry came back first. That is an
      // authentication bypass, and it is reachable by anyone who can reach the SMTP,
      // POP3 or IMAP port.
      String escaped;

      for (int i = 0; i < value.GetLength(); i++)
      {
         wchar_t character = value.GetAt(i);

         switch (character)
         {
            case L'\\': escaped += _T("\\5c"); break;
            case L'(':  escaped += _T("\\28"); break;
            case L')':  escaped += _T("\\29"); break;
            case L'*':  escaped += _T("\\2a"); break;
            case L'\0': escaped += _T("\\00"); break;
            default:
               escaped += character;
               break;
         }
      }

      return escaped;
   }

   String
   LdapClient::EscapeDistinguishedNameValue(const String &value)
   {
      // RFC 4514 section 2.4. A DN template is the other place a username is
      // interpolated, and the injection there is different in shape but not in
      // consequence: an unescaped comma adds a relative distinguished name and moves
      // the bind to a different entry.
      String escaped;

      for (int i = 0; i < value.GetLength(); i++)
      {
         wchar_t character = value.GetAt(i);

         switch (character)
         {
            case L'\\':
            case L',':
            case L'+':
            case L'"':
            case L'<':
            case L'>':
            case L';':
            case L'=':
               escaped += L'\\';
               escaped += character;
               break;

            // Space and '#' are deliberately NOT escaped, even though RFC 4514 makes
            // them special in the leading and trailing position. UserDnTemplate is
            // used for a UPN bind ("%u@progressiverobot.local") at least as often as
            // for a literal DN, and escaping a space there produces "john\ smith@..."
            // which no directory will accept - so escaping them would break the more
            // common configuration in order to normalise a value that carries no
            // injection risk. The characters that DO change which entry is bound to
            // are all handled above.

            default:
               escaped += character;
               break;
         }
      }

      return escaped;
   }

   String
   LdapClient::ExpandTemplate(const String &templateText, const String &username,
      const String &domain, const String &address, TemplateEscaping escapeFor)
   {
      String result = templateText;

      String safeUsername;
      String safeDomain;
      String safeAddress;

      if (escapeFor == TemplateEscaping::EscapeForFilter)
      {
         safeUsername = EscapeFilterValue(username);
         safeDomain = EscapeFilterValue(domain);
         safeAddress = EscapeFilterValue(address);
      }
      else
      {
         safeUsername = EscapeDistinguishedNameValue(username);
         safeDomain = EscapeDistinguishedNameValue(domain);
         safeAddress = EscapeDistinguishedNameValue(address);
      }

      // Replaced in one pass each, and the escaped values are substituted rather than
      // the raw ones, so a substituted value that itself contains "%u" cannot be
      // expanded a second time.
      result.Replace(_T("%u"), safeUsername.c_str());
      result.Replace(_T("%d"), safeDomain.c_str());
      result.Replace(_T("%m"), safeAddress.c_str());

      return result;
   }

   LdapOperationOutcome
   LdapClient::Connect(const LdapConfiguration &configuration)
   {
#ifdef HM_PLATFORM_POSIX

      Disconnect();

      last_diagnostic_.Empty();
      timeout_seconds_ = configuration.timeout_seconds;
      unprotected_password_allowed_ = configuration.allow_unprotected_password;

      const int port = configuration.EffectivePort();
      const bool useLdaps = configuration.security == LdapTransportSecurity::TransportLdaps;

      // ldap_initialize takes a URI where ldap_init took a host and a port, and the
      // scheme is how LDAPS is asked for - there is no LDAP_OPT_SSL here. An IPv6
      // literal has to be bracketed so that its colons are not read as the port
      // separator; a name or an IPv4 address goes in as it is.
      String host = configuration.server;
      host.Trim();

      if (host.Find(_T(":")) >= 0 && host.Find(_T("[")) != 0)
      {
         String bracketed = _T("[");
         bracketed += host;
         bracketed += _T("]");
         host = bracketed;
      }

      AnsiString uri;

      if (!ToUtf8_(Formatter::Format("{0}://{1}:{2}", String(useLdaps ? _T("ldaps") : _T("ldap")), host, port), uri))
         return Record_(LDAP_PARAM_ERROR);

      // ldap_initialize only allocates; nothing touches the network until the
      // ldap_connect below. That ordering is what makes every option between the two
      // reach the library before the first byte is sent.
      LDAP *session = nullptr;
      int status = ldap_initialize(&session, uri.c_str());

      if (status != LDAP_SUCCESS || session == nullptr)
         return Record_(status != LDAP_SUCCESS ? TranslateNative_(status) : LDAP_LOCAL_ERROR);

      session_ = session;

      // LDAPv3, and not optional here either: OpenLDAP's compiled-in default is also
      // version 2 unless ldap.conf says otherwise, and StartTLS does not exist in
      // version 2.
      int version = LDAP_VERSION3;
      ldap_set_option(session_, LDAP_OPT_PROTOCOL_VERSION, &version);

      // Referral chasing off, for the reason the Windows arm gives: with it on, a
      // bind can be replayed - with the user's password - against a host chosen by
      // whatever answered on the configured address.
      ldap_set_option(session_, LDAP_OPT_REFERRALS, LDAP_OPT_OFF);

      // The server-side bound on a search, as on Windows.
      int timeLimit = timeout_seconds_;
      ldap_set_option(session_, LDAP_OPT_TIMELIMIT, &timeLimit);

      // Two client-side bounds, because OpenLDAP keeps two. NETWORK_TIMEOUT is how
      // long connect() may take, which is what ldap_connect below is bounded by.
      // TIMEOUT is how long a synchronous call waits for its reply when given no
      // timeout of its own, which is ldap_start_tls_s here; the searches and the
      // binds pass theirs explicitly. Without the first, a server that drops packets
      // holds a connection thread for the kernel's own connect timeout, which is
      // minutes.
      struct timeval timeout = SecondsAsTimeval_(timeout_seconds_);
      ldap_set_option(session_, LDAP_OPT_NETWORK_TIMEOUT, &timeout);
      ldap_set_option(session_, LDAP_OPT_TIMEOUT, &timeout);

      if (configuration.security != LdapTransportSecurity::TransportPlain)
      {
         // Whether the server's certificate has to chain to a trusted root. DEMAND is
         // what the Windows arm gets for nothing from the machine store; ALLOW is
         // what its accept-anything callback amounts to - the certificate is still
         // requested, so it is in a packet capture, and it is then not checked. The
         // trust store is the system's own, which is where OpenLDAP looks (TLS_CACERT
         // in ldap.conf; /etc/ssl/certs on Debian and Ubuntu). There is no setting
         // for a private one, and that is deliberate: an issuer the machine does not
         // trust should be fixed at the machine, where every other TLS client on it
         // will meet the same issuer.
         int requireCertificate = configuration.verify_certificate
            ? LDAP_OPT_X_TLS_DEMAND
            : LDAP_OPT_X_TLS_ALLOW;

         ldap_set_option(session_, LDAP_OPT_X_TLS_REQUIRE_CERT, &requireCertificate);

         // The option above is recorded on the handle and takes effect only when a
         // TLS context is built from it. NEWCTX builds one now, for this handle, from
         // this handle's settings. Without it the library uses the process-wide
         // context made from ldap.conf, and the setting above changes nothing - the
         // kind of silence that makes VerifyCertificate=0 look broken and
         // VerifyCertificate=1 look as though it had never been on.
         int isServer = 0;
         ldap_set_option(session_, LDAP_OPT_X_TLS_NEWCTX, &isServer);

         if (!configuration.verify_certificate)
         {
            // Said out loud, but once per process rather than per connection, for
            // the reasons the Windows arm gives beside its own copy of this line.
            static std::atomic<bool> alreadyWarned(false);

            if (!alreadyWarned.exchange(true))
            {
               LOG_APPLICATION(Formatter::Format("LDAP - certificate validation is DISABLED for {0}:{1} "
                  "(hMailServer.ini [LDAP] VerifyCertificate=0). The encrypted connection no longer "
                  "authenticates the directory, so an attacker able to intercept it can present any "
                  "certificate and collect the credentials sent over it. Logged once per service start.",
                  configuration.server, port));
            }
         }
      }

      // Connects now rather than leaving it to the first bind, for the reason the
      // Windows arm calls ldap_connect: so that "the server could not be contacted"
      // is Connect's answer and is reported at the connecting stage, and so that for
      // LDAPS the handshake - and with it the certificate check - has happened before
      // Connect says success. Added to OpenLDAP in 2.5; nothing older is supported.
      status = ldap_connect(session_);

      if (status != LDAP_SUCCESS)
      {
         // Read before the handle goes. A refused certificate comes back from the
         // library as LDAP_SERVER_DOWN, and the diagnostic is the only place the
         // word "certificate" appears.
         last_diagnostic_ = ReadDiagnostic_(session_);
         Abandon_();

         return Record_(TranslateNative_(status));
      }

      if (useLdaps)
      {
         // The handshake happened inside ldap_connect, so by here the transport is
         // encrypted and - unless the administrator turned validation off - the server
         // has been authenticated.
         transport_protected_ = true;
      }
      else if (configuration.security == LdapTransportSecurity::TransportStartTls)
      {
         status = ldap_start_tls_s(session_, nullptr, nullptr);

         if (status != LDAP_SUCCESS)
         {
            // Dropped rather than continued in the clear, as on Windows: carrying on
            // would send the password over exactly the unprotected connection the
            // administrator asked to have upgraded, and would do it silently.
            last_diagnostic_ = ReadDiagnostic_(session_);
            Abandon_();

            return Record_(TranslateNative_(status));
         }

         transport_protected_ = true;
      }

      return Record_(LDAP_SUCCESS);

#else
      Disconnect();

      last_diagnostic_.Empty();
      timeout_seconds_ = configuration.timeout_seconds;
      unprotected_password_allowed_ = configuration.allow_unprotected_password;

      std::vector<wchar_t> host;
      ToMutable_(configuration.server, host);

      const ULONG port = (ULONG) configuration.EffectivePort();
      const bool useLdaps = configuration.security == LdapTransportSecurity::TransportLdaps;

      // ldap_sslinit / ldap_init only allocate; nothing touches the network until
      // ldap_connect, which is the call that takes a timeout. That ordering is what
      // makes every option below reach the library before the first byte is sent.
      session_ = useLdaps
         ? ldap_sslinitW(&host[0], port, 1)
         : ldap_initW(&host[0], port);

      if (session_ == nullptr)
         return Record_(LdapGetLastError());

      // LDAPv3, and this is not optional. wldap32 defaults to LDAPv2, in which
      // StartTLS does not exist and which a modern Active Directory will not accept;
      // leaving the default in place produces a protocol error that looks like a
      // network fault.
      ULONG version = LDAP_VERSION3;
      ldap_set_option(session_, LDAP_OPT_PROTOCOL_VERSION, (void *) &version);

      // Referral chasing off, and this one is a security decision rather than a
      // performance one. A referral tells the client to repeat the operation against
      // another server named by the directory; with chasing on, a bind can therefore
      // be replayed - with the user's password - against a host chosen by whatever
      // answered on the configured address. Nothing in an authentication needs it.
      ldap_set_option(session_, LDAP_OPT_REFERRALS, LDAP_OPT_OFF);

      if (useLdaps)
         ldap_set_option(session_, LDAP_OPT_SSL, LDAP_OPT_ON);

      // A server-side bound on the search, in addition to the client-side timeout
      // passed to ldap_search_ext_s. Both are needed: the client one bounds a server
      // that has stopped answering, this one stops a server working on a query
      // nobody is waiting for any more.
      ULONG timeLimit = (ULONG) timeout_seconds_;
      ldap_set_option(session_, LDAP_OPT_TIMELIMIT, (void *) &timeLimit);

      if (!configuration.verify_certificate &&
          configuration.security != LdapTransportSecurity::TransportPlain)
      {
         // Registered before the handshake, because after it there is nothing left to
         // decide.
         VERIFYSERVERCERT *callback = AcceptAnyServerCertificate_;
         ldap_set_option(session_, LDAP_OPT_SERVER_CERTIFICATE, (void *) &callback);

         // Said out loud, but once per process rather than per connection.
         //
         // Not ErrorManager, because it is a deliberate administrator choice and an
         // ERROR entry would break every regression fixture that asserts a clean ERROR
         // log. Not per connection either: this path runs up to twice per logon, so an
         // unguarded line here would be the noisiest thing in the application log on a
         // busy server and would bury whatever the administrator was actually reading.
         static std::atomic<bool> alreadyWarned(false);

         if (!alreadyWarned.exchange(true))
         {
            LOG_APPLICATION(Formatter::Format("LDAP - certificate validation is DISABLED for {0}:{1} "
               "(hMailServer.ini [LDAP] VerifyCertificate=0). The encrypted connection no longer "
               "authenticates the directory, so an attacker able to intercept it can present any "
               "certificate and collect the credentials sent over it. Logged once per service start.",
               configuration.server, (int) port));
         }
      }

      LDAP_TIMEVAL timeout;
      timeout.tv_sec = timeout_seconds_;
      timeout.tv_usec = 0;

      const ULONG connectResult = ldap_connect(session_, &timeout);

      if (connectResult != LDAP_SUCCESS)
      {
         Abandon_();
         return Record_(connectResult);
      }

      if (useLdaps)
      {
         // The handshake happened inside ldap_connect, so by here the transport is
         // encrypted and - unless the administrator turned validation off - the server
         // has been authenticated.
         transport_protected_ = true;
      }
      else if (configuration.security == LdapTransportSecurity::TransportStartTls)
      {
         ULONG serverReturnValue = 0;
         LDAPMessage *startTlsResult = nullptr;

         const ULONG startTls = ldap_start_tls_sW(session_, &serverReturnValue, &startTlsResult, nullptr, nullptr);

         if (startTlsResult != nullptr)
            ldap_msgfree(startTlsResult);

         if (startTls != LDAP_SUCCESS)
         {
            // The connection is dropped rather than continued in the clear. This is
            // the StartTLS downgrade case and it has to be a hard failure: carrying on
            // would send the password over exactly the unprotected connection the
            // administrator asked to have upgraded, and would do it silently.
            Abandon_();

            // serverReturnValue carries the extended result when the server answered
            // and refused, which is more specific than the client-side code.
            return Record_(serverReturnValue != LDAP_SUCCESS ? serverReturnValue : startTls);
         }

         transport_protected_ = true;
      }

      return Record_(LDAP_SUCCESS);
#endif
   }

   LdapOperationOutcome
   LdapClient::AwaitResult_(unsigned long messageId)
   {
#ifdef HM_PLATFORM_POSIX

      struct timeval timeout = SecondsAsTimeval_(timeout_seconds_);

      LDAPMessage *result = nullptr;

      const int resultType = ldap_result(session_, (int) messageId, LDAP_MSG_ALL, &timeout, &result);

      if (resultType == 0)
      {
         // Timed out. The same two steps as on Windows, for the same reason: the
         // server is told to stop, and then the session is dropped, because a bind
         // whose reply never arrived leaves nobody knowing which identity the
         // connection carries.
         ldap_abandon_ext(session_, (int) messageId, nullptr, nullptr);
         Abandon_();

         return Record_(LDAP_TIMEOUT);
      }

      if (resultType < 0)
      {
         // The library's own failure - typically the connection went away underneath
         // the wait. Its code and its diagnostic are on the handle, and both are read
         // before the handle goes.
         int sessionError = LDAP_SUCCESS;
         ldap_get_option(session_, LDAP_OPT_RESULT_CODE, &sessionError);

         last_diagnostic_ = ReadDiagnostic_(session_);
         Abandon_();

         return Record_(sessionError != LDAP_SUCCESS ? TranslateNative_(sessionError) : LDAP_LOCAL_ERROR);
      }

      int returnCode = LDAP_SUCCESS;
      char *diagnostic = nullptr;

      // freeit is 1, so ldap_parse_result releases the result message itself. The
      // diagnostic string is separately owned and is released with ldap_memfree; only
      // that one is asked for, because the matched DN and the referral list add
      // nothing an administrator can act on.
      const int parseResult = ldap_parse_result(session_, result, &returnCode, nullptr,
         &diagnostic, nullptr, nullptr, 1);

      last_diagnostic_.Empty();

      if (diagnostic != nullptr)
      {
         last_diagnostic_ = FromUtf8_(diagnostic);
         ldap_memfree(diagnostic);
      }

      if (parseResult != LDAP_SUCCESS)
         return Record_(TranslateNative_(parseResult));

      return Record_(TranslateNative_(returnCode));

#else
      LDAP_TIMEVAL timeout;
      timeout.tv_sec = timeout_seconds_;
      timeout.tv_usec = 0;

      LDAPMessage *result = nullptr;

      const ULONG resultType = ldap_result(session_, (ULONG) messageId, LDAP_MSG_ALL, &timeout, &result);

      if (resultType == 0)
      {
         // Timed out. The operation is abandoned so the server stops working on it,
         // and then the whole session is dropped: a bind whose reply never arrived
         // leaves the connection in a state where nobody knows which identity it
         // carries, and reusing it is how a search ends up running as someone else.
         ldap_abandon(session_, (ULONG) messageId);
         Abandon_();

         return Record_(LDAP_TIMEOUT);
      }

      if (resultType == (ULONG) -1)
      {
         const ULONG sessionError = LdapGetLastError();
         Abandon_();

         return Record_(sessionError != LDAP_SUCCESS ? sessionError : LDAP_LOCAL_ERROR);
      }

      ULONG returnCode = LDAP_SUCCESS;
      PWSTR diagnostic = nullptr;

      // Freeit is TRUE, so ldap_parse_result releases the result message itself. The
      // diagnostic string is separately owned and has to be released with
      // ldap_memfree; only that one is asked for, because the matched DN and the
      // referral list add nothing an administrator can act on.
      const ULONG parseResult = ldap_parse_resultW(session_, result, &returnCode, nullptr,
         &diagnostic, nullptr, nullptr, TRUE);

      last_diagnostic_.Empty();

      if (diagnostic != nullptr)
      {
         last_diagnostic_ = diagnostic;
         ldap_memfreeW(diagnostic);
      }

      if (parseResult != LDAP_SUCCESS)
         return Record_(parseResult);

      return Record_(returnCode);
#endif
   }

   LdapOperationOutcome
   LdapClient::BindAnonymous()
   {
#ifdef HM_PLATFORM_POSIX

      if (session_ == nullptr)
         return Record_(LDAP_LOCAL_ERROR);

      // An anonymous bind is a simple bind with an empty name and an EMPTY
      // credential - an octet string of length zero, which is what RFC 4513 section
      // 5.1.1 says an anonymous simple bind carries. It must be an empty berval and
      // not a null pointer: ldap_sasl_bind encodes a null credential as no
      // authentication choice at all, the request is then malformed, and slapd
      // answers it with protocolError (2) and closes the connection - which was
      // measured, and looked like a broken directory rather than a broken bind.
      //
      // Only ever used to READ the directory when no service credential is
      // configured; never to authenticate anybody - the distinction BindSimple's
      // empty-password guard exists to keep.
      char nothing[] = "";

      struct berval credentials;
      credentials.bv_val = nothing;
      credentials.bv_len = 0;

      int messageId = -1;

      const int status = ldap_sasl_bind(session_, "", LDAP_SASL_SIMPLE, &credentials, nullptr, nullptr, &messageId);

      if (status != LDAP_SUCCESS)
         return Record_(TranslateNative_(status));

      return AwaitResult_((unsigned long) messageId);

#else
      if (session_ == nullptr)
         return Record_(LDAP_LOCAL_ERROR);

      // An anonymous bind is a bind with no name and no credentials. It is only ever
      // used to READ the directory when no service credential is configured; it is
      // never used to authenticate anybody, which is the distinction BindSimple's
      // empty-password guard exists to keep.
      const ULONG messageId = ldap_simple_bindW(session_, nullptr, nullptr);

      if (messageId == (ULONG) -1)
         return Record_(LdapGetLastError());

      return AwaitResult_(messageId);
#endif
   }

   LdapOperationOutcome
   LdapClient::BindSimple(const String &dn, const String &password)
   {
#ifdef HM_PLATFORM_POSIX

      if (session_ == nullptr)
         return Record_(LDAP_LOCAL_ERROR);

      if (dn.IsEmpty())
         return Record_(LDAP_INVALID_CREDENTIALS);

      // The unauthenticated-bind guard, kept here as on Windows and for the reason
      // given there: a name with an empty password is RFC 4513's unauthenticated
      // bind, which many directories answer with success, so an empty password is a
      // bypass and not an argument error.
      if (password.IsEmpty())
         return Record_(LDAP_INVALID_CREDENTIALS);

      if (!transport_protected_ && !unprotected_password_allowed_)
      {
         // Enforced here as well as in the authenticator. See the comment on
         // unprotected_password_allowed_ for why the duplication is deliberate.
         Abandon_();
         return Record_(LDAP_STRONG_AUTH_REQUIRED);
      }

      AnsiString dnUtf8;
      AnsiString passwordUtf8;

      if (!ToUtf8_(dn, dnUtf8) || !ToUtf8_(password, passwordUtf8))
      {
         // A credential that cannot be written in the directory's own encoding is
         // not one the directory can hold, so this is a rejection and not a local
         // fault. It must not be a fault: a fault is reported as an outage, and this
         // value came from a logon prompt.
         if (!passwordUtf8.empty())
            ::explicit_bzero(&passwordUtf8[0], passwordUtf8.size());

         return Record_(LDAP_INVALID_CREDENTIALS);
      }

      struct berval credentials;
      credentials.bv_val = &passwordUtf8[0];
      credentials.bv_len = (ber_len_t) passwordUtf8.size();

      int messageId = -1;

      const int status = ldap_sasl_bind(session_, dnUtf8.c_str(), LDAP_SASL_SIMPLE, &credentials,
         nullptr, nullptr, &messageId);

      // Overwritten before the string goes out of scope, for the reason the Windows
      // arm gives beside its SecureZeroMemory: one cleartext password fewer in a heap
      // block about to be recycled. explicit_bzero because a memset over storage
      // nothing reads afterwards is one the compiler is entitled to delete.
      ::explicit_bzero(&passwordUtf8[0], passwordUtf8.size());

      if (status != LDAP_SUCCESS)
         return Record_(TranslateNative_(status));

      return AwaitResult_((unsigned long) messageId);

#else
      if (session_ == nullptr)
         return Record_(LDAP_LOCAL_ERROR);

      if (dn.IsEmpty())
         return Record_(LDAP_INVALID_CREDENTIALS);

      // An LDAP simple bind with a name and an EMPTY password is not a failed
      // authentication - it is an "unauthenticated bind" (RFC 4513 section 5.1.2),
      // and a great many directories answer it with success while treating the
      // session as anonymous. A caller that took that success as proof of identity
      // would authenticate anyone who sent a username and no password at all.
      //
      // PasswordValidator already refuses an empty password before reaching here.
      // This is checked again because the consequence of the outer check ever being
      // moved, reordered or bypassed is a complete authentication bypass, and one
      // comparison is not a price worth negotiating over.
      if (password.IsEmpty())
         return Record_(LDAP_INVALID_CREDENTIALS);

      if (!transport_protected_ && !unprotected_password_allowed_)
      {
         // Enforced here as well as in the authenticator. See the comment on
         // unprotected_password_allowed_ for why the duplication is deliberate.
         Abandon_();
         return Record_(LDAP_STRONG_AUTH_REQUIRED);
      }

      std::vector<wchar_t> dnBuffer;
      std::vector<wchar_t> passwordBuffer;

      ToMutable_(dn, dnBuffer);
      ToMutable_(password, passwordBuffer);

      const ULONG messageId = ldap_simple_bindW(session_, &dnBuffer[0], &passwordBuffer[0]);

      // Overwritten before the buffer goes out of scope. Not a guarantee - the string
      // was already copied by wldap32 and by whoever handed it to us - but a
      // cleartext password left in a heap block that is about to be recycled is worth
      // one memset, and the buffer is a local vector so nothing can be relying on it.
      if (!passwordBuffer.empty())
         SecureZeroMemory(&passwordBuffer[0], passwordBuffer.size() * sizeof(wchar_t));

      if (messageId == (ULONG) -1)
         return Record_(LdapGetLastError());

      return AwaitResult_(messageId);
#endif
   }

   LdapOperationOutcome
   LdapClient::BindNegotiate(const String &username, const String &domain, const String &password)
   {
#ifdef HM_PLATFORM_POSIX

      // Not available, and said once. A Negotiate bind is an SSPI exchange - Kerberos,
      // then NTLM - that wldap32 drives from inside ldap_bind_s, and nothing on this
      // platform stands in for it: OpenLDAP's SASL binds go through Cyrus SASL and a
      // GSSAPI mechanism, which needs a keytab and a realm configured on the host and
      // is a different feature with different settings, not this one under another
      // name.
      //
      // Recorded as LDAP_AUTH_UNKNOWN, which IsCredentialRejection classifies as
      // unavailable, and that classification is the point. LdapDirectoryAuthenticator
      // maps every non-success that is not a rejection to ResultUnavailable, which
      // PasswordValidator refuses WITHOUT falling through to LogonUser and without
      // telling the user the password was wrong; and because it is not a rejection,
      // a configuration mistake does not feed the failed-logon counters. The
      // authenticator reports it as HM5920, throttled, carrying DescribeLastError's
      // text for this code, which names the setting to change; this report says it
      // once, in full, for whoever reads the log from the top.
      //
      // The password is not touched. It has not been sent, and it will not be.
      (void) username;
      (void) domain;
      (void) password;

      static std::once_flag reported;

      std::call_once(reported, []()
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 6420, "LdapClient::BindNegotiate",
            _T("hMailServer.ini [LDAP] BindMethod=1 (Negotiate) is configured, but a Negotiate bind is Windows ")
            _T("SSPI and this platform has none. No password has been sent, and every logon that reaches this ")
            _T("bind is refused as a directory failure rather than as a wrong password. Set BindMethod=0 (simple ")
            _T("bind) with Security=2 (LDAPS) or Security=1 (StartTLS), so that the password is proved over a ")
            _T("protected connection instead. Reported once per run."));
      });

      last_diagnostic_.Empty();

      return Record_(LDAP_AUTH_UNKNOWN);

#else
      if (session_ == nullptr)
         return Record_(LDAP_LOCAL_ERROR);

      if (username.IsEmpty() || password.IsEmpty())
      {
         // Same reasoning as BindSimple's empty-password guard, with an extra edge:
         // SSPI with an empty password can negotiate a NULL session rather than
         // failing, so an empty password here is not merely useless but dangerous.
         return Record_(LDAP_INVALID_CREDENTIALS);
      }

      std::vector<wchar_t> usernameBuffer;
      std::vector<wchar_t> domainBuffer;
      std::vector<wchar_t> passwordBuffer;

      ToMutable_(username, usernameBuffer);
      ToMutable_(domain, domainBuffer);
      ToMutable_(password, passwordBuffer);

      SEC_WINNT_AUTH_IDENTITY_W identity;
      memset(&identity, 0, sizeof(identity));

      identity.User = (unsigned short *) &usernameBuffer[0];
      identity.UserLength = (unsigned long) username.GetLength();
      identity.Domain = (unsigned short *) &domainBuffer[0];
      identity.DomainLength = (unsigned long) domain.GetLength();
      identity.Password = (unsigned short *) &passwordBuffer[0];
      identity.PasswordLength = (unsigned long) password.GetLength();
      identity.Flags = SEC_WINNT_AUTH_IDENTITY_UNICODE;

      // Synchronous, and this is the one operation that is not bounded by
      // timeout_seconds_ - a SASL bind is a multi-round-trip exchange that wldap32
      // drives internally, and there is no asynchronous entry point that will run it.
      // What still bounds it: the TCP connection was established by ldap_connect
      // under the timeout, so an unreachable server has already failed by here, and
      // the exchange is over that established socket. A server that accepts a
      // connection and then stalls mid-SASL is bounded only by the TCP stack.
      //
      // That is a real, stated limitation and it is why BindMethod defaults to 0
      // (simple over TLS), whose reply IS bounded. BindMethod=1 exists because it is
      // the only thing that authenticates against a domain controller with no
      // certificate.
      const ULONG bindResult = ldap_bind_sW(session_, nullptr, (PWSTR) &identity, LDAP_AUTH_NEGOTIATE);

      SecureZeroMemory(&passwordBuffer[0], passwordBuffer.size() * sizeof(wchar_t));

      // No diagnostic is available on this path, and that is a genuine loss rather
      // than an oversight: ldap_bind_s does not hand back the result message, so the
      // server's diagnosticMessage - which is where Active Directory puts the
      // difference between a wrong password and a locked-out account - cannot be read.
      // Cleared rather than left holding a previous operation's text, which would be
      // reported against this one.
      last_diagnostic_.Empty();

      return Record_(bindResult);
#endif
   }

   LdapOperationOutcome
   LdapClient::BindService(const LdapConfiguration &configuration)
   {
      if (configuration.service_username.IsEmpty())
      {
         // No service credential configured, so read the directory anonymously.
         // Active Directory refuses anonymous searches by default, so this will
         // usually fail - and it fails with a reported reason naming ServiceUsername,
         // which is a better outcome than silently having no way to read anything.
         return BindAnonymous();
      }

      if (configuration.bind_method == LdapBindMethod::BindNegotiate)
      {
         String username = configuration.service_username;
         String domain = configuration.service_domain;

         // DOMAIN\user is split here when ServiceDomain is empty, because that is the
         // form administrators are told to use: the Control Panel offers no
         // ServiceDomain editor and its note says to put the domain in the user name.
         // SEC_WINNT_AUTH_IDENTITY_W does not do this splitting - User is documented
         // as a bare name with the domain in Domain - so a backslash-qualified name
         // with an empty Domain is rejected by NTLM, and the administrator is told
         // their credential was refused while following the instructions on screen.
         //
         // A UPN (user@domain) is deliberately NOT split: SSPI resolves those itself
         // with an empty domain, and cutting one up would break it.
         if (domain.IsEmpty())
         {
            const int separator = username.Find(_T("\\"));

            if (separator > 0)
            {
               domain = username.Mid(0, separator);
               username = username.Mid(separator + 1);
            }
         }

         return BindNegotiate(username, domain, configuration.service_password);
      }

      return BindSimple(configuration.service_username, configuration.service_password);
   }

   LdapOperationOutcome
   LdapClient::SearchEntries(const String &searchBase, const String &filter,
      const std::vector<String> &attributeNames, int maxEntries,
      std::vector<LdapDirectoryEntry> &entries, bool &truncated)
   {
#ifdef HM_PLATFORM_POSIX

      entries.clear();
      truncated = false;

      if (session_ == nullptr)
         return Record_(LDAP_LOCAL_ERROR);

      if (maxEntries <= 0)
         return Record_(LDAP_SUCCESS);

      std::vector<char> baseBuffer;
      std::vector<char> filterBuffer;

      // A base or a filter that cannot be encoded came from configuration, so it is
      // a parameter fault and reported as one - not a rejection, which would tell
      // every user their password was wrong over one byte in the ini file.
      if (!ToMutableUtf8_(searchBase, baseBuffer) || !ToMutableUtf8_(filter, filterBuffer))
         return Record_(LDAP_PARAM_ERROR);

      // The API wants an array of mutable pointers ending in a null. The buffers
      // outlive the call and the pointers are taken only after the vector has stopped
      // growing - the same two rules as the Windows arm, for the same reason. The
      // pointer at index i names attributeNames[i], and the per-entry loop below
      // relies on that.
      std::vector<std::vector<char> > attributeBuffers;
      std::vector<char *> attributePointers;

      for (const String &name : attributeNames)
      {
         attributeBuffers.push_back(std::vector<char>());

         if (!ToMutableUtf8_(name, attributeBuffers.back()))
            return Record_(LDAP_PARAM_ERROR);
      }

      for (auto &buffer : attributeBuffers)
         attributePointers.push_back(&buffer[0]);

      attributePointers.push_back(nullptr);

      // 1000 is Active Directory's own default MaxPageSize; slapd sets no ceiling
      // below it by default. See the Windows arm for why matching it matters.
      const ber_int_t pageSize = 1000;

      // The paging cookie, owned here. ldap_create_page_control copies it into the
      // control, ldap_parse_pageresponse_control allocates the next one, and
      // ReleaseCookie_ frees it between the two; an empty berval means the first
      // page.
      struct berval cookie;
      cookie.bv_val = nullptr;
      cookie.bv_len = 0;

      // The two bounds on the loop - a hard ceiling on pages and a no-progress
      // detector - and why both are needed are explained beside the Windows copy of
      // this loop. They are the same numbers here because the failure they guard
      // against is a property of the protocol, not of the library.
      const int maxPages = 10000;
      int pagesRead = 0;
      int pagesWithoutProgress = 0;

      for (;;)
      {
         if (++pagesRead > maxPages)
         {
            truncated = true;
            break;
         }

         LDAPControl *pageControl = nullptr;

         // Not critical, as on Windows: a directory without paged results answers
         // the search anyway, and one page is the right answer for a small one.
         int status = ldap_create_page_control(session_, pageSize,
            cookie.bv_val != nullptr ? &cookie : nullptr, 0, &pageControl);

         if (status != LDAP_SUCCESS)
         {
            ReleaseCookie_(cookie);
            return Record_(TranslateNative_(status));
         }

         LDAPControl *serverControls[2] = { pageControl, nullptr };

         struct timeval timeout = SecondsAsTimeval_(timeout_seconds_);

         LDAPMessage *searchResult = nullptr;

         status = ldap_search_ext_s(session_, &baseBuffer[0], LDAP_SCOPE_SUBTREE,
            &filterBuffer[0], &attributePointers[0], 0, serverControls, nullptr,
            &timeout, 0, &searchResult);

         ldap_control_free(pageControl);

         // A size-limit hit is truncation, not failure, and has to be reported as
         // such; the Windows arm records how that was learned, against slapd's own
         // default sizelimit of 500.
         if (status == LDAP_SIZELIMIT_EXCEEDED)
            truncated = true;

         if (status != LDAP_SUCCESS && status != LDAP_SIZELIMIT_EXCEEDED)
         {
            if (searchResult != nullptr)
               ldap_msgfree(searchResult);

            ReleaseCookie_(cookie);

            const unsigned long translated = TranslateNative_(status);

            if (IsTransportFailure_(translated))
               Abandon_();

            return Record_(translated);
         }

         if (searchResult == nullptr)
         {
            ReleaseCookie_(cookie);
            return Record_(LDAP_SUCCESS);
         }

         const size_t sizeBeforeThisPage = entries.size();

         for (LDAPMessage *entry = ldap_first_entry(session_, searchResult);
              entry != nullptr;
              entry = ldap_next_entry(session_, entry))
         {
            if ((int) entries.size() >= maxEntries)
            {
               truncated = true;
               break;
            }

            LdapDirectoryEntry record;

            char *entryDn = ldap_get_dn(session_, entry);

            if (entryDn != nullptr)
            {
               record.dn = FromUtf8_(entryDn);
               ldap_memfree(entryDn);
            }

            // Fetched by name rather than walked with ldap_first_attribute, for the
            // reason given on the Windows side. ldap_get_values_len rather than
            // ldap_get_values because the latter is deprecated and assumes
            // NUL-terminated text, while a value is a berval - length and bytes -
            // which is also what brings a value holding a NUL back whole rather than
            // cut short.
            for (size_t index = 0; index < attributeNames.size(); index++)
            {
               struct berval **values = ldap_get_values_len(session_, entry, attributePointers[index]);

               if (values == nullptr)
                  continue;

               std::vector<String> collected;

               for (int v = 0; values[v] != nullptr; v++)
                  collected.push_back(FromUtf8_(values[v]->bv_val, values[v]->bv_len));

               ldap_value_free_len(values);

               // Absent rather than present-and-empty, as on Windows, so that
               // "never sent" stays distinguishable from "sent as empty".
               if (!collected.empty())
                  record.attributes[attributeNames[index]] = collected;
            }

            entries.push_back(record);
         }

         if (entries.size() == sizeBeforeThisPage)
            pagesWithoutProgress++;
         else
            pagesWithoutProgress = 0;

         // This page's cookie was copied into the control that fetched it and is
         // finished with.
         ReleaseCookie_(cookie);

         if (truncated || pagesWithoutProgress >= 16)
         {
            if (pagesWithoutProgress >= 16)
               truncated = true;

            ldap_msgfree(searchResult);
            break;
         }

         LDAPControl **returnedControls = nullptr;
         int resultCode = LDAP_SUCCESS;

         status = ldap_parse_result(session_, searchResult, &resultCode, nullptr, nullptr,
            nullptr, &returnedControls, 0);

         if (status == LDAP_SUCCESS && returnedControls != nullptr)
         {
            // Found by its OID rather than assumed to be the only control there: a
            // directory is free to attach others. A directory that ignored the
            // request sends none, the cookie stays empty, and the loop ends after one
            // page - the right behaviour for a server that answered the whole search
            // at once.
            LDAPControl *response = ldap_control_find(LDAP_CONTROL_PAGEDRESULTS, returnedControls, nullptr);

            if (response != nullptr)
            {
               ber_int_t estimatedTotal = 0;

               if (ldap_parse_pageresponse_control(session_, response, &estimatedTotal, &cookie) != LDAP_SUCCESS)
                  ReleaseCookie_(cookie);
            }
         }

         if (returnedControls != nullptr)
            ldap_controls_free(returnedControls);

         ldap_msgfree(searchResult);

         if (cookie.bv_val == nullptr || cookie.bv_len == 0)
         {
            ReleaseCookie_(cookie);
            break;
         }
      }

      ReleaseCookie_(cookie);

      return Record_(LDAP_SUCCESS);

#else
      entries.clear();
      truncated = false;

      if (session_ == nullptr)
         return Record_(LDAP_LOCAL_ERROR);

      if (maxEntries <= 0)
         return Record_(LDAP_SUCCESS);

      std::vector<wchar_t> baseBuffer;
      std::vector<wchar_t> filterBuffer;

      ToMutable_(searchBase, baseBuffer);
      ToMutable_(filter, filterBuffer);

      // The API wants an array of mutable pointers ending in a null. The buffers have
      // to outlive the call, so they are held here rather than built inline - and the
      // pointers are taken only after the vector has stopped growing, or every one of
      // them would point into a buffer that has since moved.
      std::vector<std::vector<wchar_t> > attributeBuffers;
      std::vector<PWCHAR> attributePointers;

      for (const String &name : attributeNames)
      {
         attributeBuffers.push_back(std::vector<wchar_t>());
         ToMutable_(name, attributeBuffers.back());
      }

      for (auto &buffer : attributeBuffers)
         attributePointers.push_back(&buffer[0]);

      attributePointers.push_back(nullptr);

      // 1000 is Active Directory's own default MaxPageSize. Asking for more does not
      // get more - the server reduces it silently - so this matches rather than fights.
      const ULONG pageSize = 1000;

      berval *cookie = nullptr;

      // Two bounds on the loop, because a paged search terminates only when the server
      // decides to stop handing back a cookie, and this code cannot make it.
      //
      // RFC 2696 permits a page carrying NO entries and a non-empty cookie. Such a
      // page advances neither the entry count nor the truncation check, so a server -
      // or an LDAP-aware middlebox on the configured address - that returns a constant
      // cookie with an empty result set would spin here for ever, on a thread inside a
      // synchronous COM call, with no cancellation path and nothing thrown for the
      // exception barrier to catch. TimeoutSeconds bounds ONE page, not the operation.
      //
      // So: a hard ceiling on pages, and a consecutive-no-progress detector for the
      // pathological case that still answers within it.
      const int maxPages = 10000;
      int pagesRead = 0;
      int pagesWithoutProgress = 0;

      for (;;)
      {
         if (++pagesRead > maxPages)
         {
            // Treated as truncation rather than as an error, because it is exactly
            // that: entries were read, and there may be more. Saying "success" here
            // would be the silent-partial-view failure this whole method exists to
            // avoid.
            truncated = true;
            break;
         }

         PLDAPControlW pageControl = nullptr;

         // Not critical. A directory that does not implement paged results should
         // answer the search anyway rather than refuse it outright; maxEntries still
         // bounds the result, and one page is the correct answer for a small directory.
         ULONG status = ldap_create_page_controlW(session_, pageSize, cookie, 0, &pageControl);

         if (status != LDAP_SUCCESS)
         {
            if (cookie != nullptr)
               ber_bvfree(cookie);

            return Record_(status);
         }

         PLDAPControlW serverControls[2] = { pageControl, nullptr };

         LDAP_TIMEVAL timeout;
         timeout.tv_sec = timeout_seconds_;
         timeout.tv_usec = 0;

         LDAPMessage *searchResult = nullptr;

         status = ldap_search_ext_sW(session_, &baseBuffer[0], LDAP_SCOPE_SUBTREE,
            &filterBuffer[0], &attributePointers[0], 0, serverControls, nullptr,
            &timeout, 0, &searchResult);

         ldap_control_freeW(pageControl);

         // A size-limit hit describes what the server sent rather than a failure, and
         // the entries it did send are usable - but it ALSO means there were more, so
         // it is truncation and has to be reported as such.
         //
         // FindUserDn forces its ambiguity flag on this same status for the same
         // reason. This lost that discipline on the first attempt: the entries were
         // kept, LDAP_SUCCESS was returned, and `truncated` stayed false - so an
         // OpenLDAP directory with its default `sizelimit 500` and two thousand people
         // would report "read 500 entries" as a complete answer, and an account source
         // built on it would treat fifteen hundred real users as absent. That is
         // precisely the failure the paging exists to prevent, arriving through the
         // one status code paging does not cover.
         if (status == LDAP_SIZELIMIT_EXCEEDED)
            truncated = true;

         if (status != LDAP_SUCCESS && status != LDAP_SIZELIMIT_EXCEEDED)
         {
            if (searchResult != nullptr)
               ldap_msgfree(searchResult);

            if (cookie != nullptr)
               ber_bvfree(cookie);

            if (IsTransportFailure_(status))
               Abandon_();

            return Record_(status);
         }

         if (searchResult == nullptr)
         {
            if (cookie != nullptr)
               ber_bvfree(cookie);

            return Record_(LDAP_SUCCESS);
         }

         const size_t sizeBeforeThisPage = entries.size();

         for (LDAPMessage *entry = ldap_first_entry(session_, searchResult);
              entry != nullptr;
              entry = ldap_next_entry(session_, entry))
         {
            if ((int) entries.size() >= maxEntries)
            {
               truncated = true;
               break;
            }

            LdapDirectoryEntry record;

            PWSTR entryDn = ldap_get_dnW(session_, entry);

            if (entryDn != nullptr)
            {
               record.dn = entryDn;
               ldap_memfreeW(entryDn);
            }

            // Fetched by name rather than walked with ldap_first_attribute, because
            // the caller has already said which attributes it wants and the BerElement
            // the walk allocates would have to be freed on every path out of the loop.
            for (const String &name : attributeNames)
            {
               std::vector<wchar_t> nameBuffer;
               ToMutable_(name, nameBuffer);

               PWCHAR *values = ldap_get_valuesW(session_, entry, &nameBuffer[0]);

               if (values == nullptr)
                  continue;

               std::vector<String> collected;

               for (int v = 0; values[v] != nullptr; v++)
                  collected.push_back(String(values[v]));

               ldap_value_freeW(values);

               // An attribute the directory sent with no values is not recorded, so
               // the map distinguishes "never sent" from "sent as empty" for a caller
               // that inspects `attributes` directly. First() deliberately does NOT
               // preserve that distinction - it answers an empty string for both -
               // because every caller so far wants one question answered ("is there a
               // usable value?") and would otherwise have to ask it twice.
               if (!collected.empty())
                  record.attributes[name] = collected;
            }

            entries.push_back(record);
         }

         // A page that added nothing. Tolerated a few times - a server may legitimately
         // send an empty page when a filter matches nothing in that slice of the DIT -
         // but not indefinitely, because a page with no entries and a non-empty cookie
         // is the shape that never terminates on its own.
         if (entries.size() == sizeBeforeThisPage)
            pagesWithoutProgress++;
         else
            pagesWithoutProgress = 0;

         if (cookie != nullptr)
         {
            ber_bvfree(cookie);
            cookie = nullptr;
         }

         if (truncated || pagesWithoutProgress >= 16)
         {
            // truncated covers both the maxEntries stop and a server-side size limit;
            // either way there is more than has been read, so paging further would
            // change nothing about the answer being partial.
            if (pagesWithoutProgress >= 16)
               truncated = true;

            ldap_msgfree(searchResult);
            break;
         }

         ULONG totalCount = 0;
         PLDAPControlW *returnedControls = nullptr;
         ULONG resultCode = 0;

         status = ldap_parse_resultW(session_, searchResult, &resultCode, nullptr, nullptr,
            nullptr, &returnedControls, FALSE);

         if (status == LDAP_SUCCESS && returnedControls != nullptr)
         {
            // A directory that ignored the control has no cookie to give back, which
            // ends the loop after one page - the right behaviour for a server that
            // answered the whole search at once.
            ldap_parse_page_controlW(session_, returnedControls, &totalCount, &cookie);
         }

         if (returnedControls != nullptr)
            ldap_controls_freeW(returnedControls);

         ldap_msgfree(searchResult);

         if (cookie == nullptr || cookie->bv_len == 0)
         {
            if (cookie != nullptr)
            {
               ber_bvfree(cookie);
               cookie = nullptr;
            }

            break;
         }
      }

      if (cookie != nullptr)
         ber_bvfree(cookie);

      return Record_(LDAP_SUCCESS);
#endif
   }

   LdapOperationOutcome
   LdapClient::FindUserDn(const LdapConfiguration &configuration, const String &filter,
      String &dn, int &matchCount)
   {
#ifdef HM_PLATFORM_POSIX

      dn.Empty();
      matchCount = 0;

      if (session_ == nullptr)
         return Record_(LDAP_LOCAL_ERROR);

      std::vector<char> baseBuffer;
      std::vector<char> filterBuffer;

      if (!ToMutableUtf8_(configuration.search_base, baseBuffer) || !ToMutableUtf8_(filter, filterBuffer))
         return Record_(LDAP_PARAM_ERROR);

      // Only the DN is wanted, and it is requested by name for the reason the Windows
      // arm gives: readability in a packet capture. A directory with no such
      // attribute - slapd has none - ignores the request, and the DN is on the entry
      // itself regardless.
      char distinguishedName[] = "distinguishedName";
      char *attributes[2] = { distinguishedName, nullptr };

      struct timeval timeout = SecondsAsTimeval_(timeout_seconds_);

      LDAPMessage *searchResult = nullptr;

      // Two, not one, for the reason the Windows arm gives beside its own sizeLimit:
      // a filter that matches several accounts must authenticate none of them.
      const int sizeLimit = 2;

      const int searchStatus = ldap_search_ext_s(session_, &baseBuffer[0], LDAP_SCOPE_SUBTREE,
         &filterBuffer[0], attributes, 0, nullptr, nullptr, &timeout, sizeLimit, &searchResult);

      if (searchStatus != LDAP_SUCCESS && searchStatus != LDAP_SIZELIMIT_EXCEEDED)
      {
         if (searchResult != nullptr)
            ldap_msgfree(searchResult);

         const unsigned long translated = TranslateNative_(searchStatus);

         if (IsTransportFailure_(translated))
            Abandon_();

         return Record_(translated);
      }

      if (searchResult == nullptr)
         return Record_(LDAP_SUCCESS);

      matchCount = ldap_count_entries(session_, searchResult);

      if (matchCount < 0)
      {
         // ldap_count_entries answers -1 for a message it cannot read. Not a match
         // count of any kind, and not a rejection: the directory sent something the
         // client cannot use.
         ldap_msgfree(searchResult);
         matchCount = 0;

         return Record_(LDAP_LOCAL_ERROR);
      }

      if (searchStatus == LDAP_SIZELIMIT_EXCEEDED && matchCount < 2)
         matchCount = 2;

      if (matchCount == 1)
      {
         LDAPMessage *entry = ldap_first_entry(session_, searchResult);

         if (entry != nullptr)
         {
            char *entryDn = ldap_get_dn(session_, entry);

            if (entryDn != nullptr)
            {
               dn = FromUtf8_(entryDn);
               ldap_memfree(entryDn);
            }
         }

         if (dn.IsEmpty())
         {
            ldap_msgfree(searchResult);
            matchCount = 0;

            return Record_(LDAP_LOCAL_ERROR);
         }
      }

      ldap_msgfree(searchResult);

      return Record_(LDAP_SUCCESS);

#else
      dn.Empty();
      matchCount = 0;

      if (session_ == nullptr)
         return Record_(LDAP_LOCAL_ERROR);

      std::vector<wchar_t> baseBuffer;
      std::vector<wchar_t> filterBuffer;

      ToMutable_(configuration.search_base, baseBuffer);
      ToMutable_(filter, filterBuffer);

      // Only the DN is wanted. It is requested by name rather than by asking for no
      // attributes at all so that the reply is readable in a packet capture when
      // somebody is working out why a filter matches nothing.
      wchar_t distinguishedName[] = L"distinguishedName";
      PWCHAR attributes[2] = { distinguishedName, nullptr };

      LDAP_TIMEVAL timeout;
      timeout.tv_sec = timeout_seconds_;
      timeout.tv_usec = 0;

      LDAPMessage *searchResult = nullptr;

      // A size limit of two, not one, and the extra entry is the point: with a limit
      // of one there is no way to tell "exactly one match" from "the first of several",
      // and a filter that matches several accounts must authenticate none of them
      // rather than whichever the directory happened to return first.
      const ULONG sizeLimit = 2;

      const ULONG searchStatus = ldap_search_ext_sW(session_, &baseBuffer[0], LDAP_SCOPE_SUBTREE,
         &filterBuffer[0], attributes, 0, nullptr, nullptr, &timeout, sizeLimit, &searchResult);

      // LDAP_SIZELIMIT_EXCEEDED is not a failure here - it is how the server says
      // "there were more than the two you asked for", and the entries it did send are
      // still in searchResult. Treated as a successful search that found too many.
      if (searchStatus != LDAP_SUCCESS && searchStatus != LDAP_SIZELIMIT_EXCEEDED)
      {
         if (searchResult != nullptr)
            ldap_msgfree(searchResult);

         if (IsTransportFailure_(searchStatus))
            Abandon_();

         return Record_(searchStatus);
      }

      if (searchResult == nullptr)
      {
         // A success with no message should not happen; treated as zero matches
         // rather than as a match, because guessing in the permissive direction here
         // authenticates somebody.
         return Record_(LDAP_SUCCESS);
      }

      matchCount = (int) ldap_count_entries(session_, searchResult);

      // A truncated reply is ambiguous whatever it contains, and this has to be forced
      // before the single-match branch rather than after it. The server is saying there
      // were more matches than it sent, so even one returned entry cannot be shown to
      // be the only one - and binding as an entry that may not be the only match is the
      // exact failure the size limit of two exists to detect.
      if (searchStatus == LDAP_SIZELIMIT_EXCEEDED && matchCount < 2)
         matchCount = 2;

      if (matchCount == 1)
      {
         LDAPMessage *entry = ldap_first_entry(session_, searchResult);

         if (entry != nullptr)
         {
            PWSTR entryDn = ldap_get_dnW(session_, entry);

            if (entryDn != nullptr)
            {
               dn = entryDn;
               ldap_memfreeW(entryDn);
            }
         }

         if (dn.IsEmpty())
         {
            // One entry that has no DN cannot be bound as. Not a rejection: the
            // directory answered with something the client cannot use.
            ldap_msgfree(searchResult);
            matchCount = 0;
            return Record_(LDAP_LOCAL_ERROR);
         }
      }

      ldap_msgfree(searchResult);

      return Record_(LDAP_SUCCESS);
#endif
   }
}
