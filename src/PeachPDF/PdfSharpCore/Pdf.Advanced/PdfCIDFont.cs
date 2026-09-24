#region PDFsharp - A .NET library for processing PDF
//
// Authors:
//   Stefan Lange
//
// Copyright (c) 2005-2016 empira Software GmbH, Cologne Area (Germany)
//
// http://www.PeachPDF.PdfSharpCore.com
// http://sourceforge.net/projects/pdfsharp
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included
// in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
#endregion

#nullable disable warnings

using System;
using System.Collections.Generic;

using PeachPDF.PdfSharpCore.Drawing;
using PeachPDF.Fonts.OpenType;
using PeachPDF.PdfSharpCore.Pdf.Filters;

namespace PeachPDF.PdfSharpCore.Pdf.Advanced
{
    /// <summary>
    /// Represents a CIDFont dictionary.
    /// </summary>
    internal class PdfCIDFont : PdfFont
    {
        public PdfCIDFont(PdfDocument document)
            : base(document)
        { }

        public PdfCIDFont(PdfDocument document, PdfFontDescriptor fontDescriptor, XFont font)
            : base(document)
        {
            Elements.SetName(Keys.Type, "/Font");
            Elements.SetName(Keys.Subtype, "/CIDFontType2");
            PdfDictionary cid = new PdfDictionary();
            cid.Elements.SetString("/Ordering", "Identity");
            cid.Elements.SetString("/Registry", "Adobe");
            cid.Elements.SetInteger("/Supplement", 0);
            Elements.SetValue(Keys.CIDSystemInfo, cid);

            // Always Identity, and always written explicitly (not left to the /Identity default) -
            // required for PDF/A (ISO 19005-2 §6.2.11.3 / ISO 32000-1 Table 117), which does not allow
            // relying on a key's spec-default value being absent. This is also the semantically correct
            // value, not just the spec-legal default: OpenTypeFontface.CreateFontSubSet (the only place
            // this class's embedded font data comes from) never renumbers glyph indices when building a
            // subset - it keeps every original glyph's slot (zeroing out the ones that go unused), so a
            // CID written into the content stream always equals its own glyph index in the embedded font.
            Elements.SetName(Keys.CIDToGIDMap, "/Identity");

            FontDescriptor = fontDescriptor;
            // ReSharper disable once DoNotCallOverridableMethodsInConstructor
            Owner._irefTable.Add(fontDescriptor);
            Elements[Keys.FontDescriptor] = fontDescriptor.Reference;

            FontEncoding = font.PdfOptions.FontEncoding;
        }

        public string BaseFont
        {
            get { return Elements.GetName(Keys.BaseFont); }
            set { Elements.SetName(Keys.BaseFont, value); }
        }

        /// <summary>
        /// Rewrites the font's CFF table to hold only the glyphs this document
        /// draws, or returns null when it cannot be rewritten safely.
        /// </summary>
        private static byte[]? TrySubsetCff(OpenTypeFontface fontFace, IEnumerable<int> usedGlyphs)
        {
            if (!fontFace.TableDictionary.TryGetValue("CFF ", out var entry))
                return null;

            // Only a CID keyed CFF may be embedded on its own, as
            // CIDFontType0C. A plain CFF in a CIDFont has to stay inside the
            // OpenType file that carries the mapping, so it is left whole.
            if (fontFace.cff is null || !fontFace.cff.IsCidKeyed)
                return null;

            var source = fontFace.FontSource.Bytes;

            if (entry.Offset < 0 || entry.Length <= 0 || entry.Offset + entry.Length > source.Length)
                return null;

            var cff = new byte[entry.Length];
            Array.Copy(source, entry.Offset, cff, 0, entry.Length);

            return CffSubsetter.Subset(cff, [.. usedGlyphs]);
        }

        /// <summary>
        /// Prepares the object to get saved.
        /// </summary>
        internal override void PrepareForSave()
        {
            base.PrepareForSave();

#if DEBUG_
            if (FontDescriptor._descriptor.FontFace.loca == null)
            {
                GetType();
            }
#endif
            // CID fonts must be always embedded. PDFsharp embedds automatically a subset.
            var fontFace = FontDescriptor._descriptor.FontFace;
            bool isCff = fontFace.loca == null;

            byte[] fontData;
            string fontFileSubtype = "/OpenType";

            if (isCff)
            {
                // A CFF font keeps its outlines in the CFF table, so the glyph
                // subsetting below - which rewrites glyf and loca - cannot
                // touch it. Without this the whole face goes into the PDF: a
                // CJK font puts tens of megabytes into a document that prints
                // a name.
                byte[]? subsetCff = TrySubsetCff(fontFace, _cmapInfo.GlyphIndices.Keys);

                if (subsetCff is not null)
                {
                    // The bare CFF rather than the OpenType file that held it:
                    // it is what a CIDFontType0 descendant is defined to carry,
                    // and it leaves behind the layout and mapping tables, which
                    // in a CJK font are megabytes a PDF viewer never reads.
                    fontData = subsetCff;
                    fontFileSubtype = "/CIDFontType0C";
                }
                else
                {
                    fontData = fontFace.FontSource.Bytes;
                }
            }
            else
            {
                fontData = fontFace.CreateFontSubSet(_cmapInfo.GlyphIndices, true).FontSource.Bytes;
            }

            PdfDictionary fontStream = new PdfDictionary(Owner);
            Owner.Internals.AddObject(fontStream);
            if (isCff)
            {
                FontDescriptor.Elements[PdfFontDescriptor.Keys.FontFile3] = fontStream.Reference;
                fontStream.Elements.SetName("/Subtype", fontFileSubtype);
            }
            else
            {
                FontDescriptor.Elements[PdfFontDescriptor.Keys.FontFile2] = fontStream.Reference;
                fontStream.Elements["/Length1"] = new PdfInteger(fontData.Length);
            }
            if (!Owner.Options.NoCompression)
            {
                fontData = Filtering.FlateDecode.Encode(fontData, _document.Options.FlateEncodeMode);
                fontStream.Elements["/Filter"] = new PdfName("/FlateDecode");
            }
            fontStream.Elements["/Length"] = new PdfInteger(fontData.Length);
            fontStream.CreateStream(fontData);
        }

