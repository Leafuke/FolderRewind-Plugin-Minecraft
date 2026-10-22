using System.Buffers.Binary;
using System.IO.Hashing;
using K4os.Compression.LZ4;

namespace MineRewind.World;

/// <summary>Java LZ4Block framing (not the standard LZ4 frame format). Preview-only bounded decoding.</summary>
internal static class JavaLz4Block
{
    // https://github.com/lz4/lz4-java/blob/master/src/java/net/jpountz/lz4/LZ4BlockInputStream.java
    public static byte[] Decode(Stream input, CancellationToken token)
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[21];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            input.ReadExactly(header);
            if (!header[..8].SequenceEqual("LZ4Block"u8)) throw new InvalidDataException("Invalid LZ4Block magic.");
            var method = header[8] & 0xF0;
            var maximum = 1 << ((header[8] & 15) + 10);
            var packed = BinaryPrimitives.ReadInt32LittleEndian(header[9..]);
            var unpacked = BinaryPrimitives.ReadInt32LittleEndian(header[13..]);
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[17..]);
            if (method is not (0x10 or 0x20) || packed < 0 || unpacked < 0 || unpacked > maximum
                || unpacked > AnvilReader.MaximumChunkBytes || packed > AnvilReader.MaximumChunkBytes
                || (method == 0x10 && packed != unpacked) || (packed == 0) != (unpacked == 0))
                throw new InvalidDataException("Invalid LZ4Block lengths or method.");
            if (unpacked == 0)
            {
                if (checksum != 0 || input.ReadByte() != -1) throw new InvalidDataException("Invalid LZ4Block end marker.");
                return output.ToArray();
            }
            if (output.Length + unpacked > AnvilReader.MaximumChunkBytes) throw new InvalidDataException("Chunk exceeds the decoding limit.");
            var compressed = new byte[packed]; input.ReadExactly(compressed);
            var decoded = method == 0x10 ? compressed : new byte[unpacked];
            if (method == 0x20)
            {
                try
                {
                    if (LZ4Codec.Decode(compressed, decoded) != unpacked)
                        throw new InvalidDataException("Invalid LZ4Block payload.");
                }
                catch (ArgumentException ex) { throw new InvalidDataException("Invalid LZ4Block payload.", ex); }
            }
            if (XxHash32.HashToUInt32(decoded, unchecked((int)0x9747b28c)) != checksum)
                throw new InvalidDataException("LZ4Block checksum mismatch.");
            token.ThrowIfCancellationRequested();
            output.Write(decoded);
        }
    }
}
