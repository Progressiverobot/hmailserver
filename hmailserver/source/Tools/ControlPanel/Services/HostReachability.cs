using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Whether a remote machine is worth attempting a DCOM activation against.
   ///
   /// WHY THIS EXISTS, and why the answer is not "just connect on a background
   /// thread". Connecting is three blocking calls - a ProgID lookup against the
   /// remote registry, a DCOM activation, and an Authenticate - and all three ran
   /// on the UI thread, behind a single 50ms yield whose only job was to let the
   /// disabled button repaint. When the host answers, that is imperceptible. When
   /// it does not, RPC takes its own time to decide, and for the whole of it the
   /// window does not redraw, does not move, and collects a "(Not Responding)" in
   /// its title bar. That is the worst thing this application does, and it does it
   /// at the first screen a new user ever sees.
   ///
   /// The obvious fix is wrong. Moving the activation to a worker thread orphans
   /// the object: an out-of-process COM proxy belongs to the apartment that
   /// created it, so a temporary STA thread that exits takes the session with it,
   /// and every later call from the UI thread is talking to a dead apartment. The
   /// correct version of that fix is a long-lived STA thread owning the session
   /// with every COM call marshalled onto it, which is a real change to how the
   /// whole application talks to the server and not something to smuggle in
   /// alongside a progress ring.
   ///
   /// So this attacks the wait instead of the thread. The freeze people actually
   /// hit is the unreachable host, and reachability is a question that CAN be
   /// asked off the UI thread, cheaply, with a timeout we choose rather than one
   /// RPC chooses for us. Ask it first; if the answer is no, report it in a couple
   /// of seconds with a sentence that says what to check. If the answer is yes,
   /// the activation that follows is the fast case - which was never the problem.
   ///
   /// This narrows the window rather than closing it: a host that accepts TCP on
   /// the endpoint mapper and then stalls inside DCOM will still freeze the UI.
   /// That is a far rarer shape than "wrong name typed into the box", and the
   /// honest fix for it is the STA-thread rework described above.
   /// </summary>
   public static class HostReachability
   {
      /// <summary>
      /// The RPC endpoint mapper. Every DCOM activation begins by asking this
      /// port where the class object lives, so a machine that will not accept a
      /// connection here cannot serve an activation either - which makes it a
      /// sound proxy for "is an activation worth starting", without needing any
      /// hMailServer-specific port to be open.
      /// </summary>
      private const int EndpointMapperPort = 135;

      /// <summary>
      /// Long enough for a busy machine on a slow link, short enough that a
      /// mistyped host name is a pause rather than a hang. RPC's own timeout for
      /// the same failure is tens of seconds.
      /// </summary>
      public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

      /// <summary>The outcome of the check, and what to tell the user about it.</summary>
      public sealed class Result
      {
         /// <summary>True when an activation is worth attempting.</summary>
         public bool Reachable { get; init; }

         /// <summary>
         /// A complete sentence naming what failed and what to check, or null
         /// when <see cref="Reachable"/> is true.
         /// </summary>
         public string Error { get; init; }

         public static Result Ok() => new() { Reachable = true };

         public static Result Fail(string error) => new() { Reachable = false, Error = error };
      }

      /// <summary>
      /// Checks that <paramref name="host"/> resolves and accepts a connection on
      /// the RPC endpoint mapper. A local host is always reachable and is never
      /// probed - loopback is the fast case, and a machine that cannot reach
      /// itself has problems this dialog cannot help with.
      /// </summary>
      public static async Task<Result> CheckAsync(string host, TimeSpan timeout,
         CancellationToken cancellation = default)
      {
         // The same rules ServerSession.Open uses to choose between a local and a
         // remote activation, from the one place that states them. A second
         // opinion here would eventually disagree, and both ways of disagreeing
         // cost something: probing loopback for no reason, or skipping the probe
         // for a genuinely remote host and reinstating the freeze this exists to
         // stop.
         if (LocalHostNames.IsLocal(host))
            return Result.Ok();

         IPAddress[] addresses;

         try
         {
            using var resolve = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            resolve.CancelAfter(timeout);

            addresses = await Dns.GetHostAddressesAsync(host, resolve.Token).ConfigureAwait(false);
         }
         catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
         {
            return Result.Fail(NotFound(host, timedOut: true));
         }
         catch (Exception)
         {
            return Result.Fail(NotFound(host, timedOut: false));
         }

         if (addresses == null || addresses.Length == 0)
            return Result.Fail(NotFound(host, timedOut: false));

         // Every address the name resolves to gets a turn: a host with both an
         // IPv6 and an IPv4 address is routinely reachable on only one of them,
         // and giving up on the first is how "the server is down" gets reported
         // about a server that is not.
         foreach (IPAddress address in addresses)
         {
            if (cancellation.IsCancellationRequested)
               break;

            if (await AcceptsConnectionAsync(address, timeout, cancellation).ConfigureAwait(false))
               return Result.Ok();
         }

         return Result.Fail(
            Quote(host) + " did not answer on port " + EndpointMapperPort + ", which Windows needs in order to reach " +
            "the hMailServer COM API remotely. Check that the machine is switched on, that its firewall allows DCOM, " +
            "and that you are connecting to the right name.");
      }

      private static string NotFound(string host, bool timedOut)
      {
         return timedOut
            ? "Timed out looking up " + Quote(host) + ". Check the name, and that this machine can reach a DNS server."
            : Quote(host) + " could not be found. Check the spelling, or enter the server's IP address instead.";
      }

      private static string Quote(string value)
      {
         return "'" + value + "'";
      }

      private static async Task<bool> AcceptsConnectionAsync(IPAddress address, TimeSpan timeout,
         CancellationToken cancellation)
      {
         using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

         try
         {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            attempt.CancelAfter(timeout);

            await socket.ConnectAsync(new IPEndPoint(address, EndpointMapperPort), attempt.Token).ConfigureAwait(false);
            return true;
         }
         catch (Exception)
         {
            // Refused, filtered, timed out or unroutable. The distinction does not
            // change the advice, and reporting it would be guessing at a cause.
            return false;
         }
      }
   }
}
