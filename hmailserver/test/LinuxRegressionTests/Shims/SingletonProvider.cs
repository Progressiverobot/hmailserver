// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RegressionTests.Shared
{
   /// <summary>
   ///    The Windows suite's SingletonProvider, unchanged in shape: the fixtures reach
   ///    their TestSetup through SingletonProvider&lt;TestSetup&gt;.Instance, and the
   ///    TestSetup here is the REST-backed one beside this file.
   /// </summary>
   public class SingletonProvider<T> where T : new()
   {
      private SingletonProvider()
      {
      }

      public static T Instance => SingletonCreator.instance;

      private class SingletonCreator
      {
         internal static readonly T instance = new T();
      }
   }
}
