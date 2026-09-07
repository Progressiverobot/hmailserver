// Copyright (c) 2026 Christopher Holloway / Progressive Robot Ltd
// https://www.progressiverobot.com
// SPDX-License-Identifier: AGPL-3.0-or-later

#include "StdAfx.h"
#include "DeflateStreams.h"

#include "../../zlib/zlib.h"

#ifdef _DEBUG
#define DEBUG_NEW new(_NORMAL_BLOCK, __FILE__, __LINE__)
#define new DEBUG_NEW
#endif

namespace HM
{
   struct DeflateStreams::State
   {
      z_stream inflate;
      z_stream deflate;
      bool inflate_ready;
      bool deflate_ready;

      State() : inflate_ready(false), deflate_ready(false)
      {
         memset(&inflate, 0, sizeof(inflate));
         memset(&deflate, 0, sizeof(deflate));
      }
   };

   namespace
   {
      // A negative window size asks zlib for raw DEFLATE - no zlib header, no
      // adler32 trailer - which is what the wire carries.
      const int RAW_DEFLATE_WINDOW_BITS = -15;
      // Level 6 is zlib's own default: a fair trade of CPU for bytes for text that
      // is read once and never stored. Memory level 8 is the default too.
      const int COMPRESSION_LEVEL = 6;
      const int MEMORY_LEVEL = 8;

      String ZlibMessage_(const z_stream &stream, int code)
      {
         if (stream.msg)
            return String(stream.msg);
         String text;
         text.Format(_T("zlib error %d"), code);
         return text;
      }
   }

   DeflateStreams::DeflateStreams() :
      state_(new State()),
      started_(false)
   {
   }

   DeflateStreams::~DeflateStreams()
   {
      if (state_->inflate_ready)
         inflateEnd(&state_->inflate);
      if (state_->deflate_ready)
         deflateEnd(&state_->deflate);
      delete state_;
   }

   bool
   DeflateStreams::Start()
   {
      if (started_)
         return true;

      if (inflateInit2(&state_->inflate, RAW_DEFLATE_WINDOW_BITS) != Z_OK)
         return false;
      state_->inflate_ready = true;

      if (deflateInit2(&state_->deflate, COMPRESSION_LEVEL, Z_DEFLATED, RAW_DEFLATE_WINDOW_BITS, MEMORY_LEVEL, Z_DEFAULT_STRATEGY) != Z_OK)
      {
         inflateEnd(&state_->inflate);
         state_->inflate_ready = false;
         return false;
      }
      state_->deflate_ready = true;

      started_ = true;
      return true;
   }

   bool
   DeflateStreams::Inflate(const char *compressed, size_t length, std::string &output, std::string &error)
   {
      if (!started_)
      {
         error = "compression is not active";
         return false;
      }

      z_stream &stream = state_->inflate;
      stream.next_in = (Bytef *) compressed;
      stream.avail_in = (uInt) length;

      char buffer[16384];
      while (stream.avail_in > 0)
      {
         stream.next_out = (Bytef *) buffer;
         stream.avail_out = sizeof(buffer);

         int code = inflate(&stream, Z_SYNC_FLUSH);
         if (code != Z_OK && code != Z_BUF_ERROR && code != Z_STREAM_END)
         {
            error = std::string(AnsiString(ZlibMessage_(stream, code)).c_str());
            return false;
         }

         size_t produced = sizeof(buffer) - stream.avail_out;
         if (produced > 0)
            output.append(buffer, produced);

         // Z_STREAM_END: the peer finished its stream, which RFC 4978 does not
         // provide for. Whatever follows is not DEFLATE; stop here rather than
         // misread it.
         if (code == Z_STREAM_END)
         {
            if (stream.avail_in > 0)
            {
               error = "the peer ended its DEFLATE stream and sent more";
               return false;
            }
            break;
         }

         // Nothing consumed and nothing produced: zlib wants more input than it
         // has, which is the normal state between segments.
         if (code == Z_BUF_ERROR && produced == 0)
            break;
      }

      return true;
   }

   bool
   DeflateStreams::Deflate(const char *plain, size_t length, std::string &output, std::string &error)
   {
      if (!started_)
      {
         error = "compression is not active";
         return false;
      }

      z_stream &stream = state_->deflate;
      stream.next_in = (Bytef *) plain;
      stream.avail_in = (uInt) length;

      char buffer[16384];
      // Z_SYNC_FLUSH until zlib reports it has nothing more to say: the flush
      // itself can need several output buffers for a large write.
      for (;;)
      {
         stream.next_out = (Bytef *) buffer;
         stream.avail_out = sizeof(buffer);

         int code = deflate(&stream, Z_SYNC_FLUSH);
         if (code != Z_OK && code != Z_BUF_ERROR)
         {
            error = std::string(AnsiString(ZlibMessage_(stream, code)).c_str());
            return false;
         }

         size_t produced = sizeof(buffer) - stream.avail_out;
         if (produced > 0)
            output.append(buffer, produced);

         if (stream.avail_out != 0)
            break;
      }

      return true;
   }
}
