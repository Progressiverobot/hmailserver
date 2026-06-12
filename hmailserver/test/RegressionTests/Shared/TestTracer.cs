using System;

namespace RegressionTests.Shared
{
   public class TestTracer
   {
      public static void WriteTraceInfo(string format, params object[] args)
      {
         var data = string.Format(format, args);
         var completeMessage = string.Format("{0} - {1}", DateTime.Now, data);

         Console.WriteLine(completeMessage);
      }
   }
}