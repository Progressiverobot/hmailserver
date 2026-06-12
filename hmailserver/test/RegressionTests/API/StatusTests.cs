// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.API
{
   [TestFixture]
   public class StatusTests : TestFixtureBase
   {
      [Test]
      public void TestAccessThreadId()
      {
         var application = SingletonProvider<TestSetup>.Instance.GetApp();

         var threadId = application.Status.ThreadID;
         Assert.AreNotEqual(0, threadId);
      }
   }
}