namespace VMTestRunner.Console
{
   public class InstallCommand
   {
      public InstallCommand(string executable, string parameters)
      {
         Executable = executable;
         Parameters = parameters;
      }

      public string Executable { get; }

      public string Parameters { get; }
   }
}
