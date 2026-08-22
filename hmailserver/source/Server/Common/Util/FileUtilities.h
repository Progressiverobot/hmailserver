// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// SPDX-License-Identifier: AGPL-3.0-or-later

#pragma once

namespace HM
{
   class FileInfo;

   class FileUtilities
   {
   public:
      FileUtilities(void);
      ~FileUtilities(void);

      static const String PathSeparator;

      static String GetFilePath(const String &FileName);
      static String GetFileNameFromFullPath(const String & sFullPath);

      static bool DeleteFile(const String &FileName);

      //static bool ReadLine(HANDLE hFile, String &sLine);
      static bool Copy(const String &sFrom, const String &sTo, bool bCreateMissingDirectories = false);
      // Renames sFrom onto sTo, replacing sTo if it exists. There is deliberately no
      // overwrite flag: the rename replaces the destination as one operation and
      // there is no variant that does not, so a flag could only mislead. See the
      // definition for why the delete-then-rename version was removed.
      static bool Move(const String &sFrom, const String &sTo);
      static bool Exists(const String &sFilename);

      static void ReadFileToBuf(const String &sFilename, BYTE *Buf, int iStart = -1, int iCount = -1);
      static String ReadCompleteTextFile(const String &sFilename);

      static bool WriteToFile(const String &sFilename, const String &sData, bool bUnicode);
      static bool WriteToFile(const String &sFilename, const AnsiString &sData);

      // The size of a file, or 0 when it cannot be read. long is 32 bits here, so a
      // size over LONG_MAX is SATURATED rather than truncated - see the definition
      // for why the previous truncation turned three "> maximum" guards into
      // guards that admitted the oversized file. Use FileSize64 where the true
      // size matters.
      static long FileSize(const String &sFileName);

      // The true 64-bit size. Returns false when the file cannot be read, which is
      // the case FileSize cannot distinguish from an empty file.
      static bool FileSize64(const String &sFileName, unsigned __int64 &size);

      static String GetTempFileName();
      static bool CreateDirectory(const String &sName);

      // Matches the precondition CopyDirectory enforces by throwing, so a caller
      // can check first rather than catch.
      static bool DirectoryExists(const String &sDirName);

      static bool CopyDirectory(String sFrom, String sTo, String &errorMessage);
      static bool DeleteDirectory(const String &sDirName, bool force);
      static bool DeleteFilesInDirectory(const String &sDirName);
      static bool DeleteDirectoriesInDirectory(const String &sDirName);

      static std::vector<FileInfo> GetFilesInDirectory(const String &sDirectoryName, const String &regularExpressionTest);
      static bool GetDirectoryContainsFileRecursive(const String &sDirectoryName);
      static bool IsUNCPath(const String &sPath);
      static bool IsValidUNCFolder(const String &sPath);
      static bool IsFullPath(const String &sPath);

      static String Combine(const String &path1, const String &path2);

   private:

      enum FileEncoding
      {
         ANSI = 1,
         UTF8 = 2,
         UTF16 = 3
      };

      
   };

   class FileUtilitiesTester
   {
   private:

   public:

      FileUtilitiesTester() {};
      virtual ~FileUtilitiesTester() {} ;

      void Test();
   private:

      void TestDeleteFile_();
      void TestFileSize_();
      void TestReadFileToBuf_();

      // ByteBuffer and File have no tester of their own, and adding one would mean
      // editing ClassTester::DoTests to reach it. They are the layer this class is
      // built on, so their coverage lives here.
      void TestByteBuffer_();
   };
}