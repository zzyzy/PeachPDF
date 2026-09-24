using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;

using PeachPDF.Fonts;
using PeachPDF.Fonts.OpenType;

using Xunit;

namespace PeachPDF.Tests.PdfSharpCoreTests
{
    /// <summary>
    /// Exercised against a real CJK font, because the point of subsetting a
    /// CFF is the font that is too big to embed whole, and because a font that
    /// simple to synthesise would not have the shapes that make this hard:
    /// thousands of glyphs, several font dictionaries, and local subroutines
    /// that sit nowhere near the private dictionary naming them.
    /// </summary>
    public class CffSubsetterTests
    {
        private const string NotoCjk = "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc";

        private static byte[]? LoadCff()
        {
            if (!File.Exists(NotoCjk))
                return null;

            var face = TrueTypeCollection.ExtractFace(File.ReadAllBytes(NotoCjk), 0);
            return GetTable(face, "CFF ");
        }

        [Fact]
        public void Subset_KeepsTheGlyphsAskedFor()
        {
            var cff = LoadCff();

            if (cff is null)
                return;

            int[] wanted = [0, 100, 5000, 40000];

            var subset = CffSubsetter.Subset(cff, wanted);

            Assert.NotNull(subset);

            var original = new CffTable(cff, 0);
            var rewritten = new CffTable(subset!, 0);

            Assert.True(rewritten.IsSupported);
            Assert.Equal(original.IsCidKeyed, rewritten.IsCidKeyed);

            // The glyph count cannot change: the PDF refers to glyphs by the
            // ids the layout was built with.
            Assert.Equal(original.CharStrings.Count, rewritten.CharStrings.Count);

            foreach (var gid in wanted)
                Assert.True(original.CharStrings[gid].SequenceEqual(rewritten.CharStrings[gid]));
        }

        [Fact]
        public void Subset_EmptiesTheGlyphsNotAskedFor()
        {
            var cff = LoadCff();

            if (cff is null)
                return;

            var subset = CffSubsetter.Subset(cff, [0, 100]);

            Assert.NotNull(subset);

            var rewritten = new CffTable(subset!, 0);

            // 0x0E is endchar: the glyph is still there and draws nothing.
            Assert.Equal([0x0E], rewritten.CharStrings[101].ToArray());
            Assert.Equal([0x0E], rewritten.CharStrings[40000].ToArray());
        }

        [Fact]
        public void Subset_IsDramaticallySmaller()
        {
            var cff = LoadCff();

            if (cff is null)
                return;

            var subset = CffSubsetter.Subset(cff, [0, 100, 5000]);

            Assert.NotNull(subset);

            // The font is about 15MB; a handful of glyphs out of it has no
            // business being more than a small fraction of that. The bound is
            // deliberately loose -- this guards against the whole face being
            // embedded again, not against a few kilobytes of drift.
            Assert.True(
                subset!.Length < cff.Length / 10,
                $"The subset is {subset.Length:N0} bytes of an original {cff.Length:N0}.");
        }

        [Fact]
        public void Subset_NotAFont_ReturnsNull()
        {
            Assert.Null(CffSubsetter.Subset([1, 2, 3], [0]));
        }

        private static byte[]? GetTable(byte[] font, string tag)
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

            return null;
        }
    }
}
