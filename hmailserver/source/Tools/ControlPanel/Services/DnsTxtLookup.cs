using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// TXT-record lookup through the Windows resolver (DnsQuery_W in dnsapi.dll) -
   /// the same resolver the server itself uses, and no new package dependency.
   ///
   /// The whole value of this class is that it keeps apart three answers a naive
   /// lookup collapses into one "it didn't work": the record does not exist
   /// (publish it, or keep waiting for propagation), the record exists but holds
   /// the wrong value (fix the DNS entry - waiting will not help), and the lookup
   /// itself failed (the answer says nothing about the record; fix the network or
   /// the DNS server and ask again). An administrator in the middle of a DKIM
   /// rotation acts differently on each of the three, and being told the wrong
   /// one sends them down the wrong path.
   ///
   /// Nothing here throws: every method reports failure through the returned
   /// result object, so a caller on the UI thread never needs a try/catch.
   /// </summary>
   public static class DnsTxtLookup
   {
      public enum LookupStatus
      {
         /// <summary>At least one TXT record exists; see Records.</summary>
         Found,

         /// <summary>DNS answered: there is no TXT record at this name (the name
         /// does not exist, or exists without TXT data).</summary>
         NoRecord,

         /// <summary>No answer was obtained at all; see Error. This outcome says
         /// nothing about whether the record exists.</summary>
         Failed
      }

      public class LookupResult
      {
         public LookupStatus Status { get; set; }

         /// <summary>One entry per TXT record, with each record's character-strings
         /// already concatenated the way DNS consumers read them (no separator).</summary>
         public List<string> Records { get; } = new();

         /// <summary>Human-readable reason when Status is Failed.</summary>
         public string Error { get; set; }
      }

      public enum MatchStatus
      {
         /// <summary>A record with the expected value is published and visible.</summary>
         FoundAndMatches,

         /// <summary>TXT records exist at the name, but none carries the expected
         /// value. Waiting for propagation will not fix this.</summary>
         FoundButDifferent,

         /// <summary>No TXT record at the name (yet).</summary>
         NoRecord,

         /// <summary>The lookup itself failed; see Error. Says nothing about the record.</summary>
         LookupFailed
      }

      public class MatchResult
      {
         public MatchStatus Status { get; set; }

         /// <summary>What was actually found, for showing the administrator the
         /// mismatching value instead of just declaring a mismatch.</summary>
         public List<string> Records { get; set; } = new();

         /// <summary>Human-readable reason when Status is LookupFailed.</summary>
         public string Error { get; set; }
      }

      /// <summary>
      /// Looks up all TXT records for <paramref name="host"/>. Blocks for up to
      /// the resolver's own timeout (several seconds when the record is missing,
      /// which is the common case right after publishing), so call it from a
      /// worker thread rather than the UI thread.
      /// </summary>
      public static LookupResult Query(string host)
      {
         var result = new LookupResult();

         if (string.IsNullOrWhiteSpace(host))
         {
            result.Status = LookupStatus.Failed;
            result.Error = "No host name to look up.";
            return result;
         }

         IntPtr records = IntPtr.Zero;
         try
         {
            // DNS_QUERY_BYPASS_CACHE: an administrator runs this moments after
            // publishing a record, and the local resolver caches the *absence* of
            // a record (negative caching) just like it caches records. A cached
            // answer would keep reporting "not found" for the whole negative TTL
            // after the record has in fact gone live - the exact moment this
            // lookup matters most. DNS_QUERY_TREAT_AS_FQDN: without it the
            // resolver may append this machine's DNS suffix and answer for a
            // different name than the one the administrator actually published.
            int status = DnsQuery_W(host.Trim(), DnsTypeText,
               DnsQueryBypassCache | DnsQueryTreatAsFqdn, IntPtr.Zero, out records, IntPtr.Zero);

            if (status == DnsErrorRcodeNameError || status == DnsInfoNoRecords)
            {
               // These are real answers from the DNS ("the name does not exist" /
               // "the name has no TXT records"), not failures to ask, so they must
               // not fall into the Failed bucket below.
               result.Status = LookupStatus.NoRecord;
               return result;
            }

            if (status != 0)
            {
               result.Status = LookupStatus.Failed;
               result.Error = DescribeStatus(status);
               return result;
            }

            ReadTxtRecords(records, result.Records);

            // A successful query can still come back without TXT data - for
            // example when only a CNAME at the name resolved. To the caller that
            // is "no record", not "found".
            result.Status = result.Records.Count > 0 ? LookupStatus.Found : LookupStatus.NoRecord;
            return result;
         }
         catch (Exception ex)
         {
            // Contract: never throw into the caller. Whatever went wrong locally
            // is a failed lookup, not a statement about the record.
            result.Records.Clear();
            result.Status = LookupStatus.Failed;
            result.Error = ex.Message;
            return result;
         }
         finally
         {
            if (records != IntPtr.Zero)
               DnsRecordListFree(records, DnsFreeRecordList);
         }
      }

      /// <summary>
      /// Looks up the TXT records at <paramref name="host"/> and answers whether
      /// any of them carries <paramref name="expectedValue"/> - the four-way
      /// verdict an administrator needs while waiting out DNS propagation.
      /// Blocks like <see cref="Query"/>; call from a worker thread.
      /// </summary>
      public static MatchResult CheckExpected(string host, string expectedValue)
      {
         LookupResult lookup = Query(host);

         var result = new MatchResult { Records = lookup.Records, Error = lookup.Error };

         switch (lookup.Status)
         {
            case LookupStatus.Failed:
               result.Status = MatchStatus.LookupFailed;
               return result;

            case LookupStatus.NoRecord:
               result.Status = MatchStatus.NoRecord;
               return result;
         }

         foreach (string record in lookup.Records)
         {
            if (Matches(record, expectedValue))
            {
               result.Status = MatchStatus.FoundAndMatches;
               return result;
            }
         }

         result.Status = MatchStatus.FoundButDifferent;
         return result;
      }

      /// <summary>
      /// True when a published TXT record and the record we generated denote the
      /// same DKIM key. Compared loosely on purpose: DNS provider consoles
      /// reformat what the administrator pastes - whitespace around tags appears
      /// or disappears, and some consoles add tags of their own (t=, s=) - so an
      /// exact string comparison would call a perfectly good record a mismatch
      /// and stall the rotation. What actually decides whether signatures verify
      /// is the key material in the p= tag, so that is what is compared when both
      /// sides have one; whole-string comparison (ignoring whitespace) remains as
      /// the fallback for values this parser does not understand.
      /// </summary>
      public static bool Matches(string recordValue, string expectedValue)
      {
         if (recordValue == null || expectedValue == null)
            return false;

         string foundKey = ExtractTag(recordValue, "p");
         string expectedKey = ExtractTag(expectedValue, "p");
         if (foundKey != null && expectedKey != null)
            return foundKey == expectedKey; // base64 is case-sensitive; compare exactly

         return StripWhitespace(recordValue) == StripWhitespace(expectedValue);
      }

      // ---- native lookup -------------------------------------------------------

      private const ushort DnsTypeText = 0x0010;            // DNS_TYPE_TEXT
      private const uint DnsQueryBypassCache = 0x00000008;  // DNS_QUERY_BYPASS_CACHE
      private const uint DnsQueryTreatAsFqdn = 0x00001000;  // DNS_QUERY_TREAT_AS_FQDN
      private const int DnsErrorRcodeNameError = 9003;      // DNS_ERROR_RCODE_NAME_ERROR (NXDOMAIN)
      private const int DnsInfoNoRecords = 9501;            // DNS_INFO_NO_RECORDS (name exists, no TXT)
      private const int DnsFreeRecordList = 1;              // DNS_FREE_TYPE::DnsFreeRecordList

      /// <summary>
      /// The fixed header of a native DNS_RECORDW. Sequential layout reproduces
      /// the native field offsets on both x86 and x64 (the two IntPtrs and the
      /// three DWORDs align identically to the C declaration), and the per-type
      /// data union starts immediately after it - at Marshal.SizeOf of this
      /// struct - on both architectures.
      /// </summary>
      [StructLayout(LayoutKind.Sequential)]
      private struct DnsRecordHeader
      {
         public IntPtr Next;
         public IntPtr Name;
         public ushort Type;
         public ushort DataLength;
         public uint Flags;
         public uint Ttl;
         public uint Reserved;
      }

      [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
      private static extern int DnsQuery_W(
         string name, ushort type, uint options,
         IntPtr extra, out IntPtr queryResults, IntPtr reserved);

      [DllImport("dnsapi.dll")]
      private static extern void DnsRecordListFree(IntPtr recordList, int freeType);

      private static void ReadTxtRecords(IntPtr first, List<string> into)
      {
         int headerSize = Marshal.SizeOf<DnsRecordHeader>();

         for (IntPtr cursor = first; cursor != IntPtr.Zero; )
         {
            DnsRecordHeader header = Marshal.PtrToStructure<DnsRecordHeader>(cursor);

            // The result list also carries any CNAMEs the resolver followed on
            // the way to the answer; only the TXT records are the answer.
            if (header.Type == DnsTypeText)
            {
               // DNS_TXT_DATAW: a DWORD string count followed by an inline array
               // of PWSTR. The count sits at the start of the record's data area;
               // the pointer array begins at the next pointer-aligned offset,
               // which on both x86 and x64 is IntPtr.Size into the data area.
               IntPtr data = cursor + headerSize;
               int stringCount = Marshal.ReadInt32(data);
               IntPtr stringArray = data + IntPtr.Size;

               // One logical TXT record longer than 255 octets is carried as
               // several character-strings on the wire, and DNS requires the
               // reader to concatenate them with no separator. Every 2048-bit
               // DKIM record is long enough to be split like this, so getting
               // the join wrong would report every healthy record as a mismatch.
               var value = new StringBuilder();
               for (int i = 0; i < stringCount; i++)
               {
                  IntPtr chunk = Marshal.ReadIntPtr(stringArray, i * IntPtr.Size);
                  if (chunk != IntPtr.Zero)
                     value.Append(Marshal.PtrToStringUni(chunk));
               }

               into.Add(value.ToString());
            }

            cursor = header.Next;
         }
      }

      private static string DescribeStatus(int status)
      {
         string message = new System.ComponentModel.Win32Exception(status).Message;
         return message + " (error " + status + ")";
      }

      // ---- value comparison ----------------------------------------------------

      private static string ExtractTag(string txtValue, string tag)
      {
         foreach (string part in txtValue.Split(';'))
         {
            string flat = StripWhitespace(part);
            if (flat.Length > tag.Length && flat[tag.Length] == '=' &&
                string.Equals(flat.Substring(0, tag.Length), tag, StringComparison.OrdinalIgnoreCase))
               return flat.Substring(tag.Length + 1);
         }

         return null;
      }

      private static string StripWhitespace(string value)
      {
         var flat = new StringBuilder(value.Length);
         foreach (char c in value)
            if (!char.IsWhiteSpace(c))
               flat.Append(c);
         return flat.ToString();
      }
   }
}
