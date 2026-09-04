// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Connection to the hMailServer COM API via late binding (IDispatch),
   /// so no interop assembly is required.
   ///
   /// The COM server lives inside the hMailServer service process, so every
   /// interface pointer dies when that service is restarted. Rather than leave
   /// the whole Control Panel stuck on "The RPC server is unavailable" until it
   /// is closed and reopened, the session verifies the link before handing out
   /// <see cref="Application"/> and silently re-authenticates when it has gone.
   /// </summary>
   public class ServerSession
   {
      // hMailServer.idl: eSessionType
      public const int SessionSmtp = 1;
      public const int SessionPop3 = 3;
      public const int SessionImap = 5;

      // hMailServer.idl: hStateRunning
      public const int StateRunning = 3;

      /// <summary>How long a successful liveness check is trusted for. Long
      /// enough that a page refresh costs at most one extra round trip, short
      /// enough that the user's next click after a restart already heals.</summary>
      private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

      /// <summary>Minimum gap between two automatic repair attempts, so a
      /// server that stays down does not turn every click into a reconnect
      /// storm.</summary>
      private static readonly TimeSpan HealInterval = TimeSpan.FromSeconds(3);

      private readonly object gate_ = new();

      private dynamic app_;
      private ProtectedSecret password_;
      private bool linkBroken_;
      private bool healing_;
      private DateTime lastVerifiedUtc_ = DateTime.MinValue;
      private DateTime lastHealUtc_ = DateTime.MinValue;

      /// <summary>
      /// The COM application object. Reading this verifies the link (at most
      /// once every <see cref="ProbeInterval"/>) and transparently reconnects if
      /// the service was restarted underneath us. It never throws: when the
      /// server really is unreachable the caller gets the stale reference and
      /// fails on its own COM call, exactly as it did before.
      /// </summary>
      public dynamic Application => VerifiedApplication();

      public string Host { get; private set; }
      public string UserName { get; private set; }
      public bool IsConnected => app_ != null;

      /// <summary>Raised after the session has healed itself, so the shell can
      /// tell the user and refresh whatever page is on screen.</summary>
      public static event Action<ServerSession> Reconnected;

      public static ServerSession Current { get; private set; }

      public static void SetCurrent(ServerSession session)
      {
         Current = session;

         // Hand IniFeatureStore the COM route to the [Settings] section, so a
         // Control Panel connected to another machine can administer settings that
         // live in hMailServer.INI. It cannot reach ServerSession itself: it is
         // compiled into the test assembly, which must not acquire COM and
         // System.ServiceProcess as dependencies just to read a value.
         //
         // Set even when the session is null, so signing out revokes the route
         // rather than leaving a delegate closed over a dead session.
         if (session == null)
         {
            IniFeatureStore.ComReadSetting = null;
            IniFeatureStore.ComWriteSetting = null;
            return;
         }

         IniFeatureStore.ComReadSetting = key => (string)session.Application.Settings.GetIniSetting(key);

         // Always SetIniSetting, including for an empty value, because that is
         // precisely what the local file path does - WritePrivateProfileString with
         // "" leaves "Key=" behind, and the shipped hMailServer.INI is full of such
         // lines (OAuth2HmacSecret=, BATVSecret=, SRSSecret=). Deleting the key here
         // instead would make the same edit mean two different things depending on
         // which machine the Control Panel happened to be running on, and an absent
         // key is NOT equivalent: it falls back to the reading code's default, where
         // an empty one reads as 0 through GetPrivateProfileInt. DeleteIniSetting
         // exists for callers that genuinely mean "return this to its default"; a
         // text box being cleared is not one of them.
         IniFeatureStore.ComWriteSetting = (key, value) =>
            session.Application.Settings.SetIniSetting(key, value ?? "");
      }

      public bool Connect(string host, string userName, string password, out string error)
      {
         string normalizedHost = string.IsNullOrWhiteSpace(host) ? "localhost" : host;

         if (!Open(normalizedHost, userName, password, out dynamic app, out error))
            return false;

         lock (gate_)
         {
            app_ = app;
            Host = normalizedHost;
            UserName = userName;

            // Remembered so the session can re-authenticate after a service
            // restart without prompting the user again.
            password_?.Dispose();
            password_ = new ProtectedSecret(password);

            linkBroken_ = false;
            lastVerifiedUtc_ = DateTime.UtcNow;
         }

         return true;
      }

      /// <summary>
      /// Drops the current COM link and authenticates again with the
      /// credentials this session was opened with.
      /// </summary>
      /// <param name="attempts">How many times to try before giving up. More
      /// than one is useful straight after a service restart, when the COM
      /// server is registered a moment before it can serve requests.</param>
      public bool Reconnect(int attempts, TimeSpan retryDelay, out string error)
      {
         error = null;

         lock (gate_)
         {
            if (password_ == null)
            {
               error = "the session has no stored credentials to reconnect with.";
               return false;
            }

            if (healing_)
            {
               error = "a reconnect is already in progress.";
               return false;
            }

            healing_ = true;
            lastHealUtc_ = DateTime.UtcNow;

            try
            {
               string password = password_.Reveal();

               int total = Math.Max(1, attempts);
               for (int attempt = 0; attempt < total; attempt++)
               {
                  if (attempt > 0 && retryDelay > TimeSpan.Zero)
                     Thread.Sleep(retryDelay);

                  if (!Open(Host, UserName, password, out dynamic app, out error))
                     continue;

                  // The service reports "running" to the SCM well before
                  // hMailServer has finished starting, and authenticating as the
                  // administrator only reads the INI file - so both succeed while
                  // the database connection is still coming up, and every real
                  // read would fail with "no connection to the database". Wait
                  // for the server to declare itself running before adopting the
                  // link, but never throw the last attempt away over it: a
                  // still-starting server is more useful than none.
                  if (attempt < total - 1 && !IsServerReady(app))
                  {
                     error = "the server is still starting up.";
                     Release((object)app);
                     continue;
                  }

                  dynamic dead = app_;
                  app_ = app;
                  linkBroken_ = false;
                  lastVerifiedUtc_ = DateTime.UtcNow;

                  // Let go of the old proxy. A stopped hMailServer service keeps
                  // its process alive for as long as any client still holds an
                  // interface pointer, so hanging on to it would strand a whole
                  // server process per restart. A view that is still using the
                  // old reference now gets InvalidComObjectException instead of
                  // COMException - both are transport failures as far as
                  // IsTransportFailure and DescribeComError are concerned.
                  try
                  {
                     Release((object)dead);
                  }
                  catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
                  {
                     // Releasing a proxy whose server has already gone can throw;
                     // the reconnect itself has still succeeded.
                  }

                  RaiseReconnected();
                  return true;
               }

               linkBroken_ = true;
               return false;
            }
            finally
            {
               healing_ = false;
            }
         }
      }

      public bool Reconnect(out string error) => Reconnect(1, TimeSpan.Zero, out error);

      /// <summary>Marks the link as gone, so the next use of
      /// <see cref="Application"/> reconnects instead of failing.</summary>
      public void Invalidate()
      {
         lock (gate_)
            linkBroken_ = true;
      }

      /// <summary>
      /// True when the exception means the COM link itself has gone - the
      /// service was restarted, crashed, or the network dropped - rather than
      /// the server rejecting the call.
      /// </summary>
      public static bool IsTransportFailure(Exception ex)
      {
         for (Exception e = ex; e != null; e = e.InnerException)
         {
            if (e is InvalidComObjectException)
               return true;

            if (e is COMException com)
            {
               switch ((uint)com.ErrorCode)
               {
                  case 0x800706BA: // RPC_S_SERVER_UNAVAILABLE
                  case 0x800706BE: // RPC_S_CALL_FAILED
                  case 0x800706BF: // RPC_S_CALL_FAILED_DNE
                  case 0x800706B5: // RPC_S_UNKNOWN_IF
                  case 0x80010108: // RPC_E_DISCONNECTED
                  case 0x80010105: // RPC_E_SERVERFAULT
                  case 0x800401FD: // CO_E_OBJNOTCONNECTED
                  case 0x80080005: // CO_E_SERVER_EXEC_FAILURE
                     return true;
               }
            }
         }

         return false;
      }

      // ---- link maintenance ---------------------------------------------------

      /// <summary>
      /// Hands out the COM application object, repairing the link first if it
      /// has gone. Deliberately silent about failure: an unusable reference
      /// leaves the caller's own COM call to raise the error it would have
      /// raised anyway, which every view already handles.
      /// </summary>
      private dynamic VerifiedApplication()
      {
         lock (gate_)
         {
            // Nothing to repair before the first successful Connect, and no
            // re-entering the probe from inside a reconnect.
            if (app_ == null || healing_)
               return app_;

            DateTime now = DateTime.UtcNow;

            if (!linkBroken_)
            {
               if (now - lastVerifiedUtc_ < ProbeInterval)
                  return app_;

               if (IsAlive())
               {
                  lastVerifiedUtc_ = now;
                  return app_;
               }

               linkBroken_ = true;
            }

            if (now - lastHealUtc_ < HealInterval)
               return app_;

            // The COM class is registered against the service (AppID
            // LocalService), so activating it would *start* a service the
            // administrator has deliberately stopped. Only heal a link that
            // broke while the service is up.
            if (!ServiceLooksRunning())
            {
               lastHealUtc_ = now;
               return app_;
            }

            // Enough attempts to ride out a service that is still starting,
            // short enough not to freeze the UI when the server is simply gone.
            Reconnect(5, TimeSpan.FromMilliseconds(300), out _);
            return app_;
         }
      }

      private bool ServiceLooksRunning()
      {
         // Only answerable for a local server; against a remote host let the
         // reconnect attempt itself decide.
         if (!IsLocalHost(Host))
            return true;

         try
         {
            using var controller = new ServiceController("hMailServer");
            return controller.Status == ServiceControllerStatus.Running ||
                   controller.Status == ServiceControllerStatus.StartPending;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // Service not installed under that name, or no rights to query it.
            return true;
         }
      }

      /// <summary>
      /// True when the named host is this machine.
      ///
      /// Public because more than one page has to ask it. Anything that inspects
      /// the local disk, the local registry or the local Service Control Manager
      /// is only describing the SERVER while this is true; connected elsewhere,
      /// the same check answers a question nobody asked, and the honest result is
      /// "cannot tell from here" rather than a confident report about the wrong
      /// machine.
      ///
      /// The rules themselves live in <see cref="LocalHostNames"/>, so that
      /// HostReachability can apply exactly the same ones before it decides
      /// whether to probe the network. Two opinions here would either refuse a
      /// connection that would have worked, or skip the probe and reinstate the
      /// frozen window the probe exists to prevent.
      /// </summary>
      public static bool IsLocalHost(string host)
      {
         return LocalHostNames.IsLocal(host);
      }

      /// <summary>True when the current session is against this machine, including
      /// when there is no session yet - in which case nothing remote is in play.</summary>
      public static bool IsLocalSession => IsLocalHost(Current?.Host);

      /// <summary>
      /// True once the server has finished starting - ServerState only turns
      /// Running after the database connection is up and the protocol servers
      /// have started.
      /// </summary>
      private static bool IsServerReady(dynamic app)
      {
         try
         {
            return (int)app.ServerState == StateRunning;
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return false;
         }
      }

      /// <summary>
      /// One cheap round trip that answers "is this link still worth using?".
      ///
      /// It deliberately asks for ServerState rather than something simpler like
      /// Version: a service that is shutting down keeps answering Version from a
      /// static string long after it has closed its database, so a Version probe
      /// reports a perfectly healthy link while every real read fails with "no
      /// connection to the database". ServerState is still an in-memory read -
      /// it touches no database - but it only says Running once the server is
      /// genuinely serving.
      /// </summary>
      private bool IsAlive()
      {
         try
         {
            // The VALUE does not matter - only that the out-of-process server
            // answered. A paused engine (Status page, or Application.Stop over COM)
            // reports Stopped while the service, the COM object and this link are
            // all perfectly healthy. Treating that as a dead link sent the panel
            // into a permanent false-reconnect loop for as long as the pause
            // lasted: a "reconnected after the service restarted" toast and a
            // second of frozen UI every few seconds, five COM objects created and
            // thrown away each time, and the current page reloaded underneath the
            // administrator - while the Pause confirmation promised the panel would
            // stay connected.
            //
            // ServerState is still the right question to ask, for the reason below:
            // it is an in-memory read that a shutting-down server stops answering,
            // where Version keeps returning a static string long after the database
            // has gone. What changed is that "it answered" is the liveness signal,
            // not "it answered Running". Readiness after a service RESTART is a
            // different question and is still asked separately by IsServerReady.
            _ = (int)app_.ServerState;
            return true;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            // A transport failure means the link really is gone. Anything else
            // is the server answering - most likely this session is not a server
            // administrator, in which case the link is fine and we must not tear
            // it down.
            return !IsTransportFailure(ex);
         }
      }

      private static bool Open(string host, string userName, string password, out dynamic app, out string error)
      {
         app = null;
         error = null;

         try
         {
            Type comType = IsLocalHost(host)
               ? Type.GetTypeFromProgID("hMailServer.Application")
               : Type.GetTypeFromProgID("hMailServer.Application", host);

            if (comType == null)
            {
               error = "hMailServer COM API is not registered on the target machine.";
               return false;
            }

            dynamic instance = Activator.CreateInstance(comType);
            dynamic account = instance.Authenticate(userName, password);

            if (account == null)
            {
               error = "Authentication failed. Check the user name and password.";
               return false;
            }

            Release((object)account);

            app = instance;
            return true;
         }
         catch (COMException ex)
         {
            error = ex.ErrorCode == -2147023174
               ? "Unable to reach the server (RPC unavailable)."
               : ex.Message;
            return false;
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            error = ex.Message;
            return false;
         }
      }

      private void RaiseReconnected()
      {
         try
         {
            Reconnected?.Invoke(this);
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            // A misbehaving listener must not turn a successful reconnect into
            // a failed one.
         }
      }

      public class StatusSnapshot
      {
         public long ProcessedMessages;
         public long SpamBlocked;
         public long VirusesRemoved;
         public int QueueLength;
         public int SmtpSessions;
         public int ImapSessions;
         public int Pop3Sessions;
         public string StartTime = "";
         public string[] QueueRows = Array.Empty<string>();
      }

      /// <summary>Reads a full status snapshot. Throws when the link is down.</summary>
      public StatusSnapshot ReadStatus(bool includeQueueRows = false)
      {
         dynamic status = Application.Status;
         try
         {
            var snap = new StatusSnapshot
            {
               ProcessedMessages = (long)status.ProcessedMessages,
               SpamBlocked = (long)status.RemovedSpamMessages,
               VirusesRemoved = (long)status.RemovedViruses,
               SmtpSessions = ReadSessionCount(status, SessionSmtp),
               ImapSessions = ReadSessionCount(status, SessionImap),
               Pop3Sessions = ReadSessionCount(status, SessionPop3),
               StartTime = (string)status.StartTime ?? ""
            };

            string queue = (string)status.UndeliveredMessages ?? "";
            var rows = queue.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            snap.QueueLength = rows.Length;
            if (includeQueueRows)
               snap.QueueRows = rows;

            return snap;
         }
         finally
         {
            Release(status);
         }
      }

      private static int ReadSessionCount(dynamic status, int sessionType)
      {
         // SessionCount is a parameterized COM property; via IDispatch the
         // indexer form binds as DISPATCH_PROPERTYGET.
         try
         {
            return (int)status.SessionCount[sessionType];
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            try
            {
               return (int)status.SessionCount(sessionType);
            }
            catch (Exception innerFatalCheck) when (!ExceptionPolicy.IsFatal(innerFatalCheck))
            {
               return 0;
            }
         }
      }

      public static void Release(object comObject)
      {
         if (comObject != null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
      }

      /// <summary>
      /// Unwraps a COM/reflection exception (e.g. the opaque "Exception has been
      /// thrown by the target of invocation" wrapper) to its most informative inner
      /// message, and turns the server-side conditions that block essentially
      /// every read - a lost link, no database connection, or not connected as
      /// the server administrator - into clear, actionable guidance.
      ///
      /// Every view funnels its COM errors through here, which also makes this
      /// the right place to notice that the link has died and flag it for repair
      /// on the next use of <see cref="Application"/>.
      /// </summary>
      public static string DescribeComError(Exception ex)
      {
         if (IsTransportFailure(ex))
         {
            Current?.Invalidate();
            return "the connection to the hMailServer service was lost - it was most likely restarted. " +
                   "The Control Panel will reconnect by itself; press Reload to try again.";
         }

         Exception real = ex;
         while (real.InnerException != null)
            real = real.InnerException;
         string msg = string.IsNullOrEmpty(real.Message) ? (ex.Message ?? "Unknown error.") : real.Message;

         if (msg.IndexOf("connection to the database", StringComparison.OrdinalIgnoreCase) >= 0)
            return "the server cannot reach its database. Check the database connection and the hMailServer error log, then press Reload. (" + msg + ")";
         if (msg.IndexOf("do not have access", StringComparison.OrdinalIgnoreCase) >= 0)
            return "you must connect with the hMailServer server-administrator account to view or change these settings. (" + msg + ")";
         return msg;
      }
   }
}
