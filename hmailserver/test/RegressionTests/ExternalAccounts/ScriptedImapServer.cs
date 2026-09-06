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
   ///    ScriptedPop3Server is: which messages exist lives in the caller's mailboxes, so a
   ///    second session sees what the first one did to them, and every way a remote server
   ///    can disappoint the fetcher is a knob - a refused logon, a message that is gone
   ///    between the SEARCH and its FETCH, a download cut short, a STORE refused, a folder
   ///    that cannot be selected.
   ///    <para />
   ///    One mailbox - the INBOX - is what the fetcher without the mirror sees; the mirror
   ///    asks for LIST and walks every mailbox given here, so a test hands the server the
   ///    hierarchy it wants mirrored, with each message's flags and internal date.
   ///    <para />
   ///    The fetcher cannot be pointed at this very server's own IMAP port: TCPConnection
   ///    refuses to connect to a port it listens on, the loop guard the POP3 fetcher has
   ///    always lived with. So the far side is this class, one session per instance.
   /// </summary>
   public class ScriptedImapServer : TcpServer
   {
      /// <summary>One message in a simulated remote mailbox, with the UID the server gives it, its flags and its internal date.</summary>
      public sealed class RemoteMessage
      {
         public RemoteMessage(int uid, string text)
            : this(uid, text, "", null)
         {
         }

         public RemoteMessage(int uid, string text, string flags, string internalDate)
         {
            Uid = uid;
            Text = text;
            Flags = flags ?? "";
            InternalDate = internalDate ?? "01-Jan-2020 00:00:00 +0000";
         }

         public int Uid { get; private set; }
         public string Text { get; private set; }

         /// <summary>As FETCH FLAGS reports them, e.g. "\Seen \Flagged".</summary>
         public string Flags { get; private set; }

         /// <summary>As FETCH INTERNALDATE reports it, e.g. "17-Jul-1996 02:44:25 -0700".</summary>
         public string InternalDate { get; private set; }
      }

      /// <summary>
      ///    A simulated remote mailbox. Owned by the test and handed to each session's
      ///    server in turn, so what one session expunges the next one no longer lists.
      /// </summary>
      public sealed class RemoteMailbox
      {
         private readonly List<RemoteMessage> _messages = new List<RemoteMessage>();

         public RemoteMailbox()
            : this("INBOX")
         {
         }

         public RemoteMailbox(string name)
         {
            Name = name;
            Delimiter = "/";
            Selectable = true;
         }

         /// <summary>The name as LIST gives it, with the remote delimiter: "Archive/2025".</summary>
         public string Name { get; private set; }

         /// <summary>The hierarchy delimiter LIST announces for this mailbox.</summary>
         public string Delimiter { get; set; }

         /// <summary>False lists it with \Noselect - a hierarchy node with no messages of its own.</summary>
         public bool Selectable { get; set; }

         public int Count
         {
            get { return _messages.Count; }
         }

         public void Add(RemoteMessage message)
         {
            _messages.Add(message);
         }

         internal IEnumerable<int> Uids
         {
            get { return _messages.Select(message => message.Uid); }
         }

         internal RemoteMessage Find(int uid)
         {
            return _messages.FirstOrDefault(message => message.Uid == uid);
         }

         /// <summary>The message's sequence number in this mailbox, 1-based, as a FETCH response names it.</summary>
         internal int SequenceOf(RemoteMessage message)
         {
            return _messages.IndexOf(message) + 1;
         }

         internal void Remove(int uid)
         {
            _messages.RemoveAll(message => message.Uid == uid);
         }

         internal int NextUid
         {
            get { return _messages.Count == 0 ? 1 : _messages.Max(message => message.Uid) + 1; }
         }
      }

      private readonly List<RemoteMailbox> _mailboxes;
      private RemoteMailbox _mailbox;

      // \Deleted flags the client has set in the selected mailbox. Applied to it on
      // EXPUNGE, as a real server does, so a session that never reaches EXPUNGE leaves
      // the mailbox intact.
      private readonly List<int> _flaggedForDeletion = new List<int>();

      /// <summary>One mailbox, the INBOX: what the fetcher without the mirror collects.</summary>
      public ScriptedImapServer(int port, RemoteMailbox mailbox)
         : this(port, new[] { mailbox })
      {
      }

      /// <summary>The whole hierarchy, for the mirror. The first mailbox is selected until the client selects another.</summary>
      public ScriptedImapServer(int port, IEnumerable<RemoteMailbox> mailboxes)
         : base(1, port, eConnectionSecurity.eCSNone)
      {
         _mailboxes = mailboxes.ToList();
         _mailbox = _mailboxes.FirstOrDefault();
         UidValidity = 7;
         FetchedUids = new List<int>();
         FetchedMessages = new List<string>();
         StoredDeletedUids = new List<int>();
         VanishedUids = new List<int>();
         SelectedMailboxes = new List<string>();
         RefuseSelectOf = new List<string>();
         SecondsToWaitBeforeTerminate = 60;
      }

      /// <summary>The UIDVALIDITY announced on SELECT. Change it between sessions to say "a different mailbox".</summary>
      public int UidValidity { get; set; }

      /// <summary>Answer every LOGIN with a tagged NO.</summary>
      public bool RefuseLogin { get; set; }

      /// <summary>Answer every UID STORE with a tagged NO and flag nothing.</summary>
      public bool RefuseStore { get; set; }

      /// <summary>Mailboxes whose SELECT is answered with a tagged NO, by name.</summary>
      public List<string> RefuseSelectOf { get; private set; }

      /// <summary>
      ///    Send FLAGS and INTERNALDATE after the body literal rather than before it, as
      ///    some servers do; the client has to read them off the tail of the reply.
      /// </summary>
      public bool AttributesAfterBody { get; set; }

      /// <summary>UIDs the SEARCH lists but a FETCH finds nothing for - gone in between, as far as the client can tell.</summary>
      public List<int> VanishedUids { get; private set; }

      /// <summary>When above zero, announce the whole literal, send only this many bytes of it and drop the connection.</summary>
      public int TruncateFetchAfterBytes { get; set; }

      /// <summary>The UIDs whose bodies were asked for, in order.</summary>
      public List<int> FetchedUids { get; private set; }

      /// <summary>The same, qualified by mailbox: "Archive/2025:101", in order.</summary>
      public List<string> FetchedMessages { get; private set; }

      /// <summary>The UIDs the client flagged \Deleted (or tried to), in order.</summary>
      public List<int> StoredDeletedUids { get; private set; }

      /// <summary>The mailboxes the client selected, in order, whether or not the SELECT succeeded.</summary>
      public List<string> SelectedMailboxes { get; private set; }

      /// <summary>How many LIST commands arrived.</summary>
      public int ListCount { get; private set; }

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
            // One command per iteration; ProcessCommand answers false when the
            // session is over - LOGOUT, a dropped connection, or a deliberate cut.
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

         if (upper.StartsWith("LIST "))
         {
            ListCount++;
            var reply = new StringBuilder();
            foreach (var mailbox in _mailboxes)
            {
               reply.Append("* LIST (")
                  .Append(mailbox.Selectable ? "\\HasNoChildren" : "\\Noselect \\HasChildren")
                  .Append(") \"").Append(mailbox.Delimiter).Append("\" ")
                  .Append(QuoteName(mailbox.Name))
                  .Append("\r\n");
            }
            reply.Append(tag).Append(" OK LIST completed\r\n");
            Send(reply.ToString());
            return true;
         }

         if (upper.StartsWith("SELECT "))
            return HandleSelect(tag, rest);

         if (upper.StartsWith("UID SEARCH"))
         {
            var uids = string.Join(" ", _mailbox.Uids.Select(uid => uid.ToString()));
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
               _mailbox.Remove(uid);
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

      private bool HandleSelect(string tag, string rest)
      {
         // SELECT INBOX, or SELECT "Archive/2025".
         var name = UnquoteName(rest.Substring(7).Trim());
         SelectedMailboxes.Add(name);

         var mailbox = _mailboxes.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, name, System.StringComparison.OrdinalIgnoreCase));

         if (mailbox == null || RefuseSelectOf.Contains(name) || !mailbox.Selectable)
         {
            Send(tag + " NO [NONEXISTENT] Mailbox does not exist, or cannot be selected\r\n");
            return true;
         }

         _mailbox = mailbox;
         _flaggedForDeletion.Clear();

         Send("* " + _mailbox.Count + " EXISTS\r\n" +
              "* 0 RECENT\r\n" +
              "* OK [UIDVALIDITY " + UidValidity + "] UIDs valid\r\n" +
              "* OK [UIDNEXT " + _mailbox.NextUid + "] Predicted next UID\r\n" +
              "* FLAGS (\\Seen \\Deleted \\Flagged \\Answered \\Draft)\r\n" +
              tag + " OK [READ-WRITE] SELECT completed\r\n");
         return true;
      }

      private bool HandleFetch(string tag, string rest)
      {
         var uid = ParseUid(rest);
         FetchedUids.Add(uid);
         FetchedMessages.Add(_mailbox.Name + ":" + uid);

         var message = _mailbox.Find(uid);

         if (message == null || VanishedUids.Contains(uid))
         {
            // Gone between the SEARCH and the FETCH. RFC 3501: a UID FETCH naming a UID
            // that does not exist is not an error - the reply is a tagged OK with no data.
            Send(tag + " OK FETCH completed\r\n");
            return true;
         }

         var length = Encoding.UTF8.GetByteCount(message.Text);
         var sequence = _mailbox.SequenceOf(message);

         // What the client asked for besides the body, in the order a real server
         // would answer: FLAGS and INTERNALDATE before the literal - or after it, when
         // the test says so.
         var upper = rest.ToUpperInvariant();
         var attributes = "";
         if (upper.Contains("FLAGS"))
            attributes += " FLAGS (" + message.Flags + ")";
         if (upper.Contains("INTERNALDATE"))
            attributes += " INTERNALDATE \"" + message.InternalDate + "\"";

         var before = AttributesAfterBody ? "" : attributes;
         var after = AttributesAfterBody ? attributes : "";

         if (TruncateFetchAfterBytes > 0 && TruncateFetchAfterBytes < length)
         {
            Send("* " + sequence + " FETCH (UID " + uid + before + " BODY[] {" + length + "}\r\n");
            Send(message.Text.Substring(0, TruncateFetchAfterBytes));
            return false;
         }

         Send("* " + sequence + " FETCH (UID " + uid + before + " BODY[] {" + length + "}\r\n" +
              message.Text +
              after + ")\r\n" +
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

      private static string QuoteName(string name)
      {
         return "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
      }

      private static string UnquoteName(string name)
      {
         if (name.Length >= 2 && name.StartsWith("\"") && name.EndsWith("\""))
            name = name.Substring(1, name.Length - 2);
         return name.Replace("\\\"", "\"").Replace("\\\\", "\\");
      }
   }
}
