using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace PhoneCam.Tray;

/// <summary>
/// Minimal H.264 decoder using FFmpeg.AutoGen (FFmpeg 6/7/8).
/// Input: one access unit (Annex-B bytestream with start codes) per call.
/// Output: 0..N decoded frames (most of the time: 1 frame per access unit).
/// </summary>
public sealed class H264FfmpegDecoder : IDisposable
{
    /// <summary>Optional callback for diagnostics.</summary>
    public Action<string>? OnLog { get; set; }

    private unsafe AVCodecContext* _codecCtx;
    private unsafe AVCodec* _codec;
    private unsafe AVFrame* _frame;
    private unsafe SwsContext* _sws;

    private int _dstW;
    private int _dstH;
    private AVPixelFormat _srcFmt = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _disposed;

    // FFmpeg swscale flag value; constant name can differ across generated bindings.
    private const int SWS_BILINEAR_FLAG = 2;

    public unsafe H264FfmpegDecoder()
    {
        // No avcodec_register_all() in modern FFmpeg.
        _codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (_codec == null)
            throw new InvalidOperationException("FFmpeg: H.264 decoder not found (avcodec_find_decoder returned null).");

        _codecCtx = ffmpeg.avcodec_alloc_context3(_codec);
        if (_codecCtx == null)
            throw new InvalidOperationException("FFmpeg: avcodec_alloc_context3 returned null.");

        // Low-latency-ish defaults (optional)
        _codecCtx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;

        int ret = ffmpeg.avcodec_open2(_codecCtx, _codec, null);
        if (ret < 0)
            throw new InvalidOperationException($"FFmpeg: avcodec_open2 failed: {Err(ret)}");

        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null)
            throw new InvalidOperationException("FFmpeg: av_frame_alloc returned null.");
    }

    /// <summary>
    /// Decode one access unit. If it produces one or more frames, returns them as Bitmaps (BGR24).
    /// </summary>
    public IEnumerable<Bitmap> DecodeToBitmaps(byte[] accessUnit)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(H264FfmpegDecoder));
        var result = new List<Bitmap>();
        if (accessUnit is null || accessUnit.Length == 0) return result;

        unsafe
        {
            AVPacket* pkt = ffmpeg.av_packet_alloc();
            if (pkt == null)
            {
                Log("FFmpeg: av_packet_alloc returned null.");
                return result;
            }

            // Allocate packet buffer owned by FFmpeg and copy managed bytes into it.
            int ret = ffmpeg.av_new_packet(pkt, accessUnit.Length);
            if (ret < 0)
            {
                ffmpeg.av_packet_free(&pkt);
                Log($"FFmpeg: av_new_packet failed: {Err(ret)}");
                return result;
            }

            Marshal.Copy(accessUnit, 0, (IntPtr)pkt->data, accessUnit.Length);

            ret = ffmpeg.avcodec_send_packet(_codecCtx, pkt);
            ffmpeg.av_packet_unref(pkt);
            ffmpeg.av_packet_free(&pkt);

            if (ret < 0)
            {
                Log($"FFmpeg: avcodec_send_packet failed: {Err(ret)}");
                return result;
            }

            while (true)
            {
                ret = ffmpeg.avcodec_receive_frame(_codecCtx, _frame);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                    break;

                if (ret < 0)
                {
                    Log($"FFmpeg: avcodec_receive_frame failed: {Err(ret)}");
                    break;
                }

                try
                {
                    var bmp = ConvertFrameToBitmap(_frame);
                    if (bmp != null) result.Add(bmp);
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }
            }
        }

        return result;
    }

    private unsafe Bitmap? ConvertFrameToBitmap(AVFrame* src)
    {
        int w = src->width;
        int h = src->height;
        if (w <= 0 || h <= 0) return null;

        var srcPixFmt = (AVPixelFormat)src->format;

        // (Re)create sws context if input geometry/pixfmt changed.
        if (_sws == null || _dstW != w || _dstH != h || _srcFmt != srcPixFmt)
        {
            _sws = ffmpeg.sws_getCachedContext(
                _sws,
                w, h, srcPixFmt,
                w, h, AVPixelFormat.AV_PIX_FMT_BGR24,
                SWS_BILINEAR_FLAG,
                null, null, null);

            if (_sws == null)
            {
                Log("FFmpeg: sws_getCachedContext returned null.");
                return null;
            }

            _dstW = w;
            _dstH = h;
            _srcFmt = srcPixFmt;
        }

        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, w, h);

        BitmapData? bd = null;
        try
        {
            bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

            byte_ptrArray4 dstData = default;
            int_array4 dstLinesize = default;

            dstData[0] = (byte*)bd.Scan0;
            dstLinesize[0] = bd.Stride;

            var srcData = src->data;
            var srcLinesize = src->linesize;

            ffmpeg.sws_scale(
                _sws,
                srcData,
                srcLinesize,
                0,
                h,
                dstData,
                dstLinesize);

            return bmp;
        }
        catch (Exception ex)
        {
            Log("ConvertFrameToBitmap exception: " + ex.Message);
            bmp.Dispose();
            return null;
        }
        finally
        {
            if (bd != null)
                bmp.UnlockBits(bd);
        }
    }

    private void Log(string s) => OnLog?.Invoke(s);

    private static unsafe string Err(int err)
    {
        const int bufSize = 1024;
        byte* buf = stackalloc byte[bufSize];
        ffmpeg.av_strerror(err, buf, (ulong)bufSize);
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? $"err={err}";
    }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sws != null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }

        if (_frame != null)
        {
            AVFrame* f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_codecCtx != null)
        {
            AVCodecContext* c = _codecCtx;
            ffmpeg.avcodec_free_context(&c);
            _codecCtx = null;
        }
    }
}
