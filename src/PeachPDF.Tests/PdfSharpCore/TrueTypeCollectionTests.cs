using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

using PeachPDF.Fonts;

using Xunit;

namespace PeachPDF.Tests.PdfSharpCoreTests
{
    public class TrueTypeCollectionTests
    {
        [Fact]
        public void IsCollection_PlainFont_IsFalse()
        {
            var font = BuildFont(tag: 0x00010000, tables: [("cmap", [1, 2, 3])]);

            Assert.False(TrueTypeCollection.IsCollection(font));
            Assert.Equal(1, TrueTypeCollection.GetFaceCount(font));
        }

        [Fact]
        public void ExtractFace_PlainFont_ReturnsTheSameBytes()
        {
            var font = BuildFont(tag: 0x00010000, tables: [("cmap", [1, 2, 3])]);

            Assert.Same(font, TrueTypeCollection.ExtractFace(font, 0));
        }

        [Fact]
        public void GetFaceCount_Collection_CountsEveryFace()
        {
            var collection = BuildCollection(
                [("cmap", [1]), ("name", [2])],
                [("cmap", [3]), ("name", [4])],
                [("cmap", [5]), ("name", [6])]);

            Assert.True(TrueTypeCollection.IsCollection(collection));
            Assert.Equal(3, TrueTypeCollection.GetFaceCount(collection));
        }

        [Theory]
        [InlineData(0, 11)]
        [InlineData(1, 22)]
        public void ExtractFace_Collection_LiftsThatFaceOut(int faceIndex, byte expected)
        {
            // Each face carries a table whose single byte says which face it
            // belongs to, so the extracted font can be told apart.
            var collection = BuildCollection(
                [("cmap", [11]), ("name", [11])],
                [("cmap", [22]), ("name", [22])]);

            var face = TrueTypeCollection.ExtractFace(collection, faceIndex);

            Assert.False(TrueTypeCollection.IsCollection(face));
            Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(face));
            Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(face.AsSpan(4)));
            Assert.Equal(expected, ReadTable(face, "cmap")[0]);
            Assert.Equal(expected, ReadTable(face, "name")[0]);
        }

        [Fact]
        public void ExtractFace_BeyondTheLastFace_Throws()
        {
            var collection = BuildCollection([("cmap", [1])], [("cmap", [2])]);

            Assert.Throws<ArgumentOutOfRangeException>(() => TrueTypeCollection.ExtractFace(collection, 2));
        }

        [Fact]
        public void ExtractFace_TableOutsideTheFile_Throws()
        {
            var collection = BuildCollection([("cmap", [1])]);

            // Point the one table's offset past the end.
            var directoryOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(collection.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(collection.AsSpan(directoryOffset + 12 + 8), 0xFFFF);

            Assert.Throws<InvalidOperationException>(() => TrueTypeCollection.ExtractFace(collection, 0));
        }

        private static byte[] ReadTable(byte[] font, string tag)
        {
            var tableCount = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
            var wanted = BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(tag));

            for (var i = 0; i < tableCount; i++)
            {
                var record = 12 + (i * 16);

                if (BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record)) != wanted)
                    continue;

                var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 12));

                return font.AsSpan(offset, length).ToArray();
            }

            throw new InvalidOperationException($"No {tag} table.");
        }

        private static byte[] BuildFont(uint tag, (string Tag, byte[] Data)[] tables)
        {
            using var stream = new MemoryStream();
            WriteFace(stream, tag, tables, baseOffset: 0);
            return stream.ToArray();
        }

        private static byte[] BuildCollection(params (string Tag, byte[] Data)[][] faces)
        {
            var headerLength = 12 + (faces.Length * 4);
            using var stream = new MemoryStream();

            Span<byte> buffer = stackalloc byte[4];

            BinaryPrimitives.WriteUInt32BigEndian(buffer, 0x74746366); // 'ttcf'
            stream.Write(buffer);
            BinaryPrimitives.WriteUInt32BigEndian(buffer, 0x00010000); // version 1.0
            stream.Write(buffer);
            BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)faces.Length);
            stream.Write(buffer);

            // The offsets are only known once each face has been laid out, so
            // they are written into the reserved space afterwards.
            stream.Write(new byte[faces.Length * 4]);

            var offsets = new int[faces.Length];

            for (var i = 0; i < faces.Length; i++)
            {
                offsets[i] = (int)stream.Position;
                WriteFace(stream, 0x00010000, faces[i], baseOffset: offsets[i]);
            }

            var bytes = stream.ToArray();

            for (var i = 0; i < faces.Length; i++)
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12 + (i * 4)), (uint)offsets[i]);

            Assert.Equal(headerLength, offsets[0]);

            return bytes;
        }

        private static void WriteFace(
            Stream stream, uint sfntVersion, (string Tag, byte[] Data)[] tables, int baseOffset)
        {
            Span<byte> buffer = stackalloc byte[4];

            BinaryPrimitives.WriteUInt32BigEndian(buffer, sfntVersion);
            stream.Write(buffer);
            BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)tables.Length);
            stream.Write(buffer[..2]);
            stream.Write(new byte[6]); // searchRange, entrySelector, rangeShift

            var dataAt = baseOffset + 12 + (tables.Length * 16);

            foreach (var (tag, data) in tables)
            {
                stream.Write(System.Text.Encoding.ASCII.GetBytes(tag));
                stream.Write(new byte[4]); // checksum, not verified by the reader
                BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)dataAt);
                stream.Write(buffer);
                BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)data.Length);
                stream.Write(buffer);

                dataAt += Align(data.Length);
            }

            foreach (var (_, data) in tables)
            {
                stream.Write(data);
                stream.Write(new byte[Align(data.Length) - data.Length]);
            }
        }

        private static int Align(int value) => (value + 3) & ~3;
    }
}
