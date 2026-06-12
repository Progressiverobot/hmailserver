// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

namespace RegressionTests.Shared
{
   public class SingletonProvider<T> where T : new()
   {
      private SingletonProvider()
      {
      }

      public static T Instance => SingletonCreator.instance;

      #region Nested type: SingletonCreator

      private class SingletonCreator
      {
         internal static readonly T instance = new T();
      }

      #endregion
   }
}