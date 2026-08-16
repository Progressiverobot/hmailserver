// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#pragma once

#include "../Util/Event.h"

#include "SocketConstants.h"
#include "IOOperationQueue.h"
#include "DaneVerifier.h"


#include <boost/atomic.hpp>

using boost::asio::ip::tcp;

typedef boost::asio::ssl::stream<boost::asio::ip::tcp::socket&> ssl_socket;

namespace HM
{
   class ByteBuffer;
   class SecurityRange;
   class CipherInfo;

   class TCPConnection :
      public std::enable_shared_from_this<TCPConnection>
   {
   public:
      TCPConnection(ConnectionSecurity connection_security,
                    boost::asio::io_context& io_context,    
                    boost::asio::ssl::context& context,
                    std::shared_ptr<Event> disconnected,
                    AnsiString expected_remote_hostname);
      ~TCPConnection(void);

      enum Consts
      {
         BufferSize = 60000
      };

      bool Connect(const AnsiString &remote_ip_address, long remotePort, const IPAddress &localAddress);
      
      void Start();
      void SetReceiveBinary(bool binary);

      void EnqueueWrite(const AnsiString &sData);
      void EnqueueWrite(std::shared_ptr<ByteBuffer> pByteBuffer);
      void EnqueueRead();
      void EnqueueRead(const AnsiString &delimitor);

      // RFC 3030 CHUNKING (BDAT): read exactly 'numBytes' octets (byte-transparent,
      // no delimiter) and deliver them to ParseData(ByteBuffer). Any octets that were
      // already buffered beyond the requested count are left in the receive buffer for
      // the following read. The connection must be in binary receive mode.
      void EnqueueReadExact(size_t numBytes);

      // Throws away anything already received and not yet parsed.
      //
      // Exists for one specific job: when a message's end-of-data was recognised in a
      // non-standard spelling (a bare LF where the standard requires CRLF, accepted only
      // because the administrator asked for that tolerance), anything the peer has already
      // pipelined behind that terminator must NOT go on to be parsed as SMTP commands.
      // Honouring it is the SMTP smuggling primitive of CVE-2023-51764: an upstream relay
      // that does not treat those bytes as a terminator forwards one message, and a server
      // that does treat them as one, and then executes what follows, has been made to
      // accept a second message the relay never authorised.
      //
      // Discarding is safe for legitimate clients because RFC 2920 requires a client to
      // wait for the reply after end-of-data, so a well-behaved sender has nothing in
      // flight at this point.
      void DiscardBufferedInput();

      // Puts bytes at the disposal of the next read as though they had just arrived
      // from the socket. This exists for pipelined input that a protocol layer had
      // to withhold: commands a client legitimately sent behind the STANDARD SMTP
      // end-of-data marker in the same segment as the message (Postfix pipelines
      // QUIT, or the next MAIL FROM, that way routinely). Call only while no read
      // is armed - between a protocol phase ending and the EnqueueRead that starts
      // the next one - because it writes the shared receive buffer.
      void InjectPipelinedBytes(const BYTE *bytes, size_t count);

      void EnqueueShutdownSend();
      void EnqueueDisconnect();
      void EnqueueHandshake();
      
      IPAddress GetRemoteEndpointAddress();
      unsigned long GetLocalEndpointPort();

      void UpdateAutoLogoutTimer();

      void SetSecurityRange(std::shared_ptr<SecurityRange> securityRange);
      std::shared_ptr<SecurityRange> GetSecurityRange();

      boost::asio::ip::tcp::socket& GetSocket() {return socket_;}

      ConnectionSecurity GetConnectionSecurity() {return connection_security_; }

      bool IsSSLConnection(){return is_ssl_;}

      void Timeout();

      CipherInfo GetCipherInfo();

      // RFC 5929 'tls-server-end-point' channel binding: the hash of the server's
      // own TLS certificate, used by SCRAM-SHA-256-PLUS. Per RFC 5929 the hash is
      // the certificate's signature hash, except MD5/SHA-1 are replaced by SHA-256.
      // Returns false when the connection is not TLS or the certificate is
      // unavailable; on success 'out' holds the raw digest bytes.
      bool GetTlsServerEndPoint(std::vector<unsigned char> &out);

      void SetAllowConnectToSelf(bool allow)  { allow_connect_to_self_ = allow; }

      // Forces certificate verification for this connection even if the
      // global VerifyRemoteSslCertificate setting is disabled. Used by
      // MTA-STS enforced deliveries (RFC 8461).
      void SetRequirePeerVerification() { require_peer_verification_ = true; }

      // Supplies DANE-EE TLSA records for this connection. When present,
      // certificate verification is performed against these records
      // instead of the PKIX chain (RFC 7672).
      void SetDaneRecords(const std::vector<TlsaRecord> &records) { dane_records_ = records; }

      // Mutual TLS for INBOUND sessions: set by TCPServer from the listening
      // port's configuration before the connection starts. Deliberately separate
      // from SetRequirePeerVerification above, which drives the OUTBOUND
      // (client-side) verification path - conflating the two is how a setting
      // meant for one direction ends up altering MTA-STS or DANE behaviour in
      // the other.
      void SetInboundClientCertificatePolicy(ClientCertificatePolicy policy) { inbound_client_certificate_policy_ = policy; }

      // The subject of the client certificate this inbound session presented,
      // set only when the certificate chained to the port's CA bundle. Empty
      // otherwise. This is the hook through which the verified identity could
      // later reach authentication (SASL EXTERNAL); today it is surfaced in the
      // TCP/IP log by AsyncHandshakeCompleted.
      AnsiString GetVerifiedClientCertificateSubject() const { return verified_client_certificate_subject_; }

      int GetSessionID();

   protected:

      ConnectionState GetConnectionState() { return connection_state_;  }

      int GetBufferSize() {return BufferSize; }

      void SetTimeout(int seconds);

      // An absolute ceiling on the whole session, armed once and never re-armed.
      // SetTimeout's deadline is an IDLE timeout: it is pushed forward on every
      // read, so a peer that dribbles one byte before each expiry holds the
      // connection - and anything waiting on its destruction - forever. Callers
      // that block a pooled thread until this connection dies must set a ceiling.
      // 0 leaves the session unbounded.
      void SetSessionCeiling(int seconds);
      AnsiString GetIPAddressString();

      virtual void OnCouldNotConnect(const AnsiString &sErrorDescription) {};
      virtual void OnConnected() = 0;
      virtual void OnHandshakeCompleted() = 0;
      virtual void OnHandshakeFailed() = 0;
      virtual void OnConnectionTimeout() = 0;
      virtual void OnExcessiveDataReceived() = 0;
      virtual void OnDataSent() {};
      virtual void OnReadError(int errorCode) {};
      virtual AnsiString GetCommandSeparator() const = 0;

      /* PARSING METHODS */
      virtual void ParseData(const AnsiString &sAnsiString) = 0;
      virtual void ParseData(std::shared_ptr<ByteBuffer> pByteBuffer) = 0;

      // Low-cardinality OpenTelemetry span name for a single command line (the
      // protocol verb, e.g. "MAIL"/"RETR"/"LOGIN"). The default takes the first
      // whitespace-delimited token; IMAP overrides this to skip the leading tag.
      // Never includes command arguments, so no credentials/data leak into traces.
      virtual AnsiString GetOtelOperationName_(const AnsiString &sData) const;

      // Shared helper: returns the uppercased token at tokenIndex when it is a
      // clean verb (letters only, <= 16 chars), else "command".
      static AnsiString ExtractOtelVerb_(const AnsiString &sData, int tokenIndex);

      AnsiString GetSslTlsCipher();

   private:

      void ThrowIfNotConnected_();

      // Common handling for every way a TLS handshake can fail.
      //
      // retire_handshake_operation says who owns the queued BCTHandshake entry.
      // AsyncHandshakeCompleted pops it itself the moment this returns, so it
      // passes false; the paths in AsyncHandshake that give up before
      // async_handshake was ever started have nobody else to do it and pass true.
      // Getting this wrong in either direction is visible: popping twice reports
      // Critical HM5131 against a healthy session, and never popping wedges the
      // connection for good, because IOOperationQueue refuses to start any
      // operation at all while a handshake is outstanding.
      void HandshakeFailed_(const boost::system::error_code& error, bool retire_handshake_operation);

      // False when no connection attempt was started, in which case the caller has
      // already been told why through OnCouldNotConnect.
      bool StartAsyncConnect_(const String &ip_adress, int port);

      static void OnTimeout(std::weak_ptr<TCPConnection> connection, boost::system::error_code const& err);
      static void OnSessionCeilingReached(std::weak_ptr<TCPConnection> connection, boost::system::error_code const& err);
      void ArmSessionCeiling_();

      String SafeGetIPAddress();

      bool IsClient();

      void ProcessOperationQueue_(int recurse_level);

      void Disconnect();
      void Shutdown(boost::asio::socket_base::shutdown_type);
      
      void AsyncWrite(std::shared_ptr<ByteBuffer> buffer);
      void AsyncRead(const AnsiString &delimitor);
      void AsyncHandshake();

      // After a successful inbound handshake on a port with a client-certificate
      // policy: log what the peer presented (or that it presented nothing) and
      // capture the subject when it verified. Runs after the handshake rather
      // than inside the verify callback because the callback is a copied functor
      // with no channel back to the connection, and because only here is the
      // final verdict (SSL_get_verify_result) known.
      void LogInboundClientCertificate_();

      void AsyncConnectCompleted(const boost::system::error_code& err);
      void AsyncHandshakeCompleted(const boost::system::error_code& error);
      void AsyncReadCompleted(const boost::system::error_code& /*error*/, size_t bytes_transferred);
      void AsyncWriteCompleted(const boost::system::error_code& /*error*/, size_t bytes_transferred);

      void ReportDebugMessage(const String &message, const boost::system::error_code &error);
      void ReportError(ErrorManager::eSeverity sev, int code, const String &context, const String &message, const boost::system::system_error &error);
      void ReportError(ErrorManager::eSeverity sev, int code, const String &context, const String &message, const boost::system::error_code &error);
      void ReportError(ErrorManager::eSeverity sev, int code, const String &context, const String &message);

      boost::asio::ip::tcp::socket socket_;
      ssl_socket ssl_socket_;

      boost::asio::ip::tcp::resolver resolver_;
      boost::asio::steady_timer timer_;
      boost::asio::steady_timer session_ceiling_timer_;
      int session_ceiling_seconds_;
      bool session_ceiling_armed_;
      boost::asio::streambuf receive_buffer_;
      boost::asio::ssl::context& context_;

      IOOperationQueue operation_queue_;

      bool receive_binary_;

      // RFC 3030 BDAT: when non-zero, the next binary read consumes exactly this many
      // octets (transfer_exactly) and then resets to 0. Zero = classic stream read
      // (transfer_at_least(1)).
      size_t exact_read_target_;

      ConnectionSecurity connection_security_;
      long remote_port_;
      String remote_ip_address_;

      std::shared_ptr<SecurityRange> security_range_;

      int session_id_;
      int timeout_;

      // Lazily-generated per-session OpenTelemetry trace id (32 hex), so every
      // command span from one connection shares a trace. Empty until first used.
      AnsiString otel_trace_id_;

      AnsiString expected_remote_hostname_;
      std::shared_ptr<Event> disconnected_;
      bool is_ssl_;
      bool is_client_;
      bool handshake_in_progress_;
      bool allow_connect_to_self_;
      bool require_peer_verification_ = false;
      std::vector<TlsaRecord> dane_records_;

      // Inbound-only; see SetInboundClientCertificatePolicy. CCPOff keeps the
      // handshake exactly as it always was (verify_none).
      ClientCertificatePolicy inbound_client_certificate_policy_ = CCPOff;

      // Set only for an inbound session whose client certificate verified
      // against the port's CA bundle; see GetVerifiedClientCertificateSubject.
      AnsiString verified_client_certificate_subject_;

      boost::atomic<ConnectionState> connection_state_;
      boost::mutex autologout_timer_;

   };

}