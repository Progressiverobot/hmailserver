// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

using System.Runtime.InteropServices;
using hMailServer;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.Infrastructure.Persistence
{
   [TestFixture]
   public class Maintenance : TestFixtureBase
   {
      [Test]
      public void TestUnknownOperation()
      {
         var ex = Assert.Throws<COMException>(() =>
            _application.Utilities.PerformMaintenance((eMaintenanceOperation)234));
         StringAssert.Contains("Unknown maintenance operation.", ex.Message);
      }

      [Test]
      public void TestUpdateIMAPFolderUID()
      {
         // Set up a basic environment which we can work with.
         var backupRestore = new BackupRestore();
         backupRestore.SetUp();
         backupRestore.SetupEnvironment();

         _application.Utilities.PerformMaintenance(eMaintenanceOperation.eUpdateIMAPFolderUID);
      }
   }
}