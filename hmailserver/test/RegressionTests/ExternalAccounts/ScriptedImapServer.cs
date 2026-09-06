// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Text;
using hMailServer;
using RegressionTests.Shared;

namespace RegressionTests.ExternalAccounts
{
   /// <summary>
   ///    An IMAP server for the external-account fetcher to collect from, scripted the way
   ///    ScriptedPop3Server is: which messages exist lives in the caller's list, so a second
   ///    session sees what the first one did to the mailbox, and every way a remote server
   ///    can disappoint the fetcher is a knob - a refused logon, a message that is gone
   ///    between the SEARCH and its FETCH, a download cut short, a STORE refused.
   ///    <para />
   ///    The fetcher cannot be pointed at this very server's own IMAP port: TCPConnection
   ///    refuses to connect to a port it listens on, the loop guard the POP3 fetcher has
   ///    always lived with. So the far side is this class, one session per instance.
   /// </summary>
   public class ScriptedImapServer : TcpServer
   {
      /// <summary>One message in the simulated remote INBOX, with the UID the server gives it.</summary>
      public sealed class RemoteMessage
      {
         public RemoteMessage(int uid, string text)
         {
            Uid = uid;
            Text = text;
         }

         public int Uid { get; private set; }
         public string Text { get; private set; }
      }

      private readonly List<RemoteMessage> _messages;

      // \Deleted flags the client has set. Applied to the mailbox on EXPUNGE, as a real
      // server does, so a session that never reaches EXPUNGE leaves the mailbox intact.
      private readonly List<int> _flaggedForDeletion = new List<int>();

      public ScriptedImapServer(int port, List<RemoteMessage> messages)
         : base(1, port, eConnectionSecurity.eCSNone)
      {
         _messages = messages;
         UidValidity = 7;
         FetchedUids = new List<int>();
         StoredDeletedUids = new List<int>();
         VanishedUids = new List<int>();
         SecondsToWaitBeforeTerminate = 60;
      }

      /// <summary>The UIDVALIDITY announced on SELECT. Change it between sessions to say "a different mailbox".</summary>
      public int UidValidity { get; set; }

      /// <summary>Answer every LOGIN with a tagged NO.</summary>
      public bool RefuseLogin { get; set; }

      /// <summary>Answer every UID STORE with a tagged NO and flag nothing.</summary>
      public bool RefuseStore { get; set; }

      /// <summary>UIDs the SEARCH lists but a FETCH finds nothing for - gone in between, as far as the client can tell.</summary>
      public List<int> VanishedUids { get; private set; }

      /// <summary>When above zero, announce the whole literal, send only this many bytes of it and drop the connection.</summary>
      public int TruncateFetchAfterBytes { get; set; }

      /// <summary>The UIDs whose bodies were asked for, in order.</summary>
      public List<int> FetchedUids { get; private set; }

      /// <summary>The UIDs the client flagged \Deleted (or tried to), in order.</summary>
      public List<int> StoredDeletedUids { get; private set; }

      /// <summary>How many EXPUNGE commands arrived.</summary>
      public int ExpungeCount { get; private set; }

      /// <summary>The LOGIN command as received, tag removed - so a test can check the quoting.</summary>
      public string LoginLine { get; private set; }

      protected override void HandleClient()
      {
         Run();
      }

      public void Run()
      {
         Send("* OK ScriptedImapServer ready\r\n");

         while (ProcessCommand(Receive()))
         {
         }
      }

      private bool ProcessCommand(string command)
      {
         if (string.IsNullOrEmpty(command) || command.Trim().Length == 0)
            return false;

         var line = command.Trim();
         var firstSpace = line.IndexOf(' ');
         if (firstSpace < 0)
         {
            Send("* BAD Expected a tag\r\n");
            return true;
         }

         var tag = line.Substring(0, firstSpace);
         var rest = line.Substring(firstSpace + 1).Trim();
         var upper = rest.ToUpperInvariant();

         if (upper.StartsWith("LOGIN "))
         {
            LoginLine = rest;

            if (RefuseLogin)
            {
               Send(tag + " NO [AUTHENTICATIONFAILED] Authentication failed.\r\n");
               return true;
            }

            Send(tag + " OK LOGIN completed\r\n");
            return true;
         }

         if (upper.StartsWith("AUTHENTICATE") || upper.StartsWith("STARTTLS"))
         {
            Send(tag + " NO Not offered here\r\n");
            return true;
         }

         if (upper.StartsWith("SELECT "))
         {
            Send("* " + _messages.Count + " EXISTS\r\n" +
                 "* 0 RECENT\r\n" +
                 "* OK [UIDVALIDITY " + UidValidity + "] UIDs valid\r\n" +
                 "* OK [UIDNEXT " + NextUid() + "] Predicted next UID\r\n" +
                 "* FLAGS (\\Seen \\Deleted)\r\n" +
                 tag + " OK [READ-WRITE] SELECT completed\r\n");
            return true;
         }

         if (upper.StartsWith("UID SEARCH"))
         {
            var uids = string.Join(" ", _messages.Select(message => message.Uid.ToString()));
            Send("* SEARCH" + (uids.Length > 0 ? " " + uids : "") + "\r\n" +
                 tag + " OK SEARCH completed\r\n");
            return true;
         }

         if (upper.StartsWith("UID FETCH "))
            return HandleFetch(tag, rest);

         if (upper.StartsWith("UID STORE "))
            return HandleStore(tag, rest);

         if (upper == "EXPUNGE")
         {
            ExpungeCount++;
            foreach (var uid in _flaggedForDeletion)
               _messages.RemoveAll(message => message.Uid == uid);
            _flaggedForDeletion.Clear();

            Send(tag + " OK EXPUNGE completed\r\n");
            return true;
         }

         if (upper == "LOGOUT")
         {
            Send("* BYE ScriptedImapServer logging out\r\n" + tag + " OK LOGOUT completed\r\n");
            return false;
         }

         if (upper == "NOOP")
         {
            Send(tag + " OK NOOP completed\r\n");
            return true;
         }

         Send(tag + " BAD Unknown command\r\n");
         return true;
      }

      private bool HandleFetch(string tag, string rest)
      {
         var uid = ParseUid(rest);
         FetchedUids.Add(uid);

         var message = _messages.FirstOrDefault(candidate => candidate.Uid == uid);

         if (message == null || VanishedUids.Contains(uid))
         {
            // Gone between the SEARCH and the FETCH. RFC 3501: a UID FETCH naming a UID
            // that does not exist is not an error - the reply is a tagged OK with no data.
            Send(tag + " OK FETCH completed\r\n");
            return true;
         }

         var length = Encoding.UTF8.GetByteCount(message.Text);
         var sequence = _messages.IndexOf(message) + 1;

         if (TruncateFetchAfterBytes > 0 && TruncateFetchAfterBytes < length)
         {
            Send("* " + sequence + " FETCH (UID " + uid + " BODY[] {" + length + "}\r\n");
            Send(message.Text.Substring(0, TruncateFetchAfterBytes));
            return false;
         }

         Send("* " + sequence + " FETCH (UID " + uid + " BODY[] {" + length + "}\r\n" +
              message.Text +
              ")\r\n" +
              tag + " OK FETCH completed\r\n");
         return true;
      }

      private bool HandleStore(string tag, string rest)
      {
         var uid = ParseUid(rest);
         StoredDeletedUids.Add(uid);

         if (RefuseStore)
         {
            Send(tag + " NO STORE refused\r\n");
            return true;
         }

         _flaggedForDeletion.Add(uid);
         Send(tag + " OK STORE completed\r\n");
         return true;
      }

      // "UID FETCH 101 (BODY.PEEK[])" and "UID STORE 101 +FLAGS.SILENT (\Deleted)": the
      // UID is the third word of either.
      private static int ParseUid(string rest)
      {
         var parts = rest.Split(' ');
         int uid;
         return parts.Length >= 3 && int.TryParse(parts[2], out uid) ? uid : 0;
      }

      private int NextUid()
      {
         return _messages.Count == 0 ? 1 : _messages.Max(message => message.Uid) + 1;
      }
   }
}
