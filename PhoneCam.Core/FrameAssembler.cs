using System.Buffers.Binary;

namespace PhoneCam.Core
{
    public sealed class FrameAssembler
    {
        private const uint MAGIC = 0x4D414350; // "PCAM" little endian
        private const byte VERSION = 1;
        private const int MIN_HEADER = 32;

        private readonly System.Action<string>? _log;

        private uint _curFrameId;
        private ushort _expectedChunks;
        private int _receivedChunks;
        private int _totalPayload;

        private byte[][]? _chunkData;
        private bool[]? _chunkSeen;

        private uint _lastSeq;
        private bool _haveSeq;

        public FrameAssembler(System.Action<string>? log = null) => _log = log;

        public bool TryConsumePacket(byte[] packet, out byte[] accessUnit, out int lossDelta)
        {
            accessUnit = System.Array.Empty<byte>();
            lossDelta = 0;

            if (packet == null || packet.Length < MIN_HEADER)
                return false;

            var magic = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(0, 4));
            if (magic != MAGIC) return false;

            var version = packet[4];
            if (version != VERSION) return false;

            var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6, 2));
            if (headerSize < MIN_HEADER || headerSize > packet.Length) return false;

            var seq = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8, 4));
            if (_haveSeq)
            {
                var expected = _lastSeq + 1;
                if (seq > expected)
                    lossDelta = (int)(seq - expected);
            }
            _haveSeq = true;
            _lastSeq = seq;

            var frameId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(12, 4));
            var chunkIndex = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(20, 2));
            var chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(22, 2));
            var payloadLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(24, 4));

            if (chunkCount == 0) return false;
            if (chunkIndex >= chunkCount) return false;
            if (payloadLen < 0) return false;
            if (headerSize + payloadLen > packet.Length) return false;

            if (_curFrameId != frameId || _expectedChunks != chunkCount)
                ResetFrame(frameId, chunkCount);

            _chunkSeen ??= new bool[_expectedChunks];
            _chunkData ??= new byte[_expectedChunks][];

            if (_chunkSeen[chunkIndex])
                return false; // duplicate

            var payload = new byte[payloadLen];
            System.Buffer.BlockCopy(packet, headerSize, payload, 0, payloadLen);

            _chunkSeen[chunkIndex] = true;
            _chunkData[chunkIndex] = payload;
            _receivedChunks++;
            _totalPayload += payloadLen;

            if (_receivedChunks < _expectedChunks)
                return false;

            // assemble in order
            var au = new byte[_totalPayload];
            var off = 0;
            for (var i = 0; i < _expectedChunks; i++)
            {
                var c = _chunkData[i];
                if (c == null)
                {
                    _log?.Invoke($"Assembler: missing chunk {i}/{_expectedChunks} for frameId={_curFrameId}");
                    ResetState();
                    return false;
                }

                System.Buffer.BlockCopy(c, 0, au, off, c.Length);
                off += c.Length;
            }

            accessUnit = au;
            ResetState();
            return true;
        }

        private void ResetFrame(uint frameId, ushort chunkCount)
        {
            _curFrameId = frameId;
            _expectedChunks = chunkCount;
            _receivedChunks = 0;
            _totalPayload = 0;
            _chunkSeen = new bool[chunkCount];
            _chunkData = new byte[chunkCount][];
        }

        private void ResetState()
        {
            _curFrameId = 0;
            _expectedChunks = 0;
            _receivedChunks = 0;
            _totalPayload = 0;
            _chunkSeen = null;
            _chunkData = null;
        }
    }
}
