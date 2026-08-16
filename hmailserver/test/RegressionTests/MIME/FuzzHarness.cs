// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using RegressionTests.Shared;

namespace RegressionTests.MIME
{
   /// <summary>
   ///    Guards the MIME fuzzing harness in fuzz\ against the four ways a fuzz
   ///    setup rots without anybody noticing. Every one of them produces the same
   ///    symptom - a run that completes and reports nothing - which is
   ///    indistinguishable from a parser with no bugs left, and that is exactly
   ///    why these need to be assertions and not documentation.
   ///
   ///    1. Path drift. build-fuzz.ps1 names the Server\Common sources it
   ///       compiles. Move or rename Mime.cpp and the build fails - loudly, once,
   ///       for whoever runs it next, which may be months later. This project has
   ///       the precedent: SSL\TlsOptionsTests.cs sat committed and uncompiled for
   ///       months because nothing checked the file list, six tests that had never
   ///       executed once.
   ///    2. Resource drift. make-corpus.ps1 builds the seed corpus out of the real
   ///       test messages. Rename that directory and the corpus is empty; libFuzzer
   ///       runs happily on nothing and prints a plausible-looking summary.
   ///    3. Line-ending damage. The seed messages are CRLF-sensitive and two are
   ///       DKIM-signed - the repository's .gitattributes says so explicitly. A
   ///       seed corpus that lost its CRLFs still parses as *something*, just never
   ///       reaching the multipart code, so coverage quietly collapses.
   ///    4. A malformed dictionary. libFuzzer exits at startup with
   ///       "ParseDictionaryFile: error in line N" on one bad line. In a -jobs run
   ///       that message goes into a per-worker log nobody reads.
   ///
   ///    The last test is the only one that executes anything: it replays the seed
   ///    corpus through the built targets under AddressSanitizer, which is a real
   ///    regression test of the parser rather than of the harness. It is skipped
   ///    when the targets have not been built, because the clang toolchain is an
   ///    optional Visual Studio component and the suite must still run without it.
   /// </summary>
   [TestFixture]
   public class FuzzHarness : TestFixtureBase
   {
      // TestDirectory is <repo>\hmailserver\test\RegressionTests\bin\x64\Debug.
      // Six levels up is the repository root. Same relative walk SSL\SslSetup.cs
      // uses to find "SSL examples".
      private static string RepositoryRoot
      {
         get
         {
            return Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory,
                                                 @"..\..\..\..\..\.."));
         }
      }

      private static string FuzzRoot
      {
         get { return Path.Combine(RepositoryRoot, "fuzz"); }
      }

      private static string ReadFuzzFile(string relativePath)
      {
         string path = Path.Combine(FuzzRoot, relativePath);
         Assert.IsTrue(File.Exists(path),
            "Missing fuzzing harness file: " + path +
            ". The harness is documented in hmailserver\\docs\\Fuzzing.md.");

         return File.ReadAllText(path);
      }

      /// <summary>
      ///    Strips PowerShell comment lines so a test can assert on what a script
      ///    *does* without matching the prose that explains it. Only whole-line
      ///    comments are removed, which is enough for the scripts in fuzz\ and
      ///    avoids having to understand quoting.
      /// </summary>
      private static string StripPowerShellComments(string script)
      {
         var builder = new StringBuilder();

         foreach (string line in script.Split('\n'))
         {
            if (line.TrimStart().StartsWith("#"))
               continue;

            builder.AppendLine(line);
         }

         return builder.ToString();
      }

      private static IEnumerable<FileInfo> SeedSourceFiles()
      {
         string makeCorpus = StripPowerShellComments(ReadFuzzFile("make-corpus.ps1"));

         // The $sourceRoots array, as repo-relative single-quoted paths.
         var roots = Regex.Matches(makeCorpus, @"'(hmailserver\\[^']+)'")
                          .Cast<Match>()
                          .Select(match => match.Groups[1].Value)
                          .Distinct()
                          .ToList();

         Assert.IsNotEmpty(roots, "No seed source directories found in make-corpus.ps1.");

         var files = new List<FileInfo>();
         foreach (string relativeRoot in roots)
         {
            var directory = new DirectoryInfo(Path.Combine(RepositoryRoot, relativeRoot));
            Assert.IsTrue(directory.Exists,
               "make-corpus.ps1 reads seed messages from " + relativeRoot +
               ", which does not exist. Fix the list in make-corpus.ps1 - an empty seed corpus makes a fuzz run prove nothing.");

            files.AddRange(directory.GetFiles("*.*", SearchOption.AllDirectories)
                                    .Where(file => file.Extension.Equals(".eml", StringComparison.OrdinalIgnoreCase) ||
                                                   file.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)));
         }

         Assert.IsNotEmpty(files, "The seed source directories contain no .eml or .txt messages.");
         return files;
      }

      [Test]
      [Description("Every Server source the fuzz build compiles must still exist at the path build-fuzz.ps1 names.")]
      public void TestFuzzBuildSourcesExist()
      {
         string buildScript = StripPowerShellComments(ReadFuzzFile("build-fuzz.ps1"));

         var sources = Regex.Matches(buildScript, @"'((?:hmailserver|fuzz)\\[^']+\.cpp)'")
                            .Cast<Match>()
                            .Select(match => match.Groups[1].Value)
                            .Distinct()
                            .ToList();

         // Sanity-check the parse itself: if the array format in the script
         // changes, this test must fail rather than silently assert nothing.
         Assert.GreaterOrEqual(sources.Count, 12,
            "Expected at least 12 translation units in build-fuzz.ps1 (10 Server sources + the harness), found " +
            sources.Count + ". The source list format probably changed and this test is no longer reading it.");

         Assert.IsTrue(sources.Any(source => source.EndsWith(@"Common\Mime\Mime.cpp", StringComparison.OrdinalIgnoreCase)),
            "build-fuzz.ps1 no longer compiles Common\\Mime\\Mime.cpp - the fuzz targets would not be exercising the MIME parser at all.");

         var missing = sources.Where(source => !File.Exists(Path.Combine(RepositoryRoot, source))).ToList();

         Assert.IsEmpty(missing,
            "build-fuzz.ps1 compiles files that do not exist: " + string.Join(", ", missing) +
            ". Update the source list at the top of build-fuzz.ps1.");
      }

      [Test]
      [Description("Every harness and shim file build-fuzz.ps1 depends on must exist.")]
      public void TestFuzzHarnessFilesExist()
      {
         string[] required =
         {
            @"harness\shim\stdafx.h",
            @"harness\fuzz_mime_common.h",
            @"harness\fuzz_mime_common.cpp",
            @"harness\fuzz_environment.cpp",
            @"harness\mime_message_fuzzer.cpp",
            @"harness\mime_header_fuzzer.cpp",
            @"harness\mime_decode_fuzzer.cpp",
            @"dict\mime.dict",
            @"build-fuzz.ps1",
            @"make-corpus.ps1",
            @"run-fuzz.ps1",
            @"Find-ClangCl.ps1",
            @".gitattributes",
            @"README.md"
         };

         var missing = required.Where(relative => !File.Exists(Path.Combine(FuzzRoot, relative))).ToList();

         Assert.IsEmpty(missing, "Missing from fuzz\\: " + string.Join(", ", missing));
      }

      [Test]
      [Description("The seed messages must be pure CRLF: LF-only seeds never reach the multipart code and would break the DKIM-signed resources.")]
      public void TestSeedMessagesUseCrlfLineEndings()
      {
         var offenders = new List<string>();
         int inspected = 0;

         foreach (FileInfo file in SeedSourceFiles())
         {
            byte[] bytes = File.ReadAllBytes(file.FullName);
            inspected++;

            bool hasCrLf = false;
            bool hasBareLf = false;

            for (int index = 0; index < bytes.Length; index++)
            {
               if (bytes[index] != '\n')
                  continue;

               if (index > 0 && bytes[index - 1] == '\r')
                  hasCrLf = true;
               else
                  hasBareLf = true;
            }

            if (hasBareLf || !hasCrLf)
               offenders.Add(file.FullName + (hasBareLf ? " (bare LF)" : " (no CRLF at all)"));
         }

         Assert.Greater(inspected, 40, "Expected the seed corpus sources to hold dozens of messages, found " + inspected + ".");

         Assert.IsEmpty(offenders,
            "Seed messages with damaged line endings: " + string.Join(", ", offenders) +
            ". The root .gitattributes forces CRLF on checkout for exactly this reason; a normalised message is a message the MIME parser reads differently, and two of these files are DKIM-signed.");
      }

      [Test]
      [Description("The corpus generator must only use byte-level file APIs - a text API would rewrite the seeds' line endings.")]
      public void TestCorpusGeneratorUsesByteApisOnly()
      {
         string makeCorpus = StripPowerShellComments(ReadFuzzFile("make-corpus.ps1"));

         // Every one of these is encoding- and newline-aware. Using any of them
         // on a seed message would silently rewrite CRLF, add a BOM, or append a
         // trailing newline - and the resulting corpus would look completely
         // normal.
         string[] forbidden = { "Set-Content", "Out-File", "Add-Content", "Get-Content" };

         var found = forbidden.Where(cmdlet => makeCorpus.IndexOf(cmdlet, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

         Assert.IsEmpty(found,
            "make-corpus.ps1 uses text-mode cmdlet(s) " + string.Join(", ", found) +
            ". Seed messages must be copied with [System.IO.File]::ReadAllBytes / WriteAllBytes only.");

         Assert.IsTrue(makeCorpus.Contains("ReadAllBytes") && makeCorpus.Contains("WriteAllBytes"),
            "make-corpus.ps1 no longer uses the byte-level file APIs at all - it is not copying the seed messages faithfully.");
      }

      [Test]
      [Description("fuzz\\.gitattributes must keep corpus and reproducer bytes out of git's line-ending normalisation.")]
      public void TestFuzzInputsAreMarkedBinary()
      {
         string attributes = ReadFuzzFile(".gitattributes");

         foreach (string subtree in new[] { "regression/**", "corpus/**", "artifacts/**" })
         {
            string pattern = @"^\s*" + Regex.Escape(subtree) + @"\s+.*-text";
            Assert.IsTrue(Regex.IsMatch(attributes, pattern, RegexOptions.Multiline),
               "fuzz\\.gitattributes does not mark " + subtree +
               " as -text. The root .gitattributes normalises everything, and a crash reproducer whose line endings changed stops reproducing while still looking like a reproducer.");
         }
      }

      [Test]
      [Description("The MIME dictionary must be valid libFuzzer syntax; one bad line makes every target exit at startup.")]
      public void TestDictionaryIsValidLibFuzzerSyntax()
      {
         string dictionary = ReadFuzzFile(@"dict\mime.dict");

         // libFuzzer's format: blank lines, "#" comments, and entries that are
         // either "value" or name="value". Inside the quotes only \\, \" and
         // \xNN are escapes.
         var entryPattern = new Regex(@"^(?:[A-Za-z0-9_]+\s*=\s*)?""(?<value>.*)""\s*$");
         var escapePattern = new Regex(@"\\(?:\\|""|x[0-9a-fA-F]{2})");

         int entries = 0;
         int lineNumber = 0;

         foreach (string rawLine in dictionary.Split('\n'))
         {
            lineNumber++;
            string line = rawLine.Trim('\r', ' ', '\t');

            if (line.Length == 0 || line.StartsWith("#"))
               continue;

            Match match = entryPattern.Match(line);
            Assert.IsTrue(match.Success,
               "dict\\mime.dict line " + lineNumber + " is not a valid libFuzzer dictionary entry: " + line);

            string value = match.Groups["value"].Value;

            // Reject a stray backslash that is not one of the three valid
            // escapes. libFuzzer treats it as an error and refuses to start.
            string withoutEscapes = escapePattern.Replace(value, string.Empty);
            Assert.IsFalse(withoutEscapes.Contains("\\"),
               "dict\\mime.dict line " + lineNumber + " contains an invalid escape (only \\\\, \\\" and \\xNN are allowed): " + line);

            Assert.IsFalse(withoutEscapes.Contains("\""),
               "dict\\mime.dict line " + lineNumber + " contains an unescaped quote inside the value: " + line);

            Assert.Greater(value.Length, 0, "dict\\mime.dict line " + lineNumber + " is an empty token, which libFuzzer rejects.");
            entries++;
         }

         Assert.Greater(entries, 50,
            "Expected the MIME dictionary to hold more than 50 tokens, found " + entries +
            ". Without header names, media types, encodings and real boundary strings the mutator does not reach the decoders.");
      }

      [Test]
      [Description("Replays the seed corpus through the built fuzz targets under AddressSanitizer. Skipped when the targets are not built.")]
      public void TestBuiltTargetsSurviveTheSeedCorpus()
      {
         string binDirectory = Path.Combine(FuzzRoot, "bin");
         string seedsRoot = Path.Combine(FuzzRoot, @"corpus\seeds");

         var targets = new Dictionary<string, string>
         {
            { "mime_message_fuzzer", "message" },
            { "mime_header_fuzzer", "header" },
            { "mime_decode_fuzzer", "decode" }
         };

         var available = targets.Where(target => File.Exists(Path.Combine(binDirectory, target.Key + ".exe")) &&
                                                 Directory.Exists(Path.Combine(seedsRoot, target.Value)))
                                .ToList();

         if (available.Count == 0)
         {
            Assert.Ignore("Fuzz targets are not built. Run fuzz\\build-fuzz.ps1 (needs the optional \"C++ Clang tools for Windows\" component). " +
                          "This test replays the seed corpus through the real MIME parser under AddressSanitizer when they are present.");
         }

         foreach (var target in available)
         {
            string executable = Path.Combine(binDirectory, target.Key + ".exe");
            string seeds = Path.Combine(seedsRoot, target.Value);

            // libFuzzer's first positional argument is the corpus it may write
            // to, so it gets a scratch directory; the seeds are passed after it
            // and are only read. -runs=0 executes every corpus file once and
            // exits, which is the documented way to use a fuzz target as a
            // regression test.
            string scratch = Path.Combine(Path.GetTempPath(), "hm-fuzz-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);

            try
            {
               string arguments = string.Format(
                  "-runs=0 -error_exitcode=77 -timeout=25 -rss_limit_mb=2048 \"{0}\" \"{1}\"", scratch, seeds);

               int exitCode;
               string output = RunProcess(executable, arguments, TimeSpan.FromMinutes(3), out exitCode);

               Assert.AreEqual(0, exitCode,
                  target.Key + " did not survive replaying its seed corpus (exit code " + exitCode +
                  "). Exit code 77 means AddressSanitizer or libFuzzer reported a finding on a message that is already in the repository, which is a parser bug and not a harness problem. Output:" +
                  Environment.NewLine + output);
            }
            finally
            {
               try { Directory.Delete(scratch, true); } catch (IOException) { }
            }
         }
      }

      /// <summary>
      ///    Runs a process, collecting both streams asynchronously. Reading the
      ///    pipes synchronously deadlocks as soon as one of them fills while the
      ///    caller is blocked on the other - libFuzzer writes everything to
      ///    stderr and is perfectly capable of filling it.
      /// </summary>
      private static string RunProcess(string executable, string arguments, TimeSpan timeout, out int exitCode)
      {
         var output = new StringBuilder();

         var startInfo = new ProcessStartInfo(executable, arguments)
         {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)
         };

         using (var process = new Process())
         {
            process.StartInfo = startInfo;
            process.OutputDataReceived += (sender, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (sender, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit((int) timeout.TotalMilliseconds))
            {
               try { process.Kill(); } catch (InvalidOperationException) { }

               lock (output)
                  output.AppendLine("[timed out after " + timeout.TotalSeconds + "s and was killed]");

               exitCode = -1;
            }
            else
            {
               // Second, parameterless wait: it is what flushes the async
               // readers, and without it the tail of the output is missing from
               // the failure message exactly when it matters.
               process.WaitForExit();
               exitCode = process.ExitCode;
            }
         }

         lock (output)
            return output.ToString();
      }
   }
}
