using System.Collections.Generic;
using System.Net;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// Matching a configured port row against the machine's open ports.
   ///
   /// Both ways of getting this wrong cost something real. Reporting a live port as
   /// down sends an administrator hunting for a conflict that does not exist;
   /// reporting a dead port as up hides the single commonest cause of "mail stopped
   /// arriving and nothing looks wrong". The rules are small and entirely
   /// mechanical, which is exactly why they are pinned here rather than trusted.
   /// </summary>
   public class ListenerProbeTests
   {
      private static IReadOnlyList<IPEndPoint> Listening(params string[] endpoints)
      {
         var list = new List<IPEndPoint>();
         foreach (string endpoint in endpoints)
         {
            int colon = endpoint.LastIndexOf(':');
            list.Add(new IPEndPoint(IPAddress.Parse(endpoint.Substring(0, colon)),
               int.Parse(endpoint.Substring(colon + 1))));
         }
         return list;
      }

      // ---- the ordinary cases ----------------------------------------------

      [Fact]
      public void IsBound_ExactAddressAndPort()
      {
         Assert.True(ListenerProbe.IsBound("192.168.1.10", 25, Listening("192.168.1.10:25")));
      }

      [Fact]
      public void IsBound_WrongPortIsNotAMatch()
      {
         Assert.False(ListenerProbe.IsBound("192.168.1.10", 25, Listening("192.168.1.10:587")));
      }

      [Fact]
      public void IsBound_WrongAddressIsNotAMatch()
      {
         Assert.False(ListenerProbe.IsBound("192.168.1.10", 25, Listening("192.168.1.11:25")));
      }

      /// <summary>
      /// hMailServer stores a wildcard as "0.0.0.0" - IPAddress default-constructs
      /// to IPv4 any and TCPIPPort::GetAddressString returns its ToString - so this
      /// is the shape of nearly every real row.
      ///
      /// A wildcard row is matched by a WILDCARD listener, not by any listener on
      /// the port. Windows reports a successful wildcard bind literally as 0.0.0.0
      /// or [::], so the narrow test is the accurate one; the loose form could read
      /// green when hMailServer's own wildcard listener was down and some other
      /// program happened to hold that port on one specific address.
      /// </summary>
      [Theory]
      [InlineData("0.0.0.0")]
      [InlineData("")]
      [InlineData("   ")]
      [InlineData(null)]
      [InlineData("::")]
      [InlineData("[::]")]
      public void IsBound_AWildcardRowNeedsAWildcardListener(string configured)
      {
         Assert.True(ListenerProbe.IsBound(configured, 143, Listening("0.0.0.0:143")));
         Assert.True(ListenerProbe.IsBound(configured, 143, Listening(":::143")));

         // A listener on one specific address does not serve the wildcard row.
         Assert.False(ListenerProbe.IsBound(configured, 143, Listening("192.168.1.10:143")));

         // Right address shape, wrong port.
         Assert.False(ListenerProbe.IsBound(configured, 143, Listening("0.0.0.0:993")));
      }

      /// <summary>
      /// The other direction, and the one that would otherwise produce a false
      /// alarm on every properly configured server: a listener on the wildcard is
      /// serving the named address too.
      /// </summary>
      [Fact]
      public void IsBound_AWildcardListenerCoversANamedAddress()
      {
         Assert.True(ListenerProbe.IsBound("192.168.1.10", 25, Listening("0.0.0.0:25")));
         Assert.True(ListenerProbe.IsBound("fe80::1", 25, Listening(":::25")));
      }

      /// <summary>
      /// Families are kept apart on purpose. A socket bound to :: covers IPv4 when
      /// dual-stack is on and does not when it is off, and nothing here can tell
      /// which - so it claims neither. The cost is a v4 row reported as down on a
      /// dual-stack-only server; the alternative is reporting a port as up when
      /// nothing answers on it, which is the worse error to make here.
      /// </summary>
      [Fact]
      public void IsBound_DoesNotAssumeDualStack()
      {
         Assert.False(ListenerProbe.IsBound("192.168.1.10", 25, Listening(":::25")));
         Assert.False(ListenerProbe.IsBound("fe80::1", 25, Listening("0.0.0.0:25")));
      }

      // ---- the degenerate cases --------------------------------------------

      [Fact]
      public void IsBound_NoListenersIsNotAMatch()
      {
         Assert.False(ListenerProbe.IsBound("0.0.0.0", 25, Listening()));
         Assert.False(ListenerProbe.IsBound("0.0.0.0", 25, null));
      }

      /// <summary>
      /// A row whose address does not parse cannot be matched against anything.
      /// Saying "not listening" would be a claim about a configuration that is
      /// already broken in a different way, and the port dialog is where that gets
      /// fixed.
      /// </summary>
      [Fact]
      public void IsBound_AnUnparseableAddressMatchesNothing()
      {
         Assert.False(ListenerProbe.IsBound("mail.example.com", 25, Listening("0.0.0.0:25")));
      }

      [Fact]
      public void IsBound_APortOfZeroMatchesNothing()
      {
         Assert.False(ListenerProbe.IsBound("0.0.0.0", 0, Listening("0.0.0.0:0")));
      }

      // ---- the state an administrator actually reads -------------------------

      [Fact]
      public void StateOf_BoundIsListening()
      {
         Assert.Equal(ListenerState.Listening,
            ListenerProbe.StateOf("0.0.0.0", 25, true, Listening("0.0.0.0:25"), localServer: true));
      }

      /// <summary>
      /// A disabled protocol explains an unbound port completely, so it must be
      /// reported as that and not as a fault - otherwise every server that has
      /// deliberately switched POP3 off shows a warning for it forever.
      /// </summary>
      [Fact]
      public void StateOf_ADisabledProtocolIsNotAFault()
      {
         Assert.Equal(ListenerState.ProtocolDisabled,
            ListenerProbe.StateOf("0.0.0.0", 110, false, Listening(), localServer: true));
      }

      /// <summary>
      /// The case the column exists for: enabled, configured, and nothing is
      /// listening.
      /// </summary>
      [Fact]
      public void StateOf_EnabledAndUnboundIsAWarning()
      {
         Assert.Equal(ListenerState.NotListening,
            ListenerProbe.StateOf("0.0.0.0", 25, true, Listening("0.0.0.0:587"), localServer: true));

         Assert.Equal(StatusLevel.Warning, ListenerProbe.LevelFor(ListenerState.NotListening));
      }

      /// <summary>
      /// A port that IS bound is reported as listening even with the protocol
      /// switch off or unreadable - because something really is listening there,
      /// and that is worth knowing. It is very likely another program.
      /// </summary>
      [Fact]
      public void StateOf_BoundWinsOverTheProtocolSwitch()
      {
         Assert.Equal(ListenerState.Listening,
            ListenerProbe.StateOf("0.0.0.0", 25, false, Listening("0.0.0.0:25"), localServer: true));
      }

      [Fact]
      public void StateOf_AnUnreadableSwitchIsUnknownRatherThanBroken()
      {
         Assert.Equal(ListenerState.Unknown,
            ListenerProbe.StateOf("0.0.0.0", 25, null, Listening(), localServer: true));
      }

      /// <summary>
      /// Connected to another host, this machine's open ports describe the wrong
      /// computer, so nothing is claimed at all.
      /// </summary>
      [Fact]
      public void StateOf_ARemoteSessionCannotTell()
      {
         Assert.Equal(ListenerState.Unknown,
            ListenerProbe.StateOf("0.0.0.0", 25, true, Listening("0.0.0.0:25"), localServer: false));
      }

      // ---- what is shown ----------------------------------------------------

      /// <summary>
      /// Every state carries a word, so none of them depends on colour alone.
      /// </summary>
      [Theory]
      [InlineData(ListenerState.Listening, "Listening")]
      [InlineData(ListenerState.NotListening, "Not listening")]
      [InlineData(ListenerState.ProtocolDisabled, "Server off")]
      [InlineData(ListenerState.Unknown, "Unknown")]
      public void WordFor_IsAlwaysPresent(ListenerState state, string expected)
      {
         Assert.Equal(expected, ListenerProbe.WordFor(state));
      }

      /// <summary>
      /// The explanation must name the protocol it is about, and must not be a
      /// stub: an administrator reading "Not listening" needs to be told what to
      /// look for, and this is the only place they are told.
      /// </summary>
      [Fact]
      public void ExplainFor_NamesTheProtocolAndSaysWhatToLookFor()
      {
         string down = ListenerProbe.ExplainFor(ListenerState.NotListening, "SMTP");
         Assert.Contains("SMTP", down);
         Assert.Contains("another program", down);

         string off = ListenerProbe.ExplainFor(ListenerState.ProtocolDisabled, "POP3");
         Assert.Contains("POP3", off);
         Assert.Contains("Protocols page", off);
      }

      /// <summary>
      /// "Listening" must not be read as "hMailServer is listening". This check
      /// reads the operating system's port table, which cannot tell one program's
      /// listener from another's, and the wording has to admit that - the case
      /// where it matters is a port held by a different mail server.
      /// </summary>
      [Fact]
      public void ExplainFor_DoesNotClaimTheListenerIsHMailServers()
      {
         string up = ListenerProbe.ExplainFor(ListenerState.Listening, "IMAP");
         Assert.Contains("cannot tell", up);
      }

      /// <summary>
      /// The live query has to survive a machine that will not answer it: it runs
      /// while the page is being built, and an exception there would take the page
      /// down rather than degrade to "unknown".
      /// </summary>
      [Fact]
      public void ActiveListeners_NeverThrows()
      {
         Assert.NotNull(ListenerProbe.ActiveListeners());
      }
   }
}
