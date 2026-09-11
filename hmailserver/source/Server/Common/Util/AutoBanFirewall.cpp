// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "AutoBanFirewall.h"

#include "ProcessLauncher.h"
#include "VariantDateTime.h"
#include "Parsing/StringParser.h"
#include "../Application/IniFileSettings.h"
#include "../Application/Configuration.h"
#include "../BO/SecurityRanges.h"
#include "../BO/SecurityRange.h"
#include "../BO/TCPIPPorts.h"
#include "../BO/TCPIPPort.h"
#include "../TCPIP/IPAddress.h"

#include <boost/asio/ip/address.hpp>
#include <boost/system/error_code.hpp>

#include <algorithm>
#include <mutex>
#include <set>
#include <string>
#include <utility>
#include <vector>

#ifndef HM_PLATFORM_POSIX
#include <netfw.h>
#endif

#ifdef _DEBUG
#define new DEBUG_NEW
#endif

namespace HM
{
   namespace
   {
      // One reconciliation at a time. A ban is created on a protocol thread, the
      // expiry pass runs on the scheduler's, an administrator's delete arrives on
      // the COM thread and start-up on the main one; two of them interleaving would
      // add a rule the other is about to remove.
      std::mutex g_mutex;

      // Windows: the addresses that have a rule of ours, learned once by
      // enumerating the firewall for the rule group and kept current afterwards, so
      // the once-a-minute pass costs nothing when nothing changed.
      std::set<String> g_windowsRules;
      bool g_windowsRulesKnown = false;
      bool g_windowsUnavailableReported = false;

      // Hooks: the addresses each command has been told "ban" for. Empty at
      // start-up on purpose - the process cannot know what a hook did before it
      // started, so every active ban is announced again and the hook is required
      // to be idempotent; the packaged one is.
      std::set<String> g_hooked;
      bool g_hookFailureReported = false;

      // The never-ban list, parsed from the setting's text and re-parsed only when
      // that text changes, so a list of a few hundred blocks costs nothing per
      // logon failure.
      String g_neverBanSource;
      bool g_neverBanParsed = false;
      std::vector<std::pair<boost::asio::ip::address, int>> g_neverBanBlocks;

      const wchar_t *RangeNamePrefix = L"Auto-ban: ";
      const wchar_t *RuleNamePrefix = L"hMailServer auto-ban ";

      std::string Trim(const std::string &s)
      {
         size_t b = s.find_first_not_of(" \t\r\n");
         if (b == std::string::npos)
            return "";
         size_t e = s.find_last_not_of(" \t\r\n");
         return s.substr(b, e - b + 1);
      }

      bool Within(const boost::asio::ip::address &address, const boost::asio::ip::address &block, int prefix)
      {
         if (address.is_v4() != block.is_v4())
            return false;

         if (address.is_v4())
         {
            if (prefix >= 32)
               return address.to_v4() == block.to_v4();
            unsigned long mask = prefix <= 0 ? 0UL : (0xFFFFFFFFUL << (32 - prefix)) & 0xFFFFFFFFUL;
            return (address.to_v4().to_uint() & mask) == (block.to_v4().to_uint() & mask);
         }

         boost::asio::ip::address_v6::bytes_type a = address.to_v6().to_bytes();
         boost::asio::ip::address_v6::bytes_type b = block.to_v6().to_bytes();
         int bits = std::min(std::max(prefix, 0), 128);
         for (int i = 0; i < 16 && bits > 0; i++, bits -= 8)
         {
            unsigned char mask = bits >= 8 ? 0xFF : (unsigned char) (0xFF << (8 - bits));
            if ((a[i] & mask) != (b[i] & mask))
               return false;
         }
         return true;
      }

      // "1.2.3.4", "1.2.3.0/24", "2001:db8::/32": one entry of AutoBanNeverBan.
      bool ParseBlock(const std::string &entry, boost::asio::ip::address &address, int &prefix)
      {
         std::string text = Trim(entry);
         if (text.empty())
            return false;

         prefix = -1;
         size_t slash = text.find('/');
         if (slash != std::string::npos)
         {
            std::string bits = Trim(text.substr(slash + 1));
            text = Trim(text.substr(0, slash));
            if (bits.empty() || bits.find_first_not_of("0123456789") != std::string::npos)
               return false;
            prefix = atoi(bits.c_str());
         }

         boost::system::error_code ec;
         address = boost::asio::ip::make_address(text, ec);
         if (ec)
            return false;

         int widest = address.is_v4() ? 32 : 128;
         if (prefix < 0)
            prefix = widest;
         return prefix <= widest;
      }
   }

