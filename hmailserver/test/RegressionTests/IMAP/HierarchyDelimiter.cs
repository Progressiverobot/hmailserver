// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.IMAP
{
   [TestFixture]
   public class HierarchyDelimiter : TestFixtureBase
   {
      [Test]
      [Description("Test that the hierchary delimiter has effect on the Create command.")]
      public void TestHierarchyDelimiterCreate()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var settings = _settings;
         settings.IMAPHierarchyDelimiter = "/";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "quote@example.test", "test");

         var folderName = "Test.Test";

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         Assert.IsTrue(simulator.CreateFolder(folderName));
         var listResponse = simulator.List();
         Assert.IsTrue(listResponse.Contains("\"" + folderName + "\""));
         Assert.IsTrue(!listResponse.Contains("\"Test\""));
         simulator.Disconnect();
      }

      [Test]
      [Description("Test that the hierchary delimiter has effect on the Delete command.")]
      public void TestHierarchyDelimiterDelete()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var settings = _settings;
         settings.IMAPHierarchyDelimiter = "\\";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "quote@example.test", "test");

         var folderName = "Test\\Test";

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         Assert.IsTrue(simulator.CreateFolder(folderName));
         var listResponse = simulator.List();
         Assert.IsTrue(listResponse.Contains("\"Test\\Test\""));
         Assert.IsTrue(listResponse.Contains("\"Test\""));
         Assert.IsTrue(simulator.DeleteFolder("Test\\Test"));
         listResponse = simulator.List();
         Assert.IsFalse(listResponse.Contains("Test\\Test"));
         Assert.IsTrue(listResponse.Contains("Test"));
         simulator.Disconnect();
      }

      [Test]
      [Description("Test that the hierchary delimiter has effect on the List response.")]
      public void TestHierarchyDelimiterListResponse()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var settings = _settings;
         settings.IMAPHierarchyDelimiter = "\\";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "quote@example.test", "test");

         var folderName = "Test\\Test";

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         Assert.IsTrue(simulator.CreateFolder(folderName));
         var listResponse = simulator.List();
         Assert.IsTrue(listResponse.Contains("\"Test\\Test\""));
         Assert.IsTrue(listResponse.Contains("\"Test\""));
         simulator.Disconnect();
      }

      [Test]
      [Description("Test that the hierchary delimiter has effect on the LSUB response.")]
      public void TestHierarchyDelimiterLsubResponse()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var settings = _settings;
         settings.IMAPHierarchyDelimiter = "/";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "quote@example.test", "test");

         var folderName = "Test/Test";

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         Assert.IsTrue(simulator.CreateFolder(folderName));
         Assert.IsTrue(simulator.Subscribe("Test"));
         Assert.IsTrue(simulator.Subscribe("Test/Test"));
         var lsubResponse = simulator.LSUB();
         Assert.IsTrue(lsubResponse.Contains("\"Test/Test\""));
         Assert.IsTrue(lsubResponse.Contains("\"Test\""));
         simulator.Disconnect();
      }

      [Test]
      [Description("Test that the hierchary delimiter has effect on the Namespace response.")]
      public void TestHierarchyDelimiterNamespaceResponse()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var settings = _settings;
         settings.IMAPHierarchyDelimiter = "\\";
         var publicFolderName = _settings.IMAPPublicFolderName;

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "quote@example.test", "test");

         var simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");
         var result = simulator.Send("A01 NAMESPACE");
         var correctNamespaceSetting = "* NAMESPACE ((\"\" \"\\\\\")) NIL ((\"" + publicFolderName +
                                       "\" \"\\\\\"))";
         Assert.IsTrue(result.Contains(correctNamespaceSetting), result);
         simulator.Disconnect();

         settings.IMAPHierarchyDelimiter = ".";

         simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");

         result = simulator.Send("A01 NAMESPACE");
         correctNamespaceSetting = "* NAMESPACE ((\"\" \".\")) NIL ((\"" + publicFolderName + "\" \".\"))";
         Assert.IsTrue(result.Contains(correctNamespaceSetting), result);
         simulator.Disconnect();

         settings.IMAPHierarchyDelimiter = "/";

         simulator = new ImapClientSimulator();
         simulator.ConnectAndLogon(account.Address, "test");

         result = simulator.Send("A01 NAMESPACE");
         correctNamespaceSetting = "* NAMESPACE ((\"\" \"/\")) NIL ((\"" + publicFolderName + "\" \"/\"))";
         Assert.IsTrue(result.Contains(correctNamespaceSetting), result);
         simulator.Disconnect();
      }

      [Test]
      [Description("Test that the hierchary delimiter has effect on the Rename command.")]
      public void TestHierarchyDelimiterRename()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var settings = _settings;
         settings.IMAPHierarchyDelimiter = "/";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "quote@example.test", "test");

         var folderName = "Test/Test";
         var newFolderName = "Apa/Test";

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         Assert.IsTrue(simulator.CreateFolder(folderName));
         var listResponse = simulator.List();
         Assert.IsTrue(listResponse.Contains("\"" + folderName + "\""));
         Assert.IsTrue(listResponse.Contains("\"Test\""));
         Assert.IsTrue(simulator.RenameFolder("Test", "Apa"));
         listResponse = simulator.List();
         Assert.IsTrue(listResponse.Contains("\"" + newFolderName + "\""));
         Assert.IsTrue(listResponse.Contains("\"Apa\""));
         simulator.Disconnect();
      }

      [Test]
      [Description("Test that the hierchary delimiter has effect on the LSUB response.")]
      public void TestHierarchyDelimiterSlash()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         var settings = _settings;
         settings.IMAPHierarchyDelimiter = "\\";

         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "quote@example.test", "test");

         var folderName = "Test\\Test";

         var simulator = new ImapClientSimulator();
         var sWelcomeMessage = simulator.Connect();
         simulator.Logon(account.Address, "test");
         Assert.IsTrue(simulator.CreateFolder(folderName));
         Assert.IsTrue(simulator.Subscribe("Test"));
         Assert.IsTrue(simulator.Subscribe("Test\\Test"));
         var lsubResponse = simulator.LSUB();
         Assert.IsTrue(lsubResponse.Contains("\"Test\\Test\""));
         Assert.IsTrue(lsubResponse.Contains("\"Test\""));
         simulator.Disconnect();
      }
   }
}