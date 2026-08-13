// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System;
using System.Collections.Generic;
using System.Text;
using hMailServer;
using RegressionTests.Shared;

namespace RegressionTests.ExternalAccounts
{
   /// <summary>
   ///    A POP3 server that misbehaves on purpose.
   ///    <para />
   ///    RegressionTests.Shared.Pop3ServerSimulator models a well-behaved server: it always
   ///    answers +OK, always sends the whole message and always confirms a deletion. The
   ///    external account fetcher, though, talks to a machine the administrator does not
   ///    control, so the cases that matter are the other ones - a RETR the server refuses, a
   ///    DELE it refuses, a listing with a junk line in it, a connection that dies half way
   ///    through a message, a listing that never ends. Each of those is a knob here.
   ///    <para />
   ///    Which messages exist, and which have actually been deleted, lives in the caller's
   ///    list rather than in this object, so a second session sees what the first one did to
   ///    the mailbox. That is what makes a duplicate delivery visible to a test.
   /// </summary>
   public class ScriptedPop3Server : TcpServer
   {
      /// <summary>
      ///    One message in the simulated remote mailbox. The unique-id is given explicitly
      ///    rather than derived from the text, because several of these tests need two
      ///    identical messages with different ids (or the reverse).
      /// </summary>
      public sealed class RemoteMessage
      {
         public RemoteMessage(string uid, string text)
         {
            Uid = uid;
            Text = text;
         }

         public string Uid { get; private set; }
         public string Text { get; private set; }
      }

      // Deletions the client has asked for and this server has accepted. Applied to the
      // mailbox on QUIT, exactly as a real POP3 server does (RFC 1939 UPDATE state), so a
      // session that never reaches QUIT leaves the mailbox untouched.
      private readonly List<int> _acceptedDeletions = new List<int>();

      private readonly List<RemoteMessage> _messages;

      public ScriptedPop3Server(int port, List<RemoteMessage> messages)
         : base(1, port, eConnectionSecurity.eCSNone)
      {
         _messages = messages;

         RefuseRetrForMessages = new List<int>();
         DeletedMessages = new List<int>();
         RetrievedMessages = new List<int>();

         // Several of these tests deliberately make the client give up on a message and
         // move on, which takes longer than a straight download.
         SecondsToWaitBeforeTerminate = 60;
      }

      /// <summary>Message numbers this server answers "-ERR" to when they are RETRieved.</summary>
      public List<int> RefuseRetrForMessages { get; private set; }

      /// <summary>Answer "-ERR" to every DELE, and do not delete anything.</summary>
      public bool RefuseDele { get; set; }

      /// <summary>Read the DELE and then drop the connection without answering it.</summary>
      public bool DisconnectOnDele { get; set; }

      /// <summary>
      ///    When above zero, send only this many bytes of the message and then drop the
      ///    connection, so the client never sees the terminating CRLF.CRLF.
      /// </summary>
      public int TruncateRetrAfterBytes { get; set; }

      /// <summary>
      ///    Replaces the generated listing entries, verbatim, one per line. Used to feed the
      ///    fetcher lines that a well-behaved server would never produce.
      /// </summary>
      public List<string> UidlListingOverride { get; set; }

      /// <summary>
      ///    When above zero, answer UIDL with this many bytes of listing and never send the
      ///    terminating dot, which is how a remote server can make the client buffer without
      ///    limit.
      /// </summary>
      public int UnterminatedUidlBytes { get; set; }

      public List<int> DeletedMessages { get; private set; }
      public List<int> RetrievedMessages { get; private set; }

      protected override void HandleClient()
      {
         Run();
      }

      public void Run()
      {
         Send("+OK ScriptedPop3Server\r\n");

         while (ProcessCommand(Receive()))
         {
         }
      }

      private bool ProcessCommand(string command)
      {
         // An empty read means the peer has closed its side. Returning true here would
         // spin this thread forever on a dead socket.
         if (string.IsNullOrEmpty(command) || command.Trim().Length == 0)
            return false;

         var verb = command.Trim().ToLower();

         if (verb.StartsWith("quit"))
         {
            ApplyAcceptedDeletions();
            return false;
         }

         if (verb.StartsWith("user") || verb.StartsWith("pass") || verb.StartsWith("noop"))
         {
            Send("+OK\r\n");
            return true;
         }

         if (verb.StartsWith("capa"))
         {
            Send("+OK CAPA list follows\r\nUSER\r\nUIDL\r\nTOP\r\n.\r\n");
            return true;
         }

         if (verb.StartsWith("uidl"))
            return HandleUidl();

         if (verb.StartsWith("retr"))
            return HandleRetr(ParseMessageNumber(command));

         if (verb.StartsWith("dele"))
            return HandleDele(ParseMessageNumber(command));

         Send("-ERR unknown command\r\n");
         return true;
      }

