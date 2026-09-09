// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// The REST API's certificate writes and the TCP/IP ports with their certificate binding. See RestApiServer.h.
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Two collections, persisted the way the Control Panel persists them through
// COM, with one difference in each direction that is stated where it is made:
//
//   - A certificate is a name and two file paths (InterfaceSSLCertificate.cpp).
//     COM saves whatever paths it is given and the listener finds out at the
//     next start, in the error log, that a file is not there. This route asks
//     the file system first and refuses with the path named, because an
//     operator typing a path over an API is one typo from a listener that will
//     not start. The saved object is appended to the process-wide certificate
//     collection exactly as InterfaceSSLCertificate::Save's AddToParentCollection
//     appends it, so IOService finds it by id at the next start.
//
//   - A port is a protocol, an address, a number, a connection security and the
//     certificate it binds (InterfaceTCPIPPort.cpp). There is no port cache to
//     refresh: Configuration::GetTCPIPPorts reads the table afresh on every
//     call, and IOService::DoWork reads it once, when the servers start. So a
//     port written here is in the database at once and on the wire when the
//     server restarts - which is what the OpenAPI text says of every port route,
//     rather than pretending to a live rebind that nothing in the server does.
//     COM refuses a TLS or STARTTLS port without a certificate and the client
//     certificate combinations that would enforce nothing (the limitation checks
//     in PersistentTCPIPPort::SaveObject, passed through as 400 in their own
//     words); this route also refuses a certificate id that names nothing, and
//     a second record on the same address and port, which COM lets through and
//     the bind then fails on.

#include "StdAfx.h"
#include "RestApiServer.h"

