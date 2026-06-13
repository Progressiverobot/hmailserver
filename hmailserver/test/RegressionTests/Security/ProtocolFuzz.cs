// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
//
// Over-the-wire protocol fuzzing. These tests feed the live SMTP and IMAP command
// parsers, and the inbound MIME parser, a large volume of malformed / randomised
// input and assert that the server never crashes, hangs or logs an unhandled
// fault. Detection is layered:
//   * Each test ends with an explicit liveness check (the server must still accept
//     connections and answer a normal command).
//   * The base fixture's ServiceRestartDetector.ValidateProcessId() fails the next
//     test if the service process restarted (i.e. it crashed and was restarted).
//   * AssertNoReportedError (run from the shared setup) fails if the fuzzing caused
//     the server to log an unhandled error.
//
// The inputs use a fixed RNG seed so any failure is reproducible. A per-test
// [Timeout] is a hard backstop in case a malformed input ever wedged the server.

using System;
using System.Text;
using System.Threading;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   [TestFixture]
   public class ProtocolFuzz : TestFixtureBase
   {
      private const int Connections = 150;

      // ---- input generators ----

      private static string Junk(Random rng, int maxLen)
      {
         int len = rng.Next(0, maxLen);
         var sb = new StringBuilder(len);
         for (int i = 0; i < len; i++)
         {
            switch (rng.Next(0, 12))
            {
               case 0: sb.Append((char) rng.Next(1, 32)); break; // control char
               case 1: sb.Append(' '); break;
               case 2: sb.Append('\t'); break;
               case 3: sb.Append('"'); break;
               case 4: sb.Append('<'); break;
               case 5: sb.Append('>'); break;
               case 6: sb.Append(':'); break;
               case 7: sb.Append('@'); break;
               case 8: sb.Append('{'); break;
               case 9: sb.Append('}'); break;
               case 10: sb.Append('\\'); break;
               default: sb.Append((char) rng.Next(33, 127)); break;
            }
         }
         return sb.ToString();
      }

      private static string Terminator(Random rng)
      {
         switch (rng.Next(0, 6))
         {
            case 0: return "\r\n";
            case 1: return "\n";
            case 2: return "\r";
            case 3: return "";
            case 4: return "\r\n\r\n";
            default: return "\r\r\n";
         }
      }

      private static void Quiet(Action a) { try { a(); } catch { } }

      // Reads any pending response without ever blocking on a silent server.
      private static void Drain(TcpConnection tc, int maxMs = 250)
      {
         int waited = 0;
         while (waited < maxMs)
         {
            bool has;
            try { has = tc.Peek(); }
            catch { return; }

            if (has)
            {
               Quiet(() => tc.Receive());
               return;
            }

            Thread.Sleep(20);
            waited += 20;
         }
      }

      // ---- tests ----

      [Test]
      [Timeout(180000)]
      [Description("Fuzz the SMTP command parser with malformed commands across many short-lived connections; the server must stay alive and responsive.")]
      public void TestSmtpCommandParserFuzz()
      {
         string[] verbs =
         {
            "EHLO", "HELO", "MAIL FROM:", "RCPT TO:", "DATA", "RSET", "NOOP", "VRFY", "EXPN",
            "AUTH", "AUTH LOGIN", "AUTH PLAIN", "STARTTLS", "BDAT", "HELP", "ETRN", "QUIT", "MAIL", "RCPT", ""
         };
         var rng = new Random(0x5117);

         for (int i = 0; i < Connections; i++)
         {
            TcpConnection tc = null;
            try
            {
               tc = new TcpConnection();
               if (!tc.Connect(25)) continue;
               Drain(tc, 1000); // banner

               int commands = rng.Next(1, 6);
               for (int j = 0; j < commands; j++)
               {
                  string line = verbs[rng.Next(verbs.Length)] + " " + Junk(rng, 300) + Terminator(rng);
                  try { tc.Send(line); } catch { break; }
                  Drain(tc);
               }
            }
            catch (Exception) { /* connection-level errors are expected; the liveness check is the real assertion */ }
            finally { if (tc != null) Quiet(() => tc.Disconnect()); }
         }

         AssertSmtpAlive();
      }

      [Test]
      [Timeout(180000)]
      [Description("Fuzz the IMAP command parser (pre-auth) with malformed commands, including bogus and oversized literal lengths; the server must stay alive and responsive.")]
      public void TestImapCommandParserFuzz()
      {
         string[] verbs =
         {
            "LOGIN", "CAPABILITY", "AUTHENTICATE", "SELECT", "EXAMINE", "LIST", "STATUS", "APPEND",
            "FETCH", "STARTTLS", "NOOP", "LOGOUT", "ID", "ENABLE", "SEARCH", ""
         };
         var rng = new Random(0x1A4B);

         for (int i = 0; i < Connections; i++)
         {
            TcpConnection tc = null;
            try
            {
               tc = new TcpConnection();
               if (!tc.Connect(143)) continue;
               Drain(tc, 1000); // banner

               int commands = rng.Next(1, 6);
               for (int j = 0; j < commands; j++)
               {
                  string arg = Junk(rng, 300);
                  if (rng.Next(0, 3) == 0)
                  {
                     // Inject a literal length token — mostly invalid (must be rejected),
                     // sometimes valid-but-large (exercises the literal-size cap).
                     switch (rng.Next(0, 3))
                     {
                        case 0: arg += " {99999999999}"; break;
                        case 1: arg += " {" + Junk(rng, 8) + "}"; break;
                        default: arg += " {" + rng.Next(0, 50000) + "}"; break;
                     }
                  }

                  string line = "A" + rng.Next(0, 99) + " " + verbs[rng.Next(verbs.Length)] + " " + arg + Terminator(rng);
                  try { tc.Send(line); } catch { break; }
                  Drain(tc);
               }
            }
            catch (Exception) { }
            finally { if (tc != null) Quiet(() => tc.Disconnect()); }
         }

         AssertImapAlive();
      }

      [Test]
      [Timeout(180000)]
      [Description("Fuzz the inbound MIME parser with malformed messages (bad boundaries, unterminated/huge headers, deep nesting) delivered over SMTP; the server must stay alive.")]
      public void TestMimeMessageFuzz()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "mimefuzz@example.test", "secret");
         var rng = new Random(0x4D1E);

         for (int i = 0; i < 120; i++)
         {
            TcpConnection tc = null;
            try
            {
               tc = new TcpConnection();
               if (!tc.Connect(25)) continue;
               Drain(tc, 1000); // banner

               tc.Send("EHLO fuzz.example.test\r\n"); Drain(tc, 1000);
               tc.Send("MAIL FROM:<fuzz@example.test>\r\n"); Drain(tc, 1000);
               tc.Send("RCPT TO:<mimefuzz@example.test>\r\n"); Drain(tc, 1000);
               tc.Send("DATA\r\n"); Drain(tc, 1000);
               tc.Send(BuildMalformedMime(rng));
               tc.Send("\r\n.\r\n");
               Drain(tc, 1500);
               tc.Send("QUIT\r\n");
            }
            catch (Exception) { }
            finally { if (tc != null) Quiet(() => tc.Disconnect()); }
         }

         AssertSmtpAlive();
      }

      private static string BuildMalformedMime(Random rng)
      {
         var sb = new StringBuilder();

         int headerCount = rng.Next(1, 12);
         for (int h = 0; h < headerCount; h++)
         {
            switch (rng.Next(0, 8))
            {
               case 0: sb.Append("Content-Type: multipart/mixed; boundary=" + Junk(rng, 60)); break;
               case 1: sb.Append("Content-Type: " + Junk(rng, 80)); break;
               case 2: sb.Append("Content-Transfer-Encoding: " + Junk(rng, 30)); break;
               case 3: sb.Append("Subject: " + Junk(rng, 500)); break;
               case 4: sb.Append("From: " + Junk(rng, 120)); break;
               case 5: sb.Append(Junk(rng, 40) + ": " + Junk(rng, 120)); break;
               case 6: sb.Append("X-Long: " + new string('A', rng.Next(0, 9000))); break;
               default: sb.Append(Junk(rng, 200)); break; // header-shaped junk (maybe no colon)
            }
            sb.Append(rng.Next(0, 5) == 0 ? "\n" : "\r\n"); // sometimes a lone LF
         }

         sb.Append("\r\n"); // header/body separator

         int parts = rng.Next(0, 6);
         for (int p = 0; p < parts; p++)
         {
            sb.Append("--" + Junk(rng, 40) + "\r\n");
            sb.Append("Content-Type: " + Junk(rng, 60) + "\r\n\r\n");
            sb.Append(Junk(rng, 300) + "\r\n");
         }

         // Never let the generated body contain the DATA terminator sequence.
         return sb.ToString().Replace("\r\n.\r\n", "\r\n. \r\n");
      }

      // ---- liveness checks ----

      private static void AssertSmtpAlive()
      {
         using (var tc = new TcpConnection())
         {
            Assert.IsTrue(tc.Connect(25), "SMTP server is not accepting connections after fuzzing (possible crash).");
            string banner = tc.ReadUntil("220", TimeSpan.FromSeconds(15));
            Assert.IsTrue(banner.Contains("220"), "SMTP banner missing after fuzzing. Got: " + banner);
            tc.Send("EHLO liveness.example.test\r\n");
            string resp = tc.ReadUntil("250", TimeSpan.FromSeconds(15));
            Assert.IsTrue(resp.Contains("250"), "SMTP did not respond to EHLO after fuzzing. Got: " + resp);
            tc.Send("QUIT\r\n");
         }
      }

      private static void AssertImapAlive()
      {
         using (var tc = new TcpConnection())
         {
            Assert.IsTrue(tc.Connect(143), "IMAP server is not accepting connections after fuzzing (possible crash).");
            string banner = tc.ReadUntil("* OK", TimeSpan.FromSeconds(15));
            Assert.IsTrue(banner.Contains("* OK"), "IMAP banner missing after fuzzing. Got: " + banner);
            tc.Send("L1 CAPABILITY\r\n");
            string resp = tc.ReadUntil("L1 OK", TimeSpan.FromSeconds(15));
            Assert.IsTrue(resp.Contains("L1 OK"), "IMAP did not respond to CAPABILITY after fuzzing. Got: " + resp);
            tc.Send("L2 LOGOUT\r\n");
         }
      }
   }
}
