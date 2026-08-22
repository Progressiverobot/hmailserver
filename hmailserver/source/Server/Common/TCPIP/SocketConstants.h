// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{

   enum SessionType
   {
      STUnknown = 0,
      STSMTP = 1,
      STSMTPClient = 2,
      STPOP3 = 3,
      STPOP3Client = 4,
      STIMAP = 5,
      STListening = 6,
   };

   enum ConnectionSecurity
   {
      CSNone     = 0,
      CSSSL      = 1,
      CSSTARTTLSOptional = 2,
      CSSTARTTLSRequired = 3

   };

   // Per-port policy for TLS client certificates (mutual TLS) on INBOUND sessions.
   //
   // This is deliberately a per-port setting and not a global one: the population
   // connecting to a partner-relay SMTP port and the population connecting to the
   // public IMAP port are different populations with different certificate
   // authorities, and a single global switch would force the strictest policy onto
   // every listener at once - which for "require" means locking every ordinary
   // mail client out of the server the moment one relay port needs mutual TLS.
   //
   // The numeric values are persisted in hm_tcpipports.portclientcertificatepolicy
   // and exposed through the COM API, so they must never be renumbered. 0 must
   // remain "off" so that every database upgraded from an earlier schema keeps
   // exactly the behaviour it had.
   enum ClientCertificatePolicy
   {
      // Never request a certificate. The behaviour every port had before this
      // setting existed, and the default.
      CCPOff = 0,

      // Ask the client for a certificate, verify one if it is offered, and log
      // the outcome - but never fail the handshake over it. Exists so an
      // administrator can inventory which clients could survive "require"
      // before actually enforcing it.
      CCPRequest = 1,

      // The handshake fails unless the client presents a certificate that
      // chains to the port's configured CA bundle.
      CCPRequire = 2
   };

   enum ConnectionState
   {
      StatePendingConnect = 0,
      StateConnected = 1,
      StatePendingDisconnect = 2,
      StateDisconnected = 3
   };

   enum SslTlsVersion
   {
      TlsVersion10 = 2,
      TlsVersion11 = 4,
      TlsVersion12 = 8,
      TlsVersion13 = 16
   };

   enum TlsOption
   {
      TlsOptionPreferServerCiphers = 2,
      TlsOptionPrioritizeChaCha = 4
   };
}