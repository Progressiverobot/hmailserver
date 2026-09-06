// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using hMailServer;
using RegressionTests.SSL;

namespace RegressionTests.Shared
{
   internal enum SimulatedErrorType
   {
      None,
      DisconnectAfterSessionStart,
      Sleep15MinutesAfterSessionStart,
      DisconnectAfterWelcomeBannerSent,
      ForceAuthenticationFailure,
      DisconnectAfterDeliveryStarted,
      DisconnectWithoutReplyOnQuit,
      DisconnectAfterMessageAccept
   }

   internal class SmtpServerSimulator : TcpServer
   {
      private readonly List<Dictionary<string, int>> _recipientResults;
      private Dictionary<string, int> _currentRecipientResult;
      private bool _expectingPassword;
      private bool _expectingUsername;
      private bool _hasMailFrom;
      private int _mailFromresult = 250;
      private int _quitResult = 221;

      private bool _transmittingData;

      // What has arrived and not yet been handed out as a line. A pipelining
      // client sends its whole envelope in one flight, so one read can hold
      // several commands - and, after BDAT, the chunk itself.
      private string _pending = "";

      private int _acceptedRecipients;

      /// <summary>Advertise PIPELINING (RFC 2920) in the EHLO reply.</summary>
      public bool AdvertisePipelining { get; set; }

      /// <summary>Advertise CHUNKING (RFC 3030) in the EHLO reply, and accept BDAT.</summary>
      public bool AdvertiseChunking { get; set; }

      /// <summary>Advertise BINARYMIME (RFC 3030) in the EHLO reply; needs AdvertiseChunking to mean anything.</summary>
      public bool AdvertiseBinaryMime { get; set; }

      /// <summary>
      ///    How long to wait, after MAIL FROM, for more commands before replying to
      ///    it. Zero replies at once. A client that pipelines has its RCPT TO and
      ///    data command in the buffer (or on the wire) already; a client that waits
      ///    for each reply sends nothing until it gets one - so this is how a test
      ///    tells the two apart without depending on how TCP segmented them.
      /// </summary>
      public TimeSpan HoldMailFromReplyFor { get; set; }

      /// <summary>
      ///    Whether further commands had arrived by the time MAIL FROM was answered.
      /// </summary>
      public bool EnvelopeArrivedPipelined { get; private set; }

      /// <summary>Every envelope and data command line, in the order received.</summary>
      public List<string> CommandsReceived { get; } = new List<string>();

      /// <summary>Every BDAT command line, in order.</summary>
      public List<string> BdatCommands { get; } = new List<string>();


      public SmtpServerSimulator(int maxNumberOfConnections, int port, eConnectionSecurity connectionSecurity) :
         base(maxNumberOfConnections, port, connectionSecurity)
      {
         _recipientResults = new List<Dictionary<string, int>>();
         ServerSupportsEhlo = true;
         ServerSupportsHelo = true;
      }

      public SmtpServerSimulator(int maxNumberOfConnections, int port) :
         this(maxNumberOfConnections, port, eConnectionSecurity.eCSNone)
      {
      }

      public bool ServerSupportsEhlo { get; set; }
      public bool ServerSupportsHelo { get; set; }

      // RFC 1870: what EHLO advertises about SIZE. null = not advertised (the
      // default, matching the pre-existing behaviour of every test), 0 =
      // advertised without a limit, >0 = the maximum accepted message size.
      public long? EhloSizeAdvertisement { get; set; }

      public int RcptTosReceived { get; set; }

      public int QuitResult
      {
         set => _quitResult = value;
      }

      public SimulatedErrorType SimulatedError { set; get; }

      public int MailFromResult
      {
         set => _mailFromresult = value;
      }

      public string MessageData { get; private set; } = "";

      // The most recent MAIL FROM command line received (including the envelope
      // address and any parameters), so tests can assert the transmitted sender.
      public string MailFromCommand { get; private set; } = "";

      // The raw base64 blob from "AUTH XOAUTH2 <blob>", empty when the client
      // authenticated some other way.
      public string XOAuth2Blob { get; private set; } = "";

      public void AddRecipientResult(Dictionary<string, int> result)
      {
         _recipientResults.Add(result);
      }

      protected override void HandleClient()
      {
         _currentRecipientResult = _recipientResults[0];
         _recipientResults.Remove(_currentRecipientResult);

         Run();
      }

      public void Run()
      {
         if (SimulatedError == SimulatedErrorType.DisconnectAfterSessionStart)
            return;
         if (SimulatedError == SimulatedErrorType.Sleep15MinutesAfterSessionStart)
            Thread.Sleep(TimeSpan.FromMinutes(15));


         Send("220 ESMTP Test Server\r\n");

         _pending = "";

         while (true)
         {
            string line = NextLine_();

            if (line == null)
               break;

            var quit = ProcessCommand(line);

            if (quit)
               break;
         }
      }

