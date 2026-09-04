// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using RegressionTests.Shared;

namespace hMailServer.PerformanceTests.Console
{
   class Program
   {
      static void Main(string[] args)
      {
         var outputFolder = args[0];

         var app = new hMailServer.ApplicationClass();
         var version = app.Version;

         ExecuteTests();

         System.Console.WriteLine("Test results follow:");

         foreach (var result in TestPerformanceInfo.Timings)
         {
            System.Console.WriteLine("{0}\t{1}", result.Key, result.Value);
         }

         System.Console.ReadKey();
      }

      private static void ExecuteTests()
      {
         Assembly assembly = Assembly.LoadFrom("hMailServer.PerformanceTests.dll");
         var allTypes = assembly.GetTypes();

         foreach (Type type in allTypes.Where(type => type.BaseType == typeof (TestFixtureBase)))
         {
            var instance = Activator.CreateInstance(type);

            ((TestFixtureBase) instance).TestFixtureSetUp();

            RunFixtureTests(type, instance);
         }
      }

      private static void RunFixtureTests(Type type, object instance)
      {
         MethodInfo testSetup = null;
         MethodInfo testTearDown = null;

         var testMethods = new List<MethodInfo>();

         foreach (var method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public ))
         {
            if (method.GetCustomAttributes()
               .Any(attr => attr.GetType() == typeof (NUnit.Framework.SetUpAttribute)))
            {
               testSetup = method;
            }
            else if (method.GetCustomAttributes()
               .Any(attr => attr.GetType() == typeof (NUnit.Framework.TearDownAttribute)))
            {
               testTearDown = method;
            }
            else if (method.GetCustomAttributes()
               .Any(attr => attr.GetType() == typeof (NUnit.Framework.TestAttribute)))
            {
               testMethods.Add(method);
            }
         }

         foreach (var testMethod in testMethods)
         {
            ((TestFixtureBase)instance).SetUp();

            if (testSetup != null)
               testSetup.Invoke(instance, null);

            testMethod.Invoke(instance, null);

            if (testTearDown != null)
               testTearDown.Invoke(instance, null);
         }
      }
   }
}
