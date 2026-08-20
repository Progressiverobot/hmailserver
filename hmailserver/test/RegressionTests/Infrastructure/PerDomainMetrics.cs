using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Per-domain message counters on /metrics.
   ///
   ///    Every counter this server exposed was global, which is what stopped metrics
   ///    becoming reporting: an operator could see how much mail the server handled,
   ///    never how much any one domain did. On a box hosting other people's domains
   ///    that is the only question anyone actually asks.
   ///
   ///    The reason it had not been done is cardinality. Every label value is a
   ///    separate time series in whatever scrapes this, and the obvious
   ///    implementation - label the sender domain of each message - lets anyone on
   ///    the internet create series by sending mail. So the label set here is bounded
   ///    by construction: only domains this server HOSTS are ever labelled. That is
   ///    the half of this worth testing, and the second test is the one that does it.
   /// </summary>
   [TestFixture]
   public class PerDomainMetrics : TestFixtureBase
   {
      private const int MetricsPort = 9098;

      private static string ScrapeMetrics()
      {
         using (var client = new TcpClient())
         {
            client.Connect("127.0.0.1", MetricsPort);

            using (var stream = client.GetStream())
            {
               byte[] request = Encoding.ASCII.GetBytes("GET /metrics HTTP/1.0\r\nHost: 127.0.0.1\r\n\r\n");
               stream.Write(request, 0, request.Length);

               using (var reader = new StreamReader(stream, Encoding.UTF8))
                  return reader.ReadToEnd();
            }
         }
      }

      /// <summary>
      ///    The value of one labelled counter, or -1 when that series is absent -
      ///    which is a different thing from zero and the tests below rely on it.
      /// </summary>
      private static long LabelledCounter(string body, string metric, string domain)
      {
         var match = Regex.Match(body,
            "^" + Regex.Escape(metric) + "\\{domain=\"" + Regex.Escape(domain) + "\"\\}\\s+(\\d+)",
            RegexOptions.Multiline);

         return match.Success ? long.Parse(match.Groups[1].Value) : -1;
      }

      [Test]
      [Description("A message to a hosted domain is counted against that domain, and a message from it is counted separately - and neither series exists until MetricsPerDomainEnabled is set")]
      public void MessagesAreCountedAgainstTheHostedDomainTheyBelongTo()
      {
         // The address is read out NOW, as a plain string. Every COM proxy this
         // fixture holds dies at the first RestartServerAndReacquireCom below, and
         // reading the account object afterwards is an RPC call to a process that has
         // gone - which reports as "The RPC server is unavailable" and looks like an
         // infrastructure fault rather than a mistake in the test.
         string address = SingletonProvider<TestSetup>.Instance
            .AddAccount(_domain, "permetric@example.test", "test").Address;

         try
         {
            ServerIniFile.SetSetting("MetricsServerBindAddress", "127.0.0.1");
            ServerIniFile.SetSetting("MetricsServerPort", MetricsPort.ToString());
            ServerIniFile.SetSetting("MetricsPerDomainEnabled", "0");
            RestartServerAndReacquireCom();

            // Off by default, and off means absent rather than zero: an operator who
            // has not opted in should not be paying for the series at all.
            string withoutLabels = ScrapeMetrics();
            ClassicAssert.IsFalse(withoutLabels.Contains("hmailserver_domain_messages_received_total"),
               "With MetricsPerDomainEnabled unset the per-domain series must not be exposed at all. " +
               "Body: " + withoutLabels);

            ServerIniFile.SetSetting("MetricsPerDomainEnabled", "1");
            RestartServerAndReacquireCom();

            string before = ScrapeMetrics();
            long receivedBefore = Math.Max(0, LabelledCounter(before, "hmailserver_domain_messages_received_total", "example.test"));
            long sentBefore = Math.Max(0, LabelledCounter(before, "hmailserver_domain_messages_sent_total", "example.test"));

            // From the hosted domain to itself: one message sent and one received,
            // which is what an operator reporting on either side would expect.
            SmtpClientSimulator.StaticSend(address, address, "counted", "Body");
            Pop3ClientSimulator.AssertMessageCount(address, "test", 1);

            long receivedAfter = receivedBefore;
            long sentAfter = sentBefore;

            for (int attempt = 0; attempt < 30; attempt++)
            {
               string body = ScrapeMetrics();
               receivedAfter = LabelledCounter(body, "hmailserver_domain_messages_received_total", "example.test");
               sentAfter = LabelledCounter(body, "hmailserver_domain_messages_sent_total", "example.test");

               if (receivedAfter > receivedBefore && sentAfter > sentBefore)
                  break;

               System.Threading.Thread.Sleep(200);
            }

            ClassicAssert.Greater(receivedAfter, receivedBefore,
               "The hosted domain should have been credited with a received message.");
            ClassicAssert.Greater(sentAfter, sentBefore,
               "The same message was sent BY the hosted domain, so it counts on that side too.");
         }
         finally
         {
            ServerIniFile.SetSetting("MetricsServerBindAddress", null);
            ServerIniFile.SetSetting("MetricsServerPort", null);
            ServerIniFile.SetSetting("MetricsPerDomainEnabled", null);
            RestartServerAndReacquireCom();
         }
      }

      [Test]
      [Description("A sender domain this server does not host creates no series, whatever arrives from it - which is what keeps the label set bounded by configuration rather than by strangers")]
      public void AnUnhostedSenderDomainNeverCreatesASeries()
      {
         string address = SingletonProvider<TestSetup>.Instance
            .AddAccount(_domain, "permetric2@example.test", "test").Address;

         try
         {
            ServerIniFile.SetSetting("MetricsServerBindAddress", "127.0.0.1");
            ServerIniFile.SetSetting("MetricsServerPort", MetricsPort.ToString());
            ServerIniFile.SetSetting("MetricsPerDomainEnabled", "1");
            RestartServerAndReacquireCom();

            SmtpClientSimulator.StaticSend("stranger@not-hosted-here.test", address, "inbound", "Body");
            Pop3ClientSimulator.AssertMessageCount(address, "test", 1);

            // Give the counters the same chance to appear that the test above gives them.
            string body = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
               body = ScrapeMetrics();
               if (LabelledCounter(body, "hmailserver_domain_messages_received_total", "example.test") > 0)
                  break;

               System.Threading.Thread.Sleep(200);
            }

            ClassicAssert.AreEqual(-1,
               LabelledCounter(body, "hmailserver_domain_messages_sent_total", "not-hosted-here.test"),
               "A domain this server does not host must never appear as a label. If it can, anyone " +
               "who sends mail here can create time series, and the cardinality of this metric is no " +
               "longer bounded by anything the operator controls. Body: " + body);

            ClassicAssert.Greater(
               LabelledCounter(body, "hmailserver_domain_messages_received_total", "example.test"), 0,
               "The hosted recipient domain is still counted - it is only the stranger that is not. " +
               "Body: " + body);
         }
         finally
         {
            ServerIniFile.SetSetting("MetricsServerBindAddress", null);
            ServerIniFile.SetSetting("MetricsServerPort", null);
            ServerIniFile.SetSetting("MetricsPerDomainEnabled", null);
            RestartServerAndReacquireCom();
         }
      }
   }
}
