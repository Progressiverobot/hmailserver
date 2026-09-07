// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class JsonValue;

   // Does the project have a newer release than the one running.
   //
   // The first of the four parts of the roadmap's live update: the check. The feed
   // is the GitHub Releases API - releases/latest on the stable channel, which
   // GitHub already keeps free of drafts and pre-releases, or the release list on
   // the pre-release channel - unless UpdateFeedUrl names another, which is how the
   // tests point it at a fake and how a site without Internet access could point it
   // at a mirror. Nothing but the request goes out: no identifier, no configuration,
   // just a GET with the User-Agent the client always sends.
   //
   // The verdict is kept here, process-wide, and read by the Status COM object, the
   // REST route /api/v1/update and the Control Panel; every check writes one
   // application-log line so the history is in the log where the rest of the
   // server's is. Nothing is downloaded and nothing runs: that is the second and
   // third part, and each is gated on this one having found something.
   class UpdateChecker
   {
   public:
      enum State
      {
         StateNotChecked = 0,     // no check has completed since the service started
         StateUpToDate = 1,       // the running version is the latest on the channel
         StateAvailable = 2,      // a newer release exists; its details are below
         StateDownloaded = 3,     // its installer is in the data directory and verified
         StateInstalling = 4,     // the installer has been handed to the helper
         StateFailed = 5          // the last check failed; last_error says why
      };

      struct Snapshot
      {
         Snapshot() : state(StateNotChecked), installer_size(0) {}

         State state;
         String available_version;     // "6.2.28"; empty unless a newer release is known
         String release_name;          // the release's title
         String published_at;          // as the feed gives it: ISO 8601, UTC
         String release_url;           // the release page
         String installer_name;        // hMailServer-<version>-x64.exe, when the release has it
         String installer_url;
         __int64 installer_size;
         String installer_digest;      // "sha256:<hex>" as the feed states it; a hint, not the proof
         String bundle_url;            // the installer's Sigstore bundle
         String last_checked;          // local time; empty until a check has completed
         String last_error;            // empty unless the last check failed
      };

      static Snapshot Current();

      // Reads the feed on the calling thread. True when it was read and understood;
      // the snapshot then says what it said. False, with error set and the snapshot
      // recording the failure, when it was not. Either way one application-log line.
      static bool CheckNow(String &error);

      // The feed's URL: UpdateFeedUrl, or the GitHub Releases API for the channel.
      static AnsiString FeedUrl();
      static bool IsPreReleaseChannel();

      // The version this binary is, "6.2.27": HMAILSERVER_VERSION without the build.
      static String RunningVersion();

      // Numeric, component by component: "6.10.0" is newer than "6.2.27", a leading
      // "v" and anything from the first character that is neither a digit nor a dot
      // ("-B37") are ignored, and missing components are zero. <0, 0 or >0.
      static int CompareVersions(const String &left, const String &right);

      static const char *StateName(State state);

      // The snapshot as the JSON document the REST route returns.
      static AnsiString ToJson(const Snapshot &snapshot);

   private:
      struct Release
      {
         Release() : found(false), draft(false), prerelease(false), installer_size(0) {}

         bool found;
         String version;
         String name;
         String url;
         String published_at;
         bool draft;
         bool prerelease;
         String installer_name;
         String installer_url;
         __int64 installer_size;
         String installer_digest;
         String bundle_url;
      };

      static bool ReadRelease_(const JsonValue &value, Release &release);
      // The newest release the channel accepts from a feed that is one release or a
      // list of them. found is false when the feed had releases but the channel took
      // none of them (a pre-release on the stable channel), which is not an error.
      static bool SelectRelease_(const JsonValue &document, bool preReleaseChannel, Release &release, String &error);
      static bool Fail_(const String &reason, String &error);
      static AnsiString Quote_(const String &value);

      static boost::mutex mutex_;
      static Snapshot snapshot_;
   };
}
