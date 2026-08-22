// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using NUnit.Framework;
using RegressionTests.Infrastructure;
using RegressionTests.Shared;

namespace RegressionTests.SMTP
{
   /// <summary>
   ///    A message the server could not write to disk must not be acknowledged.
   ///
   ///    TransparentTransmissionBuffer::SaveToFile_ used to read
   ///
   ///        bool bResult = file_.Write(pBuffer, noOfBytesWritten);
   ///
   ///    without ever looking at bResult, and then `return true` unconditionally. So a
   ///    spool write that failed - a full disk, a quota, an I/O error - was invisible
   ///    twice: dropped where it happened, and reported to every caller as success. MSVC
   ///    does not warn about the unused variable at /W3, C4189 being a /W4 warning.
   ///
   ///    Which of the two failure shapes actually lost mail is the point of these tests,
   ///    and it is not the obvious one. Measured against the pre-fix build:
   ///
   ///      * If *no* write succeeds the spool file is left empty, and that was already
   ///        refused with 451 - not by any check that meant to catch it, but because the
   ///        accept path fails downstream on a message with no content. Nothing was
   ///        reported to the error log, so an operator got a refusal with no reason, but
   ///        no mail was lost. The fix's contribution here is diagnosis: HM5862.
   ///
   ///      * If the first write succeeds and a later one fails - which is exactly what a
   ///        disk filling up mid-message does - the spool file is left non-empty and
   ///        truncated. It has content, it has headers, its size is non-zero, so it
   ///        passes every downstream guard: the message was accepted with **250** and
   ///        delivered short. The sender's copy is gone the moment it is told 250. That
   ///        is the defect, and TruncatedSpoolFileIsRefusedRatherThanDeliveredShort is
   ///        the test that fails against the unfixed build.
   ///
   ///    Provoking either shape means [Settings] SimulateSpoolWriteFailure (1 = fail
   ///    every write, 2 = fail after the first), because the alternative is filling a
   ///    real disk. CrashSimulationMode is the existing precedent for such a switch.
   ///
   ///    451 rather than 554 is deliberate: a disk that is full now may not be in ten
   ///    minutes, and a permanent rejection makes the sending server bounce mail that a
   ///    retry would have delivered.
   /// </summary>
   [TestFixture]
   public class SpoolWriteFailure : TestFixtureBase
   {
      // Comfortably more than one flush: TransparentTransmissionBuffer::GetRequiresFlush
      // flushes every 40000 bytes, so a body this size guarantees several writes and
      // therefore that mode 2 has a successful first write to let through.
      private const int BodyBytes = 200000;

      [TearDown]
      public void StopSimulatingTheFailure()
      {
         try
         {
            IniFileSetting.Write("SimulateSpoolWriteFailure", "0");
            _application.Reinitialize();
         }
         finally
         {
            // HM5862 is the point of these tests, so it is cleared here rather than left
            // to fail whichever fixture runs next.
            LogHandler.DeleteErrorLog();
         }
      }

      private void SimulateFailure(int mode)
      {
         IniFileSetting.Write("SimulateSpoolWriteFailure", mode.ToString());

         // The setting is cached by IniFileSettings::InitInstance; Reinitialize is what
         // re-reads it. Stop()/Start() does not.
         _application.Reinitialize();
      }

      private static string LargeBody()
      {
         // Deliberately not a repeated single character: the body is written through the
         // dot-unstuffing path, and a body of one repeated byte would hide an off-by-one
         // there. Lines of readable text, so a truncated delivery is obvious in a log.
         var builder = new StringBuilder(BodyBytes + 128);
         int line = 0;

         while (builder.Length < BodyBytes)
            builder.Append("Line ").Append(line++).Append(" of a message that must not be delivered truncated.\r\n");

         return builder.ToString();
      }

      [Test]
      [Description("A spool file truncated by a mid-message write failure is refused, not delivered short")]
      public void TruncatedSpoolFileIsRefusedRatherThanDeliveredShort()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "spooltrunc@example.test", "test");

         // Mode 2: the first write lands, the rest fail. The file on disk is non-empty
         // and short - the one shape the old code accepted.
         SimulateFailure(2);

         var simulator = new SmtpClientSimulator();

         CustomAsserts.Throws<DeliveryFailedException>(() =>
            simulator.Send("test@example.test", account.Address, "Must not be accepted", LargeBody()));

         CustomAsserts.AssertReportedError("HM5862");

         // The half that matters. Against the unfixed build this is 1, and the message in
         // it is shorter than the one that was sent.
         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 0);
      }

      [Test]
      [Description("A spool file that received nothing at all is refused, and now says why")]
      public void EmptySpoolFileIsRefusedAndReported()
      {
         var account = SingletonProvider<TestSetup>.Instance.AddAccount(_domain, "spoolfail@example.test", "test");

         SimulateFailure(1);

         var simulator = new SmtpClientSimulator();

         CustomAsserts.Throws<DeliveryFailedException>(() =>
            simulator.Send("test@example.test", account.Address, "Must not be accepted", LargeBody()));

         // The refusal itself is not new - see the class comment, the empty case already
         // failed downstream. HM5862 is: the old code refused the message without
         // recording any reason anywhere, which is a support call with nothing to go on.
         CustomAsserts.AssertReportedError("HM5862");

         Pop3ClientSimulator.AssertMessageCount(account.Address, "test", 0);
      }
   }
}
