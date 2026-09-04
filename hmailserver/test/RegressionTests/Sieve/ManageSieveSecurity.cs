// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Linq;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    Authentication and resource-limit hardening on the ManageSieve (RFC 5804)
   ///    listener, covering the holes that were left after the per-IP auto-ban and
   ///    the STARTTLS work (ManageSieveAutoBan / ManageSieveTls).
   ///
   ///    Each test here fails against the code as it stood before this change, and
   ///    each one covers a different hole:
   ///
   ///    1. RE-AUTHENTICATION RESET THE ATTEMPT CAP. AUTHENTICATE was accepted on an
   ///       already-authenticated session, and a successful AUTHENTICATE sets the
   ///       failure counter back to zero. So a client holding one valid password
   ///       could log on, spend two guesses on somebody else's account, log on again
   ///       to clear the counter, and repeat - for as long as it liked, on one
   ///       connection. The per-connection cap of three attempts, which is the whole
   ///       subject of the two sibling fixtures, was therefore unbounded for anyone
   ///       with a single account on the server.
   ///
   ///    2. THE SASL LENGTH CAP ONLY COVERED ONE OF THE TWO FORMS. RFC 5804 allows
   ///       the SASL response as a literal or as a quoted string on the command line.
   ///       The 8 KB cap was applied to the literal only, so the identical payload
   ///       sent inline was bounded solely by ReadLine_'s one-megabyte line guard,
   ///       and was base64-decoded and split at that size, per command, from a client
   ///       that had not authenticated.
   ///
   ///    3. COMMANDS THAT CARRIED NO CREDENTIAL WERE UNLIMITED. The attempt cap only
   ///       counts commands that reached the password check, so CAPABILITY, NOOP and
   ///       AUTHENTICATE naming an unknown mechanism could be repeated forever. That
   ///       is worse here than on the mailbox protocols: this listener serves
   ///       connections one at a time on a single worker thread, so one client
   ///       looping NOOP holds the entire ManageSieve service, not just its own
   ///       connection.
   ///
   ///    4/5. THE SCRIPT SIZE LIMIT WAS A BUFFER GUARD, AND HAVESPACE LIED ABOUT IT.
   ///       The only bound on PUTSCRIPT was the ten-megabyte guard inside ReadBytes_,
   ///       which is reached only after ten megabytes have been buffered and which
   ///       fails by closing the connection without sending anything at all -
   ///       indistinguishable, from the client, from the network dropping mid-upload.
   ///       HAVESPACE, whose entire purpose in RFC 5804 is to let a client be told
   ///       "no" while its session is still usable, answered OK to every request
   ///       regardless of size.
   ///
   ///    6. AN OVERSIZED LITERAL LENGTH WENT THROUGH _wtoi, whose behaviour on
   ///       overflow is undefined, and the result was then trusted as a byte count.
   /// </summary>
   [TestFixture]
   public class ManageSieveSecurity : TestFixtureBase
   {
      // This fixture's own port, from the range allocated to this change. The three
      // sibling ManageSieve fixtures use 24190-24192, so nothing here can collide
      // with them if they are ever run alongside each other.
      private const int ManageSievePort = 9390;

      // Must match ManageSieveServer::MaxScriptSize.
      private const int MaxScriptSize = 1024 * 1024;

      // Must match ManageSieveServer::MaxPreAuthenticationCommands.
      private const int MaxPreAuthenticationCommands = 25;

      // Must match ManageSieveServer::MaxSaslResponseSize.
      private const int MaxSaslResponseSize = 8192;

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Paths.Combine(programDirectory, "hMailServer.ini"),
            Paths.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates.Where(File.Exists))
         {
            Assert.IsTrue(
               IniFile.WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      // The auto-ban setting as it was before this fixture touched it, so StopListener
      // can put it back.
      private bool _autoBanWasEnabled;

      /// <summary>
      /// Starts the listener, and turns auto-ban off for the duration.
      ///
      /// Auto-ban has to go: tests here make failed logons from 127.0.0.1, and once
      /// an auto-ban range exists for that address the listener refuses the
      /// connection at accept time (that is exactly what ManageSieveAutoBan proves),
      /// so the later attempts would be dropped before reaching the code under test -
      /// and the range would go on blocking SMTP, POP3 and IMAP for whatever fixture
      /// ran next. StopListener restores the previous value.
      /// </summary>
      private void StartListener()
      {
         _autoBanWasEnabled = _settings.AutoBanOnLogonFailure;
         _settings.AutoBanOnLogonFailure = false;
         _settings.ClearLogonFailureList();

         WriteSetting("ManageSieveServerBindAddress", "127.0.0.1");
         WriteSetting("ManageSieveServerPort", ManageSievePort.ToString());

         // Reinitialize so IniFileSettings re-reads the port and the listener starts.
         _application.Reinitialize();
      }

      private void StopListener()
      {
         WriteSetting("ManageSieveServerPort", "0");
         _application.Reinitialize();

         _settings.ClearLogonFailureList();
         _settings.AutoBanOnLogonFailure = _autoBanWasEnabled;
      }

      // A minimal ManageSieve client. Deliberately a local copy rather than a shared
      // helper, for the same reason the sibling fixtures each have one: this one has
      // to be able to send a malformed command and then observe a connection that is
      // closed with, or without, a response.
      private sealed class Session : IDisposable
      {
         private readonly TcpClient _client;
         private readonly NetworkStream _stream;

         public Session()
         {
            _client = new TcpClient();

            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  _client.Connect("127.0.0.1", ManageSievePort);
                  last = null;
                  break;
               }
               catch (SocketException ex)
               {
                  last = ex;
                  System.Threading.Thread.Sleep(200);
               }
            }
            if (last != null)
               throw last;

            _stream = _client.GetStream();
            _stream.ReadTimeout = 15000;
         }

         public void SetReadTimeout(int milliseconds)
         {
            _stream.ReadTimeout = milliseconds;
         }

         // Returns null when the server closed the connection, or the read timed out,
         // instead of sending a complete line.
         private string ReadLine()
         {
            var bytes = new List<byte>();
            int previous = -1;
            while (true)
            {
               int current;
               try
               {
                  current = _stream.ReadByte();
               }
               catch (IOException)
               {
                  // Read timeout. Treated the same as a close: the caller is asking
                  // for a response line and there is none.
                  return null;
               }

               if (current == -1)
               {
                  if (bytes.Count == 0)
                     return null;
                  break;
               }

               if (previous == '\r' && current == '\n')
               {
                  bytes.RemoveAt(bytes.Count - 1); // drop the trailing CR
                  break;
               }

               bytes.Add((byte) current);
               previous = current;
            }

            return Encoding.UTF8.GetString(bytes.ToArray());
         }

         // Reads response lines up to and including the completion line
         // (OK / NO / BYE), or until the server hangs up or stops answering.
         public string ReadResponse()
         {
            var sb = new StringBuilder();
            while (true)
            {
               string line = ReadLine();
               if (line == null)
                  break;

               sb.AppendLine(line);

               string trimmed = line.TrimStart();
               if (trimmed.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("NO", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("BYE", StringComparison.OrdinalIgnoreCase))
                  break;
            }

            return sb.ToString();
         }

         // Reads everything the server sends until it closes the connection. Returns
         // the empty string when the connection is already closed; if the server is
         // instead still holding it open, whatever it sent is returned once the read
         // times out, so the assertion message can show it.
         public string ReadUntilClosed(int timeoutMilliseconds)
         {
            _stream.ReadTimeout = timeoutMilliseconds;

            var sb = new StringBuilder();
            try
            {
               while (true)
               {
                  int current = _stream.ReadByte();
                  if (current == -1)
                     break;

                  sb.Append((char) current);
               }
            }
            catch (IOException)
            {
               // Timed out waiting for either data or a close.
            }

            return sb.ToString();
         }

         public void SendRaw(string text)
         {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
         }

         public string SendCommand(string command)
         {
            SendRaw(command + "\r\n");
            return ReadResponse();
         }

         private static string EncodeSaslPlain(string username, string password)
         {
            string sasl = "\0" + username + "\0" + password;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(sasl));
         }

         public string Authenticate(string username, string password)
         {
            return SendCommand("AUTHENTICATE \"PLAIN\" \"" + EncodeSaslPlain(username, password) + "\"");
         }

         public void Dispose()
         {
            try { _stream?.Dispose(); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
            try { _client?.Close(); } catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck)) { /* Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding. */ }
         }
      }

      [Test]
      [Description("AUTHENTICATE is refused on an already-authenticated session, so a successful logon cannot be used to reset the failed-attempt counter.")]
      public void ReauthenticationIsRefusedSoTheAttemptCapCannotBeReset()
      {
         const string attacker = "sieve-sec-owner@example.test";
         const string attackerPassword = "Sieve-Pass1";
         const string victim = "sieve-sec-victim@example.test";

         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, attacker, attackerPassword);
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, victim, "Sieve-Pass2");

         StartListener();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting

               StringAssert.StartsWith("OK", session.Authenticate(attacker, attackerPassword).TrimStart(),
                  "The first authentication should succeed.");

               // A guess at somebody else's password, from a session that is already
               // logged on to an account we do own.
               //
               // The fixed server does not check it at all - the command is refused
               // because this session has already authenticated. The unfixed server
               // ran the password check and answered NO "Authentication failed.",
               // which is what makes this assertion fail against it.
               //
               // Why that mattered: a failed check is counted against the connection,
               // but a *successful* AUTHENTICATE sets that counter back to zero, and
               // re-authenticating was permitted. So the loop was: log on, spend two
               // guesses, log on again to clear the counter, repeat - unbounded
               // guessing on one connection for anyone holding a single valid
               // password, with the cap of three never once reached.
               string guess = session.Authenticate(victim, "guess-1");
               StringAssert.Contains("Already authenticated", guess,
                  "AUTHENTICATE on an already-authenticated session must be refused without reaching the " +
                  "password check. Got: " + guess);

               // The same refusal for the credentials that did work a moment ago, so
               // the counter-clearing step itself is gone rather than merely being
               // harder to reach.
               string reauth = session.Authenticate(attacker, attackerPassword);
               StringAssert.Contains("Already authenticated", reauth,
                  "A second AUTHENTICATE must be refused rather than resetting the attempt counter. Got: " + reauth);

               // Negative control: refusing re-authentication must not damage the
               // session that was already established.
               string list = session.SendCommand("LISTSCRIPTS");
               Assert.IsFalse(list.TrimStart().StartsWith("NO"),
                  "The established session should still work after a refused re-authentication. Got: " + list);

               StringAssert.StartsWith("OK", session.SendCommand("LOGOUT").TrimStart());
            }
         }
         finally
         {
            StopListener();
         }
      }

      [Test]
      [Description("An oversized SASL response sent inline as a quoted string is refused on its length, before it is base64-decoded.")]
      public void OversizedInlineSaslResponseIsRefusedBeforeItIsDecoded()
      {
         StartListener();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting

               // Comfortably over the 8 KB cap and comfortably under ReadLine_'s
               // one-megabyte line guard, so that the unfixed server does answer
               // rather than dropping the connection - the point is which answer.
               //
               // Before the fix the cap was only applied to the literal form, so this
               // was decoded (a base64 'A' run decodes to NUL bytes, so the split on
               // NUL produced thousands of fields) and answered
               // NO "Invalid SASL response." A megabyte of it, per command, from an
               // unauthenticated client.
               string oversized = new string('A', MaxSaslResponseSize + 12000);

               string response = session.SendCommand("AUTHENTICATE \"PLAIN\" \"" + oversized + "\"");

               StringAssert.Contains("too large", response,
                  "An inline SASL response over the size cap should be refused on its length. Got: " + response);
            }

            // Negative control: a normal-sized inline response is still the form the
            // sibling fixtures use, and still works.
            const string address = "sieve-sec-inline@example.test";
            const string password = "Sieve-Pass1";
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

            using (var session = new Session())
            {
               session.ReadResponse(); // greeting
               StringAssert.StartsWith("OK", session.Authenticate(address, password).TrimStart(),
                  "A normal inline SASL response must still authenticate.");
            }
         }
         finally
         {
            StopListener();
         }
      }

      [Test]
      [Description("A client that never authenticates is disconnected after a bounded number of commands, rather than holding the single-threaded listener indefinitely.")]
      public void UnauthenticatedCommandsAreBounded()
      {
         StartListener();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting

               // NOOP carries no credential, so the failed-attempt cap never saw it.
               // Before the fix every one of these was answered OK for as long as the
               // client cared to keep going, and because Run_ serves one connection at
               // a time on one thread, no other client could be served meanwhile.
               bool disconnected = false;
               int sent = 0;

               // Well past the budget. If the server is still answering after this
               // many, there is no budget.
               int attempts = MaxPreAuthenticationCommands * 2;

               for (int i = 0; i < attempts; i++)
               {
                  string response;
                  try
                  {
                     response = session.SendCommand("NOOP");
                  }
                  catch (IOException)
                  {
                     // The server closed the connection under the write.
                     disconnected = true;
                     sent = i + 1;
                     break;
                  }

                  sent = i + 1;

                  if (response.Length == 0 || response.TrimStart().StartsWith("BYE", StringComparison.OrdinalIgnoreCase))
                  {
                     disconnected = true;
                     break;
                  }
               }

               Assert.IsTrue(disconnected,
                  "The listener answered " + sent + " pre-authentication NOOPs without ever disconnecting, so an " +
                  "unauthenticated client can hold it open indefinitely.");

               // It really is closed, not merely refusing to answer.
               string afterwards = session.ReadUntilClosed(5000);
               Assert.AreEqual(string.Empty, afterwards,
                  "The connection should be closed once the pre-authentication command budget is spent. Received: " + afterwards);
            }

            // Negative control: the budget is far above what a real client needs, so a
            // handful of pre-authentication commands followed by a logon still works.
            const string address = "sieve-sec-budget@example.test";
            const string password = "Sieve-Pass1";
            SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

            using (var session = new Session())
            {
               session.ReadResponse(); // greeting

               // CAPABILITY answers with the capability lines and then OK, so the
               // completion is at the end of the block rather than the start of it.
               for (int i = 0; i < 4; i++)
                  StringAssert.Contains("IMPLEMENTATION", session.SendCommand("CAPABILITY"));

               StringAssert.StartsWith("OK", session.Authenticate(address, password).TrimStart(),
                  "A client that sends a few commands before authenticating must still be able to authenticate.");

               // And the budget does not apply once authenticated: it is a
               // pre-authentication limit, not a command limit.
               for (int i = 0; i < MaxPreAuthenticationCommands * 2; i++)
                  StringAssert.StartsWith("OK", session.SendCommand("NOOP").TrimStart());
            }
         }
         finally
         {
            StopListener();
         }
      }

      [Test]
      [Description("HAVESPACE answers truthfully against the script size limit instead of returning OK to everything.")]
      public void HaveSpaceAnswersAgainstTheScriptSizeLimit()
      {
         const string address = "sieve-sec-havespace@example.test";
         const string password = "Sieve-Pass1";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

         StartListener();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting
               StringAssert.StartsWith("OK", session.Authenticate(address, password).TrimStart());

               // Before the fix this returned a bare OK: the command was a stub that
               // ignored its arguments. A client that trusted it then went on to
               // PUTSCRIPT and had its connection closed without a response.
               string tooBig = session.SendCommand("HAVESPACE \"sec-havespace\" " + (MaxScriptSize + 1000000));
               StringAssert.StartsWith("NO", tooBig.TrimStart(),
                  "HAVESPACE for a script over the limit should be refused. Got: " + tooBig);
               StringAssert.Contains("QUOTA/MAXSIZE", tooBig,
                  "The refusal should carry the RFC 5804 QUOTA/MAXSIZE response code, which is how a client " +
                  "tells 'too big' from any other failure. Got: " + tooBig);

               // Negative control: a script of an ordinary size is still permitted, so
               // this has not simply broken the command in the other direction.
               string ordinary = session.SendCommand("HAVESPACE \"sec-havespace\" 1024");
               StringAssert.StartsWith("OK", ordinary.TrimStart(),
                  "HAVESPACE for an ordinary script size should still be granted. Got: " + ordinary);

               StringAssert.StartsWith("OK", session.SendCommand("LOGOUT").TrimStart());
            }
         }
         finally
         {
            StopListener();
         }
      }

      [Test]
      [Description("PUTSCRIPT refuses a script announced over the size limit with QUOTA/MAXSIZE, instead of closing the connection without a response.")]
      public void PutScriptRefusesAnOversizedScriptWithAResponse()
      {
         const string address = "sieve-sec-putscript@example.test";
         const string password = "Sieve-Pass1";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

         StartListener();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting
               StringAssert.StartsWith("OK", session.Authenticate(address, password).TrimStart());

               // The literal is announced but deliberately not sent. The fixed server
               // refuses on the announced size without reading a byte, so it answers
               // immediately.
               //
               // Against the unfixed server this test fails by timing out here: it
               // accepted the announced length, sat in ReadBytes_ waiting for two
               // megabytes that never arrive, and eventually closed the connection on
               // the 30-second socket timeout without ever sending a response. That is
               // the behaviour being fixed - a client whose script is too big is told
               // nothing at all. The read timeout below is shortened so the failure
               // does not cost the suite the full 30 seconds.
               session.SetReadTimeout(10000);
               session.SendRaw("PUTSCRIPT \"sec-toobig\" {" + (MaxScriptSize + 1000000) + "+}\r\n");

               string response = session.ReadResponse();

               StringAssert.StartsWith("NO", response.TrimStart(),
                  "An oversized PUTSCRIPT should be refused with a response, not silence. Got: " + response);
               StringAssert.Contains("QUOTA/MAXSIZE", response,
                  "The refusal should carry the RFC 5804 QUOTA/MAXSIZE response code. Got: " + response);
            }

            // Negative control: an ordinary script still uploads. Without this the
            // test above would also pass against a server that refused every
            // PUTSCRIPT.
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting
               StringAssert.StartsWith("OK", session.Authenticate(address, password).TrimStart());

               const string scriptBody = "require \"fileinto\";\r\nkeep;\r\n";
               byte[] payload = Encoding.UTF8.GetBytes(scriptBody);

               session.SendRaw("PUTSCRIPT \"sec-ordinary\" {" + payload.Length + "+}\r\n");
               session.SendRaw(scriptBody + "\r\n");

               string put = session.ReadResponse();
               StringAssert.StartsWith("OK", put.TrimStart(),
                  "An ordinary script should still be accepted. Got: " + put);

               session.SendCommand("DELETESCRIPT \"sec-ordinary\"");
               session.SendCommand("LOGOUT");
            }
         }
         finally
         {
            StopListener();
         }
      }

      [Test]
      [Description("A literal length too large to represent as an int is rejected as a parse failure rather than being passed through _wtoi and trusted.")]
      public void OverflowingLiteralLengthIsRejected()
      {
         const string address = "sieve-sec-overflow@example.test";
         const string password = "Sieve-Pass1";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);

         StartListener();

         try
         {
            using (var session = new Session())
            {
               session.ReadResponse(); // greeting
               StringAssert.StartsWith("OK", session.Authenticate(address, password).TrimStart());

               // 99999999999 does not fit in an int. The old code handed it to _wtoi,
               // whose behaviour on overflow is undefined, and then used whatever came
               // back as a byte count: depending on what that was, the unfixed server
               // either blocked waiting for a payload that will never arrive until the
               // 30-second socket timeout closed the connection with no response, or
               // read zero bytes and cheerfully stored an empty script under the name.
               // Either way it does not answer "Expected a script literal", so this
               // assertion fails against it.
               session.SetReadTimeout(10000);
               session.SendRaw("PUTSCRIPT \"sec-overflow\" {99999999999+}\r\n");

               string response = session.ReadResponse();

               StringAssert.Contains("Expected a script literal", response,
                  "A literal length that overflows an int should be a parse failure. Got: " + response);

               // The stream is still synchronized - nothing was announced that the
               // server half-read - so the session carries on. That is the difference
               // between rejecting the length and trusting it.
               string list = session.SendCommand("LISTSCRIPTS");
               Assert.IsFalse(list.Contains("\"sec-overflow\""),
                  "No script should have been created by the rejected command. Got: " + list);

               StringAssert.StartsWith("OK", session.SendCommand("LOGOUT").TrimStart());
            }
         }
         finally
         {
            StopListener();
         }
      }
   }
}
