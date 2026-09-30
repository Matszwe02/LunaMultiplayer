using System;
using System.IO;
using System.IO.Compression;

namespace KerbalismSync
{
    /// <summary>
    /// Wire format: version byte, opcode, body. Large bodies are Deflate-compressed and split into
    /// chunks because this Lidgren build has no fragmentation and the MTU is 1408. Compiled into both
    /// halves; references neither LMP, KSP nor Kerbalism, so the two can never disagree on the format.
    /// </summary>
    public static class Protocol
    {
        /// <summary>Bump when the layout below changes incompatibly.</summary>
        public const byte Version = 1;

        /// <summary>Mod channel name. Must match the name the server plugin registers.</summary>
        public const string ModName = "Kerbalism";

        /// <summary>Key under which the per-vessel store is persisted.</summary>
        public const string VesselScope = "vessel";

        /// <summary>Key under which save-global Kerbalism state is persisted.</summary>
        public const string GlobalScope = "global";

        /// <summary>Under the 1408 byte MTU once the LMP message and chunk headers are subtracted.</summary>
        public const int MaxChunkPayload = 1000;

        /// <summary>Bytes of chunk header: total, index (2), message id (4).</summary>
        private const int ChunkHeaderSize = 7;

        /// <summary>Upper bound on a reassembled message, so a peer cannot make us allocate without limit.</summary>
        public const int MaxMessageBytes = 8 * 1024 * 1024;

        public enum Opcode : byte
        {
            /// <summary>Client to server: I have Kerbalism and this protocol version.</summary>
            Hello = 1,

            /// <summary>Client to server: please send me the current state of a vessel.</summary>
            RequestVessel = 2,

            /// <summary>Client to server: here is my authoritative state for a vessel I hold.</summary>
            VesselState = 3,

            /// <summary>Client to server: please send me the save-global state.</summary>
            RequestGlobal = 4,

            /// <summary>Client to server: here is the save-global state.</summary>
            GlobalState = 5,

            /// <summary>Client to server: I just started tracking this vessel, poll it for me.</summary>
            WatchVessel = 6,

            /// <summary>Client to server: stop tracking this vessel, stop sending me updates.</summary>
            UnwatchVessel = 7,

            /// <summary>Server to client: the current state of a vessel.</summary>
            VesselStateResponse = 20,

            /// <summary>Server to client: the current save-global state.</summary>
            GlobalStateResponse = 21,

            /// <summary>Server to client: I have no stored state. Keep your local state rather than treat this as empty.</summary>
            UnknownScope = 22,

            /// <summary>Server to client: your update was rejected, and here is why.</summary>
            Rejected = 23
        }

        #region Envelope

        /// <summary>Builds a complete (uncompressed, unchunked) message.</summary>
        public static byte[] Build(Opcode opcode, byte[] body)
        {
            body = body ?? new byte[0];
            var message = new byte[2 + body.Length];
            message[0] = Version;
            message[1] = (byte)opcode;
            Buffer.BlockCopy(body, 0, message, 2, body.Length);
            return message;
        }

        /// <summary>Validates an incoming message and splits off its body. False means drop it.</summary>
        public static bool TryParse(byte[] message, out Opcode opcode, out byte[] body)
        {
            opcode = 0;
            body = null;

            if (message == null || message.Length < 2)
                return false;

            if (message[0] != Version)
                return false;

            opcode = (Opcode)message[1];
            body = new byte[message.Length - 2];
            Buffer.BlockCopy(message, 2, body, 0, body.Length);
            return true;
        }

        #endregion

        #region Chunking

        // Combined with the receiver's timeout this only has to stay unique among messages in
        // flight at the same time.
        private static int _nextMessageId;

        /// <summary>Wraps a message as chunk frames; a small message becomes a single frame.</summary>
        public static byte[][] Split(byte[] message)
        {
            var compressed = Deflate(message);

            var frameCount = Math.Max(1, (compressed.Length + MaxChunkPayload - 1) / MaxChunkPayload);

            if (frameCount == 1)
            {
                var single = new byte[ChunkHeaderSize + compressed.Length];
                WriteChunkHeader(single, 1, 0, NextMessageId());
                Buffer.BlockCopy(compressed, 0, single, ChunkHeaderSize, compressed.Length);
                return new[] { single };
            }

            var frames = new byte[frameCount][];
            var messageId = NextMessageId();
            for (var i = 0; i < frameCount; i++)
                frames[i] = new byte[ChunkHeaderSize + MaxChunkPayload];

            var offset = 0;
            for (var i = 0; i < frameCount; i++)
            {
                var take = Math.Min(MaxChunkPayload, compressed.Length - offset);
                WriteChunkHeader(frames[i], frameCount, i, messageId);
                Buffer.BlockCopy(compressed, offset, frames[i], ChunkHeaderSize, take);
                offset += take;
            }

            return frames;
        }

