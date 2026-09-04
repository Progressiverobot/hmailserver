// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using NUnit.Framework;

namespace RegressionTests.Shared
{
   /// <summary>
   ///    A DNS server on 127.0.0.1:53, UDP and TCP, that answers exactly what a test
   ///    tells it to and NODATA for everything else.
   ///
   ///    It exists because tests that resolve real names are not tests of this server.
   ///    Seven of them - three DKIM verification tests, two SURBL ones, the POP3
   ///    unresolvable-host test and the MX lookup - failed across two consecutive gate
   ///    runs on 19 August 2026 for no reason connected to the change being gated, and
   ///    the measurement explains why:
   ///    <c>selector1._domainkey.outlook.com</c>, which the DKIM fixture needs, is a
   ///    CNAME into <c>protection.outlook.com</c> and takes between 60 ms and 4
   ///    seconds depending on nothing the test can control. Against the server's
   ///    10-second query timeout that is a coin toss under load, and a DKIM key that
   ///    cannot be fetched is a TEMPORARY error - so the message is accepted, and a
   ///    test asserting it is rejected fails while reporting nothing about DKIM.
   ///
   ///    Point <c>DNSServer</c> at 127.0.0.1, restart the service, and the same tests
   ///    ask a resolver that is up, is local, and answers in microseconds.
   ///
   ///    Answering NODATA rather than SERVFAIL for unknown names is deliberate: it is a
   ///    definite answer, so the resolver returns immediately instead of retrying, and
   ///    the caller sees "no such record" - which is what an SPF or DMARC lookup for a
   ///    test domain should see anyway. The SOA in the AUTHORITY section is the RFC 2308
   ///    shape; without it the response parser answers DNS_ERROR_BAD_PACKET (9502) over
   ///    TCP and the lookup fails for a reason that has nothing to do with the code
   ///    under test. That was measured, not assumed - see CustomDnsServer.
   /// </summary>
   public sealed class FakeDnsServer : IDisposable
   {
      public const int TypeA = 1;
      public const int TypeMx = 15;
      public const int TypeTxt = 16;

      private readonly Dictionary<string, List<string>> answers_ =
         new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

      // Names that must answer NXDOMAIN rather than the default NODATA. The two are
      // easy to conflate and are not interchangeable: NODATA says the name is there
      // and holds nothing of the type asked for, NXDOMAIN says the name is not there
      // at all. DMARC's np= tag turns on exactly that difference, so a fixture for it
      // cannot be written against a server that only knows how to say NODATA.
      private readonly HashSet<string> nonExistent_ =
         new HashSet<string>(StringComparer.OrdinalIgnoreCase);

      private readonly UdpClient udp_;
      private readonly TcpListener tcp_;
      private volatile bool stopping_;

      // Every question the server was asked, in order. Recorded for the tests that
      // care about the SHAPE of a lookup rather than its answer - the DMARC tree
      // walk is bounded at eight queries however deep the name, and a bound can only
      // be shown to hold by counting. Locked rather than concurrent because both
      // serve threads write it and a test thread reads it.
      private readonly List<string> queries_ = new List<string>();


      public FakeDnsServer()
      {
         try
         {
            udp_ = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53));
            tcp_ = new TcpListener(IPAddress.Loopback, 53);
            tcp_.Start();
         }
         catch (SocketException ex)
         {
            udp_?.Close();
            tcp_?.Stop();

            Assert.Fail("Could not bind 127.0.0.1:53 for the fake DNS server - something on this " +
                        "machine is already serving DNS on loopback: " + ex.Message);
         }

