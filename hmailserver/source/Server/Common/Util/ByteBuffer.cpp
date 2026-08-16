// Copyright (c) 2010 Martin Knafve / hMailServer.com.  
// https://www.progressiverobot.com
// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd

#include "stdafx.h"
#include "ByteBuffer.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   ByteBuffer::ByteBuffer() :
      buffer_ (0),
      buffer_size_ (0)
   {

   }

   ByteBuffer::~ByteBuffer()
   {
      if (buffer_)
      {
         delete [] buffer_;
         buffer_ = 0;

         buffer_size_ = 0;
      }   
   }

   void 
   ByteBuffer::Empty()
   {
      if (buffer_)
      {
         delete [] buffer_;
         buffer_ = 0;

         buffer_size_ = 0;
      }   
   }

   void 
   ByteBuffer::Empty(size_t iLeaveEndingBytes)
   {
      if (iLeaveEndingBytes > buffer_size_)
      {
         throw std::logic_error(Formatter::FormatAsAnsi("The number of bytes to leave exceeds buffer size. Bytes to leave: {0}, Buffer size: {1}", iLeaveEndingBytes, buffer_size_));
      }

      // Allocate a temporary buffer.
      BYTE * pRemaining = new BYTE[iLeaveEndingBytes];
      
      // Copy the remaining data to this buffer.
      memcpy(pRemaining, buffer_ + (buffer_size_ - iLeaveEndingBytes), iLeaveEndingBytes );

      // Empty this buffer.
      Empty();

      // Add the remaining data to the buffer again.
      Add(pRemaining, iLeaveEndingBytes);

      delete [] pRemaining;
   }

   void 
   ByteBuffer::Allocate(size_t lSize)
   {
      
      Empty();
      
      // Allocate a new buffer
      buffer_ = new BYTE[lSize];
      memset(buffer_, 0, lSize);
      buffer_size_ = lSize;
   }

   void 
   ByteBuffer::Add(ByteBuffer *pBuf)
   {
      Add(pBuf->GetBuffer(), pBuf->GetSize());
   }

   void 
   ByteBuffer::Add(std::shared_ptr<ByteBuffer> pBuf)
   {
      Add(pBuf->GetBuffer(), pBuf->GetSize());
   }

   void
   ByteBuffer::Add(const BYTE *pBuf, size_t lSize)
   {
      
      if (lSize == 0)
      {
         // Nothing to do.
         return;
      }

      size_t iTotBufLen = buffer_size_ + lSize;

      // Allocate a new buffer big enough to contain
      // both old and new buffer.
      BYTE *tmpbuf = new BYTE[iTotBufLen];
      memset(tmpbuf, 0, iTotBufLen);
      
      // Copy the old data to the temporary buffer.
      if (buffer_size_ > 0)
         memcpy(tmpbuf, buffer_, buffer_size_);
      
      // Copy the new data to the temporary buffer.
      memcpy(tmpbuf + buffer_size_,pBuf, lSize);

      // We should now repoint this->buffer to
      // tmpbuf. Free current buffer.
      Empty();

      buffer_ = tmpbuf;
      buffer_size_ = iTotBufLen;
   }

   void 
   ByteBuffer::DecreaseSize(size_t iDecreaseWith)
   //---------------------------------------------------------------------------()
   // DESCRIPTION:
   // Decreases the size of the buffer. This is done just by decreasing the
   // member variable that tells us how large the buffer is. This may look weird
   // but gives better performance than allocating a whole new buffer.
   //---------------------------------------------------------------------------()
   {
      // The test used to be "buffer_size_ - iDecreaseWith < 0", which cannot be
      // true: both operands are size_t, so the subtraction is unsigned and wraps
      // rather than going negative. The guard therefore never fired and the case it
      // was written to catch was the one it let through - buffer_size_ became a
      // value near SIZE_MAX, and since GetSize() and GetBuffer() are what every
      // caller reads the buffer through, the next read walked gigabytes of heap
      // past a small allocation.
      //
      // Compare before subtracting. No caller reaches this today (File::ReadChunk
      // never reads more than it allocated, and TransparentTransmissionBuffer
      // checks the buffer length before stripping a terminator from it), so this
      // is the guard doing what it always claimed rather than a fix for a live
      // fault - but it is the guard those callers are written against.
      if (iDecreaseWith > buffer_size_)
      {
         ErrorManager::Instance()->ReportError(ErrorManager::High, 4222, "ByteBuffer::DecreaseSize", "Tried to decrease the size of the buffer to a negative value.");
         return ;
      }

      buffer_size_ -= iDecreaseWith;
   }
}

