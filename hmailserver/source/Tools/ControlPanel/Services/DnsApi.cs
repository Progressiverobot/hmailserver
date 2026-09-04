// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// The two dnsapi.dll entry points the Control Panel resolves records with,
   /// declared once with source-generated marshalling. The record walking stays
   /// with the callers, because what a TXT answer and an MX answer look like in
   /// memory is their business and is documented next to each.
   /// </summary>
   internal static partial class DnsApi
   {
      [LibraryImport("dnsapi.dll", EntryPoint = "DnsQuery_W", StringMarshalling = StringMarshalling.Utf16)]
      public static partial int DnsQuery_W(string name, ushort type, uint options,
         IntPtr extra, out IntPtr queryResults, IntPtr reserved);

      [LibraryImport("dnsapi.dll", EntryPoint = "DnsRecordListFree")]
      public static partial void DnsRecordListFree(IntPtr recordList, int freeType);
   }
}
