// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// http://www.hmailserver.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "StdAfx.h"
#include ".\fileutilities.h"

#include "FileInfo.h"
#include "File.h"
#include "ByteBuffer.h"
#include "GUIDCreator.h"
#include "../Application/Dictionary.h"
#include "../Util/Assert.h"
#include "../Util/Unicode.h"
#include "../Util/RegularExpression.h"

#include <boost/filesystem.hpp>
#include <climits>

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   const String FileUtilities::PathSeparator = _T("\\");

   FileUtilities::FileUtilities(void)
   {
   }

   FileUtilities::~FileUtilities(void)
   {
   }

   bool
   FileUtilities::DeleteFile(const String &FileName)
   {
      const int iMaxNumberOfTries = 5;

      for (int i = 1; i <= iMaxNumberOfTries; i++)
      {
         boost::system::error_code error_code;

         // Two defects lived in the four lines this replaces, and they compounded.
         //
         // 1. The name was narrowed to ANSI first and the path built from those
         //    bytes, so every character outside the active code page was replaced.
         //    The delete then named a path that does not exist, boost answered
         //    "did not exist", and the code below called that success. Non-ASCII
         //    reaches these paths through IDN domains and non-ASCII account names,
         //    where the message file is under a directory named after the domain.
         //    Every other function in this file passes the wide String straight to
         //    boost; this one did not.
         //
         // 2. boost::filesystem::remove returns false BOTH when the file was not
         //    there and when the delete failed - in the failure case it also sets
         //    error_code, which is the only way to tell them apart. The old code
         //    returned true on false without looking at error_code, so a delete
         //    that failed because another process held the file open reported
         //    success. That also made the retry loop and the HM5047 report below
         //    unreachable: DeleteFile could not return false for any input, so
         //    every caller that checks the result - PersistentMessage::DeleteFile,
         //    SieveStorage::DeleteScript, the external POP3 fetcher, the log and
         //    backup retention passes - was checking a constant.
         boost::filesystem::remove(boost::filesystem::path(std::wstring(FileName)), error_code);

         if (!error_code)
         {
            // Deleted, or was not there in the first place. Both satisfy a caller
            // whose intent is "this file must not exist".
            return true;
         }

         if (i == iMaxNumberOfTries)
         {
            // We still couldn't delete the file. Lets give up and report in windows event log.
            String sErrorMessage;
            sErrorMessage.Format(_T("Could not delete the file %s. Tried %d times without success."), FileName.c_str(), iMaxNumberOfTries);
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5047, "FileUtilities::DeleteFile", sErrorMessage, error_code);
            return false;
         }

         // Some other process must have locked the file.
         Sleep(1000);
      }

      return false;
   }

   bool
   FileUtilities::Copy(const String &sFrom, const String &sTo, bool bCreateMissingDirectories)
   {
      const int iMaxNumberOfTries = 5;

      if (bCreateMissingDirectories)
      {
         String sToPath = sTo.Mid(0, sTo.ReverseFind(_T("\\")));
         CreateDirectory(sToPath);
      }

      for (int i = 1; i <= iMaxNumberOfTries; i++)
      {
         boost::system::error_code error_code;
         boost::filesystem::copy_file(sFrom, sTo, boost::filesystem::copy_options::overwrite_existing, error_code);

         // Use classic api to copy the file
         if (!error_code)
         {
            //Copy OK
            return true;
         }

         // We failed to delete the file. 

         if (i == iMaxNumberOfTries)
         {
            // We still couldn't copy the file. Lets give up and report in windows event log and hMailServer application log
            String sErrorMessage;
            sErrorMessage.Format(_T("Could not copy the file %s to %s. Tried 5 times without success."), sFrom.c_str(), sTo.c_str());
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5048, "File::Copy", sErrorMessage, error_code);
            return false;
         }

         // Some other process must have locked the file.
         Sleep(1000);
      }

      throw std::logic_error("Copy file logic error.");
   }

   bool
   FileUtilities::Move(const String &sFrom, const String &sTo)
   {
      const int iMaxNumberOfTries = 5;

      // The overwrite is done by the rename itself, deliberately, and there is no
      // DeleteFile here any more.
      //
      // This used to delete the destination first and then retry the rename up to
      // five times. That opened a window in which the destination no longer existed
      // and the replacement had not yet arrived - and if all five attempts failed
      // (a scanner or a backup holding the source, a full disk) the destination was
      // simply gone. On the SpamAssassin path, where sTo is the live message file
      // and the caller ignored the return value, that lost the message silently:
      // the sender had already been given a 250.
      //
      // It was also unnecessary. boost::filesystem::rename on Windows is
      // MoveFileExW with MOVEFILE_REPLACE_EXISTING | MOVEFILE_COPY_ALLOWED
      // (verified in the pinned Boost 1.91: operations.cpp, BOOST_MOVE_FILE), so it
      // already replaces an existing destination - and does it as one operation, so
      // sTo always names either the old file or the new one and never nothing at
      // all. Source and destination are in the same directory in every caller, so
      // COPY_ALLOWED never engages and the swap stays atomic.
      //
      // There was an overwrite parameter here, which selected between the delete-then-
      // rename above and a plain rename. It is gone rather than ignored: every value
      // now produced identical behaviour, while the call sites read as though they had
      // chosen something - Move(from, to, false) says "do not overwrite" and did - and
      // one caller had already written a careful paragraph reasoning about which
      // branch it wanted. A parameter that documents intent it cannot enforce is worse
      // than no parameter. If a non-Windows path ever needs the distinction, it can be
      // reintroduced by a change that also implements it.

      for (int i = 1; i <= iMaxNumberOfTries; i++)
      {
         boost::system::error_code error_code;
         boost::filesystem::rename(sFrom, sTo, error_code);

         if (!error_code)
            return true;

         if (i == iMaxNumberOfTries)
         {
            // We still couldn't move the file. Lets give up and report in windows event log and hMailServer application log.
            int iLastError = ::GetLastError();

            String sErrorMessage;
            sErrorMessage.Format(_T("Could not move the file %s to %s. Tried 5 times without success."), sFrom.c_str(), sTo.c_str());
            ErrorManager::Instance()->ReportError(ErrorManager::High, 5049, "File::Normal", sErrorMessage, error_code);

            return false;
         }

         // Some other process must have locked the file.
         Sleep(250);
      }

      throw std::logic_error("Move file logic error.");
   }

   bool
   FileUtilities::Exists(const String &sFilename)
   {
      return boost::filesystem::exists(sFilename);
   }

   bool
   FileUtilities::DirectoryExists(const String &sDirName)
   {
      return boost::filesystem::exists(sDirName) && boost::filesystem::is_directory(sDirName);
   }

   String
   FileUtilities::GetFilePath(const String & FileName)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Returns the path to the folder in which FileName resides.
   //---------------------------------------------------------------------------()
   {
      int iLastSlash = FileName.ReverseFind(_T("\\"));
      String Path = FileName.Mid(0, iLastSlash);

      return Path;
   }

   String
   FileUtilities::GetFileNameFromFullPath(const String & sFullPath)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Returns the filename in the full path.
   //---------------------------------------------------------------------------()
   {
      int iLastSlash = sFullPath.ReverseFind(_T("\\"));

      if (iLastSlash == -1)
      {
         // Path is relative.
         return sFullPath;
      }

      return sFullPath.Mid(iLastSlash + 1);
   }

   String
   FileUtilities::ReadCompleteTextFile(const String &sFilename)
   {
      File oFile;
      if (!oFile.Open(sFilename, File::OTReadOnly))
         return "";

      // Read file
      std::shared_ptr<ByteBuffer> pBuffer = oFile.ReadFile();

      if (!pBuffer || pBuffer->GetSize() == 0)
      {
         // Could not read from this file.
         return "";
      }

      FileEncoding file_encoding = ANSI;

      bool is_utf8 = false;
      
      // check if utf8 bom exists
      const unsigned char *unsigned_char_buffer = (const unsigned char*) pBuffer->GetCharBuffer();

      if (pBuffer->GetSize() >= 3 &&
          *unsigned_char_buffer == 0xef && 
          *(unsigned_char_buffer + 1) == 0xbb &&
          *(unsigned_char_buffer + 2) == 0xbf)
          file_encoding = UTF8;
      else if (pBuffer->GetSize() >= 2 &&
         *unsigned_char_buffer == 0xff && 
         *(unsigned_char_buffer + 1) == 0xfe)
         file_encoding = UTF16;
      
      switch (file_encoding)
      {
      case ANSI:
         {
            AnsiString sRetVal((const char*) unsigned_char_buffer, pBuffer->GetSize());
            return sRetVal;
         }
      case UTF8:
         {
            AnsiString raw_data((const char*) unsigned_char_buffer, pBuffer->GetSize());

            String utf8_data;
            Unicode::MultiByteToWide(raw_data, utf8_data);

            return utf8_data;
         }
      case UTF16:
         {
            size_t iChars = pBuffer->GetSize() / sizeof(TCHAR);
            String sRetVal((const wchar_t*) pBuffer->GetCharBuffer() +1, iChars -1);
            return sRetVal;
         }
      default:
         throw std::logic_error(Formatter::FormatAsAnsi("Unsupported encoding type: {0}", file_encoding));
      }
   }
 
   void
   FileUtilities::ReadFileToBuf(const String &sFilename, BYTE *OutBuf, int iStart, int iCount)
   {
      // Nothing asked for. Returning early also keeps the ReadChunk(0) case out of
      // the way rather than relying on new BYTE[0] behaving.
      if (iCount <= 0)
         return;

      // The declared defaults are -1/-1 and no caller uses them: -1 seeks nowhere
      // and a count of -1 becomes a near-SIZE_MAX allocation inside ReadChunk. A
      // negative start now reads from the beginning, which is what a caller asking
      // for "the whole file" means, and a non-positive count is nothing to do.
      if (iStart < 0)
         iStart = 0;

      File file;
      if (!file.Open(sFilename, File::OTReadOnly))
      {
         throw std::logic_error(Formatter::FormatAsAnsi("Unable to open file {0}", sFilename));
      }

      // The result of the seek used to be discarded. A failed seek leaves the
      // position at the start of the file, and the read then returned the FIRST
      // iCount bytes while the caller believed it had the bytes at iStart - for the
      // one caller, IMAPFetch's BODY[]<start.count> partial fetch, that is the
      // wrong region of the message handed to the client with no indication that
      // anything went wrong. Throwing matches how the failed open above is already
      // reported, so the caller's existing barrier covers it.
      if (!file.SetPosition(iStart))
      {
         throw std::runtime_error(Formatter::FormatAsAnsi("Unable to seek to offset {0} in file {1}", iStart, sFilename));
      }

      std::shared_ptr<ByteBuffer> bytes = file.ReadChunk(iCount);

      // Copy only what was actually read, and zero the rest. The old copy took
      // iCount unconditionally; it stayed inside the allocation because ReadChunk
      // allocates and zeroes iCount bytes before reading, but that is an accident of
      // ReadChunk's implementation rather than something this call was entitled to.
      const size_t bytesRead = bytes->GetSize();
      const size_t requested = (size_t) iCount;

      if (bytesRead > 0)
         memcpy(OutBuf, bytes->GetBuffer(), bytesRead < requested ? bytesRead : requested);

      if (bytesRead < requested)
         memset(OutBuf + bytesRead, 0, requested - bytesRead);
   }

   bool 
   FileUtilities::WriteToFile(const String &sFilename, const String &sData, bool bUnicode)
   {
      File oFile;
      if (!oFile.Open(sFilename, File::OTCreate))
         return false;

      if (bUnicode)
      {
         if (!oFile.WriteBOF())
            return false;

         if (!oFile.Write(sData))
            return false;
      }
      else
      {
         // Enforce ANSI format.
         AnsiString sAnsi = sData;

         if (!oFile.Write(sAnsi))
            return false;
      }

      oFile.Close();

      return true;
   }

   bool 
   FileUtilities::WriteToFile(const String &sFilename, const AnsiString &sData)
   {
      File oFile;
      if (!oFile.Open(sFilename, File::OTCreate))
         return false;

      if (!oFile.Write(sData))
         return false;

      oFile.Close();

      return true;
   }

   bool
   FileUtilities::FileSize64(const String &sFileName, unsigned __int64 &size)
   {
      size = 0;

      boost::system::error_code error_code;
      boost::uintmax_t result = boost::filesystem::file_size(sFileName, error_code);

      if (error_code)
         return false;

      size = (unsigned __int64) result;
      return true;
   }

   long
   FileUtilities::FileSize(const String &sFileName)
   {
      unsigned __int64 size = 0;

      if (!FileSize64(sFileName, size))
         return 0;

      // long is 32 bits here, so a file over 2 GiB does not fit in the result. It
      // used to be truncated by a plain (int) cast, and truncation fails in the
      // worst possible direction: at 2 GiB the value goes NEGATIVE, and at exactly
      // 4 GiB it comes out as zero.
      //
      // That matters because three separate ceilings are written as
      // "FileUtilities::FileSize(x) > Max..." - DKIM::Sign/Verify, MessageData's
      // load guard and SieveVacationTracker's store guard - and a negative number
      // passes every one of them, so each guard admitted precisely the file it
      // existed to refuse. Elsewhere the result is handed straight to
      // Message::SetSize, and a negative message size then feeds quota accounting,
      // SpaceAvailable and the POP3 octet counts.
      //
      // Saturating is the safe direction: those comparisons now refuse, and a size
      // that is capped beats one that is negative. Two other files already avoid
      // this function for exactly this reason and say so in their comments
      // (RateLimiter::LoadUsageFile_ and BackupScheduleTask) - the narrowing was
      // known and worked around twice rather than fixed here. Callers that need the
      // true size have FileSize64.
      const unsigned __int64 maxRepresentable = (unsigned __int64) LONG_MAX;

      if (size > maxRepresentable)
         return LONG_MAX;

      return (long) size;
   }

   String
   FileUtilities::GetTempFileName()
   {
      String sTmpFile;
      sTmpFile.Format(_T("%s\\%s.tmp"), IniFileSettings::Instance()->GetTempDirectory().c_str(), GUIDCreator::GetGUID().c_str());
      return sTmpFile;
   }

   bool
   FileUtilities::CreateDirectory(const String &sName)
   {
      const int iMaxNumberOfTries = 5;

      for (int i = 1; i <= iMaxNumberOfTries; i++)
      {
         boost::system::error_code error_code;

         boost::filesystem::create_directories(sName, error_code);
         
         if (!error_code)
            return true;
         
         if (i == iMaxNumberOfTries)
         {
            // We still couldn't create the directory. Lets give up and report in windows event log and hMailServer application log.
            String sErrorMessage;
            sErrorMessage.Format(_T("Could not create the directory %s. Tried 5 times without success."), sName.c_str());

            ErrorManager::Instance()->ReportError(ErrorManager::Medium, 5050, "File::CreateDirectory", sErrorMessage, error_code);
            return false;
         }

         // Some other process must have locked the file.
         Sleep(250);

      }

      throw std::logic_error("Create directory logic error.");
   }

   bool 
   FileUtilities::CopyDirectory(String sFrom, String sTo, String &errorMessage)
   {
      // Check whether the function call is valid
      if (!boost::filesystem::exists(sFrom) || !boost::filesystem::is_directory(sFrom))
      {
         throw std::logic_error(Formatter::FormatAsAnsi("Source {0} is not a valid directory.", sFrom));
      }

      if (!boost::filesystem::exists(sTo))
      {
         if (!CreateDirectory(sTo))
            return false;
      }


      for (boost::filesystem::directory_iterator file(sFrom); file != boost::filesystem::directory_iterator(); ++file )
      {
         boost::filesystem::path current(file->path());
         if (boost::filesystem::is_directory(current))
         {
            if (!CopyDirectory(current.c_str(), (sTo / current.filename()).c_str(), errorMessage))
            {
               return false;
            }
         }
         else
         {
            boost::filesystem::copy_file(current, sTo / current.filename());
         }
      }

      return true;
   }

   bool 
   FileUtilities::DeleteDirectory(const String &sDirName, bool force)
   {
      if (!force)
      {
         if (!boost::filesystem::is_directory(sDirName))
         {
            // The directory is already gone.
            return true;
         }

         if (GetDirectoryContainsFileRecursive(sDirName))
         {
            // Directory is not empty.
            return false;
         }
      }

      boost::system::error_code error_code;
      boost::filesystem::remove_all(sDirName, error_code);

      if (error_code)
         return false;
      
      return true;
   }

   bool
   FileUtilities::DeleteFilesInDirectory(const String &sDirName)
   {
      // Iterate with an error_code so a missing/inaccessible directory does
      // not throw (nothing to delete in that case).
      boost::system::error_code ec;

      // A file we could not delete, and the reason. The result of each remove used
      // to be dropped into the shared iteration error_code and then never read, so
      // this function returned true whatever happened - including for the caller
      // that matters most, BackupExecuter's restore, which clears the data
      // directory before unpacking over it and checks this return value. A restore
      // that unpacked on top of files it believed it had removed is a corrupted
      // data directory, so the failure is now carried out.
      //
      // The loop still visits every entry: deleting what can be deleted is more
      // useful to the caller than stopping at the first refusal, and the first
      // failure is the one reported.
      bool allDeleted = true;
      String firstFailure;
      boost::system::error_code firstFailureCode;

      for (boost::filesystem::directory_iterator file(std::wstring(sDirName), ec); !ec && file != boost::filesystem::directory_iterator(); ++file)
      {
         boost::filesystem::path current(file->path());
         if (boost::filesystem::is_directory(current))
            continue;

         boost::system::error_code remove_error;
         boost::filesystem::remove(current, remove_error);

         if (remove_error)
         {
            if (allDeleted)
            {
               firstFailure = current.wstring().c_str();
               firstFailureCode = remove_error;
            }

            allDeleted = false;
         }
      }

      if (!allDeleted)
      {
         String sErrorMessage;
         sErrorMessage.Format(_T("Could not delete the file %s while emptying the directory %s."),
            firstFailure.c_str(), sDirName.c_str());

         ErrorManager::Instance()->ReportError(ErrorManager::High, 6050, "FileUtilities::DeleteFilesInDirectory", sErrorMessage, firstFailureCode);
      }

      return allDeleted;
   }


   std::vector<FileInfo> 
   FileUtilities::GetFilesInDirectory(const String &sDirectoryName, const String &regularExpressionTest)
   {
      std::vector<FileInfo> result;

      // Iterate with an error_code: a missing directory yields an empty result
      // instead of an unhandled boost::filesystem exception. (An absent
      // Languages directory used to crash the service on startup via
      // Languages::Load.)
      boost::system::error_code ec;
      for (boost::filesystem::directory_iterator file(std::wstring(sDirectoryName), ec); !ec && file != boost::filesystem::directory_iterator(); ++file)
      {
         boost::filesystem::path current(file->path());

         if (RegularExpression::TestExactMatch(regularExpressionTest, current.filename().wstring()))
         {
            // The result of GetFileAttributesExW used to be dropped, which left
            // file_info - and therefore the creation time handed to FileInfo -
            // uninitialised stack when the call failed. It can fail on an entry that
            // was removed between the enumeration and this call, or one the service
            // cannot open. That timestamp is not decoration: ExceptionLogger::
            // TryToMakeRoom picks the OLDEST entry by it and deletes that file, so a
            // garbage value chose which minidump to destroy and could also make the
            // "keep everything for four hours" test read as satisfied.
            //
            // Zero-initialised, and an entry whose attributes could not be read is
            // skipped rather than reported with an invented time. Skipping is safe
            // for both callers: they prune, so a file left out this pass is
            // considered again on the next one.
            WIN32_FILE_ATTRIBUTE_DATA file_info = {};

            if (!GetFileAttributesExW(current.wstring().c_str(), GetFileExInfoStandard, &file_info))
               continue;

            result.push_back(FileInfo(current.filename().wstring(), file_info.ftCreationTime));
         }
      }


      return result;
      

   }

   bool
   FileUtilities::GetDirectoryContainsFileRecursive(const String &sDirectoryName)
   {
      if (!boost::filesystem::is_directory(sDirectoryName))
         return false;

      std::vector<FileInfo> result;

      for (boost::filesystem::directory_iterator file(sDirectoryName); file != boost::filesystem::directory_iterator(); ++file)
      {
         boost::filesystem::path current(file->path());

         if(boost::filesystem::is_directory(current))
         {
            auto result = GetDirectoryContainsFileRecursive(current.wstring().c_str());

            if (result)
               return true;
         }
         else
         {
            return true;
         }
      }

      return false;
   }


   bool
   FileUtilities::DeleteDirectoriesInDirectory(const String &sDirName)
   {
      boost::system::error_code iterate_error;
      for (boost::filesystem::directory_iterator file(std::wstring(sDirName), iterate_error); !iterate_error && file != boost::filesystem::directory_iterator(); ++file)
      {
         boost::filesystem::path current(file->path());
         if (boost::filesystem::is_directory(current))
         {
            boost::system::error_code error_code;
            boost::filesystem::remove_all(current, error_code);

            if (error_code)
            {
               String sErrorMessage;
               sErrorMessage.Format(_T("Could not delete the directory %s."), String(current.wstring()).c_str());
               ErrorManager::Instance()->ReportError(ErrorManager::High, 5700, "FileUtilities::DeleteDirectoriesInDirectory", sErrorMessage, error_code);
               return false;
            }
         }
      }

      return true;
   }

   bool 
   FileUtilities::IsUNCPath(const String &sPath)
   {
      if (sPath.StartsWith(_T("\\\\")))
         return true;
      else
         return false;
   }

   bool 
   FileUtilities::IsFullPath(const String &sPath)
   {
      if (sPath.GetLength() < 2)
         return false;

      bool isFullPath = (sPath[1] == ':' ||
                        IsUNCPath(sPath));

      return isFullPath;


   }

   String 
   FileUtilities::Combine(const String &path1, const String &path2)
   {
      String firstHalf = path1;
      String secondHalf = path2;

      if (firstHalf.EndsWith(_T("\\")) || firstHalf.EndsWith(_T("/")))
         firstHalf = firstHalf.Mid(0, firstHalf.GetLength() -1);

      if (secondHalf.StartsWith(_T("\\")) || secondHalf.StartsWith(_T("/")))
         secondHalf = secondHalf.Mid(1);

      String result = firstHalf + "\\" + secondHalf;

      return result;
   }

   /// Returns true if the supplied UNC path contains a Share name.
   bool 
   FileUtilities::IsValidUNCFolder(const String &sPath)
   {
      if (!IsUNCPath(sPath))
         return false;

      // We have at least \\server

      int shareStartPos = sPath.Find(_T("\\"), 3);
      if (shareStartPos < 0)
         return false;

      // We have at least \\server\

      int folderStartPos = sPath.Find(_T("\\"), shareStartPos + 1);
      if (folderStartPos < 0)
         return false;

      // We have at least \\server\share\

      int length = sPath.GetLength();
      if (folderStartPos == length-1)
         return false;

      // We have something after \\server\share. That is a folder.
      return true;
   }

   void FileUtilitiesTester::Test()
   {
      Assert::AreEqual("C:\\Temp", FileUtilities::Combine("C:\\", "Temp"));
      Assert::AreEqual("C:\\Temp", FileUtilities::Combine("C:\\", "\\Temp"));
      Assert::AreEqual("C:\\Temp", FileUtilities::Combine("C:", "\\Temp"));
      Assert::AreEqual("C:\\Temp", FileUtilities::Combine("C:", "Temp"));
      Assert::AreEqual("C:\\Temp", FileUtilities::Combine("C:/", "Temp"));

      Assert::IsTrue(FileUtilities::IsFullPath("C:\\"));
      Assert::IsTrue(FileUtilities::IsFullPath("C:/"));
      Assert::IsTrue(FileUtilities::IsFullPath("\\\\Test\\Monkey"));
      Assert::IsTrue(FileUtilities::IsFullPath("\\\\Test\\Monkey"));

      Assert::IsFalse(FileUtilities::IsFullPath("\\Test.eml"));
      Assert::IsFalse(FileUtilities::IsFullPath("Test.eml"));
      Assert::IsFalse(FileUtilities::IsFullPath("AB\\Data.eml"));

      // Verify that Copy overwrites an existing destination file.
      String sSrc = FileUtilities::GetTempFileName();
      String sDst = FileUtilities::GetTempFileName();
      FileUtilities::WriteToFile(sSrc, AnsiString("source"));
      FileUtilities::WriteToFile(sDst, AnsiString("original"));
      Assert::IsTrue(FileUtilities::Copy(sSrc, sDst, false));
      Assert::AreEqual("source", FileUtilities::ReadCompleteTextFile(sDst));
      FileUtilities::DeleteFile(sSrc);
      FileUtilities::DeleteFile(sDst);

      TestDeleteFile_();
      TestFileSize_();
      TestReadFileToBuf_();
      TestByteBuffer_();
   }

   void
   FileUtilitiesTester::TestDeleteFile_()
   {
      // A file that is not there is success - the caller's intent is "this must not
      // exist" - and so is one that is deleted.
      String missing = FileUtilities::GetTempFileName();
      Assert::IsTrue(FileUtilities::DeleteFile(missing));

      String present = FileUtilities::GetTempFileName();
      FileUtilities::WriteToFile(present, AnsiString("delete me"));
      Assert::IsTrue(FileUtilities::Exists(present));
      Assert::IsTrue(FileUtilities::DeleteFile(present));
      Assert::IsFalse(FileUtilities::Exists(present));

      // A non-ASCII name, written with universal-character escapes so the assertion
      // does not depend on this source file's encoding: "hm-<a-ring><a-diaeresis>
      // <o-diaeresis>-<nihongo>-<guid>.tmp".
      //
      // Against the code before this was fixed the name was narrowed to ANSI before
      // the path was built, so every character outside the active code page was
      // replaced - the delete named a path that does not exist, boost answered "did
      // not exist", and DeleteFile returned true with the file still on disk. The
      // Exists check after the delete is what catches that; the return value did
      // not, and could not.
      String unicodeName;
      unicodeName.Format(_T("%s\\hm-\u00E5\u00E4\u00F6-\u65E5\u672C\u8A9E-%s.tmp"),
         IniFileSettings::Instance()->GetTempDirectory().c_str(),
         GUIDCreator::GetGUID().c_str());

      if (FileUtilities::WriteToFile(unicodeName, AnsiString("delete me too")))
      {
         Assert::IsTrue(FileUtilities::Exists(unicodeName));
         Assert::IsTrue(FileUtilities::DeleteFile(unicodeName));
         Assert::IsFalse(FileUtilities::Exists(unicodeName));
      }

      // The failure path - a delete that cannot succeed - is deliberately NOT
      // provoked here. It reports HM5047, and an ERROR log entry written from inside
      // RunTestSuite fails the next fixture's AssertNoReportedError and every test
      // after it. It also sleeps through five attempts. Both are correct in
      // production and wrong in a self-test.
   }

   void
   FileUtilitiesTester::TestFileSize_()
   {
      String sizeFile = FileUtilities::GetTempFileName();
      FileUtilities::WriteToFile(sizeFile, AnsiString("0123456789"));

      Assert::IsTrue(FileUtilities::FileSize(sizeFile) == 10);

      unsigned __int64 size64 = 0;
      Assert::IsTrue(FileUtilities::FileSize64(sizeFile, size64));
      Assert::IsTrue(size64 == (unsigned __int64) 10);

      FileUtilities::DeleteFile(sizeFile);

      // FileSize cannot tell an unreadable file from an empty one; FileSize64 can,
      // and that is the reason it exists.
      Assert::IsTrue(FileUtilities::FileSize(sizeFile) == 0);
      Assert::IsFalse(FileUtilities::FileSize64(sizeFile, size64));
      Assert::IsTrue(size64 == (unsigned __int64) 0);

      // The saturation added to FileSize needs a file over 2 GiB to observe, which a
      // self-test has no business creating. It is stated at the definition instead.
   }

   void
   FileUtilitiesTester::TestReadFileToBuf_()
   {
      String readFile = FileUtilities::GetTempFileName();
      FileUtilities::WriteToFile(readFile, AnsiString("ABCDEFGHIJ"));

      // A partial read from an offset. Before the seek result was checked, a failed
      // seek silently returned the bytes from position 0 instead - the wrong region
      // of the message on an IMAP BODY[]<start.count> fetch.
      BYTE buffer[8];
      memset(buffer, 'x', sizeof(buffer));
      FileUtilities::ReadFileToBuf(readFile, buffer, 4, 3);
      Assert::IsTrue(buffer[0] == 'E' && buffer[1] == 'F' && buffer[2] == 'G');
      Assert::IsTrue(buffer[3] == 'x');

      // Asking for more than is there zero-fills the tail rather than leaving the
      // caller's buffer holding whatever it held before.
      memset(buffer, 'x', sizeof(buffer));
      FileUtilities::ReadFileToBuf(readFile, buffer, 8, 4);
      Assert::IsTrue(buffer[0] == 'I' && buffer[1] == 'J');
      Assert::IsTrue(buffer[2] == 0 && buffer[3] == 0);
      Assert::IsTrue(buffer[4] == 'x');

      // Nothing asked for is nothing done, and a negative start reads from the
      // beginning. Both used to reach ReadChunk with a count of -1 and ask for a
      // near-SIZE_MAX allocation.
      memset(buffer, 'x', sizeof(buffer));
      FileUtilities::ReadFileToBuf(readFile, buffer, 0, 0);
      Assert::IsTrue(buffer[0] == 'x');

      memset(buffer, 'x', sizeof(buffer));
      FileUtilities::ReadFileToBuf(readFile, buffer, -1, 2);
      Assert::IsTrue(buffer[0] == 'A' && buffer[1] == 'B');

      FileUtilities::DeleteFile(readFile);
   }

   void
   FileUtilitiesTester::TestByteBuffer_()
   {
      // ByteBuffer and File have no tester of their own and are the layer
      // FileUtilities is built on, so their coverage lives here rather than in a new
      // class that ClassTester::DoTests would have to be edited to reach.
      ByteBuffer buffer;
      const BYTE data[5] = { 0x31, 0x32, 0x33, 0x34, 0x35 }; // "12345"
      buffer.Add(data, 5);
      Assert::IsTrue(buffer.GetSize() == (size_t) 5);

      buffer.DecreaseSize(2);
      Assert::IsTrue(buffer.GetSize() == (size_t) 3);
      Assert::IsTrue(buffer.GetCharBuffer()[2] == '3');

      // Down to exactly nothing, which is the boundary the SMTP data path uses when
      // a flush ends on the transmission terminator.
      buffer.DecreaseSize(3);
      Assert::IsTrue(buffer.GetSize() == (size_t) 0);
      Assert::IsTrue(buffer.IsEmpty());

      // Decreasing by MORE than the buffer holds - the case the guard in
      // DecreaseSize was written for and, until it was fixed, could not detect - is
      // deliberately not exercised here: the refusal reports HM4222, and an ERROR
      // entry written from inside RunTestSuite fails the next fixture's
      // AssertNoReportedError and every test after it. Verified by inspection
      // instead, and see the report accompanying this change.

      // File::GetSize on a File that was never opened. The guard compared a FILE*
      // against INVALID_HANDLE_VALUE, so it never fired: the not-open case fell
      // through to boost::filesystem::file_size() with an empty path, which throws.
      // Against the unfixed code this line propagates a filesystem_error.
      File unopened;
      Assert::IsTrue(unopened.GetSize() == 0);
   }
}