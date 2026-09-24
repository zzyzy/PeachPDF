using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace PeachPDF.Fonts
{
    /// <summary>
    /// Reads the faces out of a TrueType/OpenType collection (.ttc/.otc).
    /// </summary>
    /// <remarks>
    /// A collection is several fonts in one file, sharing whatever tables are
    /// identical between them. Nothing downstream of the resolver knows what a
    /// collection is -- <see cref="PdfSharpCore.Drawing.XFontSource"/> and
    /// <see cref="OpenType.OpenTypeFontface"/> both expect the bytes of a
    /// single font -- so a face is lifted out here and handed on as an
    /// ordinary sfnt file rather than threading a face index through every
    /// layer that touches font bytes.
    ///
    /// This matters on Linux in particular: Noto CJK, and so the only CJK
    /// coverage most distributions install, is shipped as a collection. Left
    /// out, every CJK codepoint falls back to a face that has no such glyph.
    /// </remarks>
    internal static class TrueTypeCollection
    {
        /// <summary>'ttcf', the tag a collection starts with.</summary>
        private const uint CollectionTag = 0x74746366;

        private const int SfntHeaderLength = 12;
        private const int TableRecordLength = 16;

        public static bool IsCollection(ReadOnlySpan<byte> font) =>
            font.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(font) == CollectionTag;

        /// <summary>
        /// How many faces the collection holds, or 1 for a plain font file.
        /// </summary>
        public static int GetFaceCount(ReadOnlySpan<byte> font)
        {
            if (!IsCollection(font))
                return 1;

            // ttcf, majorVersion, minorVersion, then the face count.
            if (font.Length < 12)
                throw new InvalidOperationException("The font collection is truncated.");

            var count = BinaryPrimitives.ReadUInt32BigEndian(font[8..]);

            if (count == 0 || count > int.MaxValue / sizeof(uint))
                throw new InvalidOperationException($"The font collection declares {count} faces.");

            return (int)count;
        }

        /// <summary>
        /// Returns <paramref name="faceIndex"/> of the collection as a
        /// standalone font file, or the font itself when it is not one.
        /// </summary>
        public static byte[] ExtractFace(byte[] font, int faceIndex)
        {
            ArgumentNullException.ThrowIfNull(font);

            if (!IsCollection(font))
                return font;

            var faceCount = GetFaceCount(font);

            if (faceIndex < 0 || faceIndex >= faceCount)
                throw new ArgumentOutOfRangeException(
                    nameof(faceIndex), faceIndex, $"The collection holds {faceCount} faces.");

            var directoryOffset = (int)ReadUInt32(font, 12 + (faceIndex * sizeof(uint)));
            var tableCount = ReadUInt16(font, directoryOffset + 4);

            // Where the copied tables start: the header, then one record each.
            var dataStart = Align(SfntHeaderLength + (tableCount * TableRecordLength));

            var tables = new List<(uint Tag, uint Checksum, int Offset, int Length)>(tableCount);
            var totalLength = dataStart;

            for (var i = 0; i < tableCount; i++)
            {
                var record = directoryOffset + SfntHeaderLength + (i * TableRecordLength);

                var tag = ReadUInt32(font, record);
                var checksum = ReadUInt32(font, record + 4);
                var offset = (int)ReadUInt32(font, record + 8);
                var length = (int)ReadUInt32(font, record + 12);

                if (offset < 0 || length < 0 || offset + length > font.Length)
                    throw new InvalidOperationException("A table in the font collection lies outside the file.");

                tables.Add((tag, checksum, offset, length));
                totalLength = Align(totalLength + length);
            }

            var face = new byte[totalLength];

            // The face's own sfnt header, copied whole: its version says
            // whether the outlines are TrueType or CFF, and the binary search
            // hints are computed from the table count, which does not change.
            font.AsSpan(directoryOffset, SfntHeaderLength).CopyTo(face);

            var writeAt = dataStart;

            for (var i = 0; i < tables.Count; i++)
            {
                var (tag, checksum, offset, length) = tables[i];
                var record = SfntHeaderLength + (i * TableRecordLength);

                WriteUInt32(face, record, tag);
                WriteUInt32(face, record + 4, checksum);
                WriteUInt32(face, record + 8, (uint)writeAt);
                WriteUInt32(face, record + 12, (uint)length);

                font.AsSpan(offset, length).CopyTo(face.AsSpan(writeAt));

                // Tables are padded to a four byte boundary; the gap stays
                // zero, which is what the padding is defined to be.
                writeAt = Align(writeAt + length);
            }

            // head.checkSumAdjustment is left as the collection wrote it. It
            // covers the whole file and is now wrong, but nothing that reads
            // these bytes verifies it, and rewriting it would mean check-
            // summing the face twice for no reader's benefit.
            return face;
        }

        private static int Align(int value) => (value + 3) & ~3;

        private static uint ReadUInt32(byte[] font, int offset)
        {
            if (offset < 0 || offset + sizeof(uint) > font.Length)
                throw new InvalidOperationException("The font collection is truncated.");

            return BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(offset));
        }

        private static int ReadUInt16(byte[] font, int offset)
        {
            if (offset < 0 || offset + sizeof(ushort) > font.Length)
                throw new InvalidOperationException("The font collection is truncated.");

            return BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(offset));
        }

        private static void WriteUInt32(byte[] font, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(offset), value);
    }
}
