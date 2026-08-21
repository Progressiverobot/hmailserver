using System;
using System.Diagnostics;
using System.Threading.Tasks;
using hMailServer.ControlPanel.Services;
using Xunit;

namespace hMailServer.ControlPanel.Tests.Services
{
   /// <summary>
   /// The sign-in screen must fail fast, and must never probe a local host.
   ///
   /// The defect these exist for is not subtle and is not rare: connecting ran
   /// three blocking COM calls on the UI thread behind a single 50ms yield, so a
   /// host that did not answer froze the window for as long as RPC felt like
   /// taking. The first screen anybody sees would stop redrawing and collect a
   /// "(Not Responding)" in its title bar.
   ///
   /// The two properties worth pinning are therefore the two that make the fix
   /// real rather than decorative: an unreachable host is rejected in seconds
   /// instead of tens of them, and a local host is not made slower to reach in
   /// the process. The second is the one a careless change breaks - it costs
   /// nothing to notice here and is very annoying to notice in person.
   /// </summary>
   public class HostReachabilityTests
   {
      /// <summary>
      /// Well past the real timeout and well under RPC's, so this fails on a
      /// genuine regression rather than on a slow build agent.
      /// </summary>
      private static readonly TimeSpan Generous = TimeSpan.FromSeconds(20);

      [Theory]
      [InlineData("localhost")]
      [InlineData("127.0.0.1")]
      [InlineData("::1")]
      [InlineData("[::1]")]
      [InlineData("")]
      public async Task A_local_host_is_reachable_without_touching_the_network(string host)
      {
         var clock = Stopwatch.StartNew();

         HostReachability.Result result = await HostReachability.CheckAsync(host, HostReachability.DefaultTimeout);

         clock.Stop();

         Assert.True(result.Reachable, "a local host must always be considered reachable");
         Assert.Null(result.Error);

         // A DNS lookup or a TCP probe cannot finish this fast. If this starts
         // failing, the local-host branch has stopped short-circuiting and every
         // loopback sign-in has quietly become slower.
         Assert.True(clock.ElapsedMilliseconds < 250,
            "a local host must short-circuit, but took " + clock.ElapsedMilliseconds + "ms");
      }

      [Fact]
      public async Task This_machine_by_name_is_local()
      {
         HostReachability.Result result =
            await HostReachability.CheckAsync(Environment.MachineName, HostReachability.DefaultTimeout);

         Assert.True(result.Reachable);
      }

      [Fact]
      public async Task A_name_that_does_not_resolve_fails_quickly_and_says_what_to_check()
      {
         // .invalid is reserved by RFC 2606 precisely so that it can never
         // resolve, which keeps this test from depending on somebody's DNS
         // wildcard or a captive portal.
         var clock = Stopwatch.StartNew();

         HostReachability.Result result = await HostReachability.CheckAsync(
            "no-such-host.invalid", HostReachability.DefaultTimeout);

         clock.Stop();

         Assert.False(result.Reachable);
         Assert.False(string.IsNullOrWhiteSpace(result.Error));

         // The error is read by somebody who has just mistyped a host name, so it
         // has to name the thing they typed.
         Assert.Contains("no-such-host.invalid", result.Error);

         Assert.True(clock.Elapsed < Generous,
            "an unresolvable host must fail fast, but took " + clock.Elapsed.TotalSeconds.ToString("0.0") + "s");
      }

      [Fact]
      public async Task An_address_that_cannot_be_reached_fails_within_its_timeout()
      {
         // 192.0.2.0/24 is TEST-NET-1 (RFC 5737): routable-looking, guaranteed
         // never to be a real host, so the connection attempt hangs rather than
         // being refused - which is exactly the shape that froze the UI.
         var timeout = TimeSpan.FromSeconds(2);
         var clock = Stopwatch.StartNew();

         HostReachability.Result result = await HostReachability.CheckAsync("192.0.2.1", timeout);

         clock.Stop();

         Assert.False(result.Reachable);
         Assert.False(string.IsNullOrWhiteSpace(result.Error));

         // The negative control for the whole feature. Remove the timeout and
         // this sits here for RPC's own patience, which is the bug.
         Assert.True(clock.Elapsed < Generous,
            "an unreachable address must respect the timeout, but took "
            + clock.Elapsed.TotalSeconds.ToString("0.0") + "s");
      }

      [Fact]
      public async Task The_unreachable_message_explains_the_firewall_case()
      {
         HostReachability.Result result =
            await HostReachability.CheckAsync("192.0.2.1", TimeSpan.FromSeconds(2));

         Assert.False(result.Reachable);

         // Remote DCOM being blocked by a firewall is the overwhelmingly common
         // cause, and it is invisible from the client - so the message has to
         // raise it unprompted or the reader has no way to guess.
         Assert.Contains("firewall", result.Error, StringComparison.OrdinalIgnoreCase);
      }
   }
}
