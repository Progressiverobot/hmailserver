// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure
{
   [TestFixture]
   public class MainOperations : TestFixtureBase
   {
      [SetUp]
      public void Setup()
      {
         SingletonProvider<TestSetup>.Instance.PerformBasicSetup();
      }

      [Test]
      public void RestartServer()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();
         for (var i = 0; i < 5; i++)
         {
            application.Stop();

            application.Start();
         }
      }

      [Test]
      public void TestInternals()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();

         application.Utilities.RunTestSuite("I know what I am doing.");
      }

      [Test]
      public void TestLanguages()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();

         var oLanguageSwedish = application.GlobalObjects.Languages.get_ItemByName("swedish");
         var oLanguageEnglish = application.GlobalObjects.Languages.get_ItemByName("english");

         var sSWE = oLanguageSwedish.get_String("File");
         var sENG = oLanguageEnglish.get_String("File");

         Assert.AreEqual(sENG, "File");
         Assert.AreNotEqual(sSWE, sENG);
         Assert.AreNotEqual(sSWE, "");
      }
   }
}