// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

// The syslog sink. See SyslogSink.h for the three things it must never do.

#include "StdAfx.h"

#include "SyslogSink.h"

#include "Unicode.h"
#include "Time.h"
#include "../TCPIP/CertificateVerifier.h"
#include "../TCPIP/SslContextInitializer.h"

#include <chrono>

#ifdef HM_PLATFORM_POSIX
#include <sys/un.h>
#include <sys/stat.h>
#endif

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const size_t SyslogSink::QueueLimit;
   const size_t SyslogSink::BatchLimit;

   namespace
   {
#ifdef HM_PLATFORM_POSIX
      // The path systemd's journal listens on. Named once so the two places that
      // care - "is there a journal?" and "send to it" - cannot disagree. Inside the
      // guard with the field writer below, so that neither is dead code on Windows.
      const char *JournalSocketPath = "/run/systemd/journal/socket";
#endif

      // Set on the thread that is inside OnLogEntry. Without it a drop, a
      // rendering fault or anything else that wanted to log would re-enter
      // Logger::Write_ from inside Logger::Write_ and queue an entry describing
      // the queueing of an entry.
      thread_local bool inside_sink = false;

      class SinkReentryGuard
      {
      public:
         SinkReentryGuard() { inside_sink = true; }
         ~SinkReentryGuard() { inside_sink = false; }

      private:
         SinkReentryGuard(const SinkReentryGuard &);
         SinkReentryGuard &operator=(const SinkReentryGuard &);
      };

      // Send and receive deadlines on a connected socket. Windows takes a
      // millisecond count in a DWORD; POSIX takes a timeval. Written out rather
      // than reused from HttpsClient because that one passes a DWORD on both
      // platforms, where POSIX reads the first four bytes of it as tv_sec.
      void SetSocketDeadline_(SOCKET handle, int seconds)
      {
#ifdef HM_PLATFORM_POSIX
         struct timeval deadline;
         deadline.tv_sec = seconds;
         deadline.tv_usec = 0;
         ::setsockopt(handle, SOL_SOCKET, SO_SNDTIMEO, &deadline, sizeof(deadline));
         ::setsockopt(handle, SOL_SOCKET, SO_RCVTIMEO, &deadline, sizeof(deadline));
#else
         DWORD deadline = (DWORD) seconds * 1000;
         ::setsockopt(handle, SOL_SOCKET, SO_SNDTIMEO, (const char*) &deadline, sizeof(deadline));
         ::setsockopt(handle, SOL_SOCKET, SO_RCVTIMEO, (const char*) &deadline, sizeof(deadline));
#endif
      }

      // The Logger's wide message as the UTF-8 bytes RFC 5424 asks for. A
      // conversion that fails leaves the entry with an empty body rather than
      // with mojibake, and empties are visible in the collector.
      AnsiString ToUtf8_(const String &value)
      {
         AnsiString result;
         if (!Unicode::WideToMultiByte(value, result))
            return "";

         return result;
      }

#ifdef HM_PLATFORM_POSIX
      // One journal field, in the native protocol systemd's journald speaks on
      // that socket: "NAME=value\n" when the value holds no newline, and
      // "NAME\n" + a little-endian 64-bit length + the bytes + "\n" when it does.
      void AppendJournalField_(AnsiString &out, const char *name, const AnsiString &value)
      {
         if (value.Find("\n") < 0)
         {
            out += name;
            out += "=";
            out += value;
            out += "\n";
            return;
         }

         out += name;
         out += "\n";

         unsigned long long length = (unsigned long long) value.GetLength();
         for (int i = 0; i < 8; i++)
            out += (char) ((length >> (i * 8)) & 0xff);

         out += value;
         out += "\n";
      }
#endif
   }

   // Everything the transports need. Here rather than in the header so that
   // boost::asio::ssl and OpenSSL stay out of every translation unit that logs.
   struct SyslogSink::Connection
   {
      Connection() :
         udp(io),
         tcp(io),
         journal_socket(-1),
         connected(false)
      {
      }

      boost::asio::io_context io;
      boost::asio::ip::udp::socket udp;
      boost::asio::ip::udp::endpoint udp_endpoint;
      boost::asio::ip::tcp::socket tcp;
      std::unique_ptr<boost::asio::ssl::context> ssl_context;
      std::unique_ptr<boost::asio::ssl::stream<boost::asio::ip::tcp::socket> > tls;

      // The journal's AF_UNIX datagram socket, or -1. An int rather than a SOCKET
      // because this member only exists on the platform where it is used.
      int journal_socket;

      bool connected;
   };

   SyslogSink::SyslogSink() :
      enabled_(false),
      destination_(DestinationNone),
      port_(514),
      transport_(TransportUdp),
      facility_(SyslogFacilityMail),
      minimum_severity_(SyslogInformational),
      categories_(0),
      app_name_("hMailServer"),
      process_id_(0),
      backoff_seconds_(0),
      consecutive_failures_(0),
      running_(false),
      sent_count_(0),
      dropped_count_(0),
      reported_drop_count_(0)
   {

   }

   SyslogSink::~SyslogSink()
   {
      Stop();
   }

   void
   SyslogSink::Start()
   {
      // Reinitialize runs the whole start sequence again; reset cleanly each time.
      Stop();

      ApplySettings_();

      if (!enabled_)
         return;

      sent_count_ = 0;
      dropped_count_ = 0;
      reported_drop_count_ = 0;
      backoff_seconds_ = 0;
      consecutive_failures_ = 0;
      next_attempt_ = std::chrono::steady_clock::time_point();

      connection_.reset(new Connection());

      // Not enabled while the worker is being started: OnLogEntry excludes the
      // worker thread by its id, and that check must never run against an id that
      // has not been published yet.
      enabled_ = false;

      running_ = true;
      worker_ = std::thread(&SyslogSink::Run_, this);
      worker_thread_id_ = worker_.get_id();

      enabled_ = true;

      String message;
      if (destination_ == DestinationJournal)
      {
         message = _T("Syslog: writing log entries to the systemd journal.");
      }
      else
      {
         const TCHAR *transport = transport_ == TransportTls ? _T("TCP over TLS")
                                : transport_ == TransportTcp ? _T("TCP")
                                : _T("UDP");

         message.Format(_T("Syslog: sending RFC 5424 log entries to %s:%d over %s, facility %d."),
            String(host_).c_str(), port_, transport, facility_);
      }

      LOG_APPLICATION(message);
   }

   void
   SyslogSink::ApplySettings_()
   {
      enabled_ = false;
      destination_ = DestinationNone;

      std::shared_ptr<PropertySet> settings = Configuration::Instance()->GetSettings();
      if (!settings)
         return;

      if (!settings->GetBool(PROPERTY_SYSLOG_ENABLED))
         return;

      host_ = AnsiString(settings->GetString(PROPERTY_SYSLOG_HOST));
      host_.Trim();

      // Cast rather than assigned: long is 32 bits on Windows and 64 on Linux,
      // and these are ints because none of them is ever larger than a port number.
      port_ = (int) settings->GetLong(PROPERTY_SYSLOG_PORT);
      transport_ = (int) settings->GetLong(PROPERTY_SYSLOG_TRANSPORT);
      facility_ = (int) settings->GetLong(PROPERTY_SYSLOG_FACILITY);
      minimum_severity_ = (int) settings->GetLong(PROPERTY_SYSLOG_SEVERITY);
      categories_ = (int) settings->GetLong(PROPERTY_SYSLOG_LOGTYPES);

      if (transport_ < TransportUdp || transport_ > TransportTls)
         transport_ = TransportUdp;

      if (!SyslogFormatter::IsValidFacility(facility_))
         facility_ = SyslogFacilityMail;

      if (minimum_severity_ < SyslogEmergency || minimum_severity_ > SyslogDebug)
         minimum_severity_ = SyslogInformational;

      if (port_ <= 0 || port_ > 65535)
         port_ = transport_ == TransportTls ? 6514 : 514;

      // HOSTNAME: the name this server calls itself by in SMTP, which is the name
      // an administrator already correlates logs against. Falling back to the
      // machine's own name, and then to the NILVALUE, rather than inventing one.
      AnsiString hostName = AnsiString(Configuration::Instance()->GetHostName());
      if (hostName.IsEmpty())
      {
         try
         {
            hostName = boost::asio::ip::host_name().c_str();
         }
         catch (...)
         {
            hostName = "";
         }
      }

      hostname_ = hostName;
      app_name_ = "hMailServer";
      process_id_ = (int) GetCurrentProcessId();

      if (host_.IsEmpty())
      {
         // No collector. On Linux under systemd that is not a misconfiguration:
         // it is the ordinary case, and the journal is where the entries belong.
         if (RunningUnderSystemd_())
         {
            enabled_ = true;
            destination_ = DestinationJournal;
            return;
         }

         LOG_APPLICATION("Syslog: enabled, but no collector host is configured and this server is not running under systemd. Nothing will be sent.");
         return;
      }

      enabled_ = true;
      destination_ = DestinationCollector;
   }

   void
   SyslogSink::Stop()
   {
      if (!running_)
      {
         enabled_ = false;
         connection_.reset();
         return;
      }

      enabled_ = false;

      {
         std::lock_guard<std::mutex> lock(queue_mutex_);
         running_ = false;
      }
      queue_cv_.notify_all();

      if (worker_.joinable())
         worker_.join();

      CloseStream_();
      connection_.reset();
   }

   bool
   SyslogSink::SelectedCategory_(const String &category) const
   {
      // The error log is written whatever the log mask says, and so is this: an
      // error must reach a configured collector even on a server whose ordinary
      // logging is off. Mirrors Logger::LogError and the OTLP log exporter.
      if (category == _T("ERROR"))
         return true;

      int bit = 0;

      if (category == _T("SMTPD") || category == _T("SMTPC"))
         bit = Logger::LSSMTP;
      else if (category == _T("POP3D") || category == _T("POP3C"))
         bit = Logger::LSPOP3;
      else if (category == _T("IMAPD"))
         bit = Logger::LSIMAP;
      else if (category == _T("TCPIP"))
         bit = Logger::LSTCPIP;
      else if (category == _T("APPLICATION"))
         bit = Logger::LSApplication;
      else if (category == _T("DEBUG"))
         bit = Logger::LSDebug;
      else
         // A category nobody has a switch for. Sent, rather than silently lost:
         // a new category is a bug in this list, and a missing log line is the
         // hardest kind of bug to notice.
         return true;

      return (categories_ & bit) != 0;
   }

   void
   SyslogSink::OnLogEntry(const String &category, long thread, int session,
                          const String &remote_host, const String &time, const String &message)
   {
      if (!enabled_)
         return;

      // The worker's own failure lines must not be queued: a dead collector would
      // otherwise grow one failure entry per failed send, each describing the
      // failure of the one before it.
      if (std::this_thread::get_id() == worker_thread_id_)
         return;

      if (inside_sink)
         return;

      SinkReentryGuard guard;

      if (!SelectedCategory_(category))
         return;

      const int severity = SyslogFormatter::Severity(category, message);

      // Numerically lower is more severe. A minimum of 6 (info) sends the errors
      // and the ordinary record and leaves the debug chatter behind.
      if (severity > minimum_severity_)
         return;

      Entry entry;
      entry.category = category;
      entry.thread = thread;
      entry.session = session;
      entry.remote_host = remote_host;
      entry.time = time;
      entry.message = message;
      entry.severity = severity;

      {
         std::lock_guard<std::mutex> lock(queue_mutex_);

         // Bound the memory: drop the OLDEST. The newest entries describe what is
         // happening now, which is what somebody reading a collector wants.
         while (queue_.size() >= QueueLimit)
         {
            queue_.pop_front();
            dropped_count_++;
         }

         queue_.push_back(entry);
      }

      queue_cv_.notify_one();
   }

   unsigned int
   SyslogSink::GetSentCount() const
   {
      std::lock_guard<std::mutex> lock(queue_mutex_);
      return sent_count_;
   }

   unsigned int
   SyslogSink::GetDroppedCount() const
   {
      std::lock_guard<std::mutex> lock(queue_mutex_);
      return dropped_count_;
   }

   AnsiString
   SyslogSink::RenderForCollector_(const Entry &entry) const
   {
      SyslogRecord record;
      record.severity = entry.severity;
      record.timestamp = SyslogFormatter::Timestamp(entry.time, Time::GetUTCRelation());
      record.hostname = hostname_;
      record.app_name = app_name_;
      record.process_id = process_id_;
      record.message_id = SyslogFormatter::MessageId(entry.category, entry.message);
      record.thread = entry.thread;
      record.session = entry.session;
      record.remote_host = ToUtf8_(entry.remote_host);
      record.message = ToUtf8_(entry.message);

      const size_t limit = transport_ == TransportUdp
         ? SyslogFormatter::UdpByteLimit
         : SyslogFormatter::TcpByteLimit;

      return SyslogFormatter::Render(record, facility_, limit);
   }

   void
   SyslogSink::Run_()
   {
      for (;;)
      {
         std::vector<Entry> batch;
         bool stopping = false;

         {
            std::unique_lock<std::mutex> lock(queue_mutex_);
            queue_cv_.wait_for(lock, std::chrono::milliseconds(500),
               [this] { return !running_ || !queue_.empty(); });

            stopping = !running_;

            if (stopping && queue_.empty())
               break;

            while (!queue_.empty() && batch.size() < BatchLimit)
            {
               batch.push_back(queue_.front());
               queue_.pop_front();
            }
         }

         if (batch.empty())
         {
            // Nothing to send, but the outage may have ended in the meantime and
            // the summary is owed to the log either way.
            ReportDrops_();
            continue;
         }

         size_t index = 0;
         for (; index < batch.size(); index++)
         {
            const Entry &entry = batch[index];

            if (destination_ == DestinationJournal)
            {
               if (DeliverJournal_(entry))
               {
                  std::lock_guard<std::mutex> lock(queue_mutex_);
                  sent_count_++;
               }
               else
               {
                  std::lock_guard<std::mutex> lock(queue_mutex_);
                  dropped_count_++;
               }

               continue;
            }

            // While the back-off is in force nothing is attempted: paying a
            // connect timeout per entry is how a dead collector turns into a
            // worker that never drains its own queue.
            if (backoff_seconds_ > 0 && std::chrono::steady_clock::now() < next_attempt_)
               break;

            if (Deliver_(RenderForCollector_(entry), !stopping))
            {
               if (consecutive_failures_ > 0)
               {
                  String recovered;
                  recovered.Format(_T("Syslog: the collector at %s:%d is accepting entries again."),
                     String(host_).c_str(), port_);
                  LOG_APPLICATION(recovered);
               }

               consecutive_failures_ = 0;
               backoff_seconds_ = 0;

               std::lock_guard<std::mutex> lock(queue_mutex_);
               sent_count_++;
            }
            else
            {
               consecutive_failures_++;

               // One line when an outage starts. Not one per failure: a
               // collector that has been down for a day would otherwise write
               // the log it was meant to be reading - and not at all while the
               // server is stopping, where the only news is that a shutdown did
               // not dial a collector, which is the intended behaviour.
               if (consecutive_failures_ == 1 && !stopping)
               {
                  String failed;
                  failed.Format(_T("Syslog: the collector at %s:%d is not accepting entries. Retrying with a back-off; entries are being dropped. Mail delivery is unaffected and the log files are unchanged."),
                     String(host_).c_str(), port_);
                  LOG_APPLICATION(failed);
               }

               backoff_seconds_ = backoff_seconds_ == 0 ? 1 : backoff_seconds_ * 2;
               if (backoff_seconds_ > 60)
                  backoff_seconds_ = 60;

               next_attempt_ = std::chrono::steady_clock::now() + std::chrono::seconds(backoff_seconds_);
               break;
            }
         }

         // Whatever the failure stopped us sending is dropped rather than put
         // back: the queue is the buffer, and re-queueing would make the newest
         // entries the ones that fall off it.
         if (index < batch.size())
         {
            std::lock_guard<std::mutex> lock(queue_mutex_);
            dropped_count_ += (unsigned int) (batch.size() - index);
         }

         ReportDrops_();
      }

      CloseStream_();
   }

   void
   SyslogSink::ReportDrops_()
   {
      unsigned int dropped = 0;

      {
         std::lock_guard<std::mutex> lock(queue_mutex_);
         dropped = dropped_count_;
      }

      if (dropped == reported_drop_count_)
         return;

      // One line when the drops start, and then one more only once a minute, so
      // that an outage of hours is a handful of lines rather than a second log to
      // read. The worker writes it, and OnLogEntry ignores this thread, so it
      // cannot itself be queued.
      const std::chrono::steady_clock::time_point now = std::chrono::steady_clock::now();
      if (reported_drop_count_ > 0 && now < next_drop_report_)
         return;

      next_drop_report_ = now + std::chrono::seconds(60);
      reported_drop_count_ = dropped;

      String message;
      message.Format(_T("Syslog: %u log entries have been dropped because the collector is not accepting them. Mail delivery is unaffected."),
         dropped);
      LOG_APPLICATION(message);
   }

   bool
   SyslogSink::Deliver_(const AnsiString &message, bool allow_connect)
   {
      if (transport_ == TransportUdp)
         return DeliverUdp_(message, allow_connect);

      return DeliverStream_(message, allow_connect);
   }

   bool
   SyslogSink::DeliverUdp_(const AnsiString &message, bool allow_connect)
   {
      if (!connection_)
         return false;

      try
      {
         if (!connection_->connected)
         {
            if (!allow_connect)
               return false;

            boost::asio::ip::udp::resolver resolver(connection_->io);
            AnsiString portText;
            portText.Format("%d", port_);

            boost::asio::ip::udp::resolver::results_type endpoints =
               resolver.resolve(std::string(host_.c_str()), std::string(portText.c_str()));

            if (endpoints.empty())
               return false;

            connection_->udp_endpoint = *endpoints.begin();
            connection_->udp.open(connection_->udp_endpoint.protocol());
            SetSocketDeadline_(connection_->udp.native_handle(), 5);
            connection_->connected = true;
         }

         connection_->udp.send_to(
            boost::asio::buffer(message.c_str(), message.GetLength()),
            connection_->udp_endpoint);

         return true;
      }
      catch (...)
      {
         // A UDP socket does not learn that a collector is gone, so almost the
         // only failures here are a name that will not resolve and a local send
         // error. Either is worth closing and re-opening for.
         CloseStream_();
         return false;
      }
   }

   bool
   SyslogSink::DeliverStream_(const AnsiString &message, bool allow_connect)
   {
      if (!connection_)
         return false;

      if (!connection_->connected)
      {
         if (!allow_connect)
            return false;

         if (!OpenStream_())
            return false;
      }

      // RFC 5425 section 4.3: the length, a space, then the message. The same
      // framing RFC 6587 calls octet counting for plain TCP, so both stream
      // transports are framed the same way and a collector configured for one
      // reads the other.
      const AnsiString framed = SyslogFormatter::OctetCounted(message);

      try
      {
         if (connection_->tls)
            boost::asio::write(*connection_->tls, boost::asio::buffer(framed.c_str(), framed.GetLength()));
         else
            boost::asio::write(connection_->tcp, boost::asio::buffer(framed.c_str(), framed.GetLength()));

         return true;
      }
      catch (...)
      {
         CloseStream_();
         return false;
      }
   }

   bool
   SyslogSink::OpenStream_()
   {
      if (!connection_)
         return false;

      try
      {
         AnsiString portText;
         portText.Format("%d", port_);

         boost::asio::ip::tcp::resolver resolver(connection_->io);
         boost::asio::ip::tcp::resolver::results_type endpoints =
            resolver.resolve(std::string(host_.c_str()), std::string(portText.c_str()));

         if (transport_ == TransportTls)
         {
            connection_->ssl_context.reset(new boost::asio::ssl::context(boost::asio::ssl::context::tls_client));
            connection_->ssl_context->set_options(HM_TLS_CONTEXT_FLOOR);
            connection_->ssl_context->set_default_verify_paths();

            // honour_legacy_toggles false: a log collector is infrastructure of
            // this decade, not a 2008 mail client, so the TLS 1.0 and 1.1
            // switches that exist for old mail clients do not reach it.
            SslContextInitializer::InitClient(*connection_->ssl_context, false);

            connection_->tls.reset(new boost::asio::ssl::stream<boost::asio::ip::tcp::socket>(
               connection_->io, *connection_->ssl_context));

            boost::asio::connect(connection_->tls->next_layer(), endpoints);
            SetSocketDeadline_(connection_->tls->next_layer().native_handle(), 10);

            // The server's own verification, the same object the outbound mail
            // path and the HTTPS clients use: the chain against the machine's
            // root store, and the name checked against the host that was
            // configured. CSSSL rather than CSSTARTTLSOptional, so a failure ends
            // the handshake instead of being forgiven.
            connection_->tls->set_verify_mode(boost::asio::ssl::verify_peer);
            connection_->tls->set_verify_callback(CertificateVerifier(0, CSSSL, String(host_)));

            if (!SSL_set_tlsext_host_name(connection_->tls->native_handle(), host_.c_str()))
            {
               CloseStream_();
               return false;
            }

            connection_->tls->handshake(boost::asio::ssl::stream_base::client);
         }
         else
         {
            // A blocking connect, on the worker thread, exactly as HttpsClient and
            // OtelExportChannel do theirs. It is bounded by the operating system
            // rather than by us - a refused port answers at once, a host that drops
            // the SYN takes the stack's own timeout, about twenty seconds - and the
            // back-off above means it is attempted once per window rather than once
            // per entry. Nothing waits on it but this thread: no message is delayed,
            // and a shutdown never starts a new one (allow_connect is false then).
            boost::asio::connect(connection_->tcp, endpoints);
            SetSocketDeadline_(connection_->tcp.native_handle(), 10);
         }

         connection_->connected = true;
         return true;
      }
      catch (...)
      {
         CloseStream_();
         return false;
      }
   }

   void
   SyslogSink::CloseStream_()
   {
      if (!connection_)
         return;

      boost::system::error_code ignored;

      if (connection_->tls)
      {
         connection_->tls->next_layer().close(ignored);
         connection_->tls.reset();
      }

      connection_->ssl_context.reset();

      if (connection_->tcp.is_open())
         connection_->tcp.close(ignored);

      if (connection_->udp.is_open())
         connection_->udp.close(ignored);

#ifdef HM_PLATFORM_POSIX
      if (connection_->journal_socket >= 0)
      {
         ::close(connection_->journal_socket);
         connection_->journal_socket = -1;
      }
#endif

      connection_->connected = false;
   }

   bool
   SyslogSink::RunningUnderSystemd_()
   {
#ifdef HM_PLATFORM_POSIX
      struct stat details;
      if (::stat(JournalSocketPath, &details) != 0)
         return false;

      // The socket exists on any machine running systemd; these two say that THIS
      // process is one of its services. INVOCATION_ID is set for every unit;
      // JOURNAL_STREAM is set when the unit's own output goes to the journal.
      return ::getenv("INVOCATION_ID") != nullptr || ::getenv("JOURNAL_STREAM") != nullptr;
#else
      return false;
#endif
   }

   bool
   SyslogSink::DeliverJournal_(const Entry &entry)
   {
#ifdef HM_PLATFORM_POSIX
      if (!connection_)
         return false;

      if (connection_->journal_socket < 0)
      {
         connection_->journal_socket = ::socket(AF_UNIX, SOCK_DGRAM | SOCK_CLOEXEC, 0);
         if (connection_->journal_socket < 0)
            return false;

         // Never block the worker on a journal that is not reading. A datagram
         // that does not fit is dropped and counted, exactly as one refused by a
         // collector is.
         struct timeval deadline;
         deadline.tv_sec = 1;
         deadline.tv_usec = 0;
         ::setsockopt(connection_->journal_socket, SOL_SOCKET, SO_SNDTIMEO, &deadline, sizeof(deadline));
      }

      AnsiString payload;

      AnsiString value;

      // The same fields the collector gets, under the names journalctl already
      // knows - so "journalctl -u hmailserver -p err" works, and
      // "journalctl HMAILSERVER_SESSION=12" follows one conversation.
      AppendJournalField_(payload, "MESSAGE",
         SyslogFormatter::TruncateUtf8(ToUtf8_(entry.message), 8000));

      value.Format("%d", entry.severity);
      AppendJournalField_(payload, "PRIORITY", value);

      AppendJournalField_(payload, "SYSLOG_IDENTIFIER", app_name_);

      value.Format("%d", facility_);
      AppendJournalField_(payload, "SYSLOG_FACILITY", value);

      value.Format("%d", process_id_);
      AppendJournalField_(payload, "SYSLOG_PID", value);

      AppendJournalField_(payload, "HMAILSERVER_CATEGORY", AnsiString(entry.category));

      AppendJournalField_(payload, "HMAILSERVER_MSGID",
         SyslogFormatter::MessageId(entry.category, entry.message));

      value.Format("%ld", entry.thread);
      AppendJournalField_(payload, "HMAILSERVER_THREAD", value);

      if (entry.session >= 0)
      {
         value.Format("%d", entry.session);
         AppendJournalField_(payload, "HMAILSERVER_SESSION", value);
      }

      if (!entry.remote_host.IsEmpty())
         AppendJournalField_(payload, "HMAILSERVER_CLIENT", ToUtf8_(entry.remote_host));

      struct sockaddr_un address;
      memset(&address, 0, sizeof(address));
      address.sun_family = AF_UNIX;
      strncpy(address.sun_path, JournalSocketPath, sizeof(address.sun_path) - 1);

      const ssize_t written = ::sendto(connection_->journal_socket, payload.c_str(), payload.GetLength(),
         MSG_NOSIGNAL, (const struct sockaddr*) &address, sizeof(address));

      return written == (ssize_t) payload.GetLength();
#else
      (void) entry;
      return false;
#endif
   }
}
