using System.Buffers.Binary;

namespace WallTrek.Services.Providers;

public static class ProviderImageValidator
{
    private const long Limit = 24L * 1024 * 1024;
    public static (string Mime, int Width, int Height) Check(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || data.Length > Limit)
            throw new ProviderException(ProviderFailure.InvalidImage);
        string mime;
        int width = 0, height = 0;
        if (data.Slice(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (!data.Slice(12, 4).SequenceEqual("IHDR"u8))
                throw new ProviderException(ProviderFailure.InvalidImage);
            width = BinaryPrimitives.ReadInt32BigEndian(data.Slice(16, 4));
            height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(20, 4));
            mime = "image/png";
        }
        else if (data[0] == 0xff && data[1] == 0xd8)
        {
            mime = "image/jpeg";
            var at = 2;
            while (at + 4 < data.Length)
            {
                if (data[at] != 0xff) break;
                var marker = data[at + 1];
                if (marker is 0xd9 or 0xda) break;
                var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at + 2, 2));
                if (length < 2 || length > data.Length - at - 2) break;
                if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3)
                {
                    if (length < 7) break;
                    height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at + 5, 2));
                    width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at + 7, 2));
                    break;
                }
                at += length + 2;
            }
        }
        else if (data.Slice(0, 4).SequenceEqual("RIFF"u8) &&
                 data.Slice(8, 4).SequenceEqual("WEBP"u8) && data.Length >= 30)
        {
            mime = "image/webp";
            var fourcc = data.Slice(12, 4);
            if (fourcc.SequenceEqual("VP8X"u8))
            {
                width = 1 + data[24] + (data[25] << 8) + (data[26] << 16);
                height = 1 + data[27] + (data[28] << 8) + (data[29] << 16);
            }
            else if (fourcc.SequenceEqual("VP8 "u8) && data.Length >= 30)
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2)) & 0x3fff;
                height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2)) & 0x3fff;
            }
            else throw new ProviderException(ProviderFailure.InvalidImage);
        }
        else throw new ProviderException(ProviderFailure.InvalidImage);

        if (width is < 1 or > 8192 || height is < 1 or > 8192 ||
            (long)width * height > 36_000_000)
            throw new ProviderException(ProviderFailure.InvalidImage);

#if WINDOWS
        // A header can be fabricated. Decode on Windows before persistence/wallpaper updates.
        if (mime != "image/webp")
        {
            try
            {
                using var stream = new MemoryStream(data.ToArray(), writable: false);
                using var image = System.Drawing.Image.FromStream(stream,
                    useEmbeddedColorManagement: false, validateImageData: true);
                if (image.Width != width || image.Height != height)
                    throw new ProviderException(ProviderFailure.InvalidImage);
            }
            catch (ProviderException) { throw; }
            catch (Exception) { throw new ProviderException(ProviderFailure.InvalidImage); }
        }
#endif
        return (mime, width, height);
    }
}
