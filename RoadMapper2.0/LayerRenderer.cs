using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace RoadMapper
{
    // Renders marker points into an RGBA layer and encodes it as a PNG.
    //
    // Deliberately free of UnityEngine: this runs on a worker thread on a headless
    // dedicated server, where Texture2D/EncodeToPNG are either unsafe (main thread only)
    // or of doubtful availability. NomapPrinter never decodes the file server-side either;
    // it just base64s the bytes and ships them to clients, who LoadImage() it.
    internal static class LayerRenderer
    {
        // What a brush looks like, resolved on the main thread from config.
        public struct Brush
        {
            public byte R, G, B, A;
            public float WidthMetres;
        }

        // Geometry of NomapPrinter's map, which the layer must match pixel for pixel.
        // Mirrors NomapPrinter's WorldMapData.TextureSize / PixelSize:
        //   TextureSize = (int)(4096 * mapSizeMultiplier)
        //   PixelSize   = (int)(6    * mapSizeMultiplier)   (metres per pixel)
        public struct MapGeometry
        {
            public int TextureSize;
            public int PixelSize;

            public static MapGeometry FromMultiplier(float multiplier) => new MapGeometry
            {
                TextureSize = (int)(4096 * multiplier),
                PixelSize = (int)(6 * multiplier)
            };
        }

        // Reused between renders so we don't churn a 64 MB array through the GC every write.
        // Only one render runs at a time (the caller holds a lock), so sharing is safe.
        private static byte[] _pixels;

        // Paints every point and returns PNG bytes. Row 0 of the PNG is north.
        public static byte[] Render(List<RoadMapper.MarkerPoint> points, Dictionary<int, Brush> brushes, MapGeometry geo)
        {
            int size = geo.TextureSize;
            int byteCount = size * size * 4;

            if (_pixels == null || _pixels.Length != byteCount)
                _pixels = new byte[byteCount];
            else
                Array.Clear(_pixels, 0, _pixels.Length);

            foreach (RoadMapper.MarkerPoint p in points)
            {
                if (!brushes.TryGetValue(p.BrushId, out Brush brush))
                    continue;
                StampDisc(_pixels, size, geo.PixelSize, p.x, p.z, brush);
            }

            return PngEncoder.EncodeRgba(_pixels, size, size);
        }

        // World (x, z) -> continuous pixel coords in PNG space (origin top-left, north up).
        // NomapPrinter's pixel (col j, row-from-bottom i) covers world
        //   x in [(j - S/2) * P, (j - S/2 + 1) * P),  z in [(i - S/2) * P, (i - S/2 + 1) * P)
        // so col = x/P + S/2 and rowFromTop = S/2 - z/P (both continuous; pixel centres at +0.5).
        private static void StampDisc(byte[] pixels, int size, int pixelSize, float x, float z, Brush brush)
        {
            float cx = x / pixelSize + size / 2f;
            float cy = size / 2f - z / pixelSize;

            // Radius in pixels. Never smaller than half a pixel, so a narrow brush still marks
            // the pixel it lands in instead of vanishing between pixel centres.
            float r = Math.Max(0.5f, brush.WidthMetres / 2f / pixelSize);
            float r2 = r * r;

            int minX = Math.Max(0, (int)Math.Floor(cx - r));
            int maxX = Math.Min(size - 1, (int)Math.Ceiling(cx + r));
            int minY = Math.Max(0, (int)Math.Floor(cy - r));
            int maxY = Math.Min(size - 1, (int)Math.Ceiling(cy + r));

            bool painted = false;
            for (int py = minY; py <= maxY; py++)
            {
                float dy = py + 0.5f - cy;
                for (int px = minX; px <= maxX; px++)
                {
                    float dx = px + 0.5f - cx;
                    if (dx * dx + dy * dy > r2)
                        continue;
                    SetPixel(pixels, size, px, py, brush);
                    painted = true;
                }
            }

            // Tiny brush that fell between pixel centres: mark the containing pixel.
            if (!painted)
            {
                int px = (int)Math.Floor(cx), py = (int)Math.Floor(cy);
                if (px >= 0 && px < size && py >= 0 && py < size)
                    SetPixel(pixels, size, px, py, brush);
            }
        }

        // Later strokes simply replace earlier ones. With opaque brushes that's the natural
        // "last paint wins"; with translucent ones it avoids overlapping dots getting darker.
        private static void SetPixel(byte[] pixels, int size, int px, int py, Brush brush)
        {
            int i = (py * size + px) * 4;
            pixels[i] = brush.R;
            pixels[i + 1] = brush.G;
            pixels[i + 2] = brush.B;
            pixels[i + 3] = brush.A;
        }
    }

    // Minimal PNG writer: 8-bit RGBA, no interlace, filter 0 on every row, one IDAT.
    // Layers are mostly transparent, so deflate squashes them to a few KB-to-MB regardless.
    internal static class PngEncoder
    {
        private static readonly uint[] CrcTable = BuildCrcTable();

        public static byte[] EncodeRgba(byte[] rgba, int width, int height)
        {
            using (MemoryStream png = new MemoryStream())
            {
                png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

                byte[] ihdr = new byte[13];
                WriteBigEndian(ihdr, 0, (uint)width);
                WriteBigEndian(ihdr, 4, (uint)height);
                ihdr[8] = 8;   // bit depth
                ihdr[9] = 6;   // colour type: RGBA
                ihdr[10] = 0;  // compression
                ihdr[11] = 0;  // filter method
                ihdr[12] = 0;  // no interlace
                WriteChunk(png, "IHDR", ihdr);

                WriteChunk(png, "IDAT", Zlib(rgba, width, height));
                WriteChunk(png, "IEND", new byte[0]);

                return png.ToArray();
            }
        }

        // zlib stream = 2-byte header + raw deflate + Adler-32 of the uncompressed data.
        // .NET Framework's DeflateStream writes raw deflate only, so we add the wrapper.
        private static byte[] Zlib(byte[] rgba, int width, int height)
        {
            int stride = width * 4;
            uint a = 1, b = 0;
            byte[] filterByte = { 0 };

            using (MemoryStream output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x01);

                using (DeflateStream deflate = new DeflateStream(output, CompressionMode.Compress, leaveOpen: true))
                {
                    for (int y = 0; y < height; y++)
                    {
                        deflate.Write(filterByte, 0, 1);
                        deflate.Write(rgba, y * stride, stride);

                        // Adler-32 over the same bytes: the filter byte, then the row.
                        // Reduced in blocks of 5552 bytes (zlib's NMAX) so the sums can't overflow.
                        b = (b + a) % 65521; // filter byte is 0: a unchanged, b += a
                        int offset = y * stride;
                        int remaining = stride;
                        while (remaining > 0)
                        {
                            int block = Math.Min(remaining, 5552);
                            for (int k = 0; k < block; k++)
                            {
                                a += rgba[offset + k];
                                b += a;
                            }
                            a %= 65521;
                            b %= 65521;
                            offset += block;
                            remaining -= block;
                        }
                    }
                }

                uint adler = (b << 16) | a;
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);

                return output.ToArray();
            }
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            byte[] len = new byte[4];
            WriteBigEndian(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);

            byte[] typeBytes = { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(typeBytes, 0, 4);
            s.Write(data, 0, data.Length);

            uint crc = 0xFFFFFFFFu;
            crc = UpdateCrc(crc, typeBytes, 0, 4);
            crc = UpdateCrc(crc, data, 0, data.Length);
            crc ^= 0xFFFFFFFFu;

            byte[] crcBytes = new byte[4];
            WriteBigEndian(crcBytes, 0, crc);
            s.Write(crcBytes, 0, 4);
        }

        private static uint UpdateCrc(uint crc, byte[] buf, int offset, int count)
        {
            for (int i = offset; i < offset + count; i++)
                crc = CrcTable[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        private static uint[] BuildCrcTable()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static void WriteBigEndian(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }
    }
}
