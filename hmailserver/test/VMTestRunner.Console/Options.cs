using CommandLine;

namespace VMTestRunner.Console
{
   class Options
   {
      [Option('i', "installer", Required = true, HelpText = "Path to the hMailServer installer executable.")]
      public string InstallerPath { get; set; }

      [Option('p', "parallelism", Required = false, Default = 1, HelpText = "Max degree of parallelism for VM test execution.")]
      public int MaxParallelism { get; set; }
   }
}
