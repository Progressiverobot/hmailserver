using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The pure half of the service-account check: comparing the account
   /// hMailServer.INI asks for against the one the Service Control Manager
   /// reports, and pulling the executable back out of a registered command line.
   ///
   /// Both are worth pinning because both drive a claim shown to an administrator.
   /// A false mismatch tells somebody to re-run a registration they do not need; a
   /// false match hides the one they do; and a mis-parsed command line prints a
   /// /Register command that names a path which does not exist.
   /// </summary>
   public class WindowsServiceInfoTests
   {
      // ---- account comparison ----------------------------------------------

      [Theory]
      // The SCM's three spellings of the default account, which is what an empty
      // ServiceAccountName produces at registration time.
      [InlineData("LocalSystem", "localsystem")]
      [InlineData(".\\LocalSystem", "localsystem")]
      [InlineData("NT AUTHORITY\\System", "localsystem")]
      // A bare local account name is the same account the SCM writes as ".\name".
      [InlineData("hmail", ".\\hmail")]
      [InlineData(".\\hmail", ".\\hmail")]
      // Case is not part of a Windows account name.
      [InlineData("NT SERVICE\\hMailServer", "nt service\\hmailserver")]
      [InlineData("CONTOSO\\svc-mail", "contoso\\svc-mail")]
      // Nothing is not an account, and must never compare equal to anything.
      [InlineData("", "")]
      [InlineData("   ", "")]
      [InlineData(null, "")]
      public void Canonical_ReducesTheSpellingsOfOneAccountToOneForm(string input, string expected)
      {
         Assert.Equal(expected, WindowsServiceInfo.Canonical(input, "MAILBOX01"));
      }

      /// <summary>
      /// A local account qualified with this machine's own name is the same account
      /// the SCM reports as ".\name". Without this, an administrator who typed
      /// MAILBOX01\hmail would be told forever that the setting had not been
      /// applied, while the service ran as exactly that account.
      /// </summary>
      [Fact]
      public void Canonical_TreatsThisMachinesNameAsTheLocalAuthority()
      {
         Assert.Equal(".\\hmail", WindowsServiceInfo.Canonical("MAILBOX01\\hmail", "MAILBOX01"));
         Assert.Equal(".\\hmail", WindowsServiceInfo.Canonical("mailbox01\\hmail", "MAILBOX01"));

         // A different machine's name is a different account and stays qualified.
         Assert.Equal("mailbox02\\hmail", WindowsServiceInfo.Canonical("MAILBOX02\\hmail", "MAILBOX01"));
      }

      [Theory]
      [InlineData("NT SERVICE\\hMailServer", "NT SERVICE\\hMailServer", true)]
      [InlineData("nt service\\hmailserver", "NT SERVICE\\hMailServer", true)]
      [InlineData("hmail", ".\\hmail", true)]
      [InlineData("MAILBOX01\\hmail", ".\\hmail", true)]
      [InlineData("NT SERVICE\\hMailServer", "LocalSystem", false)]
      [InlineData("CONTOSO\\svc-mail", "MAILBOX02\\svc-mail", false)]
      public void SameAccount_MatchesOnIdentityRatherThanSpelling(string configured, string actual, bool expected)
      {
         Assert.Equal(expected, WindowsServiceInfo.SameAccount(configured, actual, "MAILBOX01"));
      }

      /// <summary>
      /// "Nothing is configured" must not read as "it matches". The card uses this
      /// to decide between reporting the running account and reporting a mismatch,
      /// and an empty request is neither.
      /// </summary>
      [Theory]
      [InlineData("", "LocalSystem")]
      [InlineData(null, "LocalSystem")]
      [InlineData("", "")]
      [InlineData("LocalSystem", "")]
      public void SameAccount_IsFalseWhenEitherSideSaysNothing(string configured, string actual)
      {
         Assert.False(WindowsServiceInfo.SameAccount(configured, actual, "MAILBOX01"));
      }

      // ---- describing the running account ----------------------------------

      /// <summary>
      /// The two cases where the SCM's own wording under-states what the account
      /// can do, plus the case where Windows told us nothing.
      /// </summary>
      [Fact]
      public void DescribeAccount_SaysWhatTheAccountMeans()
      {
         Assert.Contains("most privileged", WindowsServiceInfo.DescribeAccount("LocalSystem"));
         Assert.Contains("most privileged", WindowsServiceInfo.DescribeAccount("NT AUTHORITY\\System"));
         Assert.Contains("no password", WindowsServiceInfo.DescribeAccount("NT SERVICE\\hMailServer"));

         // Anything else is reported verbatim rather than characterised, because
         // there is nothing true to add about a domain account we know nothing of.
         Assert.Equal("CONTOSO\\svc-mail", WindowsServiceInfo.DescribeAccount("CONTOSO\\svc-mail"));

         Assert.Contains("did not report", WindowsServiceInfo.DescribeAccount(""));
         Assert.Contains("did not report", WindowsServiceInfo.DescribeAccount(null));
      }

      // ---- the registered command line -------------------------------------

      [Theory]
      // How hMailServer actually registers itself.
      [InlineData("\"C:\\Program Files\\hMailServer\\Bin\\hMailServer.exe\" RunAsService",
                  "C:\\Program Files\\hMailServer\\Bin\\hMailServer.exe")]
      // An unquoted path, which is what a path with no space can end up as.
      [InlineData("C:\\hMailServer\\hMailServer.exe RunAsService", "C:\\hMailServer\\hMailServer.exe")]
      // No arguments at all, quoted and not.
      [InlineData("\"C:\\hMailServer\\hMailServer.exe\"", "C:\\hMailServer\\hMailServer.exe")]
      [InlineData("C:\\hMailServer\\hMailServer.exe", "C:\\hMailServer\\hMailServer.exe")]
      public void ExecutableFrom_DropsTheArgumentsAndTheQuotes(string pathName, string expected)
      {
         Assert.Equal(expected, WindowsServiceInfo.ExecutableFrom(pathName));
      }

      /// <summary>
      /// When the SCM tells us nothing, the instruction still has to be runnable
      /// from the install directory rather than being blank or quoting an empty
      /// string.
      /// </summary>
      [Theory]
      [InlineData("")]
      [InlineData("   ")]
      [InlineData(null)]
      public void ExecutableFrom_FallsBackToTheBareExecutableName(string pathName)
      {
         Assert.Equal("hMailServer.exe", WindowsServiceInfo.ExecutableFrom(pathName));
      }

      // ---- querying ---------------------------------------------------------

      /// <summary>
      /// The query must never throw, whatever the machine says: it runs while a
      /// settings page is being built, and an exception there would take the page
      /// with it. A machine with no such service is the ordinary case on a build
      /// agent, and it has to come back as "does not exist", not as a failure.
      /// </summary>
      [Fact]
      public void Query_ForAServiceThatCannotExist_ReturnsNotExistsRatherThanThrowing()
      {
         WindowsServiceInfo info = WindowsServiceInfo.Query("hMailServerNoSuchService_ControlPanelTest");

         Assert.False(info.Exists);
         Assert.Equal("", info.StartName);
         Assert.Equal("", info.PathName);
      }

      /// <summary>
      /// A name carrying the characters that terminate a WQL string literal must
      /// not produce a malformed query - it must simply match nothing.
      /// </summary>
      [Fact]
      public void Query_EscapesTheServiceName()
      {
         WindowsServiceInfo info = WindowsServiceInfo.Query("no'such\\service");

         Assert.False(info.Exists);
      }
   }
}
