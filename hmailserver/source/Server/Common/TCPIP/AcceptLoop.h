// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   // The two halves of stopping a thread that is blocked in accept() on a
   // listening socket of its own - the shape of the metrics, ManageSieve and
   // ACME challenge listeners, which are plain blocking accept loops on a
   // std::thread rather than Boost.Asio acceptors.
   //
   // Winsock and POSIX disagree about what wakes such a thread. On Windows,
   // closesocket() on the listening socket makes the blocked accept() return
   // with an error, so closing it is how the loop has always been told to
   // stop. On Linux, close() does no such thing: the descriptor leaves the
   // table, but the thread inside accept() holds its own reference to the
   // socket and stays there until a connection arrives - which on a listener
   // nobody is scraping is never. Measured on 13 September 2026 on the Linux
   // bench: MetricsServer::Stop() sat in join() behind a worker parked in
   // __libc_accept for as long as the process was left, and every later
   // reinitialise queued on the application lock behind it. What does wake the
   // thread on Linux is shutdown(SHUT_RDWR) on the listening socket: accept()
   // returns EINVAL at once. The descriptor is then closed after the join and
   // never before it, because a descriptor closed under a thread that has not
   // yet re-entered accept() can be handed out again by the next socket() or
   // open() anywhere in the process, and the loop would wake up accepting on
   // somebody else's socket.
   //
   // So: Interrupt() before the join, Release() after it, on both platforms,
   // and each does what its platform needs.
   namespace AcceptLoop
   {
      // Makes a blocking accept() on the socket return, from another thread.
      // Leaves the descriptor open on POSIX; closes it on Windows, where the
      // close is the wake-up.
      inline void Interrupt(SOCKET &listenSocket)
      {
         if (listenSocket == INVALID_SOCKET)
            return;

#ifdef HM_PLATFORM_POSIX
         shutdown(listenSocket, SHUT_RDWR);
#else
         closesocket(listenSocket);
         listenSocket = INVALID_SOCKET;
#endif
      }

      // Gives the descriptor back, once the loop's thread has been joined.
      inline void Release(SOCKET &listenSocket)
      {
         if (listenSocket == INVALID_SOCKET)
            return;

         closesocket(listenSocket);
         listenSocket = INVALID_SOCKET;
      }
   }
}
