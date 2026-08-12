// Copyright (c) 2026 hMailServer
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// Minimal HTTP endpoint exposing server statistics in the Prometheus
// text exposition format. See MetricsServer.h.

#include "StdAfx.h"

#include "MetricsServer.h"
#include "ServerStatus.h"
#include "AcmeClient.h"
#include "FileUtilities.h"

#include "../Application/Application.h"
#include "../Application/Configuration.h"
#include "../BO/Message.h"
#include "../BO/SSLCertificate.h"
#include "../BO/SSLCertificates.h"
#include "../SQL/DatabaseConnectionManager.h"
#include "../Persistence/PersistentMessage.h"
#include "../TCPIP/SocketConstants.h"

#include <ws2tcpip.h>

#include <openssl/bio.h>
#include <openssl/pem.h>
#include <openssl/x509.h>

#include <ctime>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // The StateSet layout for hmailserver_state: one series per state, so PromQL
      // selects a state by name instead of by magic number.
      struct StateSeries
      {
         const char *label;
         ServerStatus::ServerState state;
      };

      const StateSeries StateSeriesTable[] =
      {
         { "unknown",  ServerStatus::StateUnknown },
         { "stopped",  ServerStatus::StateStopped },
         { "starting", ServerStatus::StateStarting },
         { "running",  ServerStatus::StateRunning },
         { "stopping", ServerStatus::StateStopping }
      };

      // Converts an hMailServer system timestamp ("YYYY-MM-DD HH:MM:SS") to a Unix
      // timestamp. The column is written in local time by Time::GetCurrentDateTime,
      // so it is interpreted as local time here. Returns 0 for an empty, short or
      // unparseable value, so a NULL column reads as "unknown" rather than as 1970 -
      // which would otherwise show up as a 56-year-old message in the queue.
      __int64 SystemTimeStampToUnixTime(const String &timestamp)
      {
         if (timestamp.GetLength() < 19)
            return 0;

         tm parsed = {};
         parsed.tm_year = _ttoi(timestamp.Mid(0, 4).c_str()) - 1900;
         parsed.tm_mon = _ttoi(timestamp.Mid(5, 2).c_str()) - 1;
         parsed.tm_mday = _ttoi(timestamp.Mid(8, 2).c_str());
         parsed.tm_hour = _ttoi(timestamp.Mid(11, 2).c_str());
         parsed.tm_min = _ttoi(timestamp.Mid(14, 2).c_str());
         parsed.tm_sec = _ttoi(timestamp.Mid(17, 2).c_str());

         // -1 asks the CRT to work out whether daylight saving was in effect, which
         // is what a local-time column needs.
         parsed.tm_isdst = -1;

         if (parsed.tm_year < 70)
            return 0;

         time_t converted = mktime(&parsed);
         if (converted == static_cast<time_t>(-1))
            return 0;

         return static_cast<__int64>(converted);
      }
   }

   MetricsServer::MetricsServer() :
      listen_socket_(INVALID_SOCKET),
      running_(false),
      start_tick_count_(0),
      start_unix_time_(0),
      queue_cache_tick_(0),
      queue_depth_cache_value_(0),
      queue_oldest_age_cache_value_(0),
      certificate_cache_tick_(0)
   {

   }

   MetricsServer::~MetricsServer()
   {
      Stop();
   }

   bool
   MetricsServer::Start(const String &bind_address, int port)
   {
      if (running_)
         return true;

      AnsiString narrowBindAddress = bind_address;

      sockaddr_in address = {};
      address.sin_family = AF_INET;
      address.sin_port = htons(static_cast<unsigned short>(port));

      if (inet_pton(AF_INET, narrowBindAddress.c_str(), &address.sin_addr) != 1)
      {
         LOG_APPLICATION("MetricsServer: Invalid bind address: " + bind_address);
         return false;
      }

      listen_socket_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
      if (listen_socket_ == INVALID_SOCKET)
         return false;

      BOOL reuseAddress = TRUE;
      setsockopt(listen_socket_, SOL_SOCKET, SO_REUSEADDR, (const char*) &reuseAddress, sizeof(reuseAddress));

      if (bind(listen_socket_, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR ||
          listen(listen_socket_, 5) == SOCKET_ERROR)
      {
         String message;
         message.Format(_T("MetricsServer: Failed to bind to %s:%d."), bind_address.c_str(), port);
         LOG_APPLICATION(message);

         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
         return false;
      }

      start_tick_count_ = GetTickCount64();
      start_unix_time_ = static_cast<__int64>(time(nullptr));
      running_ = true;

      worker_ = std::thread(&MetricsServer::Run_, this);

      String message;
      message.Format(_T("MetricsServer: Listening on %s:%d (/metrics, /livez, /readyz, /healthz)."), bind_address.c_str(), port);
      LOG_APPLICATION(message);

      return true;
   }

   void
   MetricsServer::Stop()
   {
      if (!running_)
         return;

      running_ = false;

      if (listen_socket_ != INVALID_SOCKET)
      {
         closesocket(listen_socket_);
         listen_socket_ = INVALID_SOCKET;
      }

      if (worker_.joinable())
         worker_.join();
   }

   void
   MetricsServer::Run_()
   {
      for (;;)
      {
         SOCKET clientSocket = accept(listen_socket_, nullptr, nullptr);

         if (clientSocket == INVALID_SOCKET)
         {
            // The listen socket was closed (shutdown) or an error occurred.
            if (!running_)
               return;

            continue;
         }

         // This function is the top of a std::thread. There is nothing above it to
         // catch anything, so an exception that escaped here would be
         // std::terminate() - the entire mail server would die because a monitoring
         // scrape went wrong. Everything the handler touches is fenced off behind
         // this barrier: the database reads behind /metrics and /readyz, the
         // certificate files, and the string building. The socket is deliberately
         // closed out here rather than inside the handler, so the barrier firing
         // cannot leak a handle either.
         //
         // The catch blocks below only record what happened; they do not report it.
         // Reporting has to wait until after the socket is closed and has to be
         // fenced off in a barrier of its own, because ErrorManager::ReportError
         // reaches ScriptServer::FireEvent and so runs operator-supplied script. An
         // exception out of that script would otherwise escape this function - the
         // std::terminate() this barrier exists to prevent - and would skip the
         // close below, leaking a socket handle per failed scrape.
         bool exceptionEscaped = false;
         bool exceptionWasStandard = false;
         AnsiString exceptionText;

         try
         {
            HandleClient_(clientSocket);
         }
         catch (const std::exception &error)
         {
            exceptionEscaped = true;
            exceptionWasStandard = true;

            // Copying the text allocates, and the exception in hand may well be the
            // bad_alloc that says allocation is failing. A throw here would escape
            // the catch block itself, so it gets its own fence; the message is then
            // simply omitted.
            try
            {
               exceptionText = error.what();
            }
            catch (...)
            {
            }
         }
         catch (...)
         {
            exceptionEscaped = true;
         }

         shutdown(clientSocket, SD_SEND);
         closesocket(clientSocket);

         if (exceptionEscaped)
         {
            try
            {
               if (exceptionWasStandard)
               {
                  String description = _T("An exception escaped while serving a metrics/health request. The request was abandoned; the listener is still running.");

                  if (!exceptionText.IsEmpty())
                     description = Formatter::Format(_T("{0}, Message: {1}"), description, exceptionText);

                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5750, "MetricsServer::Run_", description);
               }
               else
               {
                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5750, "MetricsServer::Run_",
                     "A non-standard exception escaped while serving a metrics/health request. The request was abandoned; the listener is still running.");
               }
            }
            catch (...)
            {
               // Nothing left to do: reporting is what failed, and the alternative
               // to swallowing this is std::terminate() on the mail server.
            }
         }
      }
   }

   void
   MetricsServer::HandleClient_(SOCKET client_socket)
   {
      DWORD timeout = 5000;
      setsockopt(client_socket, SOL_SOCKET, SO_RCVTIMEO, (const char*) &timeout, sizeof(timeout));
      setsockopt(client_socket, SOL_SOCKET, SO_SNDTIMEO, (const char*) &timeout, sizeof(timeout));

      char requestBuffer[2048];
      int bytesReceived = recv(client_socket, requestBuffer, sizeof(requestBuffer) - 1, 0);

      if (bytesReceived <= 0)
         return;

      requestBuffer[bytesReceived] = '\0';

      AnsiString request(requestBuffer);
      AnsiString response;

      if (request.StartsWith("GET /metrics"))
      {
         AnsiString body = BuildMetricsBody_();

         AnsiString headers;
         headers.Format("HTTP/1.0 200 OK\r\nContent-Type: text/plain; version=0.0.4; charset=utf-8\r\nContent-Length: %d\r\nConnection: close\r\n\r\n", body.GetLength());

         response = headers + body;
      }
      else if (request.StartsWith("GET /livez"))
      {
         // Liveness: if we got here, the process and the listener thread are
         // responsive. No dependency checks (so a database outage does not cause
         // an orchestrator to kill an otherwise-healthy process).
         response = BuildHttpResponse_(200, "text/plain; charset=utf-8", "alive\n");
      }
      else if (request.StartsWith("GET /readyz"))
      {
         // Readiness: the server is running and the database is connected.
         AnsiString reason;
         bool ready = IsReady_(reason);
         response = BuildHttpResponse_(ready ? 200 : 503, "text/plain; charset=utf-8",
            ready ? AnsiString("ready\n") : ("not ready: " + reason + "\n"));
      }
      else if (request.StartsWith("GET /healthz"))
      {
         bool healthy = false;
         AnsiString body = BuildHealthBody_(healthy);
         response = BuildHttpResponse_(healthy ? 200 : 503, "application/json; charset=utf-8", body);
      }
      else
      {
         response = "HTTP/1.0 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
      }

      send(client_socket, response.c_str(), response.GetLength(), 0);
   }

   AnsiString
   MetricsServer::BuildMetricsBody_()
   {
      ServerStatus *status = ServerStatus::Instance();

      AnsiString body;

      AnsiString line;

      body += "# HELP hmailserver_processed_messages_total Number of messages processed since server start.\n";
      body += "# TYPE hmailserver_processed_messages_total counter\n";
      line.Format("hmailserver_processed_messages_total %d\n", status->GetNumberOfProcessedMessages());
      body += line;

      body += "# HELP hmailserver_spam_messages_total Number of detected spam messages since server start.\n";
      body += "# TYPE hmailserver_spam_messages_total counter\n";
      line.Format("hmailserver_spam_messages_total %d\n", status->GetNumberOfDetectedSpamMessages());
      body += line;

      body += "# HELP hmailserver_viruses_removed_total Number of viruses removed since server start.\n";
      body += "# TYPE hmailserver_viruses_removed_total counter\n";
      line.Format("hmailserver_viruses_removed_total %d\n", status->GetNumberOfRemovedViruses());
      body += line;

      body += "# HELP hmailserver_tls_handshakes_total Number of completed TLS/SSL handshakes since server start.\n";
      body += "# TYPE hmailserver_tls_handshakes_total counter\n";
      line.Format("hmailserver_tls_handshakes_total %d\n", status->GetNumberOfTlsHandshakesCompleted());
      body += line;

      body += "# HELP hmailserver_tls_handshake_failures_total Number of failed TLS/SSL handshakes since server start.\n";
      body += "# TYPE hmailserver_tls_handshake_failures_total counter\n";
      line.Format("hmailserver_tls_handshake_failures_total %d\n", status->GetNumberOfTlsHandshakeFailures());
      body += line;

      body += "# HELP hmailserver_auth_success_total Number of successful authentications since server start.\n";
      body += "# TYPE hmailserver_auth_success_total counter\n";
      line.Format("hmailserver_auth_success_total %d\n", status->GetNumberOfAuthenticationsSucceeded());
      body += line;

      body += "# HELP hmailserver_auth_failures_total Number of failed authentication attempts since server start.\n";
      body += "# TYPE hmailserver_auth_failures_total counter\n";
      line.Format("hmailserver_auth_failures_total %d\n", status->GetNumberOfAuthenticationFailures());
      body += line;

      body += "# HELP hmailserver_messages_delivered_total Number of message delivery passes that completed successfully.\n";
      body += "# TYPE hmailserver_messages_delivered_total counter\n";
      line.Format("hmailserver_messages_delivered_total %d\n", status->GetNumberOfMessagesDelivered());
      body += line;

      body += "# HELP hmailserver_messages_deferred_total Number of message delivery passes rescheduled for a later attempt (temporary failure).\n";
      body += "# TYPE hmailserver_messages_deferred_total counter\n";
      line.Format("hmailserver_messages_deferred_total %d\n", status->GetNumberOfMessagesDeferred());
      body += line;

      body += "# HELP hmailserver_messages_bounced_total Number of bounce/NDR messages generated for permanently failed recipients.\n";
      body += "# TYPE hmailserver_messages_bounced_total counter\n";
      line.Format("hmailserver_messages_bounced_total %d\n", status->GetNumberOfMessagesBounced());
      body += line;

      body += "# HELP hmailserver_sessions Current number of active sessions per protocol.\n";
      body += "# TYPE hmailserver_sessions gauge\n";
      line.Format("hmailserver_sessions{protocol=\"smtp\"} %d\n", status->GetNumberOfSessions(STSMTP));
      body += line;
      line.Format("hmailserver_sessions{protocol=\"imap\"} %d\n", status->GetNumberOfSessions(STIMAP));
      body += line;
      line.Format("hmailserver_sessions{protocol=\"pop3\"} %d\n", status->GetNumberOfSessions(STPOP3));
      body += line;

      // A start timestamp rather than an uptime counter, matching the
      // process_start_time_seconds convention every Prometheus client library
      // follows. Uptime is then time() - hmailserver_start_time_seconds, and a
      // restart is a step change that changes() detects, rather than a reset to zero
      // that looks the same as a counter rollover.
      body += "# HELP hmailserver_start_time_seconds Unix timestamp at which the metrics listener started. Uptime is time() - hmailserver_start_time_seconds; restarts are visible as changes(hmailserver_start_time_seconds[1h]).\n";
      body += "# TYPE hmailserver_start_time_seconds gauge\n";
      line.Format("hmailserver_start_time_seconds %I64d\n", start_unix_time_);
      body += line;

      // Build identity via the _info convention: value always 1, everything of
      // interest in the labels. Both label values come from memory. The schema
      // version is the compiled-in REQUIRED_DB_VERSION rather than a read of
      // hm_dbversion, because Application::OnDatabaseConnected refuses to start the
      // server when the two disagree - so the constant is the running schema version
      // by construction, and this avoids putting a database read (and therefore an
      // exception source) on the listener thread once per scrape.
      body += "# HELP hmailserver_build_info Build identity of the running server. The value is always 1; the information is in the labels.\n";
      body += "# TYPE hmailserver_build_info gauge\n";
      line.Format("hmailserver_build_info{version=\"%s\",architecture=\"%s\",database_schema_version=\"%d\"} 1\n",
         EscapeLabelValue_(Application::Instance()->GetVersionNumber()).c_str(),
         EscapeLabelValue_(Application::Instance()->GetVersionArchitecture()).c_str(),
         Configuration::Instance()->GetRequiredDBVersion());
      body += line;

      // OpenMetrics models an enumeration as a StateSet: one series per state
      // carrying a boolean, so exactly one series reads 1 and an alert can say
      // hmailserver_state{state="running"} == 0 instead of encoding a magic number.
      // The series layout below is that StateSet layout. The declared type stays
      // "gauge" on purpose: this endpoint serves the Prometheus text format
      // (version=0.0.4), whose parser rejects an unrecognised type name outright, so
      // declaring "stateset" here would make Prometheus discard the whole scrape and
      // every other metric with it.
      int state = status->GetState();

      body += "# HELP hmailserver_state Current server state, one series per state (OpenMetrics StateSet layout): exactly one carries 1.\n";
      body += "# TYPE hmailserver_state gauge\n";

      for (const StateSeries &entry : StateSeriesTable)
      {
         line.Format("hmailserver_state{state=\"%s\"} %d\n", entry.label, state == entry.state ? 1 : 0);
         body += line;
      }

      // Database connectivity and connection-pool saturation. Named _connected
      // rather than _up: Prometheus synthesises its own "up" series per target, and
      // an operator reading "up" next to "hmailserver_database_up" on a dashboard has
      // no way to tell a scrape failure from a database failure.
      std::shared_ptr<DatabaseConnectionManager> db_manager = Application::Instance()->GetDBManager();
      bool database_up = db_manager && db_manager->GetIsConnected();

      body += "# HELP hmailserver_database_connected Whether the database connection pool reports at least one live connection (1=connected, 0=not connected).\n";
      body += "# TYPE hmailserver_database_connected gauge\n";
      line.Format("hmailserver_database_connected %d\n", database_up ? 1 : 0);
      body += line;

      int db_busy = db_manager ? db_manager->GetBusyConnectionCount() : 0;
      int db_available = db_manager ? db_manager->GetAvailableConnectionCount() : 0;

      body += "# HELP hmailserver_db_connections Number of database connections in the pool by state.\n";
      body += "# TYPE hmailserver_db_connections gauge\n";
      line.Format("hmailserver_db_connections{state=\"busy\"} %d\n", db_busy);
      body += line;
      line.Format("hmailserver_db_connections{state=\"available\"} %d\n", db_available);
      body += line;

      UpdateDeliveryQueueCache_();

      body += "# HELP hmailserver_delivery_queue_messages Number of messages currently in the SMTP delivery queue.\n";
      body += "# TYPE hmailserver_delivery_queue_messages gauge\n";
      line.Format("hmailserver_delivery_queue_messages %d\n", queue_depth_cache_value_);
      body += line;

      // Depth on its own cannot tell a burst from a stuck relay: a deep queue whose
      // oldest message is seconds old is a busy server draining normally, while a
      // shallow queue whose oldest message is hours old is a destination nobody is
      // getting mail to. This is the second half of that pair.
      body += "# HELP hmailserver_delivery_queue_oldest_message_age_seconds Age of the oldest message in the SMTP delivery queue, 0 when the queue is empty or the age is unknown.\n";
      body += "# TYPE hmailserver_delivery_queue_oldest_message_age_seconds gauge\n";
      line.Format("hmailserver_delivery_queue_oldest_message_age_seconds %I64d\n", queue_oldest_age_cache_value_);
      body += line;

      body += "# HELP hmailserver_messagestore_missing_files Number of messages whose backing file was missing on disk at the last consistency check (0 when consistent or the check is disabled).\n";
      body += "# TYPE hmailserver_messagestore_missing_files gauge\n";
      line.Format("hmailserver_messagestore_missing_files %d\n", status->GetMessageStoreMissingFiles());
      body += line;

      // Histograms, not summaries. Both families used to be declared "summary" while
      // emitting only _sum and _count, which meant the only thing derivable from them
      // was a mean - and a mean latency hides exactly the tail an operator cares
      // about. The bucket bounds live in ServerStatus and the buckets are filled on
      // the observation path, so the declared type is a histogram from the very first
      // scrape whether or not any traffic has arrived yet. Note that _sum and _count
      // keep their existing names and meaning, so anything already computing a mean
      // from them keeps working.
      body += "# HELP hmailserver_command_processing_seconds Processing time of client protocol command lines (SMTP/IMAP/POP3).\n";
      body += "# TYPE hmailserver_command_processing_seconds histogram\n";
      body += BuildHistogramSeries_("hmailserver_command_processing_seconds",
         ServerStatus::GetCommandLatencyBucketBounds(), status->GetCommandLatencySnapshot());

      body += "# HELP hmailserver_db_query_seconds Execution time of database statements run through the connection manager (all backends).\n";
      body += "# TYPE hmailserver_db_query_seconds histogram\n";
      body += BuildHistogramSeries_("hmailserver_db_query_seconds",
         ServerStatus::GetDatabaseLatencyBucketBounds(), status->GetDatabaseLatencySnapshot());

      body += "# HELP hmailserver_db_slow_queries_total Database statements whose execution time exceeded SlowQueryLogMilliseconds (0 when the threshold is disabled).\n";
      body += "# TYPE hmailserver_db_slow_queries_total counter\n";
      line.Format("hmailserver_db_slow_queries_total %I64u\n", status->GetDatabaseSlowQueriesCount());
      body += line;

      body += BuildCertificateExpiryMetrics_();

      return body;
   }

   AnsiString
   MetricsServer::BuildHistogramSeries_(const char *name, const std::vector<double> &bounds,
      const ServerStatus::LatencySnapshot &snapshot)
   {
      AnsiString series;

      // ServerStatus sizes the bucket vector to bounds.size() + 1. The guard is here
      // so that if the two sides ever drift apart, this emits nothing rather than a
      // malformed histogram family - a truncated bucket list would make
      // histogram_quantile() return confident nonsense.
      if (snapshot.cumulative_buckets.size() != bounds.size() + 1)
         return series;

      AnsiString line;

      for (size_t i = 0; i < bounds.size(); i++)
      {
         line.Format("%s_bucket{le=\"%.6g\"} %I64u\n", name, bounds[i], snapshot.cumulative_buckets[i]);
         series += line;
      }

      // The +Inf bucket is the total, and _count is emitted from that same value
      // rather than read separately, so the two can never disagree.
      unsigned __int64 total = snapshot.cumulative_buckets[bounds.size()];

      line.Format("%s_bucket{le=\"+Inf\"} %I64u\n", name, total);
      series += line;

      line.Format("%s_sum %.6f\n", name, static_cast<double>(snapshot.microseconds_total) / 1000000.0);
      series += line;

      line.Format("%s_count %I64u\n", name, total);
      series += line;

      return series;
   }

   void
   MetricsServer::UpdateDeliveryQueueCache_()
   {
      // Cache the delivery-queue figures for a few seconds so frequent scrapes do
      // not issue aggregate queries against hm_messages on every request. Clients are
      // handled serially on the listener thread, so no extra locking is needed.
      const ULONGLONG cacheWindowMs = 10000;
      ULONGLONG now = GetTickCount64();

      if (queue_cache_tick_ != 0 && (now - queue_cache_tick_) < cacheWindowMs)
         return;

      // Stamped before the reads, not after, so a database that cannot answer is
      // retried on the same cadence rather than on every single scrape.
      queue_cache_tick_ = now;

      std::shared_ptr<DatabaseConnectionManager> db_manager = Application::Instance()->GetDBManager();
      if (!db_manager || !db_manager->GetIsConnected())
         return;

      try
      {
         queue_depth_cache_value_ = PersistentMessage::GetDeliveryQueueCount();

         // min() over a "YYYY-MM-DD HH:MM:SS" column: the format sorts
         // lexicographically the same way it sorts chronologically, so this is one
         // portable aggregate rather than a per-backend date function.
         SQLCommand command("select min(messagecreatetime) as oldest from hm_messages where messagetype = @MESSAGETYPE");
         command.AddParameter("@MESSAGETYPE", Message::Delivering);

         std::shared_ptr<DALRecordset> recordset = db_manager->OpenRecordset(command);

         queue_oldest_age_cache_value_ = 0;

         if (recordset && !recordset->IsEOF())
         {
            __int64 oldestUnixTime = SystemTimeStampToUnixTime(recordset->GetStringValue("oldest"));

            if (oldestUnixTime > 0)
            {
               __int64 age = static_cast<__int64>(time(nullptr)) - oldestUnixTime;

               // Clamped at zero: a clock adjustment must not produce a negative age,
               // which would read as a message from the future and break any alert
               // expressed as a threshold.
               queue_oldest_age_cache_value_ = age > 0 ? age : 0;
            }
         }
      }
      catch (...)
      {
         // Serve the previous values rather than losing the whole scrape. Every other
         // metric on this endpoint is more useful than these two, and a database that
         // cannot answer is already reported by hmailserver_database_connected - so
         // this is a debug note, not an error: it is not separately actionable and it
         // would repeat on every cache refresh for as long as the outage lasted.
         LOG_DEBUG("MetricsServer: Delivery-queue metrics could not be refreshed. Serving the previously cached values.");
      }
   }

   AnsiString
   MetricsServer::BuildCertificateExpiryMetrics_()
   {
      // Cached for minutes: a certificate's notAfter only changes when the
      // certificate is replaced, and re-reading and re-parsing every configured PEM
      // on every scrape would put avoidable file I/O on the listener thread.
      const ULONGLONG cacheWindowMs = 300000;
      ULONGLONG now = GetTickCount64();

      if (certificate_cache_tick_ != 0 && (now - certificate_cache_tick_) < cacheWindowMs)
         return certificate_cache_body_;

      certificate_cache_tick_ = now;

      // Keyed by the label value, because the label value is what makes a series
      // unique. sslcertificatename has no unique constraint in any of the schemas, so
      // two certificates can legitimately share a name; emitting one series per
      // certificate row would then emit the same series twice, and Prometheus rejects
      // an entire scrape that contains a duplicate series - taking every other metric
      // on this endpoint down with it. Where a name is shared, the earliest expiry
      // wins, because that is the one worth alerting on.
      std::map<AnsiString, __int64> earliestExpiryByName;

      try
      {
         int count = 0;

         auto addCertificate = [&earliestExpiryByName, &count](const String &name, const String &certificateFile)
         {
            __int64 notAfter = ReadCertificateExpiry_(certificateFile);
            if (notAfter == 0)
               return;

            AnsiString label = EscapeLabelValue_(name);

            auto existing = earliestExpiryByName.find(label);
            if (existing == earliestExpiryByName.end() || notAfter < existing->second)
               earliestExpiryByName[label] = notAfter;

            count++;
         };

         std::shared_ptr<SSLCertificates> certificates = Configuration::Instance()->GetSSLCertificates();

         int certificateCount = certificates ? certificates->GetCount() : 0;

         for (int i = 0; i < certificateCount; i++)
         {
            std::shared_ptr<SSLCertificate> certificate = certificates->GetItem(i);
            if (!certificate)
               continue;

            addCertificate(certificate->GetName(), certificate->GetCertificateFile());
         }

         // No configured certificate row means the ACME install shape, where the
         // certificate lives on disk under the ACME directory and is never entered in
         // the certificate table - the same fallback RestApiServer applies for the
         // TLSA endpoint. Without it the metric is silent on exactly the installs
         // where a silent renewal failure is the thing worth alerting on. Nothing is
         // emitted when that file does not exist either, so the shipped default (no
         // certificate, no ACME) still produces no series and no diagnostic.
         if (count == 0)
            addCertificate(_T("ACME (automatic)"), AcmeClient::GetCertificateDirectory() + _T("\\fullchain.pem"));
      }
      catch (...)
      {
         LOG_DEBUG("MetricsServer: TLS certificate expiry metrics could not be built. Omitting them from this scrape.");
      }

      AnsiString body;

      // Nothing is emitted when no certificate is configured, which is the shipped
      // default - an empty metric family would only invite an alert on absent data.
      if (!earliestExpiryByName.empty())
      {
         body += "# HELP hmailserver_tls_certificate_expiry_seconds Unix timestamp at which a configured TLS certificate expires. Alert on hmailserver_tls_certificate_expiry_seconds - time() falling below your renewal window.\n";
         body += "# TYPE hmailserver_tls_certificate_expiry_seconds gauge\n";

         AnsiString line;
         for (const auto &entry : earliestExpiryByName)
         {
            line.Format("hmailserver_tls_certificate_expiry_seconds{certificate=\"%s\"} %I64d\n",
               entry.first.c_str(), entry.second);
            body += line;
         }
      }

      certificate_cache_body_ = body;

      return body;
   }

   __int64
   MetricsServer::ReadCertificateExpiry_(const String &certificate_file)
   {
      if (certificate_file.IsEmpty() || !FileUtilities::Exists(certificate_file))
         return 0;

      AnsiString narrowFileName = certificate_file;

      BIO *bio = BIO_new_file(narrowFileName.c_str(), "r");
      if (bio == nullptr)
         return 0;

      X509 *certificate = PEM_read_bio_X509(bio, nullptr, nullptr, nullptr);
      BIO_free(bio);

      if (certificate == nullptr)
      {
         // Debug level rather than an application-log line or a reported error: a
         // certificate the server cannot parse already fails loudly where it matters,
         // at the handshake, and repeating it here on every cache refresh would fill
         // the log without telling the operator anything new.
         LOG_DEBUG("MetricsServer: Could not parse certificate " + certificate_file + " while building the expiry metric.");
         return 0;
      }

      __int64 expiry = 0;

      tm notAfter = {};
      if (ASN1_TIME_to_tm(X509_get0_notAfter(certificate), &notAfter) == 1)
      {
         // notAfter is UTC, so it converts with _mkgmtime rather than mktime.
         time_t converted = _mkgmtime(&notAfter);
         if (converted != static_cast<time_t>(-1))
            expiry = static_cast<__int64>(converted);
      }

      X509_free(certificate);

      return expiry;
   }

   AnsiString
   MetricsServer::EscapeLabelValue_(const String &value)
   {
      // The exposition format escapes backslash, double quote and line feed in a
      // label value. Certificate names are operator-supplied text, and a single
      // unescaped quote in one would produce a malformed line and cost the whole
      // scrape. Backslash is replaced first, or it would double the escapes the
      // later replacements introduce.
      AnsiString escaped = value;

      escaped.Replace("\\", "\\\\");
      escaped.Replace("\"", "\\\"");
      escaped.Replace("\r", "");
      escaped.Replace("\n", "\\n");

      return escaped;
   }

   AnsiString
   MetricsServer::BuildHttpResponse_(int status_code, const char *content_type, const AnsiString &body)
   {
      const char *reason;
      switch (status_code)
      {
      case 200: reason = "OK"; break;
      case 404: reason = "Not Found"; break;
      case 503: reason = "Service Unavailable"; break;
      default:  reason = "OK"; break;
      }

      AnsiString headers;
      headers.Format("HTTP/1.0 %d %s\r\nContent-Type: %s\r\nContent-Length: %d\r\nConnection: close\r\n\r\n",
         status_code, reason, content_type, body.GetLength());

      return headers + body;
   }

   bool
   MetricsServer::IsReady_(AnsiString &reason)
   {
      if (ServerStatus::Instance()->GetState() != ServerStatus::StateRunning)
      {
         reason = "server not in running state";
         return false;
      }

      std::shared_ptr<DatabaseConnectionManager> db_manager = Application::Instance()->GetDBManager();
      if (!db_manager || !db_manager->GetIsConnected())
      {
         reason = "database not connected";
         return false;
      }

      return true;
   }

   AnsiString
   MetricsServer::BuildHealthBody_(bool &healthy)
   {
      ServerStatus *status = ServerStatus::Instance();
      int state = status->GetState();
      bool running = state == ServerStatus::StateRunning;

      std::shared_ptr<DatabaseConnectionManager> db_manager = Application::Instance()->GetDBManager();
      bool database_up = db_manager && db_manager->GetIsConnected();

      healthy = running && database_up;

      ULONGLONG uptime_seconds = (GetTickCount64() - start_tick_count_) / 1000;

      const char *state_name =
         state == ServerStatus::StateRunning ? "running" :
         state == ServerStatus::StateStarting ? "starting" :
         state == ServerStatus::StateStopping ? "stopping" :
         state == ServerStatus::StateStopped ? "stopped" : "unknown";

      AnsiString body;
      body.Format(
         "{\"status\":\"%s\",\"state\":\"%s\",\"database\":\"%s\","
         "\"sessions\":{\"smtp\":%d,\"imap\":%d,\"pop3\":%d},"
         "\"uptime_seconds\":%I64u}\n",
         healthy ? "ok" : "unavailable",
         state_name,
         database_up ? "up" : "down",
         status->GetNumberOfSessions(STSMTP),
         status->GetNumberOfSessions(STIMAP),
         status->GetNumberOfSessions(STPOP3),
         uptime_seconds);

      return body;
   }
}
