// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"

#include "RecipientCallout.h"

#include "../Common/BO/RemoteDomainPolicy.h"
#include "../Common/Cache/CacheContainer.h"
#include "../Common/TCPIP/DNSResolver.h"
#include "../Common/TCPIP/HostNameAndIpAddress.h"
#include "../Common/TCPIP/IPAddress.h"
#include "../Common/TCPIP/LocalIPAddresses.h"
#include "../Common/Util/Parsing/StringParser.h"
#include "../Common/Util/RateLimiter.h"
#include "../Common/Util/Utilities.h"

#include <boost/asio.hpp>

#include <chrono>
#include <string>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // Enough for a long multi-line greeting and no more. A remote that keeps
      // talking past this is not answering the question.
      const size_t MaxReplyBytes = 8 * 1024;

      // One SMTP conversation, bounded by a single wall-clock deadline rather
      // than by a per-read socket option.
      //
      // SO_RCVTIMEO is what the rest of this tree uses for its blocking clients,
      // and it takes a DWORD of milliseconds on Windows and a struct timeval on
      // Linux - the same call with the same argument is a working timeout on one
      // and an EINVAL that sets nothing on the other. A timeout that exists only
      // on Windows would mean a Linux server holding an inbound SMTP session open
      // for as long as a remote cared to keep the socket alive, which is the one
      // thing this must not do. Asynchronous operations against a private
      // io_context, driven by run_for with the time that is left, behave the same
      // on both.
      class BoundedSmtpProbe
      {
      public:
         BoundedSmtpProbe(int timeoutSeconds) :
            socket_(io_context_),
            deadline_(std::chrono::steady_clock::now() + std::chrono::seconds(timeoutSeconds))
         {
         }

         ~BoundedSmtpProbe()
         {
            boost::system::error_code ignored;
            socket_.close(ignored);
         }

         bool Connect(const boost::asio::ip::tcp::endpoint &endpoint)
         {
            bool done = false;
            boost::system::error_code result;

            socket_.async_connect(endpoint, [&done, &result](const boost::system::error_code &error)
            {
               done = true;
               result = error;
            });

            return Run_(done) && !result;
         }

         bool Send(const AnsiString &line)
         {
            std::string payload(line.c_str(), (size_t) line.GetLength());
            payload += "\r\n";

            bool done = false;
            boost::system::error_code result;

            boost::asio::async_write(socket_, boost::asio::buffer(payload),
               [&done, &result](const boost::system::error_code &error, size_t)
            {
               done = true;
               result = error;
            });

            return Run_(done) && !result;
         }

         // One complete SMTP reply, multi-line included (RFC 5321 4.2.1: a
         // continuation line has '-' as its fourth character). Returns the reply
         // code, or 0 when the conversation broke or the deadline passed.
         int ReadReply(AnsiString &text)
         {
            for (;;)
            {
               int code = CompleteReply_(text);
               if (code != 0)
                  return code;

               if (buffer_.size() > MaxReplyBytes)
                  return 0;

               bool done = false;
               boost::system::error_code result;
               size_t read = 0;

               socket_.async_read_some(boost::asio::buffer(chunk_, sizeof(chunk_)),
                  [&done, &result, &read](const boost::system::error_code &error, size_t bytes)
               {
                  done = true;
                  result = error;
                  read = bytes;
               });

               if (!Run_(done))
                  return 0;

               if (result || read == 0)
                  return 0;

               buffer_.append(chunk_, read);
            }
         }

      private:

         // Runs the one outstanding operation until it completes or the deadline
         // passes. On a timeout the socket is closed, which cancels it - the
         // handler then runs with operation_aborted against locals that are
         // still alive, because everything here outlives the io_context.
         bool Run_(bool &done)
         {
            for (;;)
            {
               auto remaining = deadline_ - std::chrono::steady_clock::now();

               if (remaining <= std::chrono::steady_clock::duration::zero())
                  break;

               io_context_.restart();
               io_context_.run_for(remaining);

               if (done)
                  return true;

               if (io_context_.stopped())
                  break;
            }

            boost::system::error_code ignored;
            socket_.close(ignored);

            io_context_.restart();
            io_context_.run_for(std::chrono::milliseconds(50));

            return false;
         }

         // The code of the first complete reply in the buffer, consuming it; 0
         // when the buffer does not hold one yet.
         int CompleteReply_(AnsiString &text)
         {
            size_t start = 0;

            for (;;)
            {
               size_t end = buffer_.find("\r\n", start);

               if (end == std::string::npos)
                  return 0;

               std::string line = buffer_.substr(start, end - start);

               if (line.size() >= 4 && line[3] == '-')
               {
                  // A continuation line; the reply is not complete yet.
                  start = end + 2;
                  continue;
               }

               if (line.size() < 3)
                  return 0;

               text = buffer_.substr(0, end).c_str();
               buffer_ = buffer_.substr(end + 2);

               return atoi(line.substr(0, 3).c_str());
            }
         }

         boost::asio::io_context io_context_;
         boost::asio::ip::tcp::socket socket_;
         std::chrono::steady_clock::time_point deadline_;
         std::string buffer_;
         char chunk_[2048];
      };
   }

   RecipientCallout::RecipientCallout() :
      probe_count_(0)
   {

   }

   RecipientCallout::~RecipientCallout()
   {

   }

   RecipientCallout::Verdict
   RecipientCallout::Verify(const String &recipientAddress, std::shared_ptr<RemoteDomainPolicy> policy, String &reason)
   {
      reason = _T("");

      if (!policy || !policy->GetCalloutEnabled())
         return CalloutUnknown;

      String address = recipientAddress;
      address.Trim();
      address.ToLower();

      if (address.IsEmpty())
         return CalloutUnknown;

      const String domain = StringParser::ExtractDomain(address).ToLower();

      if (domain.IsEmpty())
         return CalloutUnknown;

      // Never for a domain this server is authoritative for. The primary for
      // such a domain is this server: the accounts, the aliases and the lists
      // are already here and RecipientParser has already read them, so a callout
      // could only ask this server about itself - on the thread that would have
      // to answer.
      if (CacheContainer::Instance()->GetDomain(domain))
         return CalloutUnknown;

      const time_t now = time(0);

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         auto cached = cache_.find(address);

         if (cached != cache_.end())
         {
            if (cached->second.expires_at > now)
            {
               reason = cached->second.reason;
               return cached->second.verdict;
            }

            cache_.erase(cached);
         }
      }

      // The ceiling on how often one remote is asked. Reaching it is OUR
      // condition, so it accepts: refusing somebody else's mail because this
      // server had already asked its primary ten times this minute would turn a
      // protection into an outage.
      int maxPerMinute = (int) policy->GetCalloutMaxPerMinute();

      if (maxPerMinute > 0 && !RateLimiter::Instance()->TryConsume(_T("callout:") + domain, maxPerMinute))
      {
         LOG_DEBUG("Recipient callout for " + address + " skipped: the per-minute ceiling for " + domain + " is reached.");
         return CalloutUnknown;
      }

      int timeoutSeconds = (int) policy->GetCalloutTimeoutSeconds();
      if (timeoutSeconds <= 0)
         timeoutSeconds = 10;
      if (timeoutSeconds > CalloutMaxTimeoutSeconds)
         timeoutSeconds = CalloutMaxTimeoutSeconds;

      int port = (int) policy->GetCalloutPort();
      if (port <= 0 || port > 65535)
         port = 25;

      std::vector<std::pair<String, String> > targets = ResolveTargets_(domain, policy, port);

      if (targets.empty())
      {
         LOG_APPLICATION("Recipient callout for " + address + " could not be made: no server to ask for " + domain + ". The recipient is accepted.");
         return CalloutUnknown;
      }

      Verdict verdict = CalloutUnknown;
      String verdictReason;

      for (const std::pair<String, String> &target : targets)
      {
         {
            boost::lock_guard<boost::recursive_mutex> guard(mutex_);
            probe_count_++;
         }

         String probeReason;
         Verdict answer = Probe_(target.second, port, address, timeoutSeconds, probeReason);

         if (answer == CalloutAccepted || answer == CalloutRejected)
         {
            verdict = answer;
            verdictReason = probeReason;
            break;
         }

         // Unknown from this host: try the next one, which is what an MX list is
         // for. If every host is Unknown the answer is Unknown, and Unknown
         // accepts.
         verdictReason = probeReason;
      }

      {
         boost::lock_guard<boost::recursive_mutex> guard(mutex_);

         long cacheMinutes = policy->GetCalloutCacheMinutes();

         // An Unknown is cached for a minute at most whatever the policy says:
         // it is not a fact about the address, it is a fact about a moment, and
         // remembering "I could not reach the primary" for an hour would keep
         // accepting for an hour after the primary came back.
         long minutes = verdict == CalloutUnknown ? 1 : cacheMinutes;

         if (minutes > 0)
         {
            CachedVerdict entry;
            entry.verdict = verdict;
            entry.reason = verdictReason;
            entry.expires_at = now + (time_t) minutes * 60;

            cache_[address] = entry;
         }
      }

      reason = verdictReason;

      if (verdict == CalloutRejected)
         LOG_APPLICATION("Recipient callout: " + domain + " does not accept " + address + ". " + verdictReason);
      else if (verdict == CalloutAccepted)
         LOG_DEBUG("Recipient callout: " + domain + " accepts " + address + ".");
      else
         LOG_APPLICATION("Recipient callout for " + address + " gave no verdict (" + verdictReason + "). The recipient is accepted.");

      return verdict;
   }

   std::vector<std::pair<String, String> >
   RecipientCallout::ResolveTargets_(const String &domain, std::shared_ptr<RemoteDomainPolicy> policy, int port)
   {
      std::vector<std::pair<String, String> > targets;

      DNSResolver resolver;
      String configuredHost = policy->GetCalloutHost();
      configuredHost.Trim();

      std::vector<HostNameAndIpAddress> candidates;

      if (!configuredHost.IsEmpty())
      {
         std::vector<String> addresses;
         resolver.GetIpAddresses(configuredHost, addresses, true);

         for (const String &address : addresses)
         {
            HostNameAndIpAddress candidate;
            candidate.SetHostName(configuredHost);
            candidate.SetIpAddress(address);
            candidates.push_back(candidate);
         }
      }
      else
      {
         // The domain's own MX hosts, in preference order - which for a backup
         // MX means the primary first and this server somewhere below it.
         resolver.GetEmailServers(domain, candidates);
      }

      for (HostNameAndIpAddress candidate : candidates)
      {
         IPAddress parsed;

         if (!parsed.TryParse(AnsiString(candidate.GetIpAddress()), false))
            continue;

         // The loop guard, and the reason it is an address-and-port test rather
         // than a name test: a backup MX's own name IS one of the domain's MX
         // records, so asking the MX list without this would have the server
         // open an SMTP session to itself from inside an SMTP session, on the
         // thread that has to answer it.
         if (LocalIPAddresses::Instance()->IsLocalPort(parsed, port))
         {
            LOG_DEBUG("Recipient callout: skipping " + candidate.GetHostName() + " (" + candidate.GetIpAddress() + ") - it is this server.");
            continue;
         }

         targets.push_back(std::make_pair(candidate.GetHostName(), candidate.GetIpAddress()));

         // Two hosts at most. The point is to ask the primary, not to survey the
         // domain's infrastructure while a sender waits on the RCPT TO.
         if (targets.size() >= 2)
            break;
      }

      return targets;
   }

   RecipientCallout::Verdict
   RecipientCallout::Probe_(const String &host, int port, const String &address, int timeoutSeconds, String &reason)
   {
      boost::system::error_code parseError;
      boost::asio::ip::address remoteAddress =
         boost::asio::ip::make_address(std::string(AnsiString(host).c_str()), parseError);

      if (parseError)
      {
         reason = _T("the address of the verification server could not be used");
         return CalloutUnknown;
      }

      try
      {
         BoundedSmtpProbe probe(timeoutSeconds);

         boost::asio::ip::tcp::endpoint endpoint(remoteAddress, (unsigned short) port);

         if (!probe.Connect(endpoint))
         {
            reason = _T("the verification server could not be reached");
            return CalloutUnknown;
         }

         AnsiString text;

         if (probe.ReadReply(text) / 100 != 2)
         {
            reason = _T("the verification server did not greet");
            return CalloutUnknown;
         }

         if (!probe.Send("EHLO " + AnsiString(Utilities::ComputerName())))
         {
            reason = _T("the verification session ended during EHLO");
            return CalloutUnknown;
         }

         if (probe.ReadReply(text) / 100 != 2)
         {
            // A server that refuses EHLO is not going to answer the question
            // either. HELO is not tried: a server this old is not one whose
            // recipient verdict should decide somebody's mail.
            reason = _T("the verification server refused EHLO");
            return CalloutUnknown;
         }

         // The null sender (RFC 5321 4.5.5): the probe is not a delivery, and a
         // null sender cannot be bounced to.
         if (!probe.Send("MAIL FROM:<>"))
         {
            reason = _T("the verification session ended during MAIL FROM");
            return CalloutUnknown;
         }

         if (probe.ReadReply(text) / 100 != 2)
         {
            // Some servers refuse the null sender outright. That is a statement
            // about the probe, not about the address.
            reason = _T("the verification server refused the null sender");
            return CalloutUnknown;
         }

         if (!probe.Send("RCPT TO:<" + AnsiString(address) + ">"))
         {
            reason = _T("the verification session ended during RCPT TO");
            return CalloutUnknown;
         }

         int code = probe.ReadReply(text);

         // QUIT is sent and its reply is not waited for: the answer is already
         // in hand, and the session is closed by the destructor either way.
         probe.Send("QUIT");

         if (code / 100 == 2)
         {
            reason = _T("");
            return CalloutAccepted;
         }

         if (code / 100 == 5)
         {
            reason = String(text);
            return CalloutRejected;
         }

         reason = _T("the verification server gave a temporary answer");
         return CalloutUnknown;
      }
      catch (boost::system::system_error &)
      {
         reason = _T("the verification session failed");
         return CalloutUnknown;
      }
      catch (std::exception &)
      {
         reason = _T("the verification session failed");
         return CalloutUnknown;
      }
   }

   void
   RecipientCallout::ClearCache()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      cache_.clear();
   }

   int
   RecipientCallout::GetCacheSize() const
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      return (int) cache_.size();
   }

   int
   RecipientCallout::GetProbeCount() const
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      return probe_count_;
   }

   void
   RecipientCallout::ResetProbeCount()
   {
      boost::lock_guard<boost::recursive_mutex> guard(mutex_);
      probe_count_ = 0;
   }
}
