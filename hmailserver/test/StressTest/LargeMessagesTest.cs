// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd and the hMailServer contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using NUnit.Framework;
using RegressionTests.Shared;

namespace StressTest
{
   [TestFixture]
   public class LargeMessagesTest : TestFixtureBase
   {
      [SetUp]
      public new void SetUp()
      {
         SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "test@example.test", "test");
      }

      [Test]
      public void SendLargeMessage()
      {
         _settings.MaxMessageSize = 0;

         // create a 200mb attachment
         var largeFile = Paths.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

         try
         {

            var chunk = new StringBuilder();
            for (int c = 0; c < 350000; c++)
               chunk.Append("012345678901234567890123456789012345678901234567890123456789");
            string chunkText = chunk.ToString();

            for (int i = 0; i < 10; i++)
               File.AppendAllText(largeFile, chunkText);

            using (var mail = new MailMessage())
            {
               mail.From = new MailAddress("test@example.test");
               mail.To.Add("test@example.test");
               mail.Subject = "Automatic server test";
               mail.Body = "Automatic server test";
               mail.BodyEncoding = Encoding.GetEncoding(1252);
               mail.SubjectEncoding = Encoding.GetEncoding(1252);
               mail.Attachments.Add(new Attachment(largeFile));

               using (var oClient = new SmtpClient("localhost", 25))
               {
                  oClient.Send(mail);
               }
            }

         }
         finally
         {
            if (File.Exists(largeFile))
               File.Delete(largeFile);
         }

      }

   }
}