        /// <summary>
        /// Predefined keys of this dictionary.
        /// </summary>
        internal new sealed class Keys : PdfFont.Keys
        {
            /// <summary>
            /// (Required) The type of PDF object that this dictionary describes;
            /// must be Font for a CIDFont dictionary.
            /// </summary>
            [KeyInfo(KeyType.Name | KeyType.Required, FixedValue = "Font")]
            public new const string Type = "/Type";

            /// <summary>
            /// (Required) The type of CIDFont; CIDFontType0 or CIDFontType2.
            /// </summary>
            [KeyInfo(KeyType.Name | KeyType.Required)]
            public new const string Subtype = "/Subtype";

            /// <summary>
            /// (Required) The PostScript name of the CIDFont. For Type 0 CIDFonts, this
            /// is usually the value of the CIDFontName entry in the CIDFont program. For
            /// Type 2 CIDFonts, it is derived the same way as for a simple TrueType font;
            /// In either case, the name can have a subset prefix if appropriate.
            /// </summary>
            [KeyInfo(KeyType.Name | KeyType.Required)]
            public new const string BaseFont = "/BaseFont";

            /// <summary>
            /// (Required) A dictionary containing entries that define the character collection
            /// of the CIDFont.
            /// </summary>
            [KeyInfo(KeyType.Dictionary | KeyType.Required)]
            public const string CIDSystemInfo = "/CIDSystemInfo";

            /// <summary>
            /// (Required; must be an indirect reference) A font descriptor describing the
            /// CIDFont’s default metrics other than its glyph widths.
            /// </summary>
            [KeyInfo(KeyType.Dictionary | KeyType.MustBeIndirect, typeof(PdfFontDescriptor))]
            public new const string FontDescriptor = "/FontDescriptor";

            /// <summary>
            /// (Optional) The default width for glyphs in the CIDFont.
            /// Default value: 1000.
            /// </summary>
            [KeyInfo(KeyType.Integer)]
            public const string DW = "/DW";

            /// <summary>
            /// (Optional) A description of the widths for the glyphs in the CIDFont. The
            /// array’s elements have a variable format that can specify individual widths
            /// for consecutive CIDs or one width for a range of CIDs.
            /// Default value: none (the DW value is used for all glyphs).
            /// </summary>
            [KeyInfo(KeyType.Array, typeof(PdfArray))]
            public const string W = "/W";

            /// <summary>
            /// (Optional; applies only to CIDFonts used for vertical writing) An array of two
            /// numbers specifying the default metrics for vertical writing.
            /// Default value: [880 −1000].
            /// </summary>
            [KeyInfo(KeyType.Array)]
            public const string DW2 = "/DW2";

            /// <summary>
            /// (Optional; applies only to CIDFonts used for vertical writing) A description
            /// of the metrics for vertical writing for the glyphs in the CIDFont.
            /// Default value: none (the DW2 value is used for all glyphs).
            /// </summary>
            [KeyInfo(KeyType.Array, typeof(PdfArray))]
            public const string W2 = "/W2";

            /// <summary>
            /// (Optional; Type 2 CIDFonts only) A specification of the mapping from CIDs
            /// to glyph indices. If the value is a stream, the bytes in the stream contain the
            /// mapping from CIDs to glyph indices: the glyph index for a particular CID
            /// value c is a 2-byte value stored in bytes 2 × c and 2 × c + 1, where the first
            /// byte is the high-order byte. If the value of CIDToGIDMap is a name, it must
            /// be Identity, indicating that the mapping between CIDs and glyph indices is
            /// the identity mapping.
            /// Default value: Identity.
            /// This entry may appear only in a Type 2 CIDFont whose associated True-Type font 
            /// program is embedded in the PDF file.
            /// </summary>
            [KeyInfo(KeyType.Dictionary | KeyType.StreamOrName)]
            public const string CIDToGIDMap = "/CIDToGIDMap";

            /// <summary>
            /// Gets the KeysMeta for these keys.
            /// </summary>
            internal static DictionaryMeta Meta
            {
                get { return _meta ?? (_meta = CreateMeta(typeof(Keys))); }
            }
            static DictionaryMeta _meta = null!;
        }

        /// <summary>
        /// Gets the KeysMeta of this dictionary type.
        /// </summary>
        internal override DictionaryMeta Meta
        {
            get { return Keys.Meta; }
        }
    }
}
