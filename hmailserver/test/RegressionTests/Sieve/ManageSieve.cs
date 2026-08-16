// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    End-to-end verification of the ManageSieve (RFC 5804) listener: the
   ///    optional TCP service ([Settings] ManageSieveServerPort) that lets a client
   ///    authenticate with SASL PLAIN and upload, list, fetch, activate and delete
   ///    named Sieve scripts. The final check proves a script activated over
   ///    ManageSieve is honoured by local delivery.
   /// </summary>
   [TestFixture]
   public class ManageSieve : TestFixtureBase
   {
      private const int ManageSievePort = 24190;

      [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
      private static extern bool WritePrivateProfileString(string section, string key, string value, string filePath);

      private void WriteSetting(string key, string value)
      {
         string programDirectory = _application.Settings.Directories.ProgramDirectory;
         string[] candidates =
         {
            Path.Combine(programDirectory, "hMailServer.ini"),
            Path.Combine(programDirectory, "Bin", "hMailServer.ini"),
         };

         bool wroteAny = false;
         foreach (string iniPath in candidates)
         {
            if (!File.Exists(iniPath))
               continue;

            Assert.IsTrue(
               WritePrivateProfileString("Settings", key, value, iniPath),
               "Failed to write " + key + " to " + iniPath + ".");
            wroteAny = true;
         }

         Assert.IsTrue(wroteAny, "Could not locate an existing hMailServer.ini to update.");
      }

      // A minimal ManageSieve protocol client over a raw TCP connection.
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

         private string ReadLine()
         {
            var bytes = new System.Collections.Generic.List<byte>();
            int previous = -1;
            while (true)
            {
               int current = _stream.ReadByte();
               if (current == -1)
                  break;

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

         private string ReadExact(int count)
         {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
               int n = _stream.Read(buffer, read, count - read);
               if (n <= 0)
                  break;
               read += n;
            }
            return Encoding.UTF8.GetString(buffer, 0, read);
         }

         // Reads response lines up to and including the completion line
         // (OK / NO / BYE) and returns the whole block.
         public string ReadResponse()
         {
            var sb = new StringBuilder();
            while (true)
            {
               string line = ReadLine();
               sb.AppendLine(line);

               string trimmed = line.TrimStart();
               if (trimmed.StartsWith("OK", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("NO", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("BYE", StringComparison.OrdinalIgnoreCase))
                  break;
            }

            return sb.ToString();
         }

         private void SendRaw(string text)
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

         // Sends a command whose final argument is a non-synchronizing literal.
         public string SendWithLiteral(string commandPrefix, string literal)
         {
            byte[] payload = Encoding.UTF8.GetBytes(literal);
            SendRaw(commandPrefix + " {" + payload.Length + "+}\r\n");
            SendRaw(literal + "\r\n");
            return ReadResponse();
         }

         public string Authenticate(string username, string password)
         {
            string sasl = "\0" + username + "\0" + password;
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(sasl));
            return SendCommand("AUTHENTICATE \"PLAIN\" \"" + encoded + "\"");
         }

         // GETSCRIPT returns a literal {NNN} followed by the content and an OK.
         public string GetScript(string name)
         {
            SendRaw("GETSCRIPT \"" + name + "\"\r\n");

            string header = ReadLine();
            header = header.Trim();
            if (!header.StartsWith("{"))
            {
               // An error response (NO ...); drain to completion and signal failure.
               return null;
            }

            int close = header.IndexOf('}');
            int size = int.Parse(header.Substring(1, close - 1).TrimEnd('+'));

            string content = ReadExact(size);
            ReadResponse(); // trailing CRLF + OK
            return content;
         }

         public void Dispose()
         {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
         }
      }

      [Test]
      [Description("RFC 5804: authenticate, then PUTSCRIPT / LISTSCRIPTS / GETSCRIPT / SETACTIVE / DELETESCRIPT round-trip, and the activated script is honoured by delivery.")]
      public void TestManageSieveRoundTrip()
      {
         const string address = "managesieve@example.test";
         const string password = "Sieve-Pass1";
         Account account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, address, password);
         account.IMAPFolders.Add("Filed");

         WriteSetting("ManageSieveServerBindAddress", "127.0.0.1");
         WriteSetting("ManageSieveServerPort", ManageSievePort.ToString());

         // Reinitialize so IniFileSettings re-reads the port and the listener starts.
         _application.Reinitialize();

         try
         {
            using (var session = new Session())
            {
               // Greeting: capability lines followed by an OK.
               string greeting = session.ReadResponse();
               StringAssert.Contains("IMPLEMENTATION", greeting);
               StringAssert.Contains("SIEVE", greeting);

               // Every ManageSieve client builds its filter UI from this line, so a name
               // on it is a promise that using the extension does something. The rule in
               // ManageSieveServer.cpp is that a name is added in the same commit as its
               // delivery-side step; this asserts the two halves stayed together for
               // imap4flags, whose step (LocalDelivery::ApplySieveFlags_) landed on
               // 15 August 2026 with SieveFlagDelivery.cs behind it.
               //
               // The inverse is the failure worth catching: an extension advertised here
               // and inert in delivery puts a control in front of the user that quietly
               // does nothing, which is exactly what imap4flags was for months.
               StringAssert.Contains("imap4flags", greeting);
               StringAssert.Contains("vacation", greeting);

               // body followed on 15 August 2026, with SieveBodyDelivery.cs behind it -
               // seven end-to-end tests that assert where a delivered message was FILED,
               // two of them negative controls.
               StringAssert.Contains("body", greeting);

               // mailbox followed on 16 August 2026, with SieveMailboxDelivery.cs
               // behind it: mailboxexists answered from the recipient's real folder
               // list, fileinto :create proven by a client SELECTing the folder the
               // delivery created.
               StringAssert.Contains("mailbox", greeting);

               // regex followed the same day, with SieveRegexDelivery.cs behind it -
               // including the breaker test: a catastrophic pattern costs a bounded
               // evaluation and a suspension, never the message.
               StringAssert.Contains("regex", greeting);

               // ihave and environment followed, with SieveEnvironmentDelivery.cs
               // behind them - including the ihave grant, proven by a script whose
               // guarded block uses fileinto with no require for it.
               StringAssert.Contains("ihave", greeting);
               StringAssert.Contains("environment", greeting);

               // Still absent on purpose: envelope is implemented and evaluated, and is
               // held back until a test proves the envelope TEST command itself works.
               // If somebody adds it here, that test should exist first.
               StringAssert.DoesNotContain("envelope", greeting);

               // CAPABILITY before auth is allowed.
               string capability = session.SendCommand("CAPABILITY");
               StringAssert.Contains("SASL", capability);
               StringAssert.StartsWith("\"", capability);

               // A script command before auth is refused.
               string preAuth = session.SendCommand("LISTSCRIPTS");
               StringAssert.StartsWith("NO", preAuth.TrimStart());

               // Bad credentials are rejected.
               string badAuth = session.Authenticate(address, "wrong-password");
               StringAssert.StartsWith("NO", badAuth.TrimStart());

               // Correct credentials succeed.
               string auth = session.Authenticate(address, password);
               StringAssert.StartsWith("OK", auth.TrimStart());

               // Reset any state left behind by a previous run (file-based scripts
               // persist on disk between runs): deactivate, then drop known scripts.
               session.SendCommand("SETACTIVE \"\"");
               session.SendCommand("DELETESCRIPT \"primary\"");
               session.SendCommand("DELETESCRIPT \"scratch\"");
               session.SendCommand("DELETESCRIPT \"renamed\"");

               const string scriptBody =
                  "require \"fileinto\";\r\n" +
                  "if header :contains \"Subject\" \"manage-me\" {\r\n" +
                  "  fileinto \"Filed\";\r\n" +
                  "}\r\n";

               // CHECKSCRIPT validates without storing.
               string check = session.SendWithLiteral("CHECKSCRIPT", scriptBody);
               StringAssert.StartsWith("OK", check.TrimStart());

               // An invalid script is rejected by CHECKSCRIPT.
               string badCheck = session.SendWithLiteral("CHECKSCRIPT", "if header :contains {\r\n");
               StringAssert.StartsWith("NO", badCheck.TrimStart());

               // PUTSCRIPT stores a named script.
               string put = session.SendWithLiteral("PUTSCRIPT \"primary\"", scriptBody);
               StringAssert.StartsWith("OK", put.TrimStart());

               // A second script, so DELETESCRIPT has a non-active target.
               string putSecond = session.SendWithLiteral("PUTSCRIPT \"scratch\"", "keep;\r\n");
               StringAssert.StartsWith("OK", putSecond.TrimStart());

               // LISTSCRIPTS shows both, neither active yet.
               string list = session.SendCommand("LISTSCRIPTS");
               StringAssert.Contains("\"primary\"", list);
               StringAssert.Contains("\"scratch\"", list);
               Assert.IsFalse(list.Contains("ACTIVE"), "No script should be active yet. Got: " + list);

               // GETSCRIPT returns the exact stored content.
               string fetched = session.GetScript("primary");
               Assert.IsNotNull(fetched, "GETSCRIPT should return the stored script.");
               Assert.AreEqual(scriptBody, fetched, "GETSCRIPT content should round-trip.");

               // SETACTIVE marks it active; LISTSCRIPTS reflects that.
               string setActive = session.SendCommand("SETACTIVE \"primary\"");
               StringAssert.StartsWith("OK", setActive.TrimStart());

               string listActive = session.SendCommand("LISTSCRIPTS");
               StringAssert.Contains("\"primary\" ACTIVE", listActive);

               // The active script cannot be deleted (RFC 5804).
               string deleteActive = session.SendCommand("DELETESCRIPT \"primary\"");
               StringAssert.StartsWith("NO", deleteActive.TrimStart());

               // A non-active script can be deleted.
               string deleteScratch = session.SendCommand("DELETESCRIPT \"scratch\"");
               StringAssert.StartsWith("OK", deleteScratch.TrimStart());

               string listAfterDelete = session.SendCommand("LISTSCRIPTS");
               Assert.IsFalse(listAfterDelete.Contains("\"scratch\""), "scratch should be gone. Got: " + listAfterDelete);

               // RENAMESCRIPT (RFC 5804 2.11). Renaming the ACTIVE script is the case
               // that justifies the command existing: the manual
               // GETSCRIPT/PUTSCRIPT/DELETESCRIPT route cannot do it at all, because
               // the active script refuses deletion. The active status must follow
               // the new name.
               string renameMissing = session.SendCommand("RENAMESCRIPT \"no-such-script\" \"anything\"");
               StringAssert.StartsWith("NO", renameMissing.TrimStart());

               string rename = session.SendCommand("RENAMESCRIPT \"primary\" \"renamed\"");
               StringAssert.StartsWith("OK", rename.TrimStart());

               string listAfterRename = session.SendCommand("LISTSCRIPTS");
               StringAssert.Contains("\"renamed\" ACTIVE", listAfterRename);
               Assert.IsFalse(listAfterRename.Contains("\"primary\""),
                  "The old name should be gone after the rename. Got: " + listAfterRename);

               // The renamed script's content round-trips unchanged.
               string fetchedAfterRename = session.GetScript("renamed");
               Assert.AreEqual(scriptBody, fetchedAfterRename, "RENAMESCRIPT must move the content untouched.");

               // Renaming onto a taken name is refused rather than overwriting.
               string putBlocker = session.SendWithLiteral("PUTSCRIPT \"scratch\"", "keep;\r\n");
               StringAssert.StartsWith("OK", putBlocker.TrimStart());
               string renameOntoTaken = session.SendCommand("RENAMESCRIPT \"renamed\" \"scratch\"");
               StringAssert.StartsWith("NO", renameOntoTaken.TrimStart());
               session.SendCommand("DELETESCRIPT \"scratch\"");

               // UNAUTHENTICATE (RFC 5804 2.14.1): advertised in the greeting, drops
               // the session to the unauthenticated state without losing the
               // connection, after which script commands are refused until a fresh
               // AUTHENTICATE - which must then work, because reusing the session is
               // the command's purpose.
               StringAssert.Contains("UNAUTHENTICATE", greeting);

               string unauth = session.SendCommand("UNAUTHENTICATE");
               StringAssert.StartsWith("OK", unauth.TrimStart());

               string listUnauthenticated = session.SendCommand("LISTSCRIPTS");
               StringAssert.StartsWith("NO", listUnauthenticated.TrimStart());

               string reAuth = session.Authenticate(address, password);
               StringAssert.StartsWith("OK", reAuth.TrimStart());

               string listReauthenticated = session.SendCommand("LISTSCRIPTS");
               StringAssert.Contains("\"renamed\" ACTIVE", listReauthenticated);

               string logout = session.SendCommand("LOGOUT");
               StringAssert.StartsWith("OK", logout.TrimStart());
            }

            // The script activated over ManageSieve is honoured by local delivery:
            // a matching message is filed into the named folder, not INBOX.
            SmtpClientSimulator.StaticSend("sender@example.test", address, "Please manage-me now", "body");

            IMAPFolder filed = account.IMAPFolders.get_ItemByName("Filed");
            CustomAsserts.AssertFolderMessageCount(filed, 1);

            IMAPFolder inbox = account.IMAPFolders.get_ItemByName("INBOX");
            CustomAsserts.AssertFolderMessageCount(inbox, 0);
         }
         finally
         {
            WriteSetting("ManageSieveServerPort", "0");
            _application.Reinitialize();
         }
      }
   }
}