#include "../BO/SSLCertificate.h"
#include "../BO/SSLCertificates.h"
#include "../BO/TCPIPPort.h"
#include "../BO/TCPIPPorts.h"
#include "../Persistence/PersistentSSLCertificate.h"
#include "../Persistence/PersistentTCPIPPort.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The words the API uses for what SocketConstants.h numbers. The same
      // words on the way in and the way out, so that a record read from
      // GET /api/v1/ports can be sent back to PUT unchanged.
      const char *SecurityWord(ConnectionSecurity security)
      {
         switch (security)
         {
         case CSSSL: return "tls";
         case CSSTARTTLSOptional: return "starttls_optional";
         case CSSTARTTLSRequired: return "starttls_required";
         default: return "none";
         }
      }

      bool ParseSecurityWord(const AnsiString &word, ConnectionSecurity &security)
      {
         if (word == "none") { security = CSNone; return true; }
         if (word == "tls") { security = CSSSL; return true; }
         if (word == "starttls_optional") { security = CSSTARTTLSOptional; return true; }
         if (word == "starttls_required") { security = CSSTARTTLSRequired; return true; }
         return false;
      }

      const char *ProtocolWord(SessionType protocol)
      {
         switch (protocol)
         {
         case STSMTP: return "smtp";
         case STPOP3: return "pop3";
         case STIMAP: return "imap";
         default: return "unknown";
         }
      }

      bool ParseProtocolWord(const AnsiString &word, SessionType &protocol)
      {
         if (word == "smtp") { protocol = STSMTP; return true; }
         if (word == "pop3") { protocol = STPOP3; return true; }
         if (word == "imap") { protocol = STIMAP; return true; }
         return false;
      }

      // The mutual-TLS policy of a port, in the words InterfaceTCPIPPort's
      // refusal sentence uses for 0, 1 and 2.
      const char *PolicyWord(ClientCertificatePolicy policy)
      {
         switch (policy)
         {
         case CCPRequest: return "request";
         case CCPRequire: return "require";
         default: return "off";
         }
      }

      bool ParsePolicyWord(const AnsiString &word, ClientCertificatePolicy &policy)
      {
         if (word == "off") { policy = CCPOff; return true; }
         if (word == "request") { policy = CCPRequest; return true; }
         if (word == "require") { policy = CCPRequire; return true; }
         return false;
      }

      // A number in the body, as the IP range route reads its priority: the
      // digits after the key's colon, up to the next separator. A number a
      // client sent as a string ("2565") is taken as well, since nothing is
      // gained by refusing it. False when the key is not there or what follows
      // it is not a number, which the callers tell apart from a value of 0.
      bool GetJsonInteger(const AnsiString &json, const AnsiString &key, __int64 &value)
      {
         AnsiString needle = "\"" + key + "\"";

         int keyPosition = json.Find(needle);
         if (keyPosition < 0)
            return false;

         int colon = json.Find(":", keyPosition + needle.GetLength());
         if (colon < 0)
            return false;

         AnsiString digits;
         bool quoted = false;

         for (int i = colon + 1; i < json.GetLength(); i++)
         {
            char c = json[i];

            if (digits.IsEmpty() && (c == ' ' || c == '\t' || c == '\r' || c == '\n'))
               continue;

            if (digits.IsEmpty() && !quoted && c == '\"')
            {
               quoted = true;
               continue;
            }

            if ((c >= '0' && c <= '9') || (c == '-' && digits.IsEmpty()))
            {
               digits += c;
               continue;
            }

            break;
         }

         if (digits.IsEmpty() || digits == "-" || digits.GetLength() > 18)
            return false;

         value = _atoi64(digits.c_str());
         return true;
      }

      // One certificate as GET /api/v1/certificates renders it, so that what
      // the create answers is exactly what the next listing will show. The
      // private key password is not here and is not anywhere: it is accepted
      // on the way in and never emitted by any route.
      AnsiString CertificateEntryJson(const std::shared_ptr<SSLCertificate> &certificate,
                                      const AnsiString &escapedName,
                                      const AnsiString &escapedCertificateFile,
                                      const AnsiString &escapedPrivateKeyFile)
      {
         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"name\":\"%hs\",\"certificate_file\":\"%hs\",\"private_key_file\":\"%hs\"}",
            certificate->GetID(),
            escapedName.c_str(),
            escapedCertificateFile.c_str(),
            escapedPrivateKeyFile.c_str());
         return entry;
      }

      // One port as GET /api/v1/ports renders it. certificate_id is 0 when the
      // port binds none, as the stored column is.
      AnsiString PortEntryJson(const std::shared_ptr<TCPIPPort> &port,
                               const AnsiString &escapedAddress,
                               const AnsiString &escapedCaFile)
      {
         AnsiString entry;
         entry.Format("{\"id\":%I64d,\"protocol\":\"%hs\",\"address\":\"%hs\",\"port\":%d,"
                      "\"connection_security\":\"%hs\",\"certificate_id\":%I64d,"
                      "\"client_certificate_policy\":\"%hs\",\"client_certificate_ca_file\":\"%hs\"}",
            port->GetID(),
            ProtocolWord(port->GetProtocol()),
            escapedAddress.c_str(),
            port->GetPortNumber(),
            SecurityWord(port->GetConnectionSecurity()),
            port->GetSSLCertificateID(),
            PolicyWord(port->GetClientCertificatePolicy()),
            escapedCaFile.c_str());
         return entry;
      }

      // What a POST or PUT of a port says, read out of the body by the caller
      // (the JSON helpers are the server's own) and judged here. hasPort and
      // hasCertificateId tell a missing key from a 0.
      struct PortRequest
      {
         PortRequest() : hasPort(false), port(0), hasCertificateId(false), certificateId(0) { }

         AnsiString protocol;
         AnsiString address;
         AnsiString security;
         AnsiString policy;
         String caFile;
         bool hasPort;
         __int64 port;
         bool hasCertificateId;
         __int64 certificateId;
      };

      // Applies the request to the port, or names the first thing wrong with
      // it in a fixed sentence. Nothing is written to the port until every
      // check has passed, so a refused update leaves the object as it was read.
      // The address is parsed with reportOnFail false: a caller's typo is
      // answered in the response and does not become a line in the server's
      // error log.
      bool ApplyPortRequest(const PortRequest &request, const std::shared_ptr<TCPIPPort> &port,
                            const std::shared_ptr<SSLCertificates> &certificates, AnsiString &error)
      {
         if (request.protocol.IsEmpty())
         {
            error = "protocol is required";
            return false;
         }

         SessionType protocol = STUnknown;
         if (!ParseProtocolWord(request.protocol, protocol))
         {
            error = "protocol must be smtp, pop3 or imap";
            return false;
         }

         if (!request.hasPort)
         {
            error = "port is required";
            return false;
         }

         if (request.port < 1 || request.port > 65535)
         {
            error = "port must be between 1 and 65535";
            return false;
         }

         // 0.0.0.0 when the body names no address: every interface, which is
         // what a port added in the Control Panel listens on by default.
         IPAddress address;
         if (!address.TryParse(request.address.IsEmpty() ? AnsiString("0.0.0.0") : request.address, false))
         {
            error = "address must be an IP address";
            return false;
         }

         ConnectionSecurity security = CSNone;
         if (!request.security.IsEmpty() && !ParseSecurityWord(request.security, security))
         {
            error = "connection_security must be none, tls, starttls_optional or starttls_required";
            return false;
         }

         // A certificate id that names nothing is refused here. COM stores it,
         // and IOService then starts the listener with no certificate at all -
         // SslContextInitializer reports the failure at the next restart, when
         // the operator who typed the id is long gone.
         __int64 certificateId = request.hasCertificateId ? request.certificateId : 0;
         if (certificateId < 0 || (certificateId > 0 && (!certificates || !certificates->GetItemByDBID(certificateId))))
         {
            error = "certificate_id does not name a certificate";
            return false;
         }

         ClientCertificatePolicy policy = CCPOff;
         if (!request.policy.IsEmpty() && !ParsePolicyWord(request.policy, policy))
         {
            error = "client_certificate_policy must be off, request or require";
            return false;
         }

         port->SetProtocol(protocol);
         port->SetPortNumber((int) request.port);
         port->SetAddress(address);
         port->SetConnectionSecurity(security);
         port->SetSSLCertificateID((int) certificateId);
         port->SetClientCertificatePolicy(policy);
         port->SetClientCertificateCAFile(request.caFile);

         return true;
      }

      // The record that already listens on the candidate's address and port,
      // if there is one, the candidate itself excepted (an update keeps its
      // own row). Two rows on one address and port are two listeners for one
      // socket: COM saves the second and the bind fails at the next start, in
      // the error log. Judged on the exact address, as the bind is: 0.0.0.0
      // and 127.0.0.1 on the same port are two rows and two binds, and whether
      // the second bind succeeds is the operating system's decision.
      std::shared_ptr<TCPIPPort> FindListenerConflict(const std::shared_ptr<TCPIPPorts> &ports,
                                                      const std::shared_ptr<TCPIPPort> &candidate)
      {
         std::vector<std::shared_ptr<TCPIPPort> > existing = ports->GetSnapshot();

         for (size_t i = 0; i < existing.size(); i++)
         {
            std::shared_ptr<TCPIPPort> other = existing[i];
            if (!other || other->GetID() == candidate->GetID())
               continue;

            if (other->GetPortNumber() == candidate->GetPortNumber() &&
                other->GetAddressString() == candidate->GetAddressString())
            {
               return other;
            }
         }

         return std::shared_ptr<TCPIPPort>();
      }

      // "smtp 0.0.0.0:2566", for a log line or a refusal that names a port.
      String DescribePort(const std::shared_ptr<TCPIPPort> &port)
      {
         String description;
         description.Format(_T("%hs %s:%d"), ProtocolWord(port->GetProtocol()),
            port->GetAddressString().c_str(), port->GetPortNumber());
         return description;
      }
   }

   AnsiString
   RestApiServer::OpenApiCertificatesPaths_()
   {
      // The whole /api/v1/certificates entry lives here, both verbs: the head
      // of the document in HandleOpenApi_ no longer carries the path, so the
      // key appears once.
      static const char *paths =
         ",\"/api/v1/certificates\":{"
         "\"get\":{\"summary\":\"List the SSL certificates\",\"description\":\"Names and file paths, never a private key password. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of certificates: id, name, certificate_file, private_key_file\"}}},"
         "\"post\":{\"summary\":\"Add an SSL certificate\",\"description\":\"A name and the paths of a PEM certificate file and its private key file on the server. Both files must exist: a path that does not is refused with the file named, rather than saved for the listener to fail on at the next start. private_key_password unlocks an encrypted key; it is accepted here and emitted by no route. A port binds the certificate by its id. Server-wide; refused for domain-restricted keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"name\",\"certificate_file\",\"private_key_file\"],\"properties\":{\"name\":{\"type\":\"string\"},\"certificate_file\":{\"type\":\"string\"},\"private_key_file\":{\"type\":\"string\"},\"private_key_password\":{\"type\":\"string\",\"writeOnly\":true}}}}}},\"responses\":{\"201\":{\"description\":\"Created, as the listing shows it\"},\"400\":{\"description\":\"A required field is missing, or a file is not there (the error names it)\"},\"409\":{\"description\":\"A certificate with that name already exists\"}}}},"
         "\"/api/v1/certificates/{id}\":{"
         "\"delete\":{\"summary\":\"Delete an SSL certificate\",\"description\":\"Refused while a TCP/IP port binds the certificate; the refusal names the port. Delete or rebind the port first. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"},\"409\":{\"description\":\"A port binds the certificate\"}}}},"
         "\"/api/v1/ports\":{"
         "\"get\":{\"summary\":\"List the TCP/IP ports\",\"description\":\"Every listener the server is configured with: id, protocol (smtp, pop3, imap), address, port, connection_security (none, tls, starttls_optional, starttls_required), certificate_id (0 when none), client_certificate_policy (off, request, require) and client_certificate_ca_file. Read from the database, so a port written by any route is listed at once; whether it is on the wire is a matter of when the server last started. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Array of ports\"}}},"
         "\"post\":{\"summary\":\"Add a TCP/IP port\",\"description\":\"The listeners are created when the server starts, so a port added here takes effect when the server restarts. address defaults to 0.0.0.0, connection_security to none, certificate_id to 0 and client_certificate_policy to off. tls and both starttls values need a certificate_id, and a client certificate policy other than off needs a CA file and a security that runs a handshake - the same refusals the Control Panel meets, in the same words. Server-wide; refused for domain-restricted keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"protocol\",\"port\"],\"properties\":{\"protocol\":{\"type\":\"string\",\"enum\":[\"smtp\",\"pop3\",\"imap\"]},\"address\":{\"type\":\"string\"},\"port\":{\"type\":\"integer\"},\"connection_security\":{\"type\":\"string\",\"enum\":[\"none\",\"tls\",\"starttls_optional\",\"starttls_required\"]},\"certificate_id\":{\"type\":\"integer\"},\"client_certificate_policy\":{\"type\":\"string\",\"enum\":[\"off\",\"request\",\"require\"]},\"client_certificate_ca_file\":{\"type\":\"string\"}}}}}},\"responses\":{\"201\":{\"description\":\"Created, as the listing shows it\"},\"400\":{\"description\":\"A missing or unrecognised field, a TLS security without a certificate, a certificate_id that names no certificate, or a client certificate policy the port cannot enforce\"},\"409\":{\"description\":\"A port already listens on that address and port number\"}}}},"
         "\"/api/v1/ports/{id}\":{"
         "\"put\":{\"summary\":\"Replace a TCP/IP port\",\"description\":\"The whole record, with the fields and defaults of POST: a field left out takes its default, so send back what GET returned with the change made. Takes effect when the server restarts. Server-wide; refused for domain-restricted keys.\",\"requestBody\":{\"content\":{\"application/json\":{\"schema\":{\"type\":\"object\",\"required\":[\"protocol\",\"port\"],\"properties\":{\"protocol\":{\"type\":\"string\",\"enum\":[\"smtp\",\"pop3\",\"imap\"]},\"address\":{\"type\":\"string\"},\"port\":{\"type\":\"integer\"},\"connection_security\":{\"type\":\"string\",\"enum\":[\"none\",\"tls\",\"starttls_optional\",\"starttls_required\"]},\"certificate_id\":{\"type\":\"integer\"},\"client_certificate_policy\":{\"type\":\"string\",\"enum\":[\"off\",\"request\",\"require\"]},\"client_certificate_ca_file\":{\"type\":\"string\"}}}}}},\"responses\":{\"200\":{\"description\":\"Replaced, as the listing shows it\"},\"400\":{\"description\":\"As for POST\"},\"404\":{\"description\":\"Unknown id\"},\"409\":{\"description\":\"Another port already listens on that address and port number\"}}},"
         "\"delete\":{\"summary\":\"Delete a TCP/IP port\",\"description\":\"The row is gone at once; the listener stays up until the server restarts. Server-wide; refused for domain-restricted keys.\",\"responses\":{\"200\":{\"description\":\"Deleted\"},\"404\":{\"description\":\"Unknown id\"}}}}";

      return AnsiString(paths);
   }

   HttpResponse
   RestApiServer::HandleCreateCertificate_(const AnsiString &requestBody)
   {
      String name = JsonUtf8Value_(requestBody, "name");
      name.Trim();

      String certificateFile = JsonUtf8Value_(requestBody, "certificate_file");
      certificateFile.Trim();

      String privateKeyFile = JsonUtf8Value_(requestBody, "private_key_file");
      privateKeyFile.Trim();

      if (name.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"name is required\"}");

      if (certificateFile.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"certificate_file is required\"}");

      if (privateKeyFile.IsEmpty())
         return BuildResponse_(400, "{\"error\":\"private_key_file is required\"}");

      // COM does not look: InterfaceSSLCertificate::Save persists the paths as
      // given, and a path with a typo in it is found by SslContextInitializer
      // at the next start ("Failed to load certificate file ...", in the error
      // log, with the listener not started). Over an API the operator is here
      // now, so the answer goes to them, with the path they sent.
      if (!FileUtilities::Exists(certificateFile))
      {
         AnsiString body;
         body.Format("{\"error\":\"certificate_file does not exist: %hs\"}", JsonEscape_(Utf8_(certificateFile)).c_str());
         return BuildResponse_(400, body);
      }

      if (!FileUtilities::Exists(privateKeyFile))
      {
         AnsiString body;
         body.Format("{\"error\":\"private_key_file does not exist: %hs\"}", JsonEscape_(Utf8_(privateKeyFile)).c_str());
         return BuildResponse_(400, body);
      }

      // The process-wide collection, the one InterfaceSettings::get_SSLCertificates
      // hands the Control Panel and IOService reads at start. Refreshed first,
      // as the listing refreshes it, so the duplicate check sees what another
      // process wrote.
      std::shared_ptr<SSLCertificates> certificates = Configuration::Instance()->GetSSLCertificates();
      if (!certificates)
         return BuildResponse_(500, "{\"error\":\"the certificate collection is not available\"}");

      certificates->Refresh();

      // The name is what a backup's port records name their certificate by
      // (TCPIPPort::XMLStore), and GetItemByName compares without case; two
      // certificates under one name would restore as one. A conflict, not a
      // validation failure: to the caller it means "already done".
      if (certificates->GetItemByName(name))
         return BuildResponse_(409, "{\"error\":\"a certificate with that name already exists\"}");

      std::shared_ptr<SSLCertificate> certificate = std::shared_ptr<SSLCertificate>(new SSLCertificate);
      certificate->SetName(name);
      certificate->SetCertificateFile(certificateFile);
      certificate->SetPrivateKeyFile(privateKeyFile);
      certificate->SetPrivateKeyPassword(JsonUtf8Value_(requestBody, "private_key_password"));

      // The overload COM calls: PersistentSSLCertificate has no limitation
      // check, so there is no sentence to pass through and a false is the
      // INSERT failing, which is ours.
      if (!PersistentSSLCertificate::SaveObject(certificate))
         return BuildResponse_(500, "{\"error\":\"failed to save certificate\"}");

      // As InterfaceSSLCertificate::Save's AddToParentCollection: into the
      // shared collection, so a listener started from this process finds the
      // certificate by its id without another Refresh.
      certificates->AddItem(certificate);

      LOG_APPLICATION("RestApi: Certificate '" + name + "' created with id " + StringParser::IntToString(certificate->GetID()) + ".");

      return BuildResponse_(201, CertificateEntryJson(certificate,
         JsonEscape_(Utf8_(certificate->GetName())),
         JsonEscape_(Utf8_(certificate->GetCertificateFile())),
         JsonEscape_(Utf8_(certificate->GetPrivateKeyFile()))));
   }

   HttpResponse
   RestApiServer::HandleDeleteCertificate_(__int64 certificateId)
   {
      std::shared_ptr<SSLCertificates> certificates = Configuration::Instance()->GetSSLCertificates();
      if (!certificates)
         return BuildResponse_(404, "{\"error\":\"certificate not found\"}");

      certificates->Refresh();

      std::shared_ptr<SSLCertificate> certificate = certificates->GetItemByDBID(certificateId);
      if (!certificate)
         return BuildResponse_(404, "{\"error\":\"certificate not found\"}");

      // A port that binds it. COM deletes the certificate anyway and leaves
      // the port pointing at an id that is gone, which the next start reports
      // as a listener that could not load its certificate. Refused here, with
      // the port named, so that the caller can delete or rebind it first.
      std::shared_ptr<TCPIPPorts> ports = Configuration::Instance()->GetTCPIPPorts();
      if (ports)
      {
         std::vector<std::shared_ptr<TCPIPPort> > snapshot = ports->GetSnapshot();

         for (size_t i = 0; i < snapshot.size(); i++)
         {
            std::shared_ptr<TCPIPPort> port = snapshot[i];
            if (!port || port->GetSSLCertificateID() != certificateId)
               continue;

            AnsiString body;
            body.Format("{\"error\":\"the certificate is bound to port %I64d (%hs); delete or rebind the port first\",\"port_id\":%I64d}",
               port->GetID(), JsonEscape_(Utf8_(DescribePort(port))).c_str(), port->GetID());

            return BuildResponse_(409, body);
         }
      }

      String name = certificate->GetName();

      // Through the shared collection, as InterfaceSSLCertificate::Delete goes
      // through its parent collection: the row is deleted and the object
      // dropped from the collection only when the delete succeeded.
      if (!certificates->DeleteItemByDBID(certificateId))
         return BuildResponse_(500, "{\"error\":\"failed to delete certificate\"}");

      LOG_APPLICATION("RestApi: Certificate '" + name + "' (id " + StringParser::IntToString(certificateId) + ") deleted.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }

   HttpResponse
   RestApiServer::HandleListPorts_()
   {
      // Fresh from the database, as InterfaceSettings::get_TCPIPPorts hands
      // the Control Panel its collection.
      std::shared_ptr<TCPIPPorts> ports = Configuration::Instance()->GetTCPIPPorts();
      if (!ports)
         return BuildResponse_(200, "[]");

      AnsiString body = "[";
      int count = 0;

      std::vector<std::shared_ptr<TCPIPPort> > snapshot = ports->GetSnapshot();

      for (size_t i = 0; i < snapshot.size(); i++)
      {
         std::shared_ptr<TCPIPPort> port = snapshot[i];
         if (!port)
            continue;

         if (count > 0)
            body += ",";

         body += PortEntryJson(port,
            JsonEscape_(Utf8_(port->GetAddressString())),
            JsonEscape_(Utf8_(port->GetClientCertificateCAFile())));
         count++;
      }

      body += "]";

      return BuildResponse_(200, body);
   }

   HttpResponse
   RestApiServer::HandleCreatePort_(const AnsiString &requestBody)
   {
      PortRequest request;
      request.protocol = GetJsonStringValue_(requestBody, "protocol");
      request.address = GetJsonStringValue_(requestBody, "address");
      request.security = GetJsonStringValue_(requestBody, "connection_security");
      request.policy = GetJsonStringValue_(requestBody, "client_certificate_policy");
      request.caFile = JsonUtf8Value_(requestBody, "client_certificate_ca_file");
      request.hasPort = GetJsonInteger(requestBody, "port", request.port);
      request.hasCertificateId = GetJsonInteger(requestBody, "certificate_id", request.certificateId);

      std::shared_ptr<SSLCertificates> certificates = Configuration::Instance()->GetSSLCertificates();
      if (certificates)
         certificates->Refresh();

      // A fresh TCPIPPort, as InterfaceTCPIPPorts::Add makes one: every default
      // the Control Panel would leave in place is the same here.
      std::shared_ptr<TCPIPPort> port = std::shared_ptr<TCPIPPort>(new TCPIPPort);

      AnsiString error;
      if (!ApplyPortRequest(request, port, certificates, error))
         return BuildResponse_(400, "{\"error\":\"" + JsonEscape_(error) + "\"}");

      std::shared_ptr<TCPIPPorts> ports = Configuration::Instance()->GetTCPIPPorts();

      std::shared_ptr<TCPIPPort> conflict = ports ? FindListenerConflict(ports, port) : std::shared_ptr<TCPIPPort>();
      if (conflict)
      {
         AnsiString body;
         body.Format("{\"error\":\"a port already listens on that address and port number\",\"port_id\":%I64d}", conflict->GetID());
         return BuildResponse_(409, body);
      }

      String saveError;

      if (!PersistentTCPIPPort::SaveObject(port, saveError, PersistenceModeNormal))
      {
         // The limitation check's own sentence - "Certificate must be
         // specified.", the three client certificate refusals - written for an
         // administrator, which is what makes passing it through safe. No
         // sentence is the INSERT failing.
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to create port " + DescribePort(port) + ": " + saveError);

            AnsiString body;
            body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(saveError)).c_str());

            return BuildResponse_(400, body);
         }

         return BuildResponse_(500, "{\"error\":\"failed to save port\"}");
      }

      LOG_APPLICATION("RestApi: Port " + DescribePort(port) + " created with id " + StringParser::IntToString(port->GetID()) +
         ", connection security " + String(SecurityWord(port->GetConnectionSecurity())) +
         ", certificate " + StringParser::IntToString(port->GetSSLCertificateID()) + ". Takes effect when the server restarts.");

      return BuildResponse_(201, PortEntryJson(port,
         JsonEscape_(Utf8_(port->GetAddressString())),
         JsonEscape_(Utf8_(port->GetClientCertificateCAFile()))));
   }

   HttpResponse
   RestApiServer::HandleUpdatePort_(__int64 portId, const AnsiString &requestBody)
   {
      std::shared_ptr<TCPIPPorts> ports = Configuration::Instance()->GetTCPIPPorts();
      if (!ports)
         return BuildResponse_(404, "{\"error\":\"port not found\"}");

      std::shared_ptr<TCPIPPort> port = ports->GetItemByDBID(portId);
      if (!port)
         return BuildResponse_(404, "{\"error\":\"port not found\"}");

      PortRequest request;
      request.protocol = GetJsonStringValue_(requestBody, "protocol");
      request.address = GetJsonStringValue_(requestBody, "address");
      request.security = GetJsonStringValue_(requestBody, "connection_security");
      request.policy = GetJsonStringValue_(requestBody, "client_certificate_policy");
      request.caFile = JsonUtf8Value_(requestBody, "client_certificate_ca_file");
      request.hasPort = GetJsonInteger(requestBody, "port", request.port);
      request.hasCertificateId = GetJsonInteger(requestBody, "certificate_id", request.certificateId);

      std::shared_ptr<SSLCertificates> certificates = Configuration::Instance()->GetSSLCertificates();
      if (certificates)
         certificates->Refresh();

      String before = DescribePort(port);

      // The whole record: what the body does not name takes the POST default,
      // as the OpenAPI text says, so that a PUT of what GET returned - with one
      // field changed - is the ordinary way to change one field.
      AnsiString error;
      if (!ApplyPortRequest(request, port, certificates, error))
         return BuildResponse_(400, "{\"error\":\"" + JsonEscape_(error) + "\"}");

      std::shared_ptr<TCPIPPort> conflict = FindListenerConflict(ports, port);
      if (conflict)
      {
         AnsiString body;
         body.Format("{\"error\":\"another port already listens on that address and port number\",\"port_id\":%I64d}", conflict->GetID());
         return BuildResponse_(409, body);
      }

      String saveError;

      if (!PersistentTCPIPPort::SaveObject(port, saveError, PersistenceModeNormal))
      {
         if (!saveError.IsEmpty())
         {
            LOG_APPLICATION("RestApi: Refused to update port " + before + ": " + saveError);

            AnsiString body;
            body.Format("{\"error\":\"%hs\"}", JsonEscape_(Utf8_(saveError)).c_str());

            return BuildResponse_(400, body);
         }

         return BuildResponse_(500, "{\"error\":\"failed to save port\"}");
      }

      LOG_APPLICATION("RestApi: Port " + StringParser::IntToString(portId) + " updated: " + before + " is now " + DescribePort(port) +
         ", connection security " + String(SecurityWord(port->GetConnectionSecurity())) +
         ", certificate " + StringParser::IntToString(port->GetSSLCertificateID()) + ". Takes effect when the server restarts.");

      return BuildResponse_(200, PortEntryJson(port,
         JsonEscape_(Utf8_(port->GetAddressString())),
         JsonEscape_(Utf8_(port->GetClientCertificateCAFile()))));
   }

   HttpResponse
   RestApiServer::HandleDeletePort_(__int64 portId)
   {
      std::shared_ptr<TCPIPPorts> ports = Configuration::Instance()->GetTCPIPPorts();
      if (!ports)
         return BuildResponse_(404, "{\"error\":\"port not found\"}");

      std::shared_ptr<TCPIPPort> port = ports->GetItemByDBID(portId);
      if (!port)
         return BuildResponse_(404, "{\"error\":\"port not found\"}");

      String description = DescribePort(port);

      // Through the collection, as InterfaceTCPIPPort::Delete goes through its
      // parent collection, and for the reason Collection::DeleteItemByDBID
      // gives: a refused delete is reported, not reported as done.
      if (!ports->DeleteItemByDBID(portId))
         return BuildResponse_(500, "{\"error\":\"failed to delete port\"}");

      LOG_APPLICATION("RestApi: Port " + StringParser::IntToString(portId) + " (" + description + ") deleted. The listener stays up until the server restarts.");

      return BuildResponse_(200, "{\"deleted\":true}");
   }
}