         new Thread(ServeUdp_) { IsBackground = true }.Start();
         new Thread(ServeTcp_) { IsBackground = true }.Start();
      }

      /// <summary>
      ///    Adds a TXT answer. The value is split into 255-byte character-strings, which
      ///    is not a detail that can be skipped: a DKIM public key does not fit in one,
      ///    the length octet would overflow, and the packet would be malformed rather
      ///    than merely truncated. RFC 6376 3.6.2 requires a verifier to concatenate the
      ///    strings, so serving a real key this way tests that too.
      /// </summary>
      public FakeDnsServer WithTxt(string name, string value)
      {
         return Add_(TypeTxt, name, value);
      }

      /// <summary>
      ///    Adds an A answer. Used for the URI-blacklist tests, where a listed name
      ///    resolves to a 127.0.0.0/8 address and an unlisted one must not resolve at
      ///    all.
      /// </summary>
      public FakeDnsServer WithA(string name, string address)
      {
         return Add_(TypeA, name, address);
      }

      /// <summary>
      ///    Adds an MX answer. The value is "preference exchange", e.g. "10 mail.x.test";
      ///    the exchange usually needs its own WithA so the caller can resolve it.
      /// </summary>
      public FakeDnsServer WithMx(string name, int preference, string exchange)
      {
         return Add_(TypeMx, name, preference + " " + exchange);
      }

      /// <summary>
      ///    Makes a name answer NXDOMAIN for every query type - the name does not
      ///    exist. Any answer added for the same name wins, so a zone can hold both.
      /// </summary>
      public FakeDnsServer WithNxDomain(string name)
      {
         nonExistent_.Add(name.TrimEnd('.').ToLowerInvariant());
         return this;
      }

      private FakeDnsServer Add_(int type, string name, string value)
      {
         string key = Key_(type, name);

         if (!answers_.TryGetValue(key, out List<string> values))
         {
            values = new List<string>();
            answers_[key] = values;
         }

         values.Add(value);
         return this;
      }

      private static string Key_(int type, string name)
      {
         return type + "/" + name.TrimEnd('.').ToLowerInvariant();
      }

      public void Dispose()
      {
         stopping_ = true;
         udp_.Close();
         tcp_.Stop();
      }

      private void ServeUdp_()
      {
         while (!stopping_)
         {
            try
            {
               var remote = new IPEndPoint(IPAddress.Any, 0);
               byte[] query = udp_.Receive(ref remote);
               byte[] response = BuildResponse_(query);
               udp_.Send(response, response.Length, remote);
            }
            catch (SocketException)
            {
               // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
            }
            catch (ObjectDisposedException)
            {
               // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
            }
         }
      }

      private void ServeTcp_()
      {
         while (!stopping_)
         {
            try
            {
               using (TcpClient client = tcp_.AcceptTcpClient())
               using (NetworkStream stream = client.GetStream())
               {
                  byte[] lengthPrefix = ReadExactly_(stream, 2);
                  int queryLength = (lengthPrefix[0] << 8) | lengthPrefix[1];
                  byte[] query = ReadExactly_(stream, queryLength);

                  byte[] response = BuildResponse_(query);
                  stream.Write(new[] { (byte) (response.Length >> 8), (byte) (response.Length & 0xFF) }, 0, 2);
                  stream.Write(response, 0, response.Length);
                  stream.Flush();
               }
            }
            catch (SocketException)
            {
               // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
            }
            catch (ObjectDisposedException)
            {
               // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
            }
            catch (IOException)
            {
               // Deliberately ignored: best effort only, and the outcome of the surrounding operation does not depend on this succeeding.
            }
         }
      }

      private static byte[] ReadExactly_(NetworkStream stream, int count)
      {
         var buffer = new byte[count];
         int read = 0;
         while (read < count)
         {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0)
               throw new IOException("Peer closed mid-message.");
            read += n;
         }
         return buffer;
      }

      private static string QueryName_(byte[] query)
      {
         var name = new StringBuilder();
         int i = 12;
         while (query[i] != 0)
         {
            if (name.Length > 0)
               name.Append('.');
            for (int c = 1; c <= query[i]; c++)
               name.Append((char) query[i + c]);
            i += query[i] + 1;
         }
         return name.ToString().ToLowerInvariant();
      }

      private static int QueryType_(byte[] query)
      {
         int zero = 12;
         while (query[zero] != 0)
            zero += query[zero] + 1;
         return (query[zero + 1] << 8) | query[zero + 2];
      }

      private static int QuestionEnd_(byte[] query)
      {
         int i = 12;
         while (query[i] != 0)
            i += query[i] + 1;
         return i + 5;
      }

      private static void WriteSoaRdata_(List<byte> m)
      {
         var soa = new List<byte>();
         WriteName_(soa, "ns.example.test");
         WriteName_(soa, "hostmaster.example.test");
         for (int f = 0; f < 5; f++)
         {
            soa.Add(0); soa.Add(0); soa.Add(0); soa.Add(60);
         }
         WriteRdata_(m, soa);
      }

      private static void WriteHeaderAndQuestion_(List<byte> m, byte[] query, int answerCount, int authorityCount = 0, int rcode = 0)
      {
         int questionEnd = QuestionEnd_(query);
         m.Add(query[0]); m.Add(query[1]);            // ID
         m.Add(0x81); m.Add((byte) (0x80 | rcode));   // QR|RD|RA, and the RCODE
         m.Add(0); m.Add(1);                          // QDCOUNT
         m.Add(0); m.Add((byte) answerCount);         // ANCOUNT
         m.Add(0); m.Add((byte) authorityCount);      // NSCOUNT
         m.Add(0); m.Add(0);                          // ARCOUNT
         for (int i = 12; i < questionEnd; i++)
            m.Add(query[i]);
      }

      private static void WriteName_(List<byte> m, string name)
      {
         foreach (string label in name.Split('.'))
         {
            m.Add((byte) label.Length);
            foreach (char c in label)
               m.Add((byte) c);
         }
         m.Add(0);
      }

      private static void WriteAnswerHeader_(List<byte> m, int recordType)
      {
         m.Add(0xC0); m.Add(0x0C);                    // owner: the queried name
         m.Add((byte) (recordType >> 8)); m.Add((byte) (recordType & 0xFF));
         m.Add(0); m.Add(1);                          // CLASS IN
         m.Add(0); m.Add(0); m.Add(0); m.Add(60);     // TTL
      }

      private static void WriteRdata_(List<byte> m, IList<byte> rdata)
      {
         m.Add((byte) (rdata.Count >> 8));
         m.Add((byte) (rdata.Count & 0xFF));
         foreach (byte b in rdata)
            m.Add(b);
      }

      private static void WriteTxtAnswer_(List<byte> m, string value)
      {
         WriteAnswerHeader_(m, TypeTxt);

         var rdata = new List<byte>();
         for (int offset = 0; offset < value.Length; offset += 255)
         {
            int length = Math.Min(255, value.Length - offset);
            rdata.Add((byte) length);
            for (int c = 0; c < length; c++)
               rdata.Add((byte) value[offset + c]);
         }

         // An empty TXT is still one zero-length character-string, not nothing.
         if (rdata.Count == 0)
            rdata.Add(0);

         WriteRdata_(m, rdata);
      }

      private static void WriteMxAnswer_(List<byte> m, string value)
      {
         WriteAnswerHeader_(m, TypeMx);

         string[] parts = value.Split(new[] { ' ' }, 2);
         int preference = int.Parse(parts[0]);

         var rdata = new List<byte> { (byte) (preference >> 8), (byte) (preference & 0xFF) };

         // The exchange is written as an uncompressed name. A pointer would be shorter
         // and is what a real server sends, but only when the target actually appears
         // earlier in the packet - writing one that does not is how a hand-rolled DNS
         // server produces answers a resolver reports as malformed.
         foreach (string label in parts[1].TrimEnd('.').Split('.'))
         {
            rdata.Add((byte) label.Length);
            foreach (char c in label)
               rdata.Add((byte) c);
         }
         rdata.Add(0);

         WriteRdata_(m, rdata);
      }

      private static void WriteAAnswer_(List<byte> m, string address)
      {
         WriteAnswerHeader_(m, TypeA);
         WriteRdata_(m, IPAddress.Parse(address).GetAddressBytes());
      }

      /// <summary>
      ///    The questions asked so far, oldest first, as "type/name". A snapshot: the
      ///    serve threads keep appending to the real list.
      /// </summary>
      public List<string> Queries
      {
         get
         {
            lock (queries_)
               return new List<string>(queries_);
         }
      }

      /// <summary>
      ///    Forgets every recorded question. Called at the point a test starts counting,
      ///    so unrelated lookups made while the server was starting are not counted.
      /// </summary>
      public void ClearQueries()
      {
         lock (queries_)
            queries_.Clear();
      }

      private byte[] BuildResponse_(byte[] query)
      {
         string name = QueryName_(query);
         int type = QueryType_(query);

         lock (queries_)
            queries_.Add(Key_(type, name));

         if (answers_.TryGetValue(Key_(type, name), out List<string> values) && values.Count > 0)
         {
            var m = new List<byte>();
            WriteHeaderAndQuestion_(m, query, values.Count);

            foreach (string value in values)
            {
               if (type == TypeTxt)
                  WriteTxtAnswer_(m, value);
               else if (type == TypeMx)
                  WriteMxAnswer_(m, value);
               else
                  WriteAAnswer_(m, value);
            }

            return m.ToArray();
         }

         // A name declared non-existent: RCODE 3, with the same SOA in AUTHORITY so
         // the negative answer is cacheable (RFC 2308 again). Checked after the
         // answer table so a name can be given records and still have OTHER names
         // beneath it be absent.
         if (nonExistent_.Contains(name.TrimEnd('.').ToLowerInvariant()))
         {
            var nxdomain = new List<byte>();
            WriteHeaderAndQuestion_(nxdomain, query, 0, 1, 3);
            WriteAnswerHeader_(nxdomain, 6);
            WriteSoaRdata_(nxdomain);
            return nxdomain.ToArray();
         }

         // Everything else: NODATA - NOERROR, no answers, an SOA in the AUTHORITY
         // section per RFC 2308. See the class comment for why the SOA is required.
         var nodata = new List<byte>();
         WriteHeaderAndQuestion_(nodata, query, 0, 1);
         WriteAnswerHeader_(nodata, 6);
         WriteSoaRdata_(nodata);
         return nodata.ToArray();
      }
   }
}
