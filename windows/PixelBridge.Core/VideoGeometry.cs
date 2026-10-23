namespace PixelBridge.Core;

/// <summary>NV12 geometry for the Windows camera and preview. Fits the complete image into an even 16:9 canvas.</summary>
public sealed class VideoGeometry
{
    private (int w, int h, int rotation, bool mirror) _key;
    private int[] _y = [], _uv = [];
    public byte[] Transform(byte[] source, int width, int height, int rotation, bool mirror)
    {
        if (width <= 0 || height <= 0 || (width | height) % 2 != 0 || source.Length != width * height * 3 / 2)
            throw new ArgumentException("Expected a tightly packed even-sized NV12 frame");
        rotation = ((rotation % 360) + 360) % 360;
        if (rotation % 90 != 0) throw new ArgumentException("Rotation must be a quarter turn");
        if (rotation == 0 && !mirror) return source;
        var key = (width, height, rotation, mirror);
        if (_key != key)
        {
            _key = key;
            int rw = rotation is 90 or 270 ? height : width, rh = rotation is 90 or 270 ? width : height;
            double scale = Math.Min((double)width / rw, (double)height / rh);
            int dw = Math.Max(2, (int)(rw * scale) / 2 * 2), dh = Math.Max(2, (int)(rh * scale) / 2 * 2);
            int ox = (width - dw) / 4 * 2, oy = (height - dh) / 4 * 2;
            int Map(int x, int y)
            {
                if (x < ox || y < oy || x >= ox + dw || y >= oy + dh) return -1;
                int rx = Math.Min(rw - 1, (x - ox) * rw / dw), ry = Math.Min(rh - 1, (y - oy) * rh / dh);
                if (mirror) rx = rw - 1 - rx;
                var (sx, sy) = rotation switch { 90 => (ry, height - 1 - rx), 180 => (width - 1 - rx, height - 1 - ry),
                    270 => (width - 1 - ry, rx), _ => (rx, ry) };
                return sy * width + sx;
            }
            _y = new int[width * height]; _uv = new int[width * height / 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) _y[y * width + x] = Map(x, y);
            for (int y = 0; y < height; y += 2) for (int x = 0; x < width; x += 2)
            {
                int at = Map(x, y);
                _uv[y / 2 * (width / 2) + x / 2] = at < 0 ? -1 : width * height + (at / width / 2) * width + (at % width / 2) * 2;
            }
        }
        var output = new byte[source.Length];
        for (int i = 0; i < _y.Length; i++) output[i] = _y[i] < 0 ? (byte)16 : source[_y[i]];
        int uvStart = width * height;
        for (int i = 0; i < _uv.Length; i++)
        {
            int at = _uv[i]; output[uvStart + i * 2] = at < 0 ? (byte)128 : source[at];
            output[uvStart + i * 2 + 1] = at < 0 ? (byte)128 : source[at + 1];
        }
        return output;
    }
}
