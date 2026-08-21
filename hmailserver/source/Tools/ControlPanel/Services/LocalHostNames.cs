using System;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>
   /// Which host names mean "this machine".
   ///
   /// This sits on its own, with no dependencies at all, because two very
   /// different pieces of code have to agree on the answer and neither can be
   /// allowed to form its own opinion.
   ///
   /// <see cref="ServerSession"/> uses it to choose between a local and a remote
   /// COM activation, and to decide whether a local file or service can be
   /// inspected on the administrator's behalf. <see cref="HostReachability"/>
   /// uses it to decide whether to probe the network before connecting. A
   /// disagreement between those two is not a cosmetic one: a name this says is
   /// remote and the session says is local would be probed on port 135, fail, and
   /// refuse a connection that would have worked; a name this says is local and
   /// the session says is remote skips the probe and reinstates the frozen window
   /// the probe exists to prevent.
   ///
   /// It deliberately does not resolve anything. A DNS lookup here would run on
   /// the UI thread, which is the problem this whole area is trying to get away
   /// from, and matching the leading label is enough to recognise
   /// "mail.example.com" as this machine.
   /// </summary>
   public static class LocalHostNames
   {
      /// <summary>
      /// True when <paramref name="host"/> names the machine this is running on.
      /// An empty or missing name counts as local, because that is what an empty
      /// host box means.
      /// </summary>
      public static bool IsLocal(string host)
      {
         string value = (host ?? "").Trim();

         if (value.Length == 0 ||
             string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase) ||
             value == "127.0.0.1" ||
             value == "::1" ||
             value == "[::1]")
         {
            return true;
         }

         // This machine's own name is this machine. /connect takes a host name, and
         // typing the server's name while sitting at the server is an ordinary thing
         // to do - it is what an administrator with one saved shortcut does. Treating
         // it as remote made every local check say "connected to another host, so
         // this cannot be read from here" while looking straight at the files it was
         // declining to read.
         try
         {
            if (string.Equals(value, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
               return true;

            // The fully-qualified form of the same name, without a DNS lookup: a
            // resolve here would block the UI thread, and the leading label is
            // enough to recognise "mail.example.com" as this machine.
            int dot = value.IndexOf('.');
            if (dot > 0 && string.Equals(value.Substring(0, dot), Environment.MachineName, StringComparison.OrdinalIgnoreCase))
               return true;
         }
         catch (InvalidOperationException)
         {
            // MachineName can throw if the name is not set; fall through to false,
            // which is the safe direction - a check that declines to run is better
            // than one that describes the wrong machine.
         }

         return false;
      }
   }
}
