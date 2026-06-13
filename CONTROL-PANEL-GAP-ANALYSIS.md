# Control Panel — Settings Parity Gap Analysis

Comparison of the new WPF **Control Panel** (`hMailCP.exe`) against the classic
WinForms **Administrator** (`source/Tools/Administrator`). Goal: port **every**
setting and adopt the old per-pane **tab** layout (the classic UX was correct).

Legend: ✅ present · ⚠️ partial · ❌ missing

---

## 0. Structural change requested

The classic Administrator put related settings on **tabs inside one pane**
(e.g. SMTP → *Delivery of e-mail* / *RFC compliance* / *Advanced*). The Control
Panel currently flattens everything into scrolling "cards" under a tree node.

**Action:** Rebuild each settings page as a `TabControl` (Wpf.Ui `TabView`/
`TabControl`) whose tab pages mirror the classic pane tabs listed below.

**✅ DONE** — every settings page is now a `TabControl`; the Domains page has
a tabbed `DomainDialog`; the account editor is a tabbed `AccountDialog`.

---

## 1. Protocols

### 1a. SMTP (classic tabs: *Delivery of e-mail*, *RFC compliance*, *Advanced*)
| Setting (COM) | CP status |
|---|---|
| ServiceSMTP / ServicePOP3 / ServiceIMAP | ✅ |
| HostName, MaxSMTPConnections, MaxMessageSize, MaxSMTPRecipientsInBatch, WelcomeSMTP | ✅ |
| SMTPNoOfTries, SMTPMinutesBetweenTry, MaxNumberOfMXHosts | ✅ (on Delivery page) |
| SMTPDeliveryBindToIP, AddDeliveredToHeader, RuleLoopLimit | ✅ (on Delivery page) |
| AllowSMTPAuthPlain, DenyMailFromNull, AllowIncorrectLineEndings | ✅ |
| DisconnectInvalidClients, MaxNumberOfInvalidCommands | ✅ |
| **SMTPConnectionSecurity** (STARTTLS for outbound delivery) | ✅ (Delivery tab) |
| **SetSMTPRelayerPassword** (relay password) | ✅ (Delivery → Relayer) |
| SMTPRelayer / Port / RequiresAuth / Username / ConnectionSecurity | ✅ (on Delivery page) |

### 1b. IMAP
| Setting (COM) | CP status |
|---|---|
| MaxIMAPConnections, WelcomeIMAP, IMAPIdle, IMAPQuota, IMAPSort, IMAPACL | ✅ |
| IMAPPublicFolderName, IMAPMasterUser | ✅ |
| **IMAPSASLPlainEnabled** | ✅ |
| **IMAPSASLInitialResponseEnabled** | ✅ |
| **IMAPHierarchyDelimiter** | ✅ |
| **Public folders editor** (Settings.PublicFolders – add/delete/permissions) | ✅ Public folders page + ACL editor |

### 1c. POP3
| Setting (COM) | CP status |
|---|---|
| MaxPOP3Connections, WelcomePOP3 | ✅ |

---

## 2. Anti-spam (classic tabs: *General*, *Spam tests*, *SURBL*, *DNSBL*, *Greylisting*)

### 2a. General / thresholds / sender auth / host checks
| Setting | CP status |
|---|---|
| SpamMarkThreshold, SpamDeleteThreshold, AddHeaderSpam, AddHeaderReason | ✅ |
| PrependSubject, PrependSubjectText | ✅ |
| UseSPF(+Score), DKIMVerificationEnabled(+FailureScore), DMARCEnabled | ✅ |
| CheckHostInHelo(+Score), CheckPTR(+Score), UseMXChecks(+Score) | ✅ |
| **MaximumMessageSize** (max bytes to spam-scan) | ✅ |
| SpamAssassinEnabled/Host/Port/MergeScore/Score | ✅ |
| **TestSpamAssassinConnection** button | ✅ (on SpamAssassin tab) |

### 2b. List-backed sub-panes (each is a managed collection — add/edit/delete)
| Pane (COM collection) | CP status |
|---|---|
| **SURBL servers** (`AntiSpam.SURBLServers`) | ✅ list editor |
| **DNS blacklists / DNSBL** (`AntiSpam.DNSBlackLists`) | ✅ list editor |
| **Whitelisting** (`AntiSpam.WhiteListAddresses`) | ✅ list editor |
| **Greylisting whitelist** (`GreyListingWhiteAddresses`) | ✅ list editor |

### 2c. Greylisting
| Setting | CP status |
|---|---|
| GreyListingEnabled, InitialDelay, InitialDelete, FinalDelete | ✅ (note: classic stores Delete values in hours, shows ÷24 days) |
| BypassGreylistingOnSPFSuccess, BypassGreylistingOnMailFromMX | ✅ |
| Whitelist-address list | ✅ (see 2b) |

---

## 3. Anti-virus (classic: *General*, *Attachment blocking*)
| Setting (COM `AntiVirus`) | CP status |
|---|---|
| ClamAVEnabled / ClamAVHost / ClamAVPort | ✅ |
| ClamWinEnabled / ClamWinExecutable / ClamWinDBFolder | ✅ |
| **Action** (DeleteEmail vs DeleteAttachments) | ✅ |
| **NotifySender** | ✅ |
| **NotifyReceiver** | ✅ |
| **MaximumMessageSize** (max bytes to virus-scan) | ✅ |
| **CustomScannerEnabled** | ✅ |
| **CustomScannerExecutable** | ✅ |
| **CustomScannerReturnValue** | ✅ |
| **EnableAttachmentBlocking** | ✅ |
| **BlockedAttachments** list (Wildcard + Description, add/remove) | ✅ list editor |

---

## 4. SSL/TLS
| Setting | CP status |
|---|---|
| TlsVersion10/11/12/13, SslCipherList | ✅ |
| TlsOptionPreferServerCiphersEnabled, TlsOptionPrioritizeChaChaEnabled | ✅ |
| VerifyRemoteSslCertificate | ✅ |
| (ChaCha enable/disable interlock with cipher-order + TLS1.2/1.3) | ✅ live UI interlock |

---

## 5. Logging
| Setting (`Logging`) | CP status |
|---|---|
| Enabled, LogApplication, LogSMTP, LogIMAP, LogPOP3, LogTCPIP, LogDebug | ✅ |
| AWStatsEnabled, KeepFilesOpen | ✅ |
| Directory display + "Open log folder" | ⚠️ Live-logs page only |

---

## 6. Performance (classic *Performance* pane — distinct from CP "Advanced")
| Setting | CP status |
|---|---|
| MaxDeliveryThreads, MaxAsynchronousThreads, TCPIPThreads, WorkerThreadPriority | ✅ (Performance tab) |
| **Cache.Enabled** | ✅ |
| **Cache.DomainCacheTTL** | ✅ |
| **Cache.AccountCacheTTL** | ✅ |
| **Cache.AliasCacheTTL** | ✅ |
| **Cache.DistributionListCacheTTL** | ✅ |
| **MessageIndexing.Enabled** | ✅ |

---

## 7. Advanced (classic *Advanced* pane)
| Setting | CP status |
|---|---|
| MaxDeliveryThreads / async / TCPIP / WorkerThreadPriority | ✅ |
| MirrorEMailAddress (Mirror pane) | ✅ |
| Scripting.Enabled, Scripting.Language | ✅ |
| **DefaultDomain** | ✅ |
| **IPv6PreferredEnabled** | ✅ |
| **SetAdministratorPassword** (change main admin password) | ✅ |
| Scripts pane: edit/reload event script **files** on disk | ✅ Event scripts page (load/edit/save + CheckSyntax + Reload) |

---

## 8. Domains — per-domain editor (classic tabs: *General*, *Names*, *Limits*, *Signature*, *Advanced*, *DKIM*)

**Current CP:** lists domains + shows aliases/distribution lists, **plus a
tabbed `DomainDialog`** (Properties button) with General / Names (domain aliases) /
Limits / Signature / DKIM tabs.

| Setting (`Domain`) | CP status |
|---|---|
| Name, Postmaster (catch-all), Active | ✅ |
| AddSignaturesToReplies, AddSignaturesToLocalMail | ✅ |
| SignatureEnabled, SignatureMethod, SignaturePlainText, SignatureHTML | ✅ |
| MaxSize, MaxMessageSize, MaxAccountSize | ✅ |
| MaxNumberOfAccounts(+Enabled) | ✅ |
| MaxNumberOfAliases(+Enabled) | ✅ |
| MaxNumberOfDistributionLists(+Enabled) | ✅ |
| PlusAddressingEnabled, PlusAddressingCharacter | ✅ |
| AntiSpamEnableGreylisting (per-domain) | ✅ |
| DKIMSignEnabled, DKIMSignAliasesEnabled | ✅ |
| DKIMPrivateKeyFile, DKIMSelector | ✅ |
| DKIMHeaderCanonicalizationMethod (relaxed/simple) | ✅ |
| DKIMBodyCanonicalizationMethod (relaxed/simple) | ✅ |
| DKIMSigningAlgorithm (SHA1/SHA256) | ✅ |
| Domain aliases (`DomainAliases`) list | ✅ Names tab in DomainDialog |

