// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Threading;

namespace StressTest
{
   /// <summary>
   /// Summary description for SMTPSimulator.
   /// </summary>
   public class SMTPSimulator
   {
      
      readonly ClientSocket m_oSocket;

      public SMTPSimulator()
      {
         m_oSocket = new ClientSocket();  
      }

      public bool TestConnect()
      {
            while (!m_oSocket.Connect(25)) 
                Console.WriteLine(System.DateTime.Now + " " + "Failed to connect to server");

         // Receive welcome message.
         string sData = m_oSocket.Receive();

            m_oSocket.Disconnect();

            return false;
      }
   }
}
