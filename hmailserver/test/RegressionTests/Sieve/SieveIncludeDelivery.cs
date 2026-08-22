// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Reflection;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;
using hMailServer;

namespace RegressionTests.Sieve
{
   /// <summary>
   ///    include, return and global (RFC 6609), asserted through real deliveries
   ///    filed by INCLUDED scripts. The included personal scripts are written the
   ///    way a user's client writes them - as named scripts in the account's
   ///    script store - and the global one the way an administrator places it, as
   ///    a file in the shared directory.
   ///
   ///    The variable-scoping test is the one that earns RFC 6609 3.4 its keep: a
   ///    helper script's private variable must NOT leak into the includer, while a
   ///    declared-global one must. Both directions are proven by where a message
   ///    is filed.
   /// </summary>
   [TestFixture]
   public class SieveIncludeDelivery : TestFixtureBase
   {
      private const string Password = "secret";

      private static int accountSequence_;

      private static void SetScript(Account account, string script)
      {
         account.GetType().InvokeMember(
            "SieveScript", BindingFlags.SetProperty, null, account, new object[] { script });
      }

      /// <summary>
      ///    Stores a named personal script the way ManageSieve PUTSCRIPT stores it:
      ///    in the account's script store on disk. Written directly rather than
      ///    through a ManageSieve session to keep each test self-contained; the
      ///    path layout is the one SieveStorage owns and the round-trip fixture
      ///    already proves PUTSCRIPT writes exactly here.
      /// </summary>
      private void StorePersonalScript(Account account, string name, string content)
      {
         string dataDir = _settings.Directories.DataDirectory;
         string domain = account.Address.Substring(account.Address.IndexOf('@') + 1);
         string localPart = account.Address.Substring(0, account.Address.IndexOf('@'));

         string scriptsDir = Path.Combine(dataDir, "Sieve", domain, localPart, "scripts");
         Directory.CreateDirectory(scriptsDir);
         File.WriteAllText(Path.Combine(scriptsDir, name + ".sieve"), content);
      }

      private void StoreGlobalScript(string name, string content)
      {
         string dataDir = _settings.Directories.DataDirectory;
         string globalDir = Path.Combine(dataDir, "Sieve", "_global");
         Directory.CreateDirectory(globalDir);
         File.WriteAllText(Path.Combine(globalDir, name + ".sieve"), content);
      }

      private Account NewRecipient(string activeScript, params string[] folders)
      {
         accountSequence_++;
         Account recipient = SingletonProvider<TestSetup>.Instance.AddAccount(
            _domain, "sieve-inc-" + accountSequence_ + "@example.test", Password);

         foreach (string folder in folders)
            recipient.IMAPFolders.Add(folder);

         SetScript(recipient, activeScript);
         return recipient;
      }

      private void Send(Account recipient, string subject)
      {
         SmtpClientSimulator.StaticSend("sieve-inc-sender@example.test", recipient.Address, subject, "Body text.");
         CustomAsserts.AssertRecipientsInDeliveryQueue(0);
      }

      private void AssertFolderCount(Account recipient, string folder, int expected)
      {
         IMAPFolder imapFolder = recipient.IMAPFolders.get_ItemByName(folder);
         CustomAsserts.AssertFolderMessageCount(imapFolder, expected);
      }

      [Test]
      [Description("A personal included script's fileinto decides where the message lands.")]
      public void APersonalIncludedScriptFiles()
      {
         Account recipient = NewRecipient(
            "require \"include\";\r\n" +
            "include :personal \"filing\";\r\n",
            "FiledByHelper");

         StorePersonalScript(recipient, "filing",
            "require \"fileinto\";\r\n" +
            "fileinto \"FiledByHelper\";\r\n");

         Send(recipient, "Anything");

         AssertFolderCount(recipient, "FiledByHelper", 1);
         AssertFolderCount(recipient, "INBOX", 0);
      }

      [Test]
      [Description("A global included script - the administrator's shared directory - files the message.")]
      public void AGlobalIncludedScriptFiles()
      {
         StoreGlobalScript("company-policy",
            "require \"fileinto\";\r\n" +
            "if header :contains \"Subject\" \"[policy]\" {\r\n" +
            "  fileinto \"Policy\";\r\n" +
            "}\r\n");

         Account recipient = NewRecipient(
            "require \"include\";\r\n" +
            "include :global \"company-policy\";\r\n",
            "Policy");

         Send(recipient, "[policy] new rules");

         AssertFolderCount(recipient, "Policy", 1);
         AssertFolderCount(recipient, "INBOX", 0);
      }

      /// <summary>
      ///    The missing-script control: :optional means silence, and delivery must
      ///    proceed to INBOX untouched - a renamed helper cannot stop mail.
      /// </summary>
      [Test]
      [Description("A missing :optional include is silence; the message still delivers.")]
      public void AMissingOptionalIncludeIsHarmless()
      {
         Account recipient = NewRecipient(
            "require \"include\";\r\n" +
            "include :optional \"no-such-helper\";\r\n");

         Send(recipient, "Anything");

         AssertFolderCount(recipient, "INBOX", 1);
      }

      [Test]
      [Description("return unwinds one level: the includer's commands after the include still run.")]
      public void ReturnUnwindsExactlyOneLevel()
      {
         Account recipient = NewRecipient(
            "require [\"include\", \"fileinto\"];\r\n" +
            "include :personal \"early-out\";\r\n" +
            "fileinto \"AfterInclude\";\r\n",
            "AfterInclude", "NeverReached");

         StorePersonalScript(recipient, "early-out",
            "require [\"include\", \"fileinto\"];\r\n" +
            "return;\r\n" +
            "fileinto \"NeverReached\";\r\n");

         Send(recipient, "Anything");

         // The helper returned before ITS fileinto; the includer's ran.
         AssertFolderCount(recipient, "AfterInclude", 1);
         AssertFolderCount(recipient, "NeverReached", 0);
      }

      /// <summary>
      ///    RFC 6609 3.4 in both directions. The helper sets a private "verdict"
      ///    and a global "shared". The includer files on each: the private one
      ///    must have leaked nothing (its test matches the empty expansion), the
      ///    global one must carry the helper's value.
      /// </summary>
      [Test]
      [Description("A helper's private variable does not leak; its declared-global one does.")]
      public void VariableScopingHoldsAcrossInclude()
      {
         Account recipient = NewRecipient(
            "require [\"include\", \"variables\", \"fileinto\"];\r\n" +
            "global \"shared\";\r\n" +
            "include :personal \"setter\";\r\n" +
            "if string :is \"${private}\" \"\" {\r\n" +
            "  if string :is \"${shared}\" \"from-helper\" {\r\n" +
            "    fileinto \"ScopingHolds\";\r\n" +
            "  }\r\n" +
            "}\r\n",
            "ScopingHolds");

         StorePersonalScript(recipient, "setter",
            "require [\"include\", \"variables\"];\r\n" +
            "global \"shared\";\r\n" +
            "set \"private\" \"should-not-leak\";\r\n" +
            "set \"shared\" \"from-helper\";\r\n");

         Send(recipient, "Anything");

         AssertFolderCount(recipient, "ScopingHolds", 1);
         AssertFolderCount(recipient, "INBOX", 0);
      }
   }
}
