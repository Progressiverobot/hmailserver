using System;
using System.Runtime.InteropServices;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Connection to the hMailServer COM API via late binding (IDispatch),
   /// so no interop assembly is required.
   /// </summary>
   public class ServerSession
   {
      // hMailServer.idl: eSessionType
      public const int SessionSmtp = 1;
      public const int SessionPop3 = 3;
      public const int SessionImap = 5;

      // hMailServer.idl: hStateRunning
      public const int StateRunning = 3;

      public dynamic Application { get; private set; }
      public string Host { get; private set; }
      public string UserName { get; private set; }
      public bool IsConnected => Application != null;

      public static ServerSession Current { get; private set; }

      public static void SetCurrent(ServerSession session) => Current = session;

      public bool Connect(string host, string userName, string password, out string error)
      {
         error = null;

         try
         {
            Type comType = string.IsNullOrWhiteSpace(host) || host == "localhost" || host == "127.0.0.1"
               ? Type.GetTypeFromProgID("hMailServer.Application")
               : Type.GetTypeFromProgID("hMailServer.Application", host);

            if (comType == null)
            {
               error = "hMailServer COM API is not registered on the target machine.";
               return false;
            }

            dynamic app = Activator.CreateInstance(comType);
            dynamic account = app.Authenticate(userName, password);

            if (account == null)
            {
               error = "Authentication failed. Check the user name and password.";
               return false;
            }

            Application = app;
            Host = string.IsNullOrWhiteSpace(host) ? "localhost" : host;
            UserName = userName;
            return true;
         }
         catch (COMException ex)
         {
            error = ex.ErrorCode == -2147023174
               ? "Unable to reach the server (RPC unavailable)."
               : ex.Message;
            return false;
         }
         catch (Exception ex)
         {
            error = ex.Message;
            return false;
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
               ProcessedMessages = (long) status.ProcessedMessages,
               SpamBlocked = (long) status.RemovedSpamMessages,
               VirusesRemoved = (long) status.RemovedViruses,
               SmtpSessions = ReadSessionCount(status, SessionSmtp),
               ImapSessions = ReadSessionCount(status, SessionImap),
               Pop3Sessions = ReadSessionCount(status, SessionPop3),
               StartTime = (string) status.StartTime ?? ""
            };

            string queue = (string) status.UndeliveredMessages ?? "";
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
            return (int) status.SessionCount[sessionType];
         }
         catch (Exception)
         {
            try
            {
               return (int) status.SessionCount(sessionType);
            }
            catch (Exception)
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
   }
}
