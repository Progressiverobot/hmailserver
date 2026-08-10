// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// http://www.hmailserver.com

using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Exercises the opt-in message-store consistency check
   ///    (hMailServer.ini [Settings] MessageStoreConsistencyCheck): a scheduled,
   ///    read-only task cross-checks every message row against its backing file on
   ///    disk and publishes the number of missing files as the
   ///    hmailserver_messagestore_missing_files metric. The check never deletes or
   ///    repairs anything.
   /// </summary>
   [TestFixture]
   public class MessageStoreConsistency : TestFixtureBase
   {
      private const int MetricsPort = 9099;
      private const string MissingFilesMetric = "hmailserver_messagestore_missing_files";

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

      // Parses a Prometheus gauge value from a /metrics body. Returns -1 if absent.
      private static int ParseGauge(string body, string name)
      {
         foreach (string raw in body.Split('\n'))
         {
            string line = raw.Trim();
            if (line.StartsWith(name + " "))
            {
               string[] parts = line.Split(' ');
               int value;
               if (parts.Length >= 2 && int.TryParse(parts[parts.Length - 1], out value))
                  return value;
            }
         }

         return -1;
      }

      private static string ScrapeMetrics()
      {
         using (TcpClient client = new TcpClient())
         {
            Exception last = null;
            for (int attempt = 0; attempt < 25; attempt++)
            {
               try
               {
                  client.Connect("127.0.0.1", MetricsPort);
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

            using (NetworkStream stream = client.GetStream())
            using (MemoryStream memory = new MemoryStream())
            {
               byte[] request = Encoding.ASCII.GetBytes("GET /metrics HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
               stream.Write(request, 0, request.Length);

               byte[] buffer = new byte[4096];
               int read;
               while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                  memory.Write(buffer, 0, read);

               string raw = Encoding.UTF8.GetString(memory.ToArray());
               int separator = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
               return separator >= 0 ? raw.Substring(separator + 4) : "";
            }
         }
      }

      [Test]
      [Description("The consistency check detects a message whose backing file has been removed from disk.")]
      public void TestMissingMessageFileIsDetected()
      {
         WriteSetting("MetricsServerBindAddress", "127.0.0.1");
         WriteSetting("MetricsServerPort", MetricsPort.ToString());

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "consistency@example.test", "test");

         // Deliver a message and confirm it is on disk.
         SmtpClientSimulator.StaticSend(account.Address, account.Address, "Consistency", "Body");
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 1);

         var message = account.IMAPFolders.get_ItemByName("INBOX").Messages[0];
         string messageFile = message.Filename;
         Assert.IsTrue(File.Exists(messageFile), "Expected the delivered message file to exist on disk.");

         try
         {
            // Simulate store corruption: the database row remains but the file is gone.
            File.Delete(messageFile);

            // Enable the check and reinitialize so the startup consistency task runs.
            WriteSetting("MessageStoreConsistencyCheck", "1");
            _application.Reinitialize();

            // The task runs asynchronously; poll the gauge until it reflects the missing file.
            int missing = -1;
            for (int attempt = 0; attempt < 50; attempt++)
            {
               missing = ParseGauge(ScrapeMetrics(), MissingFilesMetric);
               if (missing >= 1)
                  break;

               System.Threading.Thread.Sleep(200);
            }

            Assert.GreaterOrEqual(missing, 1,
               "Expected the consistency check to report at least one missing message file.");

            // A recovery report listing the affected message must be written to the
            // log directory so an administrator can act on it.
            string reportPath = Path.Combine(
               _application.Settings.Directories.LogDirectory,
               "hMailServer_messagestore_consistency.report");

            string reportContents = null;
            for (int attempt = 0; attempt < 50; attempt++)
            {
               if (File.Exists(reportPath))
               {
                  reportContents = File.ReadAllText(reportPath);
                  if (reportContents.Contains(messageFile))
                     break;
               }

               System.Threading.Thread.Sleep(200);
            }

            Assert.IsNotNull(reportContents, "Expected a consistency recovery report to be written.");
            StringAssert.Contains("Missing files:", reportContents);
            StringAssert.Contains(messageFile, reportContents,
               "The recovery report should list the missing message's expected path.");
            StringAssert.Contains(account.Address, reportContents,
               "The recovery report should identify the affected account.");
         }
         finally
         {
            WriteSetting("MessageStoreConsistencyCheck", "0");
            _application.Reinitialize();
         }
      }
   }
}