      // One line at a time from what has arrived, reading more when needed. The
      // loop used to hand ProcessCommand everything one read returned, which was
      // one command from a client that waits for each reply and the whole envelope
      // from one that pipelines - and then answered only the first.
      private string NextLine_()
      {
         while (true)
         {
            int end = _pending.IndexOf("\r\n", StringComparison.Ordinal);

            if (end >= 0)
            {
               string line = _pending.Substring(0, end + 2);
               _pending = _pending.Substring(end + 2);
               return line;
            }

            string more = Receive();

            if (string.IsNullOrEmpty(more))
               return null;

            _pending = string.Concat(_pending, more); // one socket read at a time; a line or a chunk, never a document
         }
      }

      // Reads more input for up to the window, or until a whole line is waiting.
      private void WaitForMoreInput_(TimeSpan window)
      {
         DateTime deadline = DateTime.UtcNow + window;

         while (_pending.IndexOf("\r\n", StringComparison.Ordinal) < 0 && DateTime.UtcNow < deadline)
         {
            if (!_tcpConnection.WaitForData(deadline - DateTime.UtcNow))
               break;

            string more = Receive();

            if (string.IsNullOrEmpty(more))
               break;

            _pending = string.Concat(_pending, more); // one socket read at a time; a line or a chunk, never a document
         }
      }


