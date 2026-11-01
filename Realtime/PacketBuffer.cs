using System;
using System.Collections.Generic;
using FFmpeg.AutoGen;
using iSpyApplication.Sources.Video;

namespace iSpyApplication.Realtime
{
    /// <summary>
    /// The camera's own compressed video packets (H.264/H.265 from an FFmpeg source), kept so recordings can
    /// copy them into the file instead of decoding and re-encoding every frame ("passthrough" recording).
    /// While not recording it is the pre-record buffer: it keeps at least the buffer length, starting on a
    /// keyframe so a recording can begin there. While recording it queues every packet for the writer.
    /// </summary>
    internal sealed unsafe class PacketBuffer : IDisposable
    {
        internal struct Item
        {
            public AVPacket* Packet;
            public DateTime Time;
            public bool Key => (Packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;
        }

        // Safety net if the writer stalls or a camera never sends a keyframe.
        private const long MaxBytes = 256L * 1024 * 1024;

        private readonly Queue<Item> _items = new Queue<Item>();
        private readonly object _lock = new object();
        private AVCodecParameters* _parameters;
        private int _generation = -1;
        private long _bytes;
        private bool _recording;
        private bool _disposed;

        /// <summary>Adds a copy of a video packet. Called on the source's reading thread.</summary>
        public void Add(MediaStream source, AVPacket* packet, DateTime time, double bufferSeconds)
        {
            if (packet->size <= 0)
                return;
            lock (_lock)
            {
                if (_disposed)
                    return;

                if (source.StreamGeneration != _generation)
                {
                    // The source (re)connected. Outside a recording, start again with its parameters. During a
                    // recording, carry on if the format is unchanged, otherwise drop packets until it ends.
                    var parameters = source.VideoParameters;
                    if (parameters == null)
                        return;
                    if (_recording)
                    {
                        if (!SameFormat(parameters))
                            return;
                        _generation = source.StreamGeneration;
                    }
                    else
                    {
                        Clear();
                        SetParameters(parameters, source.StreamGeneration);
                    }
                }

                var copy = ffmpeg.av_packet_clone(packet);
                if (copy == null)
                    return;
                _items.Enqueue(new Item {Packet = copy, Time = time});
                _bytes += copy->size;

                if (!_recording)
                    Trim(time, bufferSeconds);
                while (_bytes > MaxBytes && _items.Count > 0)
                    Drop();
            }
        }

        /// <summary>Whether a recording can start from this buffer: a supported codec and a keyframe to begin on.</summary>
        public bool CanRecord
        {
            get
            {
                lock (_lock)
                    return CanRecordLocked;
            }
        }

        private bool CanRecordLocked =>
            !_disposed && _parameters != null &&
            (_parameters->codec_id == AVCodecID.AV_CODEC_ID_H264 || _parameters->codec_id == AVCodecID.AV_CODEC_ID_HEVC) &&
            _items.Count > 0 && _items.Peek().Key;

        /// <summary>
        /// Stops trimming so every packet from here on (and the buffered ones) can be taken for the recording.
        /// On success <paramref name="parameters"/> is a copy of the stream parameters for the writer; free it
        /// with avcodec_parameters_free.
        /// </summary>
        public bool BeginRecording(out AVCodecParameters* parameters, out DateTime start)
        {
            parameters = null;
            start = DateTime.MinValue;
            lock (_lock)
            {
                if (!CanRecordLocked)
                    return false;
                parameters = ffmpeg.avcodec_parameters_alloc();
                if (parameters == null || ffmpeg.avcodec_parameters_copy(parameters, _parameters) < 0)
                {
                    if (parameters != null)
                    {
                        var p = parameters;
                        ffmpeg.avcodec_parameters_free(&p);
                        parameters = null;
                    }
                    return false;
                }
                start = _items.Peek().Time;
                _recording = true;
                return true;
            }
        }

        public void EndRecording()
        {
            lock (_lock)
                _recording = false;
        }

        /// <summary>Takes the oldest packet; the caller owns it and frees it with <see cref="Free"/>.</summary>
        public bool TryTake(out Item item)
        {
            lock (_lock)
            {
                if (_disposed || _items.Count == 0)
                {
                    item = default(Item);
                    return false;
                }
                item = _items.Dequeue();
                _bytes -= item.Packet->size;
                return true;
            }
        }

        public static void Free(Item item)
        {
            var p = item.Packet;
            if (p != null)
                ffmpeg.av_packet_free(&p);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                Clear();
                if (_parameters != null)
                {
                    var p = _parameters;
                    ffmpeg.avcodec_parameters_free(&p);
                    _parameters = null;
                }
            }
        }

        private void Trim(DateTime now, double bufferSeconds)
        {
            // A recording has to start on a keyframe: anything before the first one is no use.
            while (_items.Count > 0 && !_items.Peek().Key)
                Drop();

            // Keep from the last keyframe at or before the cutoff, so the buffer covers at least bufferSeconds.
            var cutoff = now.AddSeconds(-bufferSeconds);
            int lastKey = -1, i = 0;
            foreach (var item in _items)
            {
                if (item.Time > cutoff)
                    break;
                if (item.Key)
                    lastKey = i;
                i++;
            }
            for (var j = 0; j < lastKey; j++)
                Drop();
        }

        private void Drop()
        {
            var item = _items.Dequeue();
            _bytes -= item.Packet->size;
            Free(item);
        }

        private void Clear()
        {
            while (_items.Count > 0)
                Drop();
            _bytes = 0;
        }

        private void SetParameters(AVCodecParameters* source, int generation)
        {
            if (_parameters == null)
                _parameters = ffmpeg.avcodec_parameters_alloc();
            if (_parameters != null && ffmpeg.avcodec_parameters_copy(_parameters, source) >= 0)
                _generation = generation;
        }

        private bool SameFormat(AVCodecParameters* p) =>
            _parameters != null && p->codec_id == _parameters->codec_id &&
            p->width == _parameters->width && p->height == _parameters->height;
    }
}
