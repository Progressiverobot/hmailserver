// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.Security
{
   /// <summary>
   ///    An auto-ban is an expiring IP range that the listeners consult at accept
   ///    time. With AutoBanFirewall=1 the server also writes a block rule for the
   ///    address into Windows Defender Firewall, and removes it when the range goes -
   ///    whether by expiry or because an administrator deleted it. AutoBanCommand
   ///    runs any program with the same two events, and AutoBanNeverBan is the list
   ///    of addresses that are never banned at all, which matters far more once a
   ///    ban can lock an administrator out of the whole machine's mail ports.
   ///
   ///    The rule is created for 127.0.0.1, the address every test connects from.
   ///    That is safe because Windows Filtering Platform does not filter loopback
   ///    traffic, so the rule is inert for the suite itself; what these tests check
   ///    is that the rule exists with the right shape and that it disappears with
   ///    the range. Every test deletes the range it made in its finally block,
   ///    because a range left behind refuses the next fixture's connections.
   ///
   ///    The firewall is read through IDispatch by reflection rather than through a
   ///    NetFwTypeLib reference, so the test project gains no dependency for it.
   /// </summary>
   [TestFixture]
   public class AutoBanFirewall : TestFixtureBase
   {
      private const string Address = "127.0.0.1";
      private const string RuleGroup = "hMailServer auto-ban";

      private bool _originalAutoBan;
      private int _originalMaxAttempts;
      private int _originalWithin;
      private int _originalMinutes;

      private static string RuleName(string address)
      {
         return "hMailServer auto-ban " + address;
      }

      /// <summary>
      ///    The error log, or a note that there is none. LogHandler.ReadErrorLog
      ///    throws when the file does not exist, and an assertion message that
      ///    throws while being built reports the wrong failure.
      /// </summary>
      private static string SafeErrorLog()
      {
         // ReadErrorLog asserts that the file exists, and NUnit records a failed
         // assertion even when its exception is caught. Here, no file is the
         // ordinary case, so the existence check comes first.
         if (!File.Exists(LogHandler.GetErrorLogFileName()))
            return "(no error log was written)";
         return LogHandler.ReadErrorLog();
      }

      private static object Property(object comObject, string name)
      {
         return comObject.GetType().InvokeMember(name, BindingFlags.GetProperty, null, comObject, null);
      }

      private static object Call(object comObject, string name, params object[] args)
      {
         return comObject.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, comObject, args);
      }

      /// <summary>
      ///    Windows Defender Firewall's rule collection, read-only. Reading needs no
      ///    elevation; the service, which runs as LocalSystem, is what writes.
      /// </summary>
      private static object FirewallRules()
      {
         Type type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
         Assert.IsNotNull(type, "Windows Defender Firewall's COM policy object is not registered on this machine.");
         object policy = Activator.CreateInstance(type);
         return Property(policy, "Rules");
      }

      private static object FindRule(string name)
      {
         try
         {
            return Call(FirewallRules(), "Item", name);
         }
         catch (TargetInvocationException)
         {
            // Item() answers "the system cannot find the file specified" for a
            // name that is not there, wrapped by the reflection call.
            return null;
         }
         catch (FileNotFoundException)
         {
            return null;
         }
         catch (COMException)
         {
            return null;
         }
      }

      private static object WaitForRule(string name, bool present, TimeSpan timeout)
      {
         DateTime deadline = DateTime.UtcNow + timeout;
         object rule = FindRule(name);
         while ((rule != null) != present && DateTime.UtcNow < deadline)
         {
            Thread.Sleep(250);
            rule = FindRule(name);
         }
         return rule;
      }

      private hMailServer.SecurityRange FindRange(string user)
      {
         _settings.SecurityRanges.Refresh();
         try
         {
            return _settings.SecurityRanges.get_ItemByName("Auto-ban: " + user);
         }
         catch (COMException)
         {
            return null;
         }
      }

      private void DeleteRangeIfPresent(string user)
      {
         hMailServer.SecurityRange range = FindRange(user);
         if (range != null)
            range.Delete();
      }

      /// <summary>
      ///    One failed PASS per connection, so that the per-connection cap never
      ///    fires and every failure is a counted one.
      /// </summary>
      private static void FailLogons(string user, int count)
      {
         for (int i = 0; i < count; i++)
         {
            var tc = new TcpConnection();
            Assert.IsTrue(tc.Connect(110), "POP3 refused the connection on attempt " + (i + 1) + " - is 127.0.0.1 already banned?");
            tc.ReadUntil("+OK");
            tc.Send("USER " + user + "\r\n");
            tc.ReadUntil("+OK");
            tc.Send("PASS wrong-" + i + "\r\n");
            try
            {
               tc.Receive();
            }
            catch (Exception e) when (!ExceptionPolicy.IsFatal(e))
            {
               // The connection may be dropped on the failure that bans; that is fine.
            }
            tc.Disconnect();
         }
      }

      [SetUp]
      public void RememberAutoBanSettings()
      {
         _originalAutoBan = _settings.AutoBanOnLogonFailure;
         _originalMaxAttempts = _settings.MaxInvalidLogonAttempts;
         _originalWithin = _settings.MaxInvalidLogonAttemptsWithin;
         _originalMinutes = _settings.AutoBanMinutes;

         _settings.AutoBanOnLogonFailure = true;
         _settings.MaxInvalidLogonAttempts = 3;
         _settings.MaxInvalidLogonAttemptsWithin = 5;
         _settings.AutoBanMinutes = 5;
         _settings.ClearLogonFailureList();
      }

      [TearDown]
      public void RestoreAutoBanSettings()
      {
         _settings.AutoBanOnLogonFailure = _originalAutoBan;
         _settings.MaxInvalidLogonAttempts = _originalMaxAttempts;
         _settings.MaxInvalidLogonAttemptsWithin = _originalWithin;
         _settings.AutoBanMinutes = _originalMinutes;
         _settings.ClearLogonFailureList();
      }

      [Test]
      [Description("With AutoBanFirewall=1 a ban writes an inbound TCP block rule for the address into Windows Defender Firewall, scoped to the server's ports, and deleting the range removes the rule")]
      public void TheBanIsMirroredInWindowsDefenderFirewallAndFollowsTheRange()
      {
         string user = "fwban@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, user, "secret");

         try
         {
            ServerIniFile.SetSetting("AutoBanFirewall", "1");
            RestartServerAndReacquireCom();

            // The start-up pass removes any rule of ours that has no range behind
            // it, so a rule left by an earlier aborted run is gone by now.
            Assert.IsNull(WaitForRule(RuleName(Address), false, TimeSpan.FromSeconds(5)),
               "A rule for " + Address + " existed before any ban; the start-up reconciliation should have removed it.");

            FailLogons(user, 3);

            Assert.IsNotNull(FindRange(user), "Three failed logons should have created the auto-ban range.");

            object rule = WaitForRule(RuleName(Address), true, TimeSpan.FromSeconds(20));
            Assert.IsNotNull(rule, "The ban was created but no firewall rule named '" + RuleName(Address) + "' appeared. Error log: " + SafeErrorLog());

            Assert.IsTrue((bool) Property(rule, "Enabled"), "The rule must be enabled.");
            Assert.AreEqual(1, (int) Property(rule, "Direction"), "The rule must be inbound (NET_FW_RULE_DIR_IN = 1).");
            Assert.AreEqual(0, (int) Property(rule, "Action"), "The rule must block (NET_FW_ACTION_BLOCK = 0).");
            Assert.AreEqual(6, (int) Property(rule, "Protocol"), "The rule must be TCP only, so that a banned address is not cut off from everything else on the machine.");
            Assert.AreEqual(RuleGroup, (string) Property(rule, "Grouping"), "Every rule of ours carries the same group, which is how they are told from everything else.");
            StringAssert.Contains(Address, (string) Property(rule, "RemoteAddresses"), "The rule must name the banned address.");
            StringAssert.Contains("110", (string) Property(rule, "LocalPorts"), "The rule must be scoped to the ports the server listens on; POP3's 110 is one of them.");

            // An administrator lifts a ban by deleting the range. The firewall
            // follows the table, at once rather than on the next expiry pass.
            FindRange(user).Delete();

            Assert.IsNull(WaitForRule(RuleName(Address), false, TimeSpan.FromSeconds(20)),
               "The range was deleted but the firewall rule stayed - which is a lockout that outlives the ban. Error log: " + SafeErrorLog());
         }
         finally
         {
            DeleteRangeIfPresent(user);
            ServerIniFile.SetSetting("AutoBanFirewall", null);
            RestartServerAndReacquireCom();
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("An address inside an AutoBanNeverBan block is never counted, so it is never banned however many logons it fails")]
      public void AnAddressOnTheNeverBanListIsNeverBanned()
      {
         string user = "neverban@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, user, "secret");

         try
         {
            // A block that contains 127.0.0.1, written the way an administrator
            // would write it - with a space after the comma and another entry first.
            ServerIniFile.SetSetting("AutoBanNeverBan", "10.0.0.0/8, 127.0.0.0/8");
            RestartServerAndReacquireCom();

            FailLogons(user, 6);

            Assert.IsNull(FindRange(user),
               "Six failed logons from a never-ban address created an auto-ban range; the list was not honoured.");
            Assert.IsNull(FindRule(RuleName(Address)), "No range means no rule.");
         }
         finally
         {
            DeleteRangeIfPresent(user);
            ServerIniFile.SetSetting("AutoBanNeverBan", null);
            RestartServerAndReacquireCom();
         }
      }

      [Test]
      [Description("A list entry that is not an address is reported by name in the error log, because a list the administrator believes protects them and does not is worse than no list")]
      public void AnUnreadableNeverBanEntryIsReported()
      {
         string user = "badlist@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, user, "secret");

         try
         {
            ServerIniFile.SetSetting("AutoBanNeverBan", "not-an-address, 127.0.0.1/40");
            RestartServerAndReacquireCom();
            LogHandler.DeleteErrorLog();

            // The list is parsed on the first failure that consults it.
            FailLogons(user, 1);

            string errorLog = SafeErrorLog();
            StringAssert.Contains("AutoBanNeverBan", errorLog, "The error log must name the setting. Log: " + errorLog);
            StringAssert.Contains("not-an-address", errorLog, "The error log must name the entry that was dropped. Log: " + errorLog);
            StringAssert.Contains("127.0.0.1/40", errorLog, "A prefix wider than the address is not a block either. Log: " + errorLog);
         }
         finally
         {
            DeleteRangeIfPresent(user);
            ServerIniFile.SetSetting("AutoBanNeverBan", null);
            RestartServerAndReacquireCom();
            LogHandler.DeleteErrorLog();
         }
      }

      [Test]
      [Description("AutoBanCommand is run as '<command> ban <address> <minutes> <ports>' when a ban is created and '<command> unban <address>' when the range is deleted")]
      public void TheHookCommandIsToldAboutBansAndUnbans()
      {
         string user = "hookban@example.test";
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, user, "secret");

         string folder = Path.Combine(Path.GetTempPath(), "hmailserver-autoban-hook-test");
         Directory.CreateDirectory(folder);
         string script = Path.Combine(folder, "hook.cmd");
         string log = Path.Combine(folder, "hook.log");
         File.Delete(log);
         File.WriteAllText(script, "@echo %* >> \"" + log + "\"\r\n");

         try
         {
            ServerIniFile.SetSetting("AutoBanCommand", "cmd.exe /c \"" + script + "\"");
            RestartServerAndReacquireCom();

            FailLogons(user, 3);
            Assert.IsNotNull(FindRange(user), "Three failed logons should have created the auto-ban range.");

            string content = WaitForFileContaining(log, "ban " + Address + " ", TimeSpan.FromSeconds(15));
            StringAssert.Contains("ban " + Address + " 5 ", content, "The hook must be told 'ban <address> <minutes> <ports>'. Hook log: " + content + " Error log: " + SafeErrorLog());
            StringAssert.Contains("110", content, "The port list the server listens on is passed as the fourth argument. Hook log: " + content);

            FindRange(user).Delete();

            content = WaitForFileContaining(log, "unban " + Address, TimeSpan.FromSeconds(15));
            StringAssert.Contains("unban " + Address, content, "Deleting the range must run the hook with 'unban <address>'. Hook log: " + content + " Error log: " + SafeErrorLog());
         }
         finally
         {
            DeleteRangeIfPresent(user);
            ServerIniFile.SetSetting("AutoBanCommand", null);
            RestartServerAndReacquireCom();
            LogHandler.DeleteErrorLog();
            try
            {
               Directory.Delete(folder, true);
            }
            catch (IOException)
            {
               // The service may still hold the log for a moment; it is temp space.
            }
         }
      }

      private static string WaitForFileContaining(string path, string text, TimeSpan timeout)
      {
         DateTime deadline = DateTime.UtcNow + timeout;
         string content = "";
         while (DateTime.UtcNow < deadline)
         {
            if (File.Exists(path))
            {
               using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
               using (var reader = new StreamReader(stream))
                  content = reader.ReadToEnd();
               if (content.Contains(text))
                  return content;
            }
            Thread.Sleep(250);
         }
         return content;
      }
   }
}