---

## 9. Accounts — per-account editor (classic tabs: *General*, *Auto-reply*, *Forwarding*, *External accounts*, *Active Directory*, *Rules*)

**Current CP `AccountDialog`:** tabbed (General / Forwarding / Auto-reply /
Signature / External / Rules / Folders / Directory).

| Setting (`Account`) | CP status |
|---|---|
| Address, MaxSize, Active, First/Last name, Password | ✅ |
| ForwardEnabled / ForwardAddress / ForwardKeepOriginal | ✅ |
| **ForwardAbortSpamFlagged** | ✅ |
| VacationMessageIsOn / Subject / Message | ✅ |
| **VacationMessageExpires + ExpiresDate** | ✅ |
| **VacationMessageAbortSpamFlagged** | ✅ |
| **SignatureEnabled / SignaturePlainText / SignatureHTML** | ✅ |
| **IsAD / ADDomain / ADUsername** (Active Directory account) | ✅ |
| **AdminLevel** (user / domain admin / server admin) | ✅ |
| **LastLogonTime** (read-only display) | ✅ |
| **External (download) accounts** (`Account.FetchAccounts`) | ✅ External tab |
| **Account-level rules** | ✅ Rules tab (add/rename/enable/delete) |
| **IMAP folders editor** (per account) | ✅ Folders tab |
| Empty account / Unlock buttons | ✅ Folders tab (Empty mailbox / Unlock mailbox) |

---

## 10. Whole panes missing from the Control Panel

| Classic node | COM | CP status |
|---|---|---|
| **Groups / Group** (security groups & members) | `Application.Groups` | ✅ list editor |
| **Server messages** (system message templates / bounce text) | `Settings.ServerMessages` | ✅ list editor |
| **Scripts** (event-script file editor) | event scripts on disk | ✅ Event scripts page |
| **Status** (uptime, version, session counts, processes) | `Application.Status` | ⚠️ Dashboard covers some |
| **Distribution list** property editor (Address, Active, Mode, RequireSenderAddress, AnnounceOnly) | `DistributionList` | ✅ Properties dialog + members (RecipientsDialog) |
| **Incoming relay** full options (beyond Name/LowerIP/UpperIP) | `IncomingRelay` | ✅ (COM exposes only Name/IP range) |
| **Route** advanced options (RouteAddress, ConnectionSecurity, RelayMode, GreyListing, etc.) | `Route` | ✅ Properties dialog (security, auth, local-domain, retries) |

---

## 11. Status

### ✅ Closed
1. **Tab-based layout** across all settings pages and both editors (Section 0).
2. **Domain editor** — tabbed General/Names/Limits/Signature/DKIM incl. domain-alias
   list (Sections 8).
3. **Account editor** — tabbed General/Forwarding/Auto-reply/Signature/External/
   Rules/Folders/Directory; admin level, AD, expiry/abort-spam flags, last-logon,
   external fetch accounts, account rules, IMAP folders, Empty/Unlock (Section 9).
4. **Anti-virus** completion + blocked-attachments list (Section 3).
5. **Anti-spam list panes** — SURBL, DNSBL, whitelist, greylist whitelist (Section 2b)
   plus the **Test SpamAssassin connection** button (Section 2a).
6. **Performance** cache + message indexing (Section 6); **Advanced** default
   domain / IPv6 / admin password (Section 7).
7. **IMAP** SASL + hierarchy delimiter; **SMTP** delivery STARTTLS + relay
   password (Sections 1a/1b); **public folders** editor with ACL permissions (1b).
8. **Groups**, **Server messages** list editors (Section 10).
9. **Event scripts** page — load/edit/save the event-handler file, check syntax,
   reload the engine (Sections 7 & 10).
10. **Routes** advanced properties (connection security, auth, local-domain treatment)
    and **distribution-list** properties dialog (mode, auth, sender) (Section 10).
11. **SSL/TLS ChaCha ↔ cipher-order/TLS-version** live dependency interlock (Section 4).

### ⬜ Remaining
- **Status** pane parity (uptime / session counts / process list) — partly covered by
  the Dashboard.
- Full account **rule criteria/action** editing (CP lists and toggles rules; the
  detailed criteria/action builder is not ported).

---

*Generated 2026-06-13 from `source/Tools/Administrator/Main panes/*.cs` vs
`source/Tools/ControlPanel/Views/*`. Status updated 2026-06-13 after the
full gap-closure pass (tab-strip redesign, domain/account sub-editors, public
folders, event scripts, route/distribution-list properties, SpamAssassin test,
ChaCha interlock).*
