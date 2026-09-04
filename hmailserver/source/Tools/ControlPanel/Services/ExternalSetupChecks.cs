// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>Ordered so that aggregating an item is Max over its findings:
   /// the last member is the worst.</summary>
   public enum SetupItemState
   {
      NotNeeded,
      Done,
      CannotTell,
      ActionNeeded
   }

   /// <summary>One per-domain / per-port / per-certificate line inside an item.</summary>
   public sealed class SetupFinding
   {
      public SetupItemState State;
      public string Text;
   }

   /// <summary>
   /// One external prerequisite: what it is for, its aggregated state, the
   /// concrete thing to do about it, and the navigation key of the page that
   /// owns its settings.
   /// </summary>
   public sealed class SetupItem
   {
      public string Title;
      public SetupItemState State;

      /// <summary>One sentence: what the feature is for.</summary>
      public string Purpose;

      /// <summary>The concrete thing to do, and where to do it.</summary>
      public string Action;

      /// <summary>Navigation key of the page that owns the settings, or null.</summary>
      public string Page;

      public readonly List<SetupFinding> Findings = new();

      public void Add(SetupItemState state, string text) => Findings.Add(new SetupFinding { State = state, Text = text });

      /// <summary>The item is as bad as its worst finding.</summary>
      public void AggregateFromFindings()
      {
         if (Findings.Count > 0)
            State = Findings.Max(f => f.State);
      }
   }

   /// <summary>
   /// The computation behind "what does this server still need done OUTSIDE it":
   /// eleven prerequisite checks (DNS records, key and CA files, trusted lists,
   /// identity-provider key material) read from COM, hMailServer.INI and the
   /// local disk, each yielding one of four honest states.
   ///
   /// This lived inside <c>Views/ExternalSetupView</c> until the dashboard needed
   /// the same answer for its "needs attention" card. It is one class used by
   /// both, rather than two copies, because two copies of a checklist drift and
   /// then disagree in front of the administrator - which is worse than no
   /// checklist. The External setup page renders every item; the dashboard
   /// renders the aggregate and the items that need action.
   ///
   /// Usage, in three steps that mirror the threading rules of the data:
   ///
   ///   1. <see cref="Run"/> on the UI thread - it reads apartment-bound COM
   ///      objects the way every page does, plus INI and local files, and
   ///      schedules a DNS TXT probe for each record it wants to verify.
   ///   2. <see cref="ResolveDnsLookups"/> on a background thread - each lookup
   ///      can block for the resolver's full timeout, several seconds when the
   ///      record is missing, which is the common case right after publishing.
   ///      It touches no COM object: everything the probes need was copied into
   ///      plain strings during the build.
   ///   3. <see cref="ApplyDnsResults"/> back on the UI thread - it rewrites the
   ///      "checking..." findings into verdicts and re-aggregates the items the
   ///      caller is rendering.
   ///
   /// Honesty rule, learned the hard way from fifteen documented overclaims in
   /// this project: nothing here asserts a status it did not actually determine.
   /// Where the answer is knowable from COM, the local filesystem or a TXT
   /// lookup it is checked; where it is not, the item says "Cannot tell" and
   /// exactly what to check instead.
   /// </summary>
   public sealed class ExternalSetupChecks
   {
      private ExternalSetupChecks()
      {
      }

      /// <summary>
      /// Reads the configuration and builds the checklist. Call on the UI
      /// thread: the COM objects it reads are apartment-bound there, and the
      /// synchronous part is the same cost every settings page pays on open.
      /// </summary>
      public static ExternalSetupChecks Run()
      {
         var checks = new ExternalSetupChecks();
         checks.items_ = checks.BuildItems();
         return checks;
      }

      /// <summary>The checklist, one item per external prerequisite.</summary>
      public IReadOnlyList<SetupItem> Items => items_;

      /// <summary>How many COM/INI reads failed while building; the items are
      /// incomplete when this is non-zero.</summary>
      public int FailedReads => failedReads_;

      /// <summary>The first read failure, described for the administrator, or null.</summary>
      public string FirstError => firstError_;

      /// <summary>
      /// How many DNS TXT lookups this run scheduled. Zero means the items are
      /// already final; otherwise the findings those lookups belong to say
      /// "checking..." until <see cref="ApplyDnsResults"/> rewrites them.
      /// </summary>
      public int DnsLookupCount => probes_.Count;

      /// <summary>
      /// Performs the scheduled DNS lookups, blocking until all have answered
      /// or timed out. Call on a background thread; it reads only the plain
      /// strings captured at build time, never a COM object.
      /// </summary>
      public void ResolveDnsLookups()
      {
         Parallel.ForEach(probes_, probe => probe.Result = DnsTxtLookup.Query(probe.Host));
      }

      /// <summary>
      /// Rewrites the "checking..." findings from the completed lookups and
      /// re-aggregates every item's state. Call on the UI thread, after
      /// <see cref="ResolveDnsLookups"/>, because it mutates the findings the
      /// caller is rendering.
      /// </summary>
      public void ApplyDnsResults()
      {
         foreach (DnsProbe probe in probes_)
            ApplyProbe(probe);

         foreach (SetupItem item in items_)
            item.AggregateFromFindings();
      }

      /// <summary>The state as the word the administrator reads. The word is
      /// always in the rendered text - never only a colour or a shape.</summary>
      public static string StateWord(SetupItemState state)
      {
         switch (state)
         {
            case SetupItemState.Done: return "Done";
            case SetupItemState.NotNeeded: return "Not needed";
            case SetupItemState.ActionNeeded: return "Action needed";
            default: return "Cannot tell";
         }
      }

      /// <summary>
      /// Reuses the application-wide status vocabulary (colour token + distinct
      /// shape) so the checklist's badges mean the same thing they mean everywhere
      /// else. The words, though, are the checklist's own four states.
      /// </summary>
      public static StatusLevel LevelFor(SetupItemState state)
      {
         switch (state)
         {
            case SetupItemState.Done: return StatusLevel.Good;
            case SetupItemState.NotNeeded: return StatusLevel.Normal;
            case SetupItemState.ActionNeeded: return StatusLevel.Warning;
            default: return StatusLevel.Information;
         }
      }

      // ---- reading the configuration -------------------------------------------

      private int failedReads_;
      private string firstError_;

      /// <summary>The items of this run, kept so the DNS pass can update them in place.</summary>
      private List<SetupItem> items_ = new();

      /// <summary>DNS lookups scheduled during the current build; run after rendering.</summary>
      private readonly List<DnsProbe> probes_ = new();

      /// <summary>
      /// File and INI checks are only meaningful on the machine the server runs
      /// on: every path this page inspects is a path the SERVER will open, and
      /// IniFeatureStore locates hMailServer.INI through the local registry and
      /// service table. Connected to a remote host, checking this machine's disk
      /// would answer a question nobody asked - so those checks honestly degrade
      /// to "Cannot tell" instead.
      /// </summary>
      private static bool IsLocalServer() => ServerSession.IsLocalSession;

      private T Read<T>(Func<T> read)
      {
         try
         {
            return read();
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
            return default;
         }
      }

      /// <summary>What this page needs to know about one hosted domain.</summary>
      private sealed class DomainDkim
      {
         // Every string starts empty rather than null. These are filled from an
         // object initialiser whose right-hand sides are dynamic COM reads, which the
         // compiler cannot see as assignments - hence CS0649 on the last of them - and
         // more usefully, a domain row that fails to read part-way would otherwise
         // leave a null here for code that goes straight to .Length.
         public string Name = "";
         public bool Active;
         public bool SignEnabled;
         public string Selector = "";
         public string KeyFile = "";
         public string SecondarySelector = "";
         public string SecondaryKeyFile = "";
      }

      private List<DomainDkim> ReadDomains()
      {
         var result = new List<DomainDkim>();
         dynamic domains = null;
         try
         {
            domains = ServerSession.Current.Application.Domains;
            int count = (int)domains.Count;
            for (int i = 0; i < count; i++)
            {
               dynamic domain = domains.Item[i];
               try
               {
                  result.Add(new DomainDkim
                  {
                     Name = ((string)domain.Name ?? "").Trim(),
                     Active = (bool)domain.Active,
                     SignEnabled = (bool)domain.DKIMSignEnabled,
                     Selector = ((string)domain.DKIMSelector ?? "").Trim(),
                     KeyFile = ((string)domain.DKIMPrivateKeyFile ?? "").Trim(),
                     SecondarySelector = ((string)domain.DKIMSecondarySelector ?? "").Trim(),
                     SecondaryKeyFile = ((string)domain.DKIMSecondaryPrivateKeyFile ?? "").Trim()
                  });
               }
               finally
               {
                  ServerSession.Release((object)domain);
               }
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
         }
         finally
         {
            ServerSession.Release((object)domains);
         }
         return result;
      }

      /// <summary>
      /// True when a PEM private key file is passphrase-protected. These are the
      /// two markers OpenSSL's own loader keys off: the PKCS#8 "ENCRYPTED PRIVATE
      /// KEY" block, and the PKCS#1 "Proc-Type: 4,ENCRYPTED" header. Reading only
      /// the head of the file: both markers sit before the key material.
      /// </summary>
      private static bool LooksLikeEncryptedPem(string path)
      {
         using var reader = new StreamReader(path);
         var buffer = new char[4096];
         int read = reader.Read(buffer, 0, buffer.Length);
         string head = new string(buffer, 0, Math.Max(read, 0));
         return head.Contains("ENCRYPTED PRIVATE KEY") || head.Contains("Proc-Type: 4,ENCRYPTED");
      }

      // ---- DNS probes -----------------------------------------------------------

      private enum ProbeKind
      {
         DkimPrimary,
         DkimSecondary,
         MtaSts
      }

      /// <summary>
      /// One DNS TXT lookup scheduled while the items were built, applied to its
      /// finding when the background pass completes. The probes run off the UI
      /// thread because DnsTxtLookup.Query blocks for the resolver's own timeout
      /// - several seconds when the record is missing, which is the common case
      /// right after publishing, and the one this page exists for.
      /// </summary>
      private sealed class DnsProbe
      {
         public ProbeKind Kind;
         public string Host;
         public string Label;

         /// <summary>Expected DKIM record ("v=DKIM1; p=..."), derived from the
         /// domain's private key file when it is readable, or null when only the
         /// record's presence can be judged.</summary>
         public string Expected;

         public SetupFinding Target;
         public DnsTxtLookup.LookupResult Result;
      }

      /// <summary>
      /// The DKIM record this domain's key SHOULD publish, derived from the
      /// private key file - the p= tag is the SubjectPublicKeyInfo of the key
      /// pair, so it can be computed without any secret leaving this process.
      /// Null when the file is unreadable or encrypted, in which case the DNS
      /// check degrades to presence-only rather than guessing at the value.
      /// </summary>
      private static string TryDeriveDkimExpectedRecord(string keyFile)
      {
         try
         {
            string pem = File.ReadAllText(keyFile);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return "v=DKIM1; p=" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
         }
         catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
         {
            return null;
         }
      }

      /// <summary>RFC 8461: a policy-discovery record begins "v=STSv1".</summary>
      private static bool HasStsMarker(IEnumerable<string> records)
         => records.Any(r => (r ?? "").Replace(" ", "").StartsWith("v=STSv1", StringComparison.OrdinalIgnoreCase));

      /// <summary>
      /// Rewrites a probe's finding from "checking..." into the four-way verdict
      /// DnsTxtLookup keeps apart on purpose: found-and-right, found-but-wrong
      /// (waiting will not help), not found (publish, or keep waiting), and
      /// lookup-failed (which says nothing about the record at all).
      /// </summary>
      private static void ApplyProbe(DnsProbe probe)
      {
         SetupFinding finding = probe.Target;
         DnsTxtLookup.LookupResult result = probe.Result;

         if (result == null || result.Status == DnsTxtLookup.LookupStatus.Failed)
         {
            finding.State = SetupItemState.CannotTell;
            finding.Text = probe.Label + ": the DNS lookup for " + probe.Host + " failed - "
               + (result != null ? result.Error : "no result.")
               + " This says nothing about whether the record exists; check this machine's DNS resolver and press Refresh.";
            return;
         }

         bool matches = probe.Expected != null && result.Records.Any(r => DnsTxtLookup.Matches(r, probe.Expected));

         switch (probe.Kind)
         {
            case ProbeKind.DkimPrimary:
               if (result.Status == DnsTxtLookup.LookupStatus.NoRecord)
               {
                  finding.State = SetupItemState.ActionNeeded;
                  finding.Text = probe.Label + ": no TXT record was found at " + probe.Host
                     + ", so receivers cannot verify this domain's signatures and may treat its mail as forged. "
                     + "Publish the record at your DNS host - or, if you just did, wait for propagation and press Refresh.";
               }
               else if (probe.Expected == null)
               {
                  finding.State = SetupItemState.CannotTell;
                  finding.Text = probe.Label + ": a TXT record exists at " + probe.Host
                     + ", but whether it carries the right public key could not be verified - the private key file was not readable from this panel.";
               }
               else if (matches)
               {
                  finding.State = SetupItemState.Done;
                  finding.Text = probe.Label + ": the TXT record at " + probe.Host + " is published and carries this key's public key.";
               }
               else
               {
                  finding.State = SetupItemState.ActionNeeded;
                  finding.Text = probe.Label + ": TXT record(s) exist at " + probe.Host
                     + " but none carries this key's public key, so signatures fail verification. Fix the record's value - waiting for propagation will not help.";
               }
               return;

            case ProbeKind.DkimSecondary:
               if (result.Status == DnsTxtLookup.LookupStatus.NoRecord)
               {
                  finding.State = SetupItemState.ActionNeeded;
                  finding.Text = probe.Label + ": the secondary key is staged but no TXT record exists at " + probe.Host
                     + " yet. Publish it and let it propagate BEFORE promoting the secondary key, or mail will sign with a key the world cannot look up.";
               }
               else if (probe.Expected == null)
               {
                  finding.State = SetupItemState.CannotTell;
                  finding.Text = probe.Label + ": a TXT record exists at " + probe.Host
                     + ", but it could not be compared with the staged secondary key from this panel. Confirm the value before promoting.";
               }
               else if (matches)
               {
                  finding.State = SetupItemState.Done;
                  finding.Text = probe.Label + ": the rotation record at " + probe.Host
                     + " is published and matches the staged secondary key. Promote whenever it has propagated everywhere.";
               }
               else
               {
                  finding.State = SetupItemState.ActionNeeded;
                  finding.Text = probe.Label + ": TXT record(s) exist at " + probe.Host
                     + " but none matches the staged secondary key. Fix the value before promoting - waiting will not help.";
               }
               return;

            default: // MtaSts
               if (result.Status == DnsTxtLookup.LookupStatus.NoRecord)
               {
                  finding.State = SetupItemState.ActionNeeded;
                  finding.Text = probe.Label + ": no TXT record at " + probe.Host
                     + ", so sending servers never discover the policy and the hosting does nothing for this domain. "
                     + "Publish it (v=STSv1; id=...) - or, if you just did, wait for propagation and press Refresh.";
               }
               else if (HasStsMarker(result.Records))
               {
                  finding.State = SetupItemState.Done;
                  finding.Text = probe.Label + ": the " + probe.Host + " TXT record is published. Not checkable from here: "
                     + "that mta-sts." + probe.Label + " resolves to this server, and that the certificate covers that name.";
               }
               else
               {
                  finding.State = SetupItemState.ActionNeeded;
                  finding.Text = probe.Label + ": TXT record(s) exist at " + probe.Host
                     + " but none begins with v=STSv1, so senders will not recognise a policy there. Fix the record's value.";
               }
               return;
         }
      }

      // ---- the checklist itself -------------------------------------------------

      private List<SetupItem> BuildItems()
      {
         bool local = IsLocalServer();

         // The INI store finds hMailServer.INI through this machine's registry
         // and service table, so it is only consulted when this machine IS the
         // server; otherwise it would happily describe some other install.
         IniFeatureStore ini = local ? new IniFeatureStore() : null;
         bool iniReadable = ini != null && ini.IsAvailable;
         string iniExcuse = local
            ? "hMailServer.INI was not found on this machine, so its settings cannot be read from here."
            : "the Control Panel is connected to '" + (ServerSession.Current != null ? ServerSession.Current.Host : "?")
              + "'; hMailServer.INI and files on disk can only be inspected on the server machine itself.";

         List<DomainDkim> domains = ReadDomains();

         var items = new List<SetupItem>
         {
            DkimItem(domains, local),
            ArcFilteringItem(),
            ArcSealingItem(domains, ini, iniReadable, iniExcuse),
            MtaStsItem(domains, ini, iniReadable, iniExcuse),
            TlsRptItem(ini, iniReadable, iniExcuse),
            DaneItem(ini, iniReadable),
            EncryptedKeysItem(local, iniExcuse),
            AcmeItem(ini, iniReadable, iniExcuse),
            ClientCertificatesItem(local, iniExcuse),
            OAuth2Item(ini, iniReadable, iniExcuse, local),
            AutoconfigItem(ini, iniReadable, iniExcuse),
            ServiceAccountItem(ini, iniReadable, iniExcuse, local)
         };

         return items;
      }

      /// <summary>
      /// The Windows account the service logs on as.
      ///
      /// It belongs on this checklist for the reason the checklist exists: saving
      /// ServiceAccountName changes nothing by itself. The value is read once, by
      /// ServiceManager::RegisterService, which runs only from
      /// <c>hMailServer.exe /Register</c> - so between saving it and running that
      /// command, elevated, the configuration and the machine disagree and the
      /// machine wins. The account also needs a privilege and a set of file rights
      /// that are granted in Windows, not here.
      ///
      /// The state is read from the Service Control Manager rather than inferred,
      /// so this reports what the service IS running as, not what was asked for.
      /// </summary>
      private SetupItem ServiceAccountItem(IniFeatureStore ini, bool iniReadable, string iniExcuse, bool local)
      {
         var item = new SetupItem
         {
            Title = "Windows service account - a least-privilege account needs granting outside hMailServer",
            Purpose = "By default the service runs as LocalSystem, the most privileged account on the machine, so anything reachable through SMTP, IMAP or POP3 is reachable with full control of the computer. A dedicated account contains that.",
            Action = "Set an account on the Server limits & expert settings page - NT SERVICE\\hMailServer needs no password - then, from an elevated "
                     + "Command Prompt, run hMailServer.exe /Register and restart the service. Grant that account \"Log on as a service\" "
                     + "(secpol.msc > Local Policies > User Rights Assignment), read access to the program folder, and full control of the data, "
                     + "log and database folders. An external database needs a login for it as well.",
            Page = "hardening"
         };

         if (!local)
         {
            item.Add(SetupItemState.CannotTell, "The service account cannot be read: " + iniExcuse);
            item.AggregateFromFindings();
            return item;
         }

         WindowsServiceInfo service = WindowsServiceInfo.Query();

         if (!service.Exists)
         {
            item.Add(SetupItemState.CannotTell,
               service.Error != null
                  ? "The Service Control Manager could not be queried, so the account the service runs as is unknown: " + service.Error
                  : "Windows does not report an hMailServer service on this machine, so there is no service to re-register yet.");
            item.AggregateFromFindings();
            return item;
         }

         string configured = iniReadable ? ini.Read("ServiceAccountName", "").Trim() : "";
         bool runningAsLocalSystem = string.Equals(
            WindowsServiceInfo.Canonical(service.StartName, Environment.MachineName), "localsystem",
            StringComparison.OrdinalIgnoreCase);

         if (!iniReadable)
         {
            // The SCM half is still knowable and is the half that matters, so this
            // degrades to a partial answer rather than to nothing at all.
            item.Add(runningAsLocalSystem ? SetupItemState.ActionNeeded : SetupItemState.Done,
               "The service is running as " + WindowsServiceInfo.DescribeAccount(service.StartName)
               + ". The requested account could not be read: " + iniExcuse);
            item.AggregateFromFindings();
            return item;
         }

         if (configured.Length == 0)
         {
            if (runningAsLocalSystem)
            {
               item.Add(SetupItemState.ActionNeeded,
                  "The service is running as " + WindowsServiceInfo.DescribeAccount(service.StartName)
                  + ", and no other account is requested. This is the default and it works; it is on this list because "
                  + "moving to a dedicated account is the single largest reduction in what a compromise of the mail "
                  + "server is worth, and it cannot be done from inside hMailServer.");
            }
            else
            {
               item.Add(SetupItemState.Done,
                  "The service is running as " + service.StartName + ", which is not LocalSystem, so it is already "
                  + "contained. No account is requested in hMailServer.INI, so re-registering the service would leave "
                  + "that unchanged.");
            }
         }
         else if (WindowsServiceInfo.SameAccount(configured, service.StartName, Environment.MachineName))
         {
            item.Add(SetupItemState.Done,
               "The service is running as " + service.StartName + ", which is the account configured in "
               + "hMailServer.INI. The registration step has been done.");
         }
         else
         {
            item.Add(SetupItemState.ActionNeeded,
               "Not applied. hMailServer.INI asks for " + configured + " but the service is running as "
               + WindowsServiceInfo.DescribeAccount(service.StartName) + ". This setting is read only when the service "
               + "is registered, so it will not take effect on a restart - run hMailServer.exe /Register from an "
               + "elevated Command Prompt, then restart the service.");
         }

         if (configured.Length > 0 && ini.Read("ServiceAccountPassword", "").Trim().Length > 0)
         {
            // ActionNeeded, not CannotTell. This finding is a fact the panel just
            // established by reading the value's length - the one thing it is not is
            // unknown, and CannotTell means unknown everywhere else in this file.
            //
            // The ranking is what made it actively harmful. AggregateFromFindings
            // takes Max over the enum, where CannotTell (2) outranks Done (1) but
            // ranks below ActionNeeded (3). So a service correctly re-registered
            // under the configured account, carrying a leftover plaintext password,
            // aggregated to "Cannot tell": the completed prerequisite was reported
            // as unverifiable AND the leftover secret was never raised as an action.
            // One state, both halves wrong.
            //
            // Only emptiness is tested; the value is never read into the UI.
            item.Add(SetupItemState.ActionNeeded,
               "A service account password is stored in hMailServer.INI in plain text - the DPAPI setting does not "
               + "cover it, because the registration step has nowhere else to read it from. Now that the service has "
               + "been registered, Windows keeps its own copy, so clear the value on the Server limits & expert "
               + "settings page. A virtual account (NT SERVICE\\hMailServer) or a group managed service account "
               + "avoids the question entirely.");
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// DKIM signing. The server can hold the key and sign every message, but a
      /// signature is only worth anything once the matching public key is
      /// published in the domain's DNS zone - and DNS is the one place the
      /// server has no write access to. Verified against DKIMSigner::Sign: with
      /// signing enabled but no selector or key file, the server reports error
      /// 5305 and the mail simply goes out unsigned.
      /// </summary>
      private SetupItem DkimItem(List<DomainDkim> domains, bool local)
      {
         var item = new SetupItem
         {
            Title = "DKIM signing - publish each domain's public key in DNS",
            Purpose = "Signs outgoing mail so receivers can verify it really came from your domain; without the DNS record every signature fails verification.",
            Action = "At your DNS host, publish a TXT record named <selector>._domainkey.<domain> containing the public key "
                     + "(v=DKIM1; k=rsa; p=...). This page looks the record up through the Windows resolver, and where the key file is "
                     + "readable it also checks that the published key is the RIGHT key - a wrong value cannot be fixed by waiting.",
            Page = "domains"
         };

         var signing = domains.Where(d => d.SignEnabled).ToList();

         if (domains.Count == 0)
         {
            item.Add(SetupItemState.NotNeeded, "No domains are configured, so there is nothing to sign and no record to publish.");
         }
         else if (signing.Count == 0)
         {
            item.Add(SetupItemState.NotNeeded, "No domain has DKIM signing switched on, so no DNS record is required.");
         }
         else
         {
            foreach (DomainDkim domain in signing)
            {
               string label = domain.Name + (domain.Active ? "" : " (domain is inactive)");

               if (domain.Selector.Length == 0 || domain.KeyFile.Length == 0)
               {
                  // Verified: DKIMSigner reports error 5305 and returns without
                  // signing, so mail leaves unsigned while the checkbox says on.
                  item.Add(SetupItemState.ActionNeeded,
                     label + ": signing is on but the " + (domain.Selector.Length == 0 ? "selector" : "private key file")
                     + " is not set, so every message goes out unsigned (server error 5305). Set both on the domain, then publish the DNS record.");
               }
               else if (local && !File.Exists(domain.KeyFile))
               {
                  item.Add(SetupItemState.ActionNeeded,
                     label + ": the key file " + domain.KeyFile + " does not exist on this machine, so signing fails and mail goes out unsigned. "
                     + "Restore the file or generate a new key, then publish the matching DNS record.");
               }
               else
               {
                  string fileNote = local ? "The key file exists on disk." : "The key file could not be checked from this machine.";
                  string host = domain.Selector + "._domainkey." + domain.Name;

                  var finding = new SetupFinding
                  {
                     State = SetupItemState.CannotTell,
                     Text = label + ": selector '" + domain.Selector + "' and key file are set. " + fileNote
                            + " Checking DNS for the TXT record " + host + "…"
                  };
                  item.Findings.Add(finding);

                  probes_.Add(new DnsProbe
                  {
                     Kind = ProbeKind.DkimPrimary,
                     Host = host,
                     Label = label,
                     Expected = local ? TryDeriveDkimExpectedRecord(domain.KeyFile) : null,
                     Target = finding
                  });
               }

               if (domain.SecondarySelector.Length > 0)
               {
                  // Verified in DKIMSigner: only the primary pair ever signs; the
                  // secondary exists purely to stage a rotation, and promoting it
                  // before its record propagates makes every receiver fail the mail.
                  string host = domain.SecondarySelector + "._domainkey." + domain.Name;

                  var finding = new SetupFinding
                  {
                     State = SetupItemState.CannotTell,
                     Text = label + ": a secondary key is staged for rotation. Checking DNS for the TXT record " + host + "…"
                  };
                  item.Findings.Add(finding);

                  probes_.Add(new DnsProbe
                  {
                     Kind = ProbeKind.DkimSecondary,
                     Host = host,
                     Label = label,
                     Expected = local && domain.SecondaryKeyFile.Length > 0 && File.Exists(domain.SecondaryKeyFile)
                        ? TryDeriveDkimExpectedRecord(domain.SecondaryKeyFile)
                        : null,
                     Target = finding
                  });
               }
            }
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// ARC inbound filtering. This is the clearest instance of the whole
      /// problem: SpamTestArc refuses to act unless the trusted-sealer list has
      /// entries, because a forwarder's seal is only worth anything from a
      /// forwarder the administrator trusts (RFC 8617 7.1 - anyone can fabricate
      /// a chain that validates). Naming who you trust is a judgement only the
      /// administrator can make, which is why the server cannot default it.
      ///
      /// Both values ARE readable now, through AntiSpam.ArcFilteringEnabled and
      /// AntiSpam.ArcTrustedSealers. They were not when this page was first
      /// written - they lived only in hm_settings with no COM property - and this
      /// item reported "cannot tell" plus a SELECT for the administrator to run.
      /// The COM accessors landed straight afterwards, so it reads them, because a
      /// page whose whole purpose is to say what is really configured must not
      /// keep claiming it cannot see something it can.
      /// </summary>
      private SetupItem ArcFilteringItem()
      {
         var item = new SetupItem
         {
            Title = "ARC inbound filtering - name the forwarders you trust",
            Purpose = "Rescues legitimate forwarded mail from DMARC-failure penalties, but only for forwarders on your trusted-sealer list - "
                      + "with the list empty the feature does nothing at all, even when enabled.",
            Action = "On the Anti-spam settings page, list the sealing domains you trust (the mailing-list and forwarding services your users "
                     + "rely on) and turn on the ARC offset. Naming who you trust is a judgement only you can make, which is why there is no default.",
            Page = "antispam"
         };

         int arcFailuresBefore = failedReads_;

         bool arcEnabled = Read(() =>
         {
            dynamic settings = null;
            dynamic antiSpam = null;
            try
            {
               settings = ServerSession.Current.Application.Settings;
               antiSpam = settings.AntiSpam;
               return (bool)antiSpam.ArcFilteringEnabled;
            }
            finally
            {
               ServerSession.Release((object)antiSpam);
               ServerSession.Release((object)settings);
            }
         });

         string arcSealers = Read(() =>
         {
            dynamic settings = null;
            dynamic antiSpam = null;
            try
            {
               settings = ServerSession.Current.Application.Settings;
               antiSpam = settings.AntiSpam;
               return (string)antiSpam.ArcTrustedSealers ?? "";
            }
            finally
            {
               ServerSession.Release((object)antiSpam);
               ServerSession.Release((object)settings);
            }
         });

         if (failedReads_ != arcFailuresBefore)
         {
            item.Add(SetupItemState.CannotTell,
               "The ARC filtering settings could not be read from the server, so this page cannot say whether the feature is configured.");
         }
         else if (!arcEnabled)
         {
            item.Add(SetupItemState.NotNeeded,
               "ARC filtering is off, so forwarded mail that fails DMARC is scored as it arrives. That is the default and it is a "
               + "reasonable choice; turn it on only once you have forwarders whose seals you are willing to trust.");
         }
         else if (string.IsNullOrWhiteSpace(arcSealers))
         {
            // The exact shape this whole page exists to catch: switched on, looks
            // configured, does nothing whatsoever.
            item.Add(SetupItemState.ActionNeeded,
               "ARC filtering is ENABLED but the trusted-sealer list is EMPTY, so it does nothing at all. That is deliberate, not a bug: "
               + "anyone can fabricate a whole ARC chain and seal it with a key they publish in their own DNS, and it will validate "
               + "perfectly - so a passing chain proves nothing unless you already trust the sealer. The list is not an option of the "
               + "feature, it is the feature.");
         }
         else
         {
            item.Add(SetupItemState.Done,
               "ARC filtering is enabled and trusts: " + arcSealers.Trim() + ". Seals from any other domain are ignored, and matching is "
               + "exact - a subdomain or a lookalike of a trusted name is not trusted.");
         }

         // The one part that IS readable from here: ARC's score offset is defined
         // as minus the DMARC failure score, so a zero score makes ARC filtering
         // a no-op regardless of the sealer list. Verified in SpamTestArc::RunTest.
         //
         // Guarded on the read actually succeeding: Read returns default(int)=0
         // on a COM failure, and asserting "your score is 0" off a failed read
         // would be exactly the kind of overclaim this page exists to prevent.
         int failuresBefore = failedReads_;
         int dmarcFailureScore = Read(() =>
         {
            dynamic settings = null;
            dynamic antiSpam = null;
            try
            {
               settings = ServerSession.Current.Application.Settings;
               antiSpam = settings.AntiSpam;
               return (int)antiSpam.DMARCFailureScore;
            }
            finally
            {
               ServerSession.Release((object)antiSpam);
               ServerSession.Release((object)settings);
            }
         });

         if (failedReads_ == failuresBefore && dmarcFailureScore <= 0)
         {
            item.Add(SetupItemState.ActionNeeded,
               "The DMARC failure score is currently " + dmarcFailureScore + ". ARC works by offsetting that score, "
               + "so at zero ARC filtering can do nothing even with trusted sealers listed. Set a positive DMARC failure score on the Anti-spam settings page first.");
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// ARC sealing of forwarded mail (ArcSealingEnabled in hMailServer.INI,
      /// default off). Verified in DKIMSigner::GetHostedDomainIdentity_: the seal
      /// borrows a hosted domain's primary DKIM selector and key, and with no
      /// domain fully configured the sealing path silently produces nothing.
      /// The external half is the same DKIM TXT record the signing item covers,
      /// because the next hop verifies the seal against that record.
      /// </summary>
      private SetupItem ArcSealingItem(List<DomainDkim> domains, IniFeatureStore ini, bool iniReadable, string iniExcuse)
      {
         var item = new SetupItem
         {
            Title = "ARC sealing of forwarded mail - needs a domain DKIM key",
            Purpose = "Seals mail this server forwards so the next hop can still trust the original authentication results; the seal is made with a hosted domain's DKIM key.",
            Action = "Give at least one active domain a DKIM selector and key file, and publish that domain's DKIM TXT record - "
                     + "the receiving server verifies the seal against the same record DKIM signing uses.",
            Page = "security"
         };

         if (!iniReadable)
         {
            item.Add(SetupItemState.CannotTell, "ArcSealingEnabled could not be read: " + iniExcuse);
         }
         else if (!ini.ReadBool("ArcSealingEnabled", false))
         {
            item.Add(SetupItemState.NotNeeded, "ARC sealing is off (ArcSealingEnabled=0, the default), so nothing external is required.");
         }
         else
         {
            bool anySealingIdentity = domains.Any(d => d.Active && d.SignEnabled && d.Selector.Length > 0 && d.KeyFile.Length > 0);
            if (anySealingIdentity)
            {
               item.Add(SetupItemState.Done,
                  "ARC sealing is on and at least one active domain has a DKIM selector and key to seal with. "
                  + "The DNS side is covered by the DKIM item above - the seal verifies against the same TXT record.");
            }
            else
            {
               item.Add(SetupItemState.ActionNeeded,
                  "ARC sealing is on (ArcSealingEnabled=1) but no active domain has DKIM signing with a selector and key file configured, "
                  + "so forwarded mail is silently not sealed at all.");
            }
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// MTA-STS policy hosting. Verified against WebServicesServer: hosting is
      /// enabled by default (MtaStsHostingEnabled=1) while both listener ports
      /// default to 0, so a stock install advertises the feature and serves
      /// nothing - the startup log now says so, and this item says it where an
      /// administrator will actually look. RFC 8461 3.3 requires the policy be
      /// fetched over HTTPS at https://mta-sts.&lt;domain&gt;/.well-known/mta-sts.txt,
      /// and the discovery record _mta-sts.&lt;domain&gt; lives in DNS, which the
      /// server cannot write.
      /// </summary>
      private SetupItem MtaStsItem(List<DomainDkim> domains, IniFeatureStore ini, bool iniReadable, string iniExcuse)
      {
         var item = new SetupItem
         {
            Title = "MTA-STS policy hosting - HTTPS listener plus two DNS entries per domain",
            Purpose = "Tells sending servers they must use TLS when delivering to your domains, closing the downgrade hole in opportunistic TLS.",
            Action = "Set WebServicesHttpsPort in hMailServer.ini (RFC 8461 requires the policy over HTTPS), give the listener a certificate that covers "
                     + "mta-sts.<domain>, then for each domain publish an A/AAAA (or CNAME) record for mta-sts.<domain> pointing at this server "
                     + "and a TXT record at _mta-sts.<domain> (v=STSv1; id=...). This page checks the TXT record; the address record and the "
                     + "certificate's name coverage cannot be checked from here.",
            Page = "webservices"
         };

         if (!iniReadable)
         {
            item.Add(SetupItemState.CannotTell, "The MTA-STS hosting settings could not be read: " + iniExcuse);
            item.AggregateFromFindings();
            return item;
         }

         // Defaults below mirror IniFileSettings.cpp exactly: hosting on,
         // both ports off, no certificate file, ACME off.
         bool hosting = ini.ReadBool("MtaStsHostingEnabled", true);
         int httpsPort = ReadIniInt(ini, "WebServicesHttpsPort", 0);
         string certFile = ini.Read("WebServicesCertificateFile", "").Trim();
         bool acme = ini.ReadBool("AcmeEnabled", false);

         if (!hosting)
         {
            item.Add(SetupItemState.NotNeeded, "MTA-STS hosting is off (MtaStsHostingEnabled=0), so nothing needs serving and no record should be published.");
         }
         else if (httpsPort <= 0)
         {
            item.Add(SetupItemState.ActionNeeded,
               "MTA-STS hosting is enabled (MtaStsHostingEnabled=1, the shipped default) but WebServicesHttpsPort is 0, so the policy cannot be served at all - "
               + "RFC 8461 only allows it over HTTPS. Set WebServicesHttpsPort if you want MTA-STS; if you do not, this row is safe to ignore, or set MtaStsHostingEnabled=0 to silence it.");
         }
         else if (certFile.Length == 0 && !acme)
         {
            // Verified: with no certificate configured and ACME off, the HTTPS
            // listener logs "No TLS certificate available yet" and stays down.
            item.Add(SetupItemState.ActionNeeded,
               "WebServicesHttpsPort is set, but no certificate is configured (WebServicesCertificateFile is empty and ACME is off), "
               + "so the HTTPS listener does not start and the policy still cannot be fetched. Set WebServicesCertificateFile/WebServicesPrivateKeyFile or enable ACME.");
         }
         else
         {
            item.Add(SetupItemState.Done, "The server side is in place: hosting is enabled, the HTTPS port is set, and a certificate source exists.");

            foreach (DomainDkim domain in domains.Where(d => d.Active))
            {
               string host = "_mta-sts." + domain.Name;

               var finding = new SetupFinding
               {
                  State = SetupItemState.CannotTell,
                  Text = domain.Name + ": checking DNS for the " + host + " TXT record…"
               };
               item.Findings.Add(finding);

               probes_.Add(new DnsProbe
               {
                  Kind = ProbeKind.MtaSts,
                  Host = host,
                  Label = domain.Name,
                  Target = finding
               });
            }
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// TLS-RPT sending. Verified against TlsRptReporterTask: with
      /// TlsRptFromAddress empty (the shipped default) the task collects
      /// statistics every hour and discards each completed day unsent - the
      /// deliberate, safe default, since a mail server should not start mailing
      /// third parties without being told to, but invisible without this row.
      /// Choosing the sender address is the administrator's call, not the
      /// server's, which is why it cannot default itself on.
      /// </summary>
      private SetupItem TlsRptItem(IniFeatureStore ini, bool iniReadable, string iniExcuse)
      {
         var item = new SetupItem
         {
            Title = "TLS reporting (TLS-RPT) - set a sender address to start sending",
            Purpose = "Mails daily TLS-failure reports to domains that ask for them, so their operators learn when connections to them are being downgraded.",
            Action = "Set TlsRptFromAddress in the [Settings] section of hMailServer.ini to a mailbox reports should be sent from. "
                     + "Leaving it empty is a valid choice - it just means the collected statistics are discarded and no reports are ever sent.",
            Page = "security"
         };

         if (!iniReadable)
         {
            item.Add(SetupItemState.CannotTell, "TlsRptFromAddress could not be read: " + iniExcuse);
         }
         else
         {
            string from = ini.Read("TlsRptFromAddress", "").Trim();
            if (from.Length > 0)
            {
               item.Add(SetupItemState.Done,
                  "TlsRptFromAddress is set to " + from + ", so completed days of statistics are mailed to domains that request reports. Nothing external remains.");
            }
            else
            {
               item.Add(SetupItemState.ActionNeeded,
                  "TlsRptFromAddress is empty (the default), so the server collects TLS statistics hourly and discards every completed day unsent. "
                  + "Set an address if you want reports to go out; ignore this row if you deliberately do not.");
            }
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// DANE for mail sent TO this server. Entirely a DNS matter: TLSA records
      /// for the MX hosts, in a DNSSEC-signed zone, published at the registrar or
      /// DNS host - three things hMailServer has no ability to do, and this panel
      /// has no TLSA/DNSSEC lookup to verify with, so the state is honestly
      /// unknown. The one thing that IS verifiable from here is the interaction
      /// with ACME key rollover, which the AcmeReuseKey default exists for.
      /// </summary>
      private SetupItem DaneItem(IniFeatureStore ini, bool iniReadable)
      {
         var item = new SetupItem
         {
            Title = "DANE for inbound mail - publish TLSA records for your MX hosts",
            Purpose = "Lets sending servers pin your certificate through DNSSEC, so nobody can strip or spoof TLS on mail coming to you.",
            Action = "In a DNSSEC-signed zone, publish a TLSA record (usually usage 3, selector 1, matching type 1 - '3 1 1' with the SHA-256 of your certificate's public key) "
                     + "at _25._tcp.<mx-host> for each MX host. Verify with: nslookup -type=TLSA _25._tcp.<mx-host>. "
                     + "hMailServer cannot publish DNS records or sign your zone, and this panel has no TLSA lookup to check them with.",
            Page = "security"
         };

         item.Add(SetupItemState.CannotTell,
            "Whether TLSA records exist, and whether your zone is DNSSEC-signed, can only be seen in DNS. If you have never set up DNSSEC for the zone, no TLSA record will validate.");

         if (iniReadable && ini.ReadBool("AcmeEnabled", false) && !ini.ReadBool("AcmeReuseKey", true))
         {
            // Verified: AcmeReuseKey defaults to 1 precisely so '3 1 1' TLSA
            // records survive renewals; with it off, every renewal generates a
            // new key and silently invalidates any published record.
            item.Add(SetupItemState.ActionNeeded,
               "ACME is enabled with AcmeReuseKey=0, so every certificate renewal generates a new private key. "
               + "If you publish '3 1 1' TLSA records they will break on the next renewal - set AcmeReuseKey=1 (the default), or update the TLSA record after every renewal.");
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// Encrypted private keys. Verified against SslContextInitializer: when
      /// OpenSSL finds a key file passphrase-protected and no PrivateKeyPassword
      /// is configured for the certificate, it reports error 6170, the key is
      /// not loaded, and every listener bound to that certificate stays down.
      /// The passphrase was chosen by whoever produced the key file - outside
      /// this server - which is why the server cannot supply it itself.
      /// </summary>
      private SetupItem EncryptedKeysItem(bool local, string remoteExcuse)
      {
         var item = new SetupItem
         {
            Title = "TLS private keys - a passphrase-protected key needs its passphrase configured",
            Purpose = "Every listener with TLS loads a certificate and its private key at startup; an encrypted key without its passphrase does not load, and the listener never starts.",
            Action = "For each certificate whose key file is an encrypted PEM, set the certificate's private key password on the SSL certificates page "
                     + "(the passphrase was set by whoever created the key, so only you can supply it) - or replace the file with an unencrypted key.",
            Page = "certs"
         };

         dynamic settings = null;
         dynamic certs = null;
         try
         {
            settings = ServerSession.Current.Application.Settings;
            certs = settings.SSLCertificates;
            int count = (int)certs.Count;

            if (count == 0)
               item.Add(SetupItemState.NotNeeded, "No SSL certificates are configured, so there are no key files to check.");

            for (int i = 0; i < count; i++)
            {
               dynamic cert = certs.Item[i];
               try
               {
                  string name = ((string)cert.Name ?? "").Trim();
                  string keyFile = ((string)cert.PrivateKeyFile ?? "").Trim();
                  string label = name.Length > 0 ? name : "certificate " + (i + 1);

                  if (keyFile.Length == 0)
                  {
                     item.Add(SetupItemState.ActionNeeded,
                        label + ": no private key file is set, so this certificate cannot be used by any listener.");
                     continue;
                  }

                  if (!local)
                  {
                     item.Add(SetupItemState.CannotTell, label + ": the key file cannot be inspected because " + remoteExcuse);
                     continue;
                  }

                  if (!File.Exists(keyFile))
                  {
                     item.Add(SetupItemState.ActionNeeded,
                        label + ": the key file " + keyFile + " does not exist on this machine, so every port using this certificate will fail to start.");
                     continue;
                  }

                  bool encrypted;
                  try
                  {
                     encrypted = LooksLikeEncryptedPem(keyFile);
                  }
                  catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
                  {
                     // Readable by the service is what matters, but unreadable by
                     // this panel still means the state cannot be reported.
                     item.Add(SetupItemState.CannotTell, label + ": the key file exists but could not be read from this panel, so whether it is encrypted is unknown.");
                     continue;
                  }

                  if (!encrypted)
                  {
                     item.Add(SetupItemState.Done, label + ": the key file is not encrypted, so no passphrase is needed.");
                     continue;
                  }

                  // Only the EMPTINESS of the passphrase is tested; the value is
                  // never rendered anywhere. Reading it requires the server-
                  // administrator session, so a failure here degrades honestly.
                  bool passwordKnown = true;
                  bool passwordSet = false;
                  try
                  {
                     string password = (string)cert.PrivateKeyPassword;
                     passwordSet = !string.IsNullOrEmpty(password);
                  }
                  catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
                  {
                     passwordKnown = false;
                  }

                  if (!passwordKnown)
                     item.Add(SetupItemState.CannotTell,
                        label + ": the key file is encrypted, but whether a passphrase is configured could not be read (this requires the server-administrator account).");
                  else if (passwordSet)
                     item.Add(SetupItemState.Done,
                        label + ": the key file is encrypted and a passphrase is configured. Whether it is the RIGHT passphrase is only proven when the server loads the key - check the error log for error 6170 after a restart.");
                  else
                     item.Add(SetupItemState.ActionNeeded,
                        label + ": the key file is an encrypted PEM but no passphrase is configured, so the key will not load (server error 6170) and every port using this certificate will not start. Set the private key password on the certificate.");
               }
               finally
               {
                  ServerSession.Release((object)cert);
               }
            }
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
            item.Add(SetupItemState.CannotTell, "The SSL certificate list could not be read - " + firstError_);
         }
         finally
         {
            ServerSession.Release((object)certs);
            ServerSession.Release((object)settings);
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// ACME certificate issuance. Verified against AcmeClient: enabled with no
      /// AcmeDomains it logs "No domains configured" and does nothing. Beyond
      /// that, http-01 issuance stands or falls on something no code here can
      /// see: whether the CA, out on the internet, can reach this server on the
      /// challenge port - firewalls, NAT and public DNS are all outside both
      /// the server and this panel.
      /// </summary>
      private SetupItem AcmeItem(IniFeatureStore ini, bool iniReadable, string iniExcuse)
      {
         var item = new SetupItem
         {
            Title = "ACME certificates - the CA must reach this server from the internet",
            Purpose = "Issues and renews certificates automatically, but only if the certificate authority can fetch the http-01 challenge from this server.",
            Action = "Publish a public A/AAAA record for every name listed in AcmeDomains, and allow inbound TCP on the challenge port "
                     + "from the internet (port-forward through any NAT). Reachability from outside cannot be probed from inside, "
                     + "so test it from an external network or an online port checker.",
            Page = "acme"
         };

         if (!iniReadable)
         {
            item.Add(SetupItemState.CannotTell, "The ACME settings could not be read: " + iniExcuse);
            item.AggregateFromFindings();
            return item;
         }

         if (!ini.ReadBool("AcmeEnabled", false))
         {
            item.Add(SetupItemState.NotNeeded, "ACME is off (AcmeEnabled=0, the default), so nothing external is required.");
            item.AggregateFromFindings();
            return item;
         }

         string domainList = ini.Read("AcmeDomains", "").Trim();
         int challengePort = ReadIniInt(ini, "AcmeHttpPort", 80);

         var names = domainList.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

         if (names.Count == 0)
         {
            // Verified: RequestCertificate logs and returns before contacting the CA.
            item.Add(SetupItemState.ActionNeeded,
               "ACME is enabled but AcmeDomains is empty, so no certificate is ever requested - the client logs 'No domains configured' and stops. "
               + "List the host names the certificate should cover (for example mail.yourdomain.com, mta-sts.yourdomain.com).");
         }
         else
         {
            foreach (string name in names)
            {
               item.Add(SetupItemState.CannotTell,
                  name + ": the CA must be able to fetch http://" + name + "/.well-known/acme-challenge/... on port " + challengePort
                  + " from the internet. Confirm the public DNS record points here and the port is open end to end.");
            }
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// Inbound client certificates (mutual TLS). Verified against TCPServer:
      /// with a policy of Require and a CA file that is missing, unloadable or
      /// holds no certificate, the listener does not start at all (error 6140);
      /// with Request it starts but collects nothing. The CA bundle and the
      /// client certificates themselves are things the administrator must
      /// produce or obtain - hMailServer verifies certificates, it does not
      /// operate a certificate authority.
      /// </summary>
      private SetupItem ClientCertificatesItem(bool local, string remoteExcuse)
      {
         var item = new SetupItem
         {
            Title = "Inbound client certificates - the port needs a CA bundle, the clients need certificates",
            Purpose = "Requires (or requests) a TLS client certificate from anyone connecting to a port, so only holders of a certificate from your chosen CA get in.",
            Action = "Produce or obtain a CA (for example with OpenSSL or your PKI), save its certificate(s) as a PEM bundle on the server, point the port's CA file at it, "
                     + "and issue a client certificate from that CA to every client that must connect. hMailServer verifies against the bundle but cannot create the CA or the client certificates.",
            Page = "ports"
         };

         dynamic settings = null;
         dynamic ports = null;
         try
         {
            settings = ServerSession.Current.Application.Settings;
            ports = settings.TCPIPPorts;
            int count = (int)ports.Count;
            int withPolicy = 0;

            for (int i = 0; i < count; i++)
            {
               dynamic port = ports.Item[i];
               try
               {
                  int policy = (int)port.ClientCertificatePolicy;
                  if (policy == 0)
                     continue;

                  withPolicy++;

                  string label = ProtocolName((int)port.Protocol) + " " + (string)port.Address + ":" + (int)port.PortNumber
                                 + " (" + (policy == 2 ? "require" : "request") + ")";
                  int security = (int)port.ConnectionSecurity;
                  string caFile = ((string)port.ClientCertificateCAFile ?? "").Trim();

                  if (security == 0)
                  {
                     // Verified: a plaintext port never performs a handshake, so
                     // the policy can never run; for Require the listener is not
                     // even started (error 6142).
                     item.Add(SetupItemState.ActionNeeded,
                        label + ": the port has a client certificate policy but no SSL/TLS or STARTTLS, so no handshake ever happens"
                        + (policy == 2 ? " and the listener is not started (server error 6142)." : " and the policy verifies nothing.")
                        + " Enable connection security on the port, or set the policy to off.");
                     continue;
                  }

                  if (caFile.Length == 0)
                  {
                     item.Add(SetupItemState.ActionNeeded,
                        label + ": no CA file is set, so there is no trust anchor to verify client certificates against"
                        + (policy == 2 ? " and the listener will not start (server error 6140)." : "."));
                     continue;
                  }

                  if (!local)
                  {
                     item.Add(SetupItemState.CannotTell, label + ": the CA file " + caFile + " cannot be inspected because " + remoteExcuse);
                     continue;
                  }

                  if (!File.Exists(caFile))
                  {
                     item.Add(SetupItemState.ActionNeeded,
                        label + ": the CA file " + caFile + " does not exist on this machine"
                        + (policy == 2 ? ", so the listener will not start (server error 6140)." : ", so verification can never succeed."));
                     continue;
                  }

                  bool looksLikePem;
                  try
                  {
                     // The same first-pass test OpenSSL's PEM reader applies: a
                     // usable bundle must contain at least one CERTIFICATE block.
                     looksLikePem = File.ReadAllText(caFile).Contains("-----BEGIN CERTIFICATE-----");
                  }
                  catch (Exception fatalCheck) when (!ExceptionPolicy.IsFatal(fatalCheck))
                  {
                     item.Add(SetupItemState.CannotTell, label + ": the CA file exists but could not be read from this panel.");
                     continue;
                  }

                  if (!looksLikePem)
                  {
                     item.Add(SetupItemState.ActionNeeded,
                        label + ": " + caFile + " contains no PEM certificate block, so it holds no usable CA certificate (server error 6140). "
                        + "Export the CA certificate in PEM (Base64) format.");
                  }
                  else
                  {
                     item.Add(SetupItemState.Done,
                        label + ": the CA bundle exists and contains a PEM certificate. What remains is outside the server: issue certificates from that CA to the connecting clients.");
                  }
               }
               finally
               {
                  ServerSession.Release((object)port);
               }
            }

            if (withPolicy == 0)
               item.Add(SetupItemState.NotNeeded, "No port has a client certificate policy, so no CA bundle or client certificates are required.");
         }
         catch (Exception ex) when (!ExceptionPolicy.IsFatal(ex))
         {
            failedReads_++;
            firstError_ ??= ServerSession.DescribeComError(ex);
            item.Add(SetupItemState.CannotTell, "The TCP/IP port list could not be read - " + firstError_);
         }
         finally
         {
            ServerSession.Release((object)ports);
            ServerSession.Release((object)settings);
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// OAuth2 / XOAUTH2 bearer tokens. Verified against OAuth2TokenValidator:
      /// RS256 tokens are verified against OAuth2PublicKeyFile and HS256 tokens
      /// against OAuth2HmacSecret, so with the feature enabled and the matching
      /// key material absent every token fails signature verification - enabled,
      /// and no client can ever log on with it. The key comes from the identity
      /// provider (an app registration at Microsoft, Google, or your own IdP),
      /// which is precisely why the server cannot conjure it.
      /// </summary>
      private SetupItem OAuth2Item(IniFeatureStore ini, bool iniReadable, string iniExcuse, bool local)
      {
         var item = new SetupItem
         {
            Title = "OAuth2 / XOAUTH2 - the identity provider's key material must be installed",
            Purpose = "Lets clients log on with bearer tokens from an identity provider instead of passwords; tokens are only accepted if their signature verifies against key material you install.",
            Action = "Register this server as an application with the identity provider, download the provider's public signing key as a PEM file, "
                     + "and point OAuth2PublicKeyFile in hMailServer.ini at it (or set OAuth2HmacSecret for HS256). "
                     + "Set OAuth2Issuer and OAuth2Audience to the values the provider puts in its tokens, or validation will reject them.",
            Page = "authentication"
         };

         if (!iniReadable)
         {
            item.Add(SetupItemState.CannotTell, "The OAuth2 settings could not be read: " + iniExcuse);
            item.AggregateFromFindings();
            return item;
         }

         if (!ini.ReadBool("OAuth2Enabled", false))
         {
            item.Add(SetupItemState.NotNeeded, "OAuth2 is off (OAuth2Enabled=0, the default), so no identity provider or key material is required.");
            item.AggregateFromFindings();
            return item;
         }

         // Defaults mirror IniFileSettings.cpp: algorithms default to RS256 alone.
         string algorithms = ini.Read("OAuth2AllowedAlgorithms", "RS256").ToUpperInvariant();
         bool allowsRs256 = algorithms.Contains("RS256");
         bool allowsHs256 = algorithms.Contains("HS256");
         string publicKeyFile = ini.Read("OAuth2PublicKeyFile", "").Trim();
         // Only EMPTINESS is tested; the secret's value is never read into the UI.
         bool hmacSecretSet = ini.Read("OAuth2HmacSecret", "").Trim().Length > 0;

         bool anythingWrong = false;

         if (allowsRs256)
         {
            if (publicKeyFile.Length == 0)
            {
               anythingWrong = true;
               item.Add(SetupItemState.ActionNeeded,
                  "OAuth2 is enabled and allows RS256, but OAuth2PublicKeyFile is empty - every RS256 token fails signature verification, so no client can log on with a token. "
                  + "Install the identity provider's public key as a PEM file and point OAuth2PublicKeyFile at it.");
            }
            else if (local && !File.Exists(publicKeyFile))
            {
               anythingWrong = true;
               item.Add(SetupItemState.ActionNeeded,
                  "OAuth2PublicKeyFile points at " + publicKeyFile + ", which does not exist on this machine, so RS256 tokens cannot be verified.");
            }
         }

         if (allowsHs256 && !hmacSecretSet)
         {
            anythingWrong = true;
            item.Add(SetupItemState.ActionNeeded,
               "HS256 is in OAuth2AllowedAlgorithms but OAuth2HmacSecret is empty, so HS256 tokens can never verify. Set the shared secret, or remove HS256 from the list.");
         }

         // Blank is not "will be checked and might mismatch" - it is "not checked".
         // OAuth2TokenValidator applies each of these only when it is set
         // (`if (!config.issuer.IsEmpty())`), and blank is the shipped default, so
         // the permissive case is the one an untouched configuration lands in. The
         // previous wording ("a mismatch rejects every token") described only the
         // case where they ARE set, which is the case that needs no warning.
         bool noIssuer = ini.Read("OAuth2Issuer", "").Trim().Length == 0;
         bool noAudience = ini.Read("OAuth2Audience", "").Trim().Length == 0;

         if (noIssuer || noAudience)
         {
            anythingWrong = true;

            string unchecked_ = noIssuer && noAudience
               ? "Neither OAuth2Issuer nor OAuth2Audience is set, so neither is checked"
               : noIssuer ? "OAuth2Issuer is not set, so the issuer is not checked"
                          : "OAuth2Audience is not set, so the audience is not checked";

            item.Add(SetupItemState.ActionNeeded,
               unchecked_ + ". Any token the identity provider signs with this key is then accepted, including one "
               + "it issued to a different application - which is enough to log in as whichever mailbox that token "
               + "names. Set both to the exact values the provider puts in its tokens. The values come from the "
               + "provider, which is why they are on this list.");
         }

         if (!anythingWrong)
         {
            item.Add(SetupItemState.Done,
               "OAuth2 is enabled, the key material its allowed algorithms need is in place, and both the issuer "
               + "and the audience are checked - so a token has to have been minted by your provider for this "
               + "server specifically.");
         }

         item.AggregateFromFindings();
         return item;
      }

      /// <summary>
      /// Client autoconfiguration. The same shape as MTA-STS hosting, verified
      /// against the same startup diagnostic: AutoconfigEnabled defaults to 1
      /// while both web-services ports default to 0, so a stock install has the
      /// feature "on" with nothing answering the URLs. The DNS names clients
      /// probe (autoconfig.&lt;domain&gt;, autodiscover.&lt;domain&gt;) live in the
      /// administrator's zone, where the server cannot create them.
      /// </summary>
      private SetupItem AutoconfigItem(IniFeatureStore ini, bool iniReadable, string iniExcuse)
      {
         var item = new SetupItem
         {
            Title = "Client autoconfiguration - a listener plus the DNS names clients probe",
            Purpose = "Lets Outlook, Thunderbird and mobile clients set themselves up from an e-mail address alone, by fetching this server's configuration URLs.",
            Action = "Set WebServicesHttpPort and/or WebServicesHttpsPort in hMailServer.ini so the URLs answer, then publish autoconfig.<domain> and "
                     + "autodiscover.<domain> DNS records for each mail domain, pointing at this server. These are address records, and this page "
                     + "checks TXT records only, so they cannot be confirmed from here.",
            Page = "webservices"
         };

         if (!iniReadable)
         {
            item.Add(SetupItemState.CannotTell, "The web services settings could not be read: " + iniExcuse);
         }
         else if (!ini.ReadBool("AutoconfigEnabled", true))
         {
            item.Add(SetupItemState.NotNeeded, "Autoconfiguration is off (AutoconfigEnabled=0), so no listener or DNS names are required.");
         }
         else if (ReadIniInt(ini, "WebServicesHttpPort", 0) <= 0 && ReadIniInt(ini, "WebServicesHttpsPort", 0) <= 0)
         {
            item.Add(SetupItemState.ActionNeeded,
               "Autoconfiguration is enabled (AutoconfigEnabled=1, the shipped default) but both WebServicesHttpPort and WebServicesHttpsPort are 0, "
               + "so nothing answers the configuration URLs. Set a port if you want clients to self-configure; if you do not, this row is safe to ignore, or set AutoconfigEnabled=0 to silence it.");
         }
         else
         {
            item.Add(SetupItemState.CannotTell,
               "The listener is configured. What remains is outside the server: publish autoconfig.<domain> and autodiscover.<domain> records for each mail domain, "
               + "then test from a client machine.");
         }

         item.AggregateFromFindings();
         return item;
      }

      private static int ReadIniInt(IniFeatureStore ini, string key, int defaultValue)
      {
         return int.TryParse(ini.Read(key, defaultValue.ToString()).Trim(), out int value) ? value : defaultValue;
      }

      private static string ProtocolName(int protocol)
      {
         switch (protocol)
         {
            case ServerSession.SessionSmtp: return "SMTP";
            case ServerSession.SessionPop3: return "POP3";
            case ServerSession.SessionImap: return "IMAP";
            default: return "Port";
         }
      }
   }
}
