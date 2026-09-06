// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Infrastructure
{
   /// <summary>
   ///    Schema 6030 gives every child column that can only ever name a real parent
   ///    a FOREIGN KEY with ON DELETE CASCADE. Until then every cascade was a loop in
   ///    a Persistent* class, and a crash between the parent's DELETE and the loop's
   ///    left orphans nothing ever cleaned up. Two things are true now and are proved
   ///    here against the database itself rather than through the loops: a child row
   ///    cannot be written for a parent that does not exist, and deleting a parent
   ///    row - by hand, behind the server's back, the way a crash would leave it -
   ///    takes its children with it, two levels down.
   ///
   ///    The counts are read straight from the SQL CE file with the server's own OLE
   ///    DB provider, because the COM API can execute SQL but not read a result set.
   /// </summary>
   [TestFixture]
   public class ReferentialIntegrity : TestFixtureBase
   {
      private static string connectionString_;

      private static string ConnectionString()
      {
         if (connectionString_ != null)
            return connectionString_;

         string ini = null;
         foreach (string candidate in IniFileSetting.ExistingIniFiles())
         {
            ini = candidate;
            break;
         }
         Assert.IsNotNull(ini, "No hMailServer.ini found for the test server.");

         string text = File.ReadAllText(ini);
         string type = Regex.Match(text, @"(?m)^\s*Type\s*=\s*(\S+)").Groups[1].Value;
         if (!string.Equals(type, "MSSQLCE", StringComparison.OrdinalIgnoreCase))
            Assert.Ignore("Direct row counts are read from the SQL CE test database; this server runs " + type + ".");

         string folder = Regex.Match(text, @"(?m)^\s*DatabaseFolder\s*=\s*(.+?)\s*$").Groups[1].Value;
         string database = Regex.Match(text, @"(?m)^\s*Database\s*=\s*(.+?)\s*$").Groups[1].Value;
         string encoded = Regex.Match(text, @"(?m)^\s*Password\s*=\s*(.+?)\s*$").Groups[1].Value;

         // The ini's password is a DPAPI envelope for the machine, the way the server
         // writes it; the provider wants it plain.
         string password = Encoding.UTF8.GetString(UnprotectForMachine(Convert.FromBase64String(encoded)));

         connectionString_ = "Provider=Microsoft.SQLSERVER.CE.OLEDB.4.0;Data Source=" + Paths.Combine(folder, database + ".sdf") +
                             ";SSCE:Database Password=" + password;
         return connectionString_;
      }

      // CryptUnprotectData with the machine scope, through the framework's wrapper for
      // the identical call (System.Security.dll, referenced by the project), so no
      // P/Invoke is needed for it.
      private static byte[] UnprotectForMachine(byte[] envelope)
      {
         return ProtectedData.Unprotect(envelope, null, DataProtectionScope.LocalMachine);
      }

      // "select count(*) ..." against the database file, through ADODB by late binding
      // so that the test project needs nothing it does not already reference.
      private static int Count(string sql)
      {
         Type connectionType = Type.GetTypeFromProgID("ADODB.Connection", true);
         object connection = Activator.CreateInstance(connectionType);
         connectionType.InvokeMember("Open", BindingFlags.InvokeMethod, null, connection, new object[] { ConnectionString() });
         try
         {
            object recordset = connectionType.InvokeMember("Execute", BindingFlags.InvokeMethod, null, connection, new object[] { sql });
            Type recordsetType = recordset.GetType();
            object fields = recordsetType.InvokeMember("Fields", BindingFlags.GetProperty, null, recordset, null);
            object field = fields.GetType().InvokeMember("Item", BindingFlags.GetProperty, null, fields, new object[] { 0 });
            object value = field.GetType().InvokeMember("Value", BindingFlags.GetProperty, null, field, null);
            recordsetType.InvokeMember("Close", BindingFlags.InvokeMethod, null, recordset, null);
            return Convert.ToInt32(value);
         }
         finally
         {
            connectionType.InvokeMember("Close", BindingFlags.InvokeMethod, null, connection, null);
         }
      }

      private void ExecuteSql(string sql)
      {
         SingletonProvider<TestSetup>.Instance.GetApp().Database.ExecuteSQL(sql);
      }

      [Test]
      [Description("A child row that names a parent which does not exist is refused by the database itself, whatever code path tries to write it.")]
      public void AChildRowCannotNameAParentThatDoesNotExist()
      {
         const int NoSuchGroup = 987654321;
         Assert.AreEqual(0, Count("select count(*) from hm_groups where groupid = " + NoSuchGroup));

         var refused = Assert.Throws<COMException>(() =>
            ExecuteSql("insert into hm_group_members (membergroupid, memberaccountid) values (" + NoSuchGroup + ", 1)"),
            "The insert must be refused: nothing may reference a group that does not exist.");
         StringAssert.Contains("hm_group_members", refused.Message);

         Assert.AreEqual(0, Count("select count(*) from hm_group_members where membergroupid = " + NoSuchGroup),
            "Nothing was written.");

         // The refusal is reported through the API error and also, by the connection
         // that saw it, to the error log; that line is this test's, not a fault.
         LogHandler.ReadAndDeleteErrorLog();
      }

      [Test]
      [Description("Deleting a domain row behind the server's back takes its aliases, domain aliases and distribution lists with it, and the lists' recipients two levels down - what a crash mid-delete used to leave behind.")]
      public void DeletingAParentRowTakesItsChildrenWithIt()
      {
         Domain domain = _application.Domains.Add();
         domain.Name = "cascade.test";
         domain.Active = true;
         domain.Save();
         int domainId = domain.ID;

         Alias alias = domain.Aliases.Add();
         alias.Name = "sales@cascade.test";
         alias.Value = "someone@cascade.test";
         alias.Active = true;
         alias.Save();

         DomainAlias domainAlias = domain.DomainAliases.Add();
         domainAlias.AliasName = "cascade-alias.test";
         domainAlias.Save();

         DistributionList list = domain.DistributionLists.Add();
         list.Address = "everyone@cascade.test";
         list.Active = true;
         list.Save();
         int listId = list.ID;

         DistributionListRecipient recipient = list.Recipients.Add();
         recipient.RecipientAddress = "member@elsewhere.test";
         recipient.Save();

         Assert.AreEqual(1, Count("select count(*) from hm_aliases where aliasdomainid = " + domainId));
         Assert.AreEqual(1, Count("select count(*) from hm_domain_aliases where dadomainid = " + domainId));
         Assert.AreEqual(1, Count("select count(*) from hm_distributionlists where distributionlistdomainid = " + domainId));
         Assert.AreEqual(1, Count("select count(*) from hm_distributionlistsrecipients where distributionlistrecipientlistid = " + listId));

         // The parent row alone, the way a crash between the DELETE and the cascade
         // loop would have left things.
         ExecuteSql("delete from hm_domains where domainid = " + domainId);

         Assert.AreEqual(0, Count("select count(*) from hm_aliases where aliasdomainid = " + domainId), "aliases cascade");
         Assert.AreEqual(0, Count("select count(*) from hm_domain_aliases where dadomainid = " + domainId), "domain aliases cascade");
         Assert.AreEqual(0, Count("select count(*) from hm_distributionlists where distributionlistdomainid = " + domainId), "lists cascade");
         Assert.AreEqual(0, Count("select count(*) from hm_distributionlistsrecipients where distributionlistrecipientlistid = " + listId),
            "a list's recipients cascade with the list, two levels below the domain");

         // The server's cache still holds the domain it did not delete itself.
         _application.Reinitialize();
         Assert.Throws<COMException>(() => { var gone = _application.Domains.ItemByName["cascade.test"]; },
            "After the reload the domain is gone from the API as well.");
      }
   }
}