        private static int NextMessageId()
        {
            return System.Threading.Interlocked.Increment(ref _nextMessageId);
        }

        private static void WriteChunkHeader(byte[] frame, int total, int index, int messageId)
        {
            frame[0] = (byte)total;
            frame[1] = (byte)(index & 0xFF);
            frame[2] = (byte)((index >> 8) & 0xFF);
            frame[3] = (byte)(messageId & 0xFF);
            frame[4] = (byte)((messageId >> 8) & 0xFF);
            frame[5] = (byte)((messageId >> 16) & 0xFF);
            frame[6] = (byte)((messageId >> 24) & 0xFF);
        }

        /// <summary>
        /// Accumulates chunk frames by the message id in the header. A partial message is dropped
        /// after <see cref="TimeoutMilliseconds"/> so a peer that stops sending cannot pin memory.
        /// </summary>
        public sealed class Reassembler
        {
            private sealed class Pending
            {
                public byte[] Buffer;
                public int Received;
                public bool[] Seen;
                public long CreatedTicks;
            }

            private readonly System.Collections.Generic.Dictionary<int, Pending> _pending =
                new System.Collections.Generic.Dictionary<int, Pending>();

            /// <summary>How long a partly-received message is kept before being discarded.</summary>
            public const long TimeoutMilliseconds = 10000;

            /// <summary>Messages that never completed, for diagnostics.</summary>
            public int TimedOutCount { get; private set; }

            /// <summary>Feeds one frame in. Returns the message when its last chunk arrives, else null.</summary>
            public byte[] Accept(byte[] frame)
            {
                if (frame == null || frame.Length <= ChunkHeaderSize)
                    return null;

                var total = frame[0];
                var index = frame[1] | (frame[2] << 8);
                var messageId = frame[3] | (frame[4] << 8) | (frame[5] << 16) | (frame[6] << 24);

                if (total == 0 || index >= total)
                    return null;

                if (!_pending.TryGetValue(messageId, out var pending))
                {
                    pending = new Pending
                    {
                        Buffer = new byte[MaxChunkPayload * total],
                        Seen = new bool[total],
                        CreatedTicks = NowMs()
                    };
                    _pending[messageId] = pending;
                }

                var payloadLength = Math.Min(frame.Length - ChunkHeaderSize, MaxChunkPayload);

                Buffer.BlockCopy(frame, ChunkHeaderSize, pending.Buffer, index * MaxChunkPayload, payloadLength);
                pending.Seen[index] = true;
                pending.Received++;

                if (pending.Received < total)
                    return null;

                _pending.Remove(messageId);

                var totalPayload = (total - 1) * MaxChunkPayload + payloadLength;
                if (totalPayload > MaxMessageBytes)
                    return null;

                var compressed = new byte[totalPayload];
                Buffer.BlockCopy(pending.Buffer, 0, compressed, 0, totalPayload);
                return Inflate(compressed, totalPayload);
            }

            /// <summary>Drops partial messages older than the timeout.</summary>
            public void Expire()
            {
                if (_pending.Count == 0)
                    return;

                var now = NowMs();
                var expired = new System.Collections.Generic.List<int>();
                foreach (var pair in _pending)
                {
                    if (now - pair.Value.CreatedTicks > TimeoutMilliseconds)
                        expired.Add(pair.Key);
                }

                foreach (var key in expired)
                {
                    _pending.Remove(key);
                    TimedOutCount++;
                }
            }

            public int PendingCount => _pending.Count;

            private static long NowMs() => (long)DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        #endregion

        #region Compression

        public static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                // leaveOpen so we can read the buffer before disposing
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                    deflate.Write(data, 0, data.Length);

                return output.ToArray();
            }
        }

        /// <summary>Reverses <see cref="Deflate"/>. Null if the input is invalid or would exceed the limit.</summary>
        public static byte[] Inflate(byte[] data, int length)
        {
            try
            {
                using (var input = new MemoryStream(data, 0, length))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int read;
                    while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + read > MaxMessageBytes)
                            return null;

                        output.Write(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion
    }
}
