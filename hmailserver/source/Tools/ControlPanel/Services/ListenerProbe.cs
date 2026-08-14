using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace hMailServer.ControlPanel.Services
{
   /// <summary>What the machine says about one configured port.</summary>
   public enum ListenerState
   {
      /// <summary>Something is listening on that address and port.</summary>
      Listening,

      /// <summary>Nothing is listening, and the protocol is switched on - so it should be.</summary>
      NotListening,

      /// <summary>Nothing is listening because the protocol server is switched off.</summary>
      ProtocolDisabled,

      /// <summary>The question cannot be answered from here.</summary>
      Unknown
   }

   /// <summary>
   /// Whether the ports configured in hMailServer are actually being listened on.
   ///
   /// A TCP/IP port row is a request, not a state. TCPServer::InitAcceptor can fail
   /// to bind - another program already has the port, or the address does not exist
   /// on this machine - and when it does the server reports error 4316 to a log
   /// nobody has open and carries on running every other listener. The port page
   /// went on showing the row exactly as before. The commonest form of "mail stopped
   /// arriving and nothing looks wrong" is a port that never came up.
   ///
   /// TLS ports have two more ways to fail before the bind is even attempted, both
   /// in TCPServer::Run: an SSL context that will not initialise (a missing,
   /// unreadable or passphrase-protected certificate) and a client-certificate
   /// policy set to "require" on a port with no TLS. Neither starts the listener,
   /// and neither changes anything this page could previously show.
   ///
   /// The check is the operating system's own list of listening endpoints, so it
   /// answers "is this port open" and not "is it open because of hMailServer" - a
   /// port held by a different program looks the same from here. That distinction
   /// is stated where the result is shown rather than papered over, because the
   /// other program IS the usual cause when a configured port is not hMailServer's.
   /// </summary>
   public static class ListenerProbe
   {
      /// <summary>
      /// Every TCP endpoint this machine is listening on, or an empty list when the
      /// question cannot be answered. Never throws: it is called while a page is
      /// being built, and a machine with an unusual network stack must degrade to
      /// "cannot tell" rather than take the page down.
      /// </summary>
      public static IReadOnlyList<IPEndPoint> ActiveListeners()
      {
         try
         {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
         }
         catch (NetworkInformationException)
         {
            return Array.Empty<IPEndPoint>();
         }
         catch (PlatformNotSupportedException)
         {
            return Array.Empty<IPEndPoint>();
         }
      }

      /// <summary>
      /// True when one of <paramref name="listeners"/> covers the configured
      /// address and port.
      ///
      /// Pure, so the matching rules can be tested without a network stack. Two
      /// things make it more than string equality:
      ///
      ///  - hMailServer stores a wildcard as "0.0.0.0" (IPAddress default-
      ///    constructs to IPv4 any, and TCPIPPort::GetAddressString returns its
      ///    ToString), so a wildcard row must match a listener on any address.
      ///  - A listener on the wildcard covers every address of its own family, so
      ///    a row naming 192.168.1.10 is served by a listener on 0.0.0.0.
      ///
      /// Families are kept apart deliberately. A socket bound to :: covers IPv4 too
      /// when dual-stack is on and does not when it is off, and this cannot tell
      /// which - so it does not claim either. The cost is a v4 row reported as not
      /// listening on a dual-stack-only server; the alternative is reporting a port
      /// as up when nothing answers on it, which is the worse error here.
      /// </summary>
      public static bool IsBound(string configuredAddress, int port, IReadOnlyList<IPEndPoint> listeners)
      {
         if (listeners == null || port <= 0)
            return false;

         string address = (configuredAddress ?? "").Trim();

         bool wildcardRow = address.Length == 0
                            || address == "0.0.0.0"
                            || address == "::"
                            || address == "[::]";

         IPAddress configured = null;
         if (!wildcardRow && !IPAddress.TryParse(address, out configured))
         {
            // A row whose address does not parse cannot be matched to anything, and
            // saying "not listening" would be a claim about a configuration that is
            // already broken in a different way.
            return false;
         }

         foreach (IPEndPoint listener in listeners)
         {
            if (listener == null || listener.Port != port)
               continue;

            if (wildcardRow)
               return true;

            if (listener.Address.Equals(configured))
               return true;

            // The wildcard of the configured address's own family covers it.
            if (configured.AddressFamily == AddressFamily.InterNetwork &&
                listener.Address.Equals(IPAddress.Any))
            {
               return true;
            }

            if (configured.AddressFamily == AddressFamily.InterNetworkV6 &&
                listener.Address.Equals(IPAddress.IPv6Any))
            {
               return true;
            }
         }

         return false;
      }

      /// <summary>
      /// The state of one configured port, given whether its protocol server is
      /// switched on and whether the Control Panel is looking at this machine.
      /// </summary>
      /// <param name="protocolEnabled">
      /// ServiceSMTP / ServiceIMAP / ServicePOP3 for this row's protocol, or null
      /// when that could not be read - in which case a port that is not bound is
      /// reported as unknown rather than as broken.
      /// </param>
      public static ListenerState StateOf(string configuredAddress, int port, bool? protocolEnabled,
         IReadOnlyList<IPEndPoint> listeners, bool localServer)
      {
         if (!localServer)
            return ListenerState.Unknown;

         if (IsBound(configuredAddress, port, listeners))
            return ListenerState.Listening;

         // Order matters: a disabled protocol explains an unbound port completely,
         // and reporting it as a fault would send an administrator hunting for a
         // port conflict that does not exist.
         if (protocolEnabled == false)
            return ListenerState.ProtocolDisabled;

         if (protocolEnabled == null)
            return ListenerState.Unknown;

         return ListenerState.NotListening;
      }

      /// <summary>The word shown beside the badge. Always present, never colour alone.</summary>
      public static string WordFor(ListenerState state) => state switch
      {
         ListenerState.Listening => "Listening",
         ListenerState.NotListening => "Not listening",
         ListenerState.ProtocolDisabled => "Server off",
         _ => "Unknown"
      };

      public static StatusLevel LevelFor(ListenerState state) => state switch
      {
         ListenerState.Listening => StatusLevel.Good,
         ListenerState.NotListening => StatusLevel.Warning,
         ListenerState.ProtocolDisabled => StatusLevel.Information,
         _ => StatusLevel.Normal
      };

      /// <summary>
      /// The sentence under the list: what the state means and, where it is a
      /// fault, what to do about it.
      /// </summary>
      public static string ExplainFor(ListenerState state, string protocol) => state switch
      {
         ListenerState.Listening =>
            "Something on this machine is listening on that address and port. This check reads the operating "
            + "system's list of open ports, so it cannot tell hMailServer's listener from another program's - if "
            + "connections are being refused or answered by the wrong service, that is what to look for.",

         ListenerState.NotListening =>
            "The " + protocol + " server is switched on but nothing is listening here, so every connection to this "
            + "port is refused. The usual causes are another program already holding the port, an address that does "
            + "not exist on this machine, or - on a TLS port - a certificate the server could not load. The server "
            + "records the reason once, at start-up, in the error log.",

         ListenerState.ProtocolDisabled =>
            "The " + protocol + " server is switched off on the Protocols page, so this port is not listened on. "
            + "The row is kept; it starts working again when the protocol is enabled.",

         _ =>
            "Whether this port is being listened on could not be determined from here. Open the Control Panel on "
            + "the server itself to see it."
      };
   }
}