   String
   AutoBanFirewall::RuleName(const String &address)
   {
      return String(RuleNamePrefix) + address;
   }

   const wchar_t *
   AutoBanFirewall::RuleGroup()
   {
      return L"hMailServer auto-ban";
   }

   const wchar_t *
   AutoBanFirewall::PackagedHook()
   {
      return L"/usr/lib/hmailserver/autoban-hook";
   }

   String
   AutoBanFirewall::ListeningPorts()
   {
      std::set<int> ports;

      std::shared_ptr<TCPIPPorts> tcpipPorts = Configuration::Instance()->GetTCPIPPorts();
      if (tcpipPorts)
      {
         for (std::shared_ptr<TCPIPPort> port : tcpipPorts->GetVector())
            ports.insert(port->GetPortNumber());
      }

      String result;
      for (int port : ports)
      {
         if (!result.IsEmpty())
            result += _T(",");
         result += StringParser::IntToString(port);
      }
      return result;
   }

   bool
   AutoBanFirewall::IsNeverBanned(const IPAddress &address)
   {
      std::lock_guard<std::mutex> guard(g_mutex);

      String source = IniFileSettings::Instance()->GetAutoBanNeverBan();
      if (!g_neverBanParsed || source != g_neverBanSource)
      {
         g_neverBanBlocks.clear();
         g_neverBanSource = source;
         g_neverBanParsed = true;

         // Comma, semicolon or whitespace between entries - whichever the
         // administrator reached for. Addresses are ASCII, so narrowing is exact.
         std::vector<std::string> entries;
         std::string current;
         for (wchar_t c : std::wstring(source.c_str()))
         {
            if (c == L',' || c == L';' || c == L' ' || c == L'\t' || c == L'\r' || c == L'\n')
            {
               if (!current.empty())
                  entries.push_back(current);
               current.clear();
            }
            else
               current += (char) c;
         }
         if (!current.empty())
            entries.push_back(current);

         String rejected;
         for (const std::string &entry : entries)
         {
            std::string text = Trim(entry);
            if (text.empty())
               continue;

            boost::asio::ip::address block;
            int prefix = 0;
            if (ParseBlock(text, block, prefix))
               g_neverBanBlocks.push_back(std::make_pair(block, prefix));
            else
            {
               if (!rejected.IsEmpty())
                  rejected += _T(", ");
               rejected += String(text.c_str());
            }
         }

         // A list that cannot be read is worse than no list, because the
         // administrator believes their address is protected. Reported once per
         // value of the setting, naming every entry that was dropped.
         if (!rejected.IsEmpty())
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6524, "AutoBanFirewall::IsNeverBanned",
               "AutoBanNeverBan contains entries that are not an address or a CIDR block and were ignored: " + rejected +
               ". Addresses in those entries are NOT protected from auto-ban.");
         }
      }

      if (g_neverBanBlocks.empty())
         return false;

      boost::asio::ip::address asioAddress = address.GetAddress();
      for (const std::pair<boost::asio::ip::address, int> &block : g_neverBanBlocks)
      {
         if (Within(asioAddress, block.first, block.second))
            return true;
      }
      return false;
   }

   std::vector<AutoBanFirewall::Ban>
   AutoBanFirewall::ActiveBans_()
   {
      std::vector<Ban> bans;

      std::shared_ptr<SecurityRanges> ranges = std::shared_ptr<SecurityRanges>(new SecurityRanges());
      ranges->Refresh();

      DateTime now = DateTime::GetCurrentTime();

      for (std::shared_ptr<SecurityRange> range : ranges->GetVector())
      {
         // An auto-ban is a single address, expiring, named by the code that made
         // it. Nothing else on the IP ranges page is ours to mirror: a permanent
         // block an administrator wrote belongs in the firewall only if they put
         // it there themselves.
         if (!range->GetExpires())
            continue;
         if (!range->GetName().StartsWith(RangeNamePrefix))
            continue;

         IPAddress lower = range->GetLowerIP();
         IPAddress upper = range->GetUpperIP();
         if (!(lower == upper))
            continue;

         DateTimeSpan left = range->GetExpiresTime() - now;
         double seconds = left.GetNumberOfSeconds();
         if (seconds <= 0)
            continue;

         Ban ban;
         ban.address = String(lower.ToString());
         ban.minutesLeft = (int) ((seconds + 59) / 60);
         bans.push_back(ban);
      }

      return bans;
   }

   void
   AutoBanFirewall::Banned(const IPAddress &address, int minutes, const String &username, int failures)
   {
      // The one line fail2ban and a person reading the log are both after: what
      // was banned, for how long, and why. Written whether or not anything below
      // the server is told, because the ban itself has happened.
      LOG_APPLICATION(_T("Auto-ban: ") + String(address.ToString()) + _T(" is banned for ") + StringParser::IntToString(minutes) +
         _T(" minutes after ") + StringParser::IntToString(failures) + _T(" failed logons; last user name ") + username);

      Synchronise(_T("ban"));
   }

   void
   AutoBanFirewall::Synchronise(const String &why)
   {
      std::lock_guard<std::mutex> guard(g_mutex);

      IniFileSettings *ini = IniFileSettings::Instance();
      bool firewall = ini->GetAutoBanFirewallEnabled();
      String command = ini->GetAutoBanCommand();

      std::vector<Ban> bans = ActiveBans_();
      std::set<String> desired;
      for (const Ban &ban : bans)
         desired.insert(ban.address);

      String ports = ListeningPorts();

      LOG_DEBUG(_T("AutoBanFirewall::Synchronise - ") + why + _T(": ") + StringParser::IntToString((int) bans.size()) +
         _T(" active auto-ban(s); firewall ") + String(firewall ? _T("on") : _T("off")) +
         (command.IsEmpty() ? String(_T("; no command")) : String(_T("; command configured"))));

#ifndef HM_PLATFORM_POSIX
      ReconcileWindowsRules_(firewall, desired, ports);
#endif

      std::vector<String> commands;
      if (!command.IsEmpty())
         commands.push_back(command);
#ifdef HM_PLATFORM_POSIX
      if (firewall)
         commands.push_back(String(PackagedHook()));
#endif
      ReconcileHooks_(commands, bans, ports);
   }

   void
   AutoBanFirewall::ReconcileHooks_(const std::vector<String> &commands, const std::vector<Ban> &bans, const String &ports)
   {
      if (commands.empty())
      {
         g_hooked.clear();
         return;
      }

      std::set<String> desired;
      for (const Ban &ban : bans)
         desired.insert(ban.address);

      // Un-ban first: an address that has expired should stop being blocked
      // before a new one starts, and a hook that is failing altogether should
      // not be asked to add before it has been asked to remove.
      std::vector<String> gone;
      for (const String &address : g_hooked)
      {
         if (desired.find(address) == desired.end())
            gone.push_back(address);
      }

      for (const String &address : gone)
      {
         bool ok = true;
         for (const String &command : commands)
         {
            String failure;
            if (!RunHook_(command, _T("unban"), address, 0, ports, failure))
            {
               ok = false;
               if (!g_hookFailureReported)
               {
                  g_hookFailureReported = true;
                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6523, "AutoBanFirewall::Synchronise",
                     "The auto-ban hook could not be run to un-ban " + address + ": " + failure +
                     ". Whatever the hook blocks stays blocked until it can run. Reported once; later failures are in the debug log.");
               }
               else
                  LOG_DEBUG(_T("AutoBanFirewall - unban hook failed for ") + address + _T(": ") + failure);
            }
         }
         // Forgotten either way: the ban is gone from the database, so the hook
         // will not be told about it again, and asking every minute would be a
         // stream of the same failure.
         (void) ok;
         g_hooked.erase(address);
      }

      for (const Ban &ban : bans)
      {
         if (g_hooked.find(ban.address) != g_hooked.end())
            continue;

         for (const String &command : commands)
         {
            String failure;
            if (!RunHook_(command, _T("ban"), ban.address, ban.minutesLeft, ports, failure))
            {
               if (!g_hookFailureReported)
               {
                  g_hookFailureReported = true;
                  ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6523, "AutoBanFirewall::Synchronise",
                     "The auto-ban hook could not be run to ban " + ban.address + ": " + failure +
                     ". The address is still refused by the server itself; it is not blocked below it. Reported once; later failures are in the debug log.");
               }
               else
                  LOG_DEBUG(_T("AutoBanFirewall - ban hook failed for ") + ban.address + _T(": ") + failure);
            }
         }
         g_hooked.insert(ban.address);
      }
   }

   bool
   AutoBanFirewall::RunHook_(const String &command, const String &verb, const String &address, int minutes, const String &ports, String &failure)
   {
      String commandLine = command + _T(" ") + verb + _T(" ") + address;
      if (verb == _T("ban"))
         commandLine += _T(" ") + StringParser::IntToString(minutes) + _T(" ") + (ports.IsEmpty() ? String(_T("-")) : ports);

      LOG_DEBUG(_T("AutoBanFirewall - running: ") + commandLine);

      ProcessLauncher launcher(commandLine);
      launcher.SetErrorLogTimeout(30000);

      unsigned int exitCode = 0;
      ProcessLauncher::FailureReason reason = ProcessLauncher::FailureReason::None;
      if (!launcher.Launch(exitCode, reason))
      {
         failure = reason == ProcessLauncher::FailureReason::TimedOut
            ? _T("it ran for more than the external process timeout and was stopped")
            : _T("it could not be started (check the path, and that the service account may execute it)");
         return false;
      }

      if (exitCode != 0)
      {
         failure = _T("it exited with code ") + StringParser::IntToString((int) exitCode) + _T(" (command line: ") + commandLine + _T(")");
         return false;
      }

      return true;
   }

