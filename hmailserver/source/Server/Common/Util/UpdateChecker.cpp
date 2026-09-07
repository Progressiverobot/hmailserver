// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "UpdateChecker.h"
#include "HttpsClient.h"
#include "JsonDocument.h"
#include "Time.h"
#include "Unicode.h"
#include "FileUtilities.h"
#include "../Application/IniFileSettings.h"
#include "../Application/Version.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      const char *STABLE_FEED = "https://api.github.com/repos/Progressiverobot/hmailserver/releases/latest";
      const char *PRERELEASE_FEED = "https://api.github.com/repos/Progressiverobot/hmailserver/releases?per_page=10";

      // A release list with ten entries and their notes is a few hundred kilobytes;
      // a feed that sends more than this is not the one this reader is for.
      const size_t MAX_FEED_BYTES = 4 * 1024 * 1024;

      // The feed is UTF-8, as JSON is; a release title can carry any character.
      String FromUtf8_(const std::string &text)
      {
         String wide;
         Unicode::MultiByteToWide(AnsiString(text), wide);
         return wide;
      }

      // Up to four numeric components, from the first digit after an optional "v",
      // until the first character that is neither a digit nor a dot.
      void ParseVersion_(const String &text, int components[4])
      {
         for (int i = 0; i < 4; i++)
            components[i] = 0;

         int position = 0;
         if (text.GetLength() > 0 && (text[0] == 'v' || text[0] == 'V'))
            position = 1;

         int index = 0;
         while (position < text.GetLength() && index < 4)
         {
            TCHAR c = text[position];
            if (c >= '0' && c <= '9')
            {
               // Capped, so that a component built to overflow an int cannot.
               if (components[index] < 100000000)
                  components[index] = components[index] * 10 + (c - '0');
               position++;
            }
            else if (c == '.')
            {
               index++;
               position++;
            }
            else
               break;
         }
      }
   }

   boost::mutex UpdateChecker::mutex_;
   UpdateChecker::Snapshot UpdateChecker::snapshot_;

   UpdateChecker::Snapshot
   UpdateChecker::Current()
   {
      boost::lock_guard<boost::mutex> guard(mutex_);
      return snapshot_;
   }

   AnsiString
   UpdateChecker::FeedUrl()
   {
      String configured = IniFileSettings::Instance()->GetUpdateFeedUrl();
      if (!configured.IsEmpty())
         return AnsiString(configured);

      return IsPreReleaseChannel() ? PRERELEASE_FEED : STABLE_FEED;
   }

   bool
   UpdateChecker::IsPreReleaseChannel()
   {
      String channel = IniFileSettings::Instance()->GetUpdateChannel();
      return channel.CompareNoCase(_T("prerelease")) == 0 || channel.CompareNoCase(_T("pre-release")) == 0;
   }

   String
   UpdateChecker::RunningVersion()
   {
      return HMAILSERVER_VERSION;
   }

   int
   UpdateChecker::CompareVersions(const String &left, const String &right)
   {
      int a[4];
      int b[4];
      ParseVersion_(left, a);
      ParseVersion_(right, b);

      for (int i = 0; i < 4; i++)
      {
         if (a[i] != b[i])
            return a[i] < b[i] ? -1 : 1;
      }

      return 0;
   }

   const char *
   UpdateChecker::StateName(State state)
   {
      switch (state)
      {
      case StateUpToDate: return "up-to-date";
      case StateAvailable: return "available";
      case StateDownloaded: return "downloaded";
      case StateInstalling: return "installing";
      case StateFailed: return "failed";
      case StateNotChecked:
      default:
         return "not-checked";
      }
   }

   bool
   UpdateChecker::CheckNow(String &error)
   {
      error.Empty();

      AnsiString url = FeedUrl();
      bool preReleaseChannel = IsPreReleaseChannel();

      std::vector<AnsiString> headers;
      headers.push_back("X-GitHub-Api-Version: 2022-11-28");

      HttpsClient::Response response;
      String requestError;
      if (!HttpsClient::Request("GET", url, headers, "", "", response, requestError, 20, MAX_FEED_BYTES))
         return Fail_(Formatter::Format(_T("The update feed could not be read: {0}"), requestError), error);

      if (response.status_code != 200)
         return Fail_(Formatter::Format(_T("The update feed answered HTTP {0}."), response.status_code), error);

      JsonValue document;
      std::string parseError;
      if (!JsonValue::Parse(std::string(response.body), document, parseError))
         return Fail_(Formatter::Format(_T("The update feed is not JSON: {0}."), String(parseError.c_str())), error);

      Release release;
      String selectError;
      if (!SelectRelease_(document, preReleaseChannel, release, selectError))
         return Fail_(selectError, error);

      String running = RunningVersion();
      const TCHAR *channelName = preReleaseChannel ? _T("pre-release") : _T("stable");

      Snapshot snapshot;
      snapshot.last_checked = Time::GetCurrentDateTime();

      String message;
      if (!release.found)
      {
         snapshot.state = StateUpToDate;
         message = Formatter::Format(_T("Update check: this server runs {0}; the feed's only release is a pre-release and the channel is {1}."),
            running, String(channelName));
      }
      else if (CompareVersions(release.version, running) > 0)
      {
         snapshot.state = StateAvailable;
         snapshot.available_version = release.version;
         snapshot.release_name = release.name;
         snapshot.published_at = release.published_at;
         snapshot.release_url = release.url;
         snapshot.installer_name = release.installer_name;
         snapshot.installer_url = release.installer_url;
         snapshot.installer_size = release.installer_size;
         snapshot.installer_digest = release.installer_digest;
         snapshot.bundle_url = release.bundle_url;

         message = Formatter::Format(_T("Update check: hMailServer {0} is available (published {1}); this server runs {2}. {3}"),
            release.version, release.published_at, running, release.url);
         if (release.installer_url.IsEmpty())
            message += _T(" The release carries no x64 installer, so there is nothing to download.");
      }
      else
      {
         snapshot.state = StateUpToDate;
         message = Formatter::Format(_T("Update check: this server runs {0}, which is the latest release on the {1} channel ({2})."),
            running, String(channelName), release.version);
      }

      {
         boost::lock_guard<boost::mutex> guard(mutex_);
         // A verified installer of the same version survives the re-check that
         // found it still current; a different version, or a file that has gone,
         // starts again from available.
         if (snapshot.state == StateAvailable && snapshot_.state == StateDownloaded &&
             snapshot_.available_version == snapshot.available_version && FileUtilities::Exists(snapshot_.installer_path))
         {
            snapshot.state = StateDownloaded;
            snapshot.installer_path = snapshot_.installer_path;
            snapshot.signer_identity = snapshot_.signer_identity;
            snapshot.integrated_time = snapshot_.integrated_time;
            snapshot.verified_at = snapshot_.verified_at;
         }
         snapshot_ = snapshot;
      }

      LOG_APPLICATION(message);
      return true;
   }

   void
   UpdateChecker::RecordDownloaded(const String &path, const AnsiString &identity, __int64 integratedTime)
   {
      boost::lock_guard<boost::mutex> guard(mutex_);
      snapshot_.state = StateDownloaded;
      snapshot_.installer_path = path;
      snapshot_.signer_identity = identity;
      snapshot_.integrated_time = integratedTime;
      snapshot_.verified_at = Time::GetCurrentDateTime();
      snapshot_.last_error.Empty();
   }

   void
   UpdateChecker::RecordFailure(const String &reason)
   {
      boost::lock_guard<boost::mutex> guard(mutex_);
      // The failure replaces the verdict but not what the last successful check
      // learned: a release that was available before the feed went quiet is still
      // available, and the Control Panel can keep saying so beside the error.
      snapshot_.state = StateFailed;
      snapshot_.last_error = reason;
   }

   void
   UpdateChecker::RecordInstalling()
   {
      boost::lock_guard<boost::mutex> guard(mutex_);
      snapshot_.state = StateInstalling;
      snapshot_.last_error.Empty();
   }

   void
   UpdateChecker::RecordApplyOutcome(const String &status, const String &version, const String &detail)
   {
      boost::lock_guard<boost::mutex> guard(mutex_);
      snapshot_.apply_status = status;
      snapshot_.apply_version = version;
      snapshot_.apply_detail = detail;
      // An outcome means the helper has finished: whatever the state said before
      // the restart, the service is running now, and the next check decides.
      if (snapshot_.state == StateInstalling)
         snapshot_.state = StateNotChecked;
   }

   AnsiString
   UpdateChecker::ReleaseByTagUrl(const String &version)
   {
      std::string configured = std::string(AnsiString(IniFileSettings::Instance()->GetUpdateFeedUrl()).c_str());
      std::string tag = "/tags/v" + std::string(AnsiString(version).c_str());
      if (configured.empty())
         return AnsiString(("https://api.github.com/repos/Progressiverobot/hmailserver/releases" + tag).c_str());
      size_t releases = configured.find("/releases");
      if (releases == std::string::npos)
         return AnsiString((configured + tag).c_str());
      return AnsiString((configured.substr(0, releases + 9) + tag).c_str());
   }

   bool
   UpdateChecker::FetchReleaseByTag(const String &version, ReleaseAssets &assets, String &error)
   {
      assets = ReleaseAssets();

      std::vector<AnsiString> headers;
      headers.push_back("X-GitHub-Api-Version: 2022-11-28");

      HttpsClient::Response response;
      String requestError;
      AnsiString url = ReleaseByTagUrl(version);
      if (!HttpsClient::Request("GET", url, headers, "", "", response, requestError, 20, MAX_FEED_BYTES))
      {
         error = Formatter::Format(_T("the release feed could not be read for {0}: {1}"), version, requestError);
         return false;
      }
      if (response.status_code != 200)
      {
         error = Formatter::Format(_T("the release feed answered HTTP {0} for release {1}"), response.status_code, version);
         return false;
      }

      JsonValue document;
      std::string parseError;
      Release release;
      if (!JsonValue::Parse(std::string(response.body), document, parseError) || !ReadRelease_(document, release))
      {
         error = Formatter::Format(_T("the feed's entry for release {0} is not a release"), version);
         return false;
      }

      assets.version = release.version;
      assets.installer_url = release.installer_url;
      assets.installer_size = release.installer_size;
      assets.installer_digest = release.installer_digest;
      assets.bundle_url = release.bundle_url;
      return true;
   }

   String
   UpdateChecker::FormatUnixTime(__int64 seconds)
   {
      if (seconds <= 0)
         return _T("");
      time_t value = (time_t) seconds;
      struct tm parts;
      if (gmtime_s(&parts, &value) != 0)
         return _T("");
      String text;
      text.Format(_T("%04d-%02d-%02dT%02d:%02d:%02dZ"), parts.tm_year + 1900, parts.tm_mon + 1, parts.tm_mday, parts.tm_hour, parts.tm_min, parts.tm_sec);
      return text;
   }

   bool
   UpdateChecker::Fail_(const String &reason, String &error)
   {
      error = reason;
      RecordFailure(reason);

      {
         boost::lock_guard<boost::mutex> guard(mutex_);
         snapshot_.last_checked = Time::GetCurrentDateTime();
      }

      LOG_APPLICATION(Formatter::Format(_T("Update check failed: {0}"), reason));
      return false;
   }

   bool
   UpdateChecker::ReadRelease_(const JsonValue &value, Release &release)
   {
      release = Release();

      if (!value.IsObject())
         return false;

      std::string tag = value.GetString("tag_name");
      if (tag.empty())
         return false;

      if (tag[0] == 'v' || tag[0] == 'V')
         tag = tag.substr(1);

      release.found = true;
      release.version = FromUtf8_(tag);
      release.name = FromUtf8_(value.GetString("name"));
      release.url = FromUtf8_(value.GetString("html_url"));
      release.published_at = FromUtf8_(value.GetString("published_at"));
      release.draft = value.GetBool("draft");
      release.prerelease = value.GetBool("prerelease");

      // The installer is the one asset the update can use, and it is named by the
      // release flow: hMailServer-<version>-x64.exe, with its Sigstore bundle beside
      // it under the same name and .cosign.bundle.
      std::string installerName = "hMailServer-" + tag + "-x64.exe";
      std::string bundleName = installerName + ".cosign.bundle";

      const JsonValue *assets = value.Get("assets");
      if (assets && assets->IsArray())
      {
         for (size_t i = 0; i < assets->Size(); i++)
         {
            const JsonValue *asset = assets->At(i);
            if (!asset || !asset->IsObject())
               continue;

            std::string name = asset->GetString("name");
            if (name == installerName)
            {
               release.installer_name = FromUtf8_(name);
               release.installer_url = FromUtf8_(asset->GetString("browser_download_url"));
               release.installer_size = asset->GetInt64("size");
               release.installer_digest = FromUtf8_(asset->GetString("digest"));
            }
            else if (name == bundleName)
            {
               release.bundle_url = FromUtf8_(asset->GetString("browser_download_url"));
            }
         }
      }

      return true;
   }

   bool
   UpdateChecker::SelectRelease_(const JsonValue &document, bool preReleaseChannel, Release &release, String &error)
   {
      release = Release();

      std::vector<const JsonValue *> candidates;
      if (document.IsObject())
         candidates.push_back(&document);
      else if (document.IsArray())
      {
         for (size_t i = 0; i < document.Size(); i++)
            candidates.push_back(document.At(i));
      }
      else
      {
         error = _T("The update feed is neither a release nor a list of releases.");
         return false;
      }

      bool anyRelease = false;
      Release best;
      for (size_t i = 0; i < candidates.size(); i++)
      {
         Release candidate;
         if (!ReadRelease_(*candidates[i], candidate))
            continue;

         anyRelease = true;

         // A draft is not published; a pre-release is published to those who asked
         // for it. The stable channel is everyone else.
         if (candidate.draft)
            continue;
         if (candidate.prerelease && !preReleaseChannel)
            continue;

         if (!best.found || CompareVersions(candidate.version, best.version) > 0)
            best = candidate;
      }

      if (!anyRelease)
      {
         error = _T("The update feed has no release in it.");
         return false;
      }

      release = best;
      return true;
   }

   AnsiString
   UpdateChecker::Quote_(const String &value)
   {
      // UTF-8, quoted for JSON: the two characters JSON requires escaped, and the
      // controls, which a feed's release title could carry and a reader must not
      // receive raw.
      AnsiString utf8;
      Unicode::WideToMultiByte(value, utf8);

      AnsiString result = "\"";
      for (int i = 0; i < utf8.GetLength(); i++)
      {
         unsigned char c = (unsigned char) utf8[i];
         if (c == '"')
            result += "\\\"";
         else if (c == '\\')
            result += "\\\\";
         else if (c < 0x20)
         {
            char buffer[8];
            sprintf_s(buffer, sizeof(buffer), "\\u%04x", c);
            result += buffer;
         }
         else
            result += (char) c;
      }
      result += "\"";
      return result;
   }

   AnsiString
   UpdateChecker::ToJson(const Snapshot &snapshot)
   {
      AnsiString size;
      size.Format("%I64d", snapshot.installer_size);

      AnsiString state;
      state.Format("%d", (int) snapshot.state);

      AnsiString body = "{";
      body += "\"state\":" + state;
      body += ",\"stateName\":\"" + AnsiString(StateName(snapshot.state)) + "\"";
      body += ",\"runningVersion\":" + Quote_(RunningVersion());
      body += ",\"channel\":\"" + AnsiString(IsPreReleaseChannel() ? "prerelease" : "stable") + "\"";
      body += ",\"checkEnabled\":" + AnsiString(IniFileSettings::Instance()->GetUpdateCheckEnabled() ? "true" : "false");
      body += ",\"availableVersion\":" + Quote_(snapshot.available_version);
      body += ",\"releaseName\":" + Quote_(snapshot.release_name);
      body += ",\"publishedAt\":" + Quote_(snapshot.published_at);
      body += ",\"releaseUrl\":" + Quote_(snapshot.release_url);
      body += ",\"installer\":{";
      body += "\"name\":" + Quote_(snapshot.installer_name);
      body += ",\"url\":" + Quote_(snapshot.installer_url);
      body += ",\"size\":" + size;
      body += ",\"digest\":" + Quote_(snapshot.installer_digest);
      body += ",\"bundleUrl\":" + Quote_(snapshot.bundle_url);
      body += "}";
      body += ",\"downloaded\":{";
      body += "\"path\":" + Quote_(snapshot.installer_path);
      body += ",\"signer\":" + Quote_(String(snapshot.signer_identity));
      body += ",\"logTime\":" + Quote_(FormatUnixTime(snapshot.integrated_time));
      body += ",\"verifiedAt\":" + Quote_(snapshot.verified_at);
      body += "}";
      body += ",\"apply\":{";
      body += "\"status\":" + Quote_(snapshot.apply_status);
      body += ",\"version\":" + Quote_(snapshot.apply_version);
      body += ",\"detail\":" + Quote_(snapshot.apply_detail);
      body += "}";
      body += ",\"lastChecked\":" + Quote_(snapshot.last_checked);
      body += ",\"lastError\":" + Quote_(snapshot.last_error);
      body += "}";
      return body;
   }
}
