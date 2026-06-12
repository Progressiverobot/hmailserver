namespace VMTestRunner.Console
{
   public class FileCopyCommand
   {
      public FileCopyCommand(string fromHost, string toGuest)
      {
         From = fromHost;
         To = toGuest;
      }

      public string From { get; }

      public string To { get; }
   }
}