      private bool HandleUidl()
      {
         Send("+OK\r\n");

         if (UnterminatedUidlBytes > 0)
         {
            SendUnterminatedListing();
            return false;
         }

         var builder = new StringBuilder();

         if (UidlListingOverride != null)
         {
            foreach (var line in UidlListingOverride)
               builder.Append(line + "\r\n");
         }
         else
         {
            for (var i = 0; i < _messages.Count; i++)
               builder.Append(string.Format("{0} {1}\r\n", i + 1, _messages[i].Uid));
         }

         builder.Append(".\r\n");

         Send(builder.ToString());
         return true;
      }

      private void SendUnterminatedListing()
      {
         // Sent straight down the socket rather than through Send, which accumulates
         // everything into Conversation - at these sizes that alone would dominate the
         // test's run time.
         var chunk = new StringBuilder();
         var messageNumber = 1;

         while (chunk.Length < 512 * 1024)
         {
            chunk.Append(string.Format("{0} unique-id-for-message-number-{0}\r\n", messageNumber));
            messageNumber++;
         }

         var chunkText = chunk.ToString();
         var sent = 0;

         while (sent < UnterminatedUidlBytes)
         {
            try
            {
               _tcpConnection.Send(chunkText);
               sent += chunkText.Length;
            }
            catch (Exception)
            {
               // Expected once the client has had enough and closed the connection -
               // which is the whole point of the test.
               return;
            }
         }
      }

      private bool HandleRetr(int messageNumber)
      {
         RetrievedMessages.Add(messageNumber);

         if (messageNumber < 1 || messageNumber > _messages.Count)
         {
            // What a real server answers for a message number that is not in the
            // mailbox. The fetcher can be made to ask for one by putting a line in the
            // listing that it mis-parses.
            Send("-ERR no such message\r\n");
            return true;
         }

         if (RefuseRetrForMessages.Contains(messageNumber))
         {
            // The server listed the message and now will not hand it over. A message
            // deleted by another client between UIDL and RETR looks exactly like this.
            Send("-ERR message unavailable\r\n");
            return true;
         }

         var message = _messages[messageNumber - 1];

         if (TruncateRetrAfterBytes > 0)
         {
            var truncated = message.Text.Length > TruncateRetrAfterBytes
               ? message.Text.Substring(0, TruncateRetrAfterBytes)
               : message.Text;

            Send("+OK\r\n");
            Send(truncated);

            // No terminator, and no more conversation: the download is cut off mid-message.
            return false;
         }

         Send("+OK\r\n");
         Send(message.Text);
         Send("\r\n.\r\n");

         return true;
      }

      private bool HandleDele(int messageNumber)
      {
         if (DisconnectOnDele)
         {
            // The message has been delivered by the client at this point, but the
            // deletion is never confirmed - so the mailbox is left untouched.
            DeletedMessages.Add(messageNumber);
            return false;
         }

         if (RefuseDele)
         {
            DeletedMessages.Add(messageNumber);
            Send("-ERR deletion refused\r\n");
            return true;
         }

         DeletedMessages.Add(messageNumber);
         _acceptedDeletions.Add(messageNumber);

         Send("+OK\r\n");
         return true;
      }

      private void ApplyAcceptedDeletions()
      {
         _acceptedDeletions.Sort();
         _acceptedDeletions.Reverse();

         foreach (var messageNumber in _acceptedDeletions)
            if (messageNumber >= 1 && messageNumber <= _messages.Count)
               _messages.RemoveAt(messageNumber - 1);

         _acceptedDeletions.Clear();
      }

      private static int ParseMessageNumber(string command)
      {
         var parts = command.Trim().Split(' ');

         if (parts.Length < 2)
            return 0;

         int messageNumber;
         return int.TryParse(parts[1], out messageNumber) ? messageNumber : 0;
      }
   }
}