#ifndef HM_PLATFORM_POSIX

   namespace
   {
      // COM on whatever thread we are on. A thread that already initialised as
      // STA answers RPC_E_CHANGED_MODE and keeps its apartment; the calls below
      // work from either, and only an initialisation that succeeded is undone.
      struct ComScope
      {
         HRESULT hr;
         ComScope() : hr(CoInitializeEx(NULL, COINIT_MULTITHREADED)) {}
         ~ComScope() { if (hr == S_OK || hr == S_FALSE) CoUninitialize(); }
      };

      String Describe(HRESULT hr)
      {
         String text;
         text.Format(_T("0x%08X"), (unsigned int) hr);
         return text;
      }

      bool OpenRules(CComPtr<INetFwRules> &rules, String &error)
      {
         CComPtr<INetFwPolicy2> policy;
         HRESULT hr = policy.CoCreateInstance(__uuidof(NetFwPolicy2), NULL, CLSCTX_INPROC_SERVER);
         if (FAILED(hr) || !policy)
         {
            error = _T("Windows Defender Firewall's policy object could not be created (") + Describe(hr) + _T("). Is the Windows Defender Firewall service (mpssvc) running?");
            return false;
         }

         hr = policy->get_Rules(&rules);
         if (FAILED(hr) || !rules)
         {
            error = _T("the firewall's rule collection could not be opened (") + Describe(hr) + _T(")");
            return false;
         }
         return true;
      }

      // Every rule in our group, by the address in its name. Once, at first use.
      bool EnumerateOurRules(INetFwRules *rules, std::set<String> &addresses, String &error)
      {
         CComPtr<IUnknown> unknown;
         HRESULT hr = rules->get__NewEnum(&unknown);
         if (FAILED(hr) || !unknown)
         {
            error = _T("the firewall's rules could not be enumerated (") + Describe(hr) + _T(")");
            return false;
         }

         CComPtr<IEnumVARIANT> enumerator;
         hr = unknown->QueryInterface(IID_IEnumVARIANT, (void **) &enumerator);
         if (FAILED(hr) || !enumerator)
         {
            error = _T("the firewall's rule enumerator is not available (") + Describe(hr) + _T(")");
            return false;
         }

         String prefix(RuleNamePrefix);
         CComVariant item;
         ULONG fetched = 0;
         while (enumerator->Next(1, &item, &fetched) == S_OK && fetched == 1)
         {
            if (item.vt == VT_DISPATCH && item.pdispVal)
            {
               CComPtr<INetFwRule> rule;
               if (SUCCEEDED(item.pdispVal->QueryInterface(__uuidof(INetFwRule), (void **) &rule)) && rule)
               {
                  CComBSTR grouping;
                  if (SUCCEEDED(rule->get_Grouping(&grouping)) && grouping && wcscmp(grouping, AutoBanFirewall::RuleGroup()) == 0)
                  {
                     CComBSTR name;
                     if (SUCCEEDED(rule->get_Name(&name)) && name)
                     {
                        String ruleName((const wchar_t *) name);
                        if (ruleName.StartsWith(prefix.c_str()))
                           addresses.insert(ruleName.Mid(prefix.GetLength()));
                     }
                  }
               }
            }
            item.Clear();
         }
         return true;
      }

      bool AddRule(INetFwRules *rules, const String &address, const String &ports, String &error)
      {
         CComPtr<INetFwRule> rule;
         HRESULT hr = rule.CoCreateInstance(__uuidof(NetFwRule), NULL, CLSCTX_INPROC_SERVER);
         if (FAILED(hr) || !rule)
         {
            error = _T("a firewall rule object could not be created (") + Describe(hr) + _T(")");
            return false;
         }

         String name = AutoBanFirewall::RuleName(address);
         rule->put_Name(CComBSTR(name.c_str()));
         rule->put_Description(CComBSTR(L"Created by hMailServer auto-ban after repeated failed logons from this address. hMailServer removes it when the ban expires or when the matching IP range is deleted; delete the IP range rather than this rule."));
         rule->put_Grouping(CComBSTR(AutoBanFirewall::RuleGroup()));
         rule->put_Direction(NET_FW_RULE_DIR_IN);
         rule->put_Action(NET_FW_ACTION_BLOCK);
         rule->put_Protocol(NET_FW_IP_PROTOCOL_TCP);
         if (!ports.IsEmpty())
            rule->put_LocalPorts(CComBSTR(ports.c_str()));
         rule->put_RemoteAddresses(CComBSTR(address.c_str()));
         rule->put_Profiles(NET_FW_PROFILE2_ALL);
         rule->put_Enabled(VARIANT_TRUE);

         hr = rules->Add(rule);
         if (FAILED(hr))
         {
            error = _T("the rule '") + name + _T("' could not be added (") + Describe(hr) + _T("). The service account needs to be an administrator or LocalSystem to write firewall rules.");
            return false;
         }
         return true;
      }

      bool RemoveRule(INetFwRules *rules, const String &address, String &error)
      {
         String name = AutoBanFirewall::RuleName(address);
         HRESULT hr = rules->Remove(CComBSTR(name.c_str()));
         if (FAILED(hr))
         {
            error = _T("the rule '") + name + _T("' could not be removed (") + Describe(hr) + _T(")");
            return false;
         }
         return true;
      }
   }

   void
   AutoBanFirewall::ReconcileWindowsRules_(bool enabled, const std::set<String> &desired, const String &ports)
   {
      // Nothing to do and nothing ever done: the common case on a server that has
      // never had the setting on, and it must not touch COM or the firewall.
      if (!enabled && g_windowsRulesKnown && g_windowsRules.empty())
         return;

      ComScope com;

      CComPtr<INetFwRules> rules;
      String error;
      if (!OpenRules(rules, error))
      {
         if (enabled && !g_windowsUnavailableReported)
         {
            g_windowsUnavailableReported = true;
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6525, "AutoBanFirewall::Synchronise",
               "AutoBanFirewall is on but " + error + ". Banned addresses are refused by the server itself and are NOT blocked in the firewall. Reported once.");
         }
         return;
      }

      // The first pass learns what a previous run of the service left behind, so
      // that a rule for a ban that expired while the service was stopped - or every
      // rule, if the setting was turned off in between - is removed rather than
      // left to block an address for ever.
      if (!g_windowsRulesKnown)
      {
         if (!EnumerateOurRules(rules, g_windowsRules, error))
         {
            if (enabled && !g_windowsUnavailableReported)
            {
               g_windowsUnavailableReported = true;
               ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6525, "AutoBanFirewall::Synchronise",
                  "AutoBanFirewall is on but " + error + ". Reported once.");
            }
            return;
         }
         g_windowsRulesKnown = true;
         if (!g_windowsRules.empty())
            LOG_DEBUG(_T("AutoBanFirewall - ") + StringParser::IntToString((int) g_windowsRules.size()) + _T(" rule(s) of ours found in Windows Defender Firewall"));
      }

      const std::set<String> &target = enabled ? desired : std::set<String>();

      std::vector<String> toRemove;
      for (const String &address : g_windowsRules)
      {
         if (target.find(address) == target.end())
            toRemove.push_back(address);
      }

      for (const String &address : toRemove)
      {
         if (RemoveRule(rules, address, error))
         {
            g_windowsRules.erase(address);
            LOG_APPLICATION(_T("Auto-ban: the Windows Defender Firewall rule for ") + address + _T(" was removed"));
         }
         else
         {
            // Left in the set so that it is tried again next minute; a rule that
            // cannot be removed is a lockout that outlives the ban, which is the
            // one outcome this class exists to prevent, so it is reported each time.
            ErrorManager::Instance()->ReportError(ErrorManager::High, 6522, "AutoBanFirewall::Synchronise",
               "The auto-ban for " + address + " has ended but " + error + ". The address stays blocked in the firewall until this succeeds; remove the rule by hand if it does not.");
         }
      }

      for (const String &address : target)
      {
         if (g_windowsRules.find(address) != g_windowsRules.end())
            continue;

         if (AddRule(rules, address, ports, error))
         {
            g_windowsRules.insert(address);
            LOG_APPLICATION(_T("Auto-ban: ") + address + _T(" is blocked in Windows Defender Firewall on TCP port(s) ") + (ports.IsEmpty() ? String(_T("all")) : ports));
         }
         else
         {
            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 6521, "AutoBanFirewall::Synchronise",
               "The auto-ban for " + address + " is in force in the server but " + error + " The address is refused by the server itself and is NOT blocked in the firewall.");
         }
      }
   }

#endif
}