      private bool ProcessCommand(string command)
      {
         if (ServerSupportsHelo && command.ToUpper().StartsWith("HELO"))
         {
            Send("250 Test Server - Helo\r\n");
            return false;
         }

         if (ServerSupportsEhlo && command.ToUpper().StartsWith("EHLO"))
         {
            var response = new StringBuilder();

            if (_connectionSecurity == eConnectionSecurity.eCSSTARTTLSRequired ||
                _connectionSecurity == eConnectionSecurity.eCSSTARTTLSOptional)
               response.AppendLine("250-STARTTLS");

            if (EhloSizeAdvertisement.HasValue)
               response.AppendLine(EhloSizeAdvertisement.Value > 0
                  ? "250-SIZE " + EhloSizeAdvertisement.Value
                  : "250-SIZE");

            if (AdvertisePipelining)
               response.AppendLine("250-PIPELINING");

            if (AdvertiseChunking)
               response.AppendLine("250-CHUNKING");

            if (AdvertiseBinaryMime)
               response.AppendLine("250-BINARYMIME");

            response.AppendLine("250 AUTH LOGIN PLAIN");

            Send(response.ToString());
            return false;
         }

         if (command.ToUpper().StartsWith("STARTTLS"))
         {
            Send("220 Ready to start TLS\r\n");
            _tcpConnection.HandshakeAsServer(SslSetup.GetCertificate());
            return false;
         }

         if (command.ToUpper().StartsWith("AUTH LOGIN"))
         {
            if (_connectionSecurity == eConnectionSecurity.eCSSTARTTLSRequired &&
                !_tcpConnection.IsSslConnection)
            {
               Send("503 STARTTLS required..\r\n");
               return false;
            }

            Send("334 VXNlcm5hbWU6\r\n");
            _expectingUsername = true;
            return false;
         }

         if (command.ToUpper().StartsWith("AUTH XOAUTH2"))
         {
            // The Microsoft-style one-round-trip bearer login: the blob rides on
            // the AUTH line itself. Recorded raw so a test can decode it and
            // assert on the exact user and token the client presented.
            XOAuth2Blob = command.Substring("AUTH XOAUTH2".Length).Trim();
            Send("235 2.7.0 Authentication successful\r\n");
            return false;
         }

         if (command.ToUpper().StartsWith("MAIL"))
         {
            if (_connectionSecurity == eConnectionSecurity.eCSSTARTTLSRequired &&
                !_tcpConnection.IsSslConnection)
            {
               Send("503 STARTTLS required..\r\n");
               return false;
            }

            CommandsReceived.Add(command.TrimEnd());

            // Pipelining shows itself here: a client that pipelines has already sent
            // RCPT TO and the data command behind MAIL FROM, so they are in the
            // buffer, or arrive within the hold, before this reply goes out.
            if (HoldMailFromReplyFor > TimeSpan.Zero)
               WaitForMoreInput_(HoldMailFromReplyFor);

            EnvelopeArrivedPipelined = _pending.IndexOf("\r\n", StringComparison.Ordinal) >= 0;

            Send(_mailFromresult + "\r\n");

            if (_mailFromresult == 250)
               _hasMailFrom = true;

            MailFromCommand = command;

            return false;
         }

         if (command.ToUpper().StartsWith("RCPT"))
         {
            // Recorded before the sender check: a pipelining client sends its RCPT
            // TOs behind a MAIL FROM it has not yet seen refused, and a test that
            // proves the group arrived needs to see them.
            CommandsReceived.Add(command.TrimEnd());

            if (!_hasMailFrom)
            {
               Send("503 must have sender first.\r\n");
               return false;
            }

            var StartPos = command.IndexOf("<") + 1;
            var EndPos = command.LastIndexOf(">");
            var length = EndPos - StartPos;

            var address = command.Substring(StartPos, length);

            if (!_currentRecipientResult.TryGetValue(address, out var recipientResult))
               throw new Exception("Unexpected address");

            var result = recipientResult.ToString();

            Send(result + " " + address + "\r\n");

            RcptTosReceived++;

            if (recipientResult == 250)
               _acceptedRecipients++;

            return false;
         }

         if (command.ToUpper().StartsWith("BDAT"))
         {
            CommandsReceived.Add(command.TrimEnd());
            BdatCommands.Add(command.TrimEnd());

            string[] parts = command.Trim().Split(' ');
            int size = int.Parse(parts[1]);
            bool last = parts.Length > 2 && parts[2].Equals("LAST", StringComparison.OrdinalIgnoreCase);

            // The chunk follows the command with no reply in between: exactly its
            // size, from what has arrived and whatever more is needed. The size is
            // in octets and this transport decodes UTF-8, so a non-ASCII chunk
            // would count wrong here - the tests that use BDAT send ASCII.
            while (_pending.Length < size)
            {
               string more = Receive();

               if (string.IsNullOrEmpty(more))
                  return true;

               _pending = string.Concat(_pending, more); // one socket read at a time; a line or a chunk, never a document
            }

            MessageData += _pending.Substring(0, size);
            _pending = _pending.Substring(size);

            if (!_hasMailFrom)
            {
               Send("503 must have sender first.\r\n");
               return false;
            }

            if (_acceptedRecipients == 0)
            {
               Send("554 no valid recipients.\r\n");
               return false;
            }

            Send(last ? "250 Test Server - Queued for delivery\r\n" : "250 chunk received\r\n");

            if (last && SimulatedError == SimulatedErrorType.DisconnectAfterMessageAccept)
            {
               Disconnect();
               return true;
            }

            return false;
         }

         if (command.ToUpper().StartsWith("DATA"))
         {
            CommandsReceived.Add("DATA");

            // What a real server says to DATA after a refused MAIL FROM or with no
            // accepted recipient. A client that pipelines sends DATA before it has
            // seen either reply, and RFC 2920 3.2 has it check this one too.
            if (!_hasMailFrom)
            {
               Send("503 must have sender first.\r\n");
               return false;
            }

            if (_acceptedRecipients == 0)
            {
               Send("554 no valid recipients.\r\n");
               return false;
            }

            Send("354 Test Server - Give it to me...\r\n");
            _transmittingData = true;
            MessageData = "";
            return false;
         }

         if (command.ToUpper().StartsWith("QUIT"))
         {
            if (SimulatedError != SimulatedErrorType.DisconnectWithoutReplyOnQuit)
               Send(_quitResult + " Test Server - Goodbye\r\n");

            Disconnect();

            return true;
         }

         if (_transmittingData)
         {
            if (SimulatedError == SimulatedErrorType.DisconnectAfterDeliveryStarted)
            {
               // We've received some message data. Disconenct!
               Disconnect();
               return true;
            }

            MessageData += command;

            if (MessageData.IndexOf("\r\n.\r\n") > 0)
            {
               // remove the ending...
               MessageData = MessageData.Replace("\r\n.\r\n", "\r\n");

               Send("250 Test Server - Queued for delivery\r\n");

               if (SimulatedError == SimulatedErrorType.DisconnectAfterMessageAccept)
               {
                  Disconnect();
                  return true;
               }

               _transmittingData = false;
               return false;
            }

            return false;
         }

         if (_expectingUsername)
         {
            _expectingUsername = false;
            Send("334 UGFzc3dvcmQ6\r\n");
            _expectingPassword = true;
            return false;
         }

         if (_expectingPassword)
         {
            if (SimulatedError == SimulatedErrorType.ForceAuthenticationFailure)
               Send("535 Authentication failed. Restarting authentication process.\r\n");
            else
               Send("235 authenticated.\r\n");

            _expectingPassword = false;

            return false;
         }


         var commandName = command.Substring(0, 4);

         Send(string.Format("550 Command {0} not recognized.\r\n", commandName));

         return false;
      }
   }
}