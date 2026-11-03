using System.Buffers.Binary;

// Rosella cursor lab: writes the Rosella-Ring Xcursor theme. The ordinary
// pointer is a hollow ring — black outside, white inside, a grey dot on the
// exact click point — so it reads over light and dark windows without
// looking like any other desktop's arrow. The shapes that exist to say
// "drag this edge" or "type here" keep that palette but carry their
// conventional glyph: a double-headed arrow, a four-way arrow or an
// I-beam. Everything else (links, waits, forbidden actions) stays a ring,
// so the desktop keeps one identity.
//
// No external tools: the Xcursor container, the raster and the preview
// PNGs are all produced here.
//
//   dotnet run --project tools/Rosella.CursorLab -- [output-theme-dir]
//
// Verify the result with the independent libXcursor loader:
//
//   cc -O2 -o /tmp/cursor-check tools/Rosella.CursorLab/check.c -ldl
//   /tmp/cursor-check ~/.local/share/icons/Rosella-Ring

var root = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "icons", "Rosella-Ring");

// 3x of a nominal size must exist natively, or the shake-to-find
// enlargement has to ask for the nearest image instead of the exact one:
// 24 -> 72, 32 -> 96, 40 -> 120, 48 -> 144 (the scale-2 case).
int[] sizes = [24, 28, 32, 40, 48, 64, 72, 96, 120, 144, 192];

// The distinct art. Every name below resolves to one of these files: a
// cursor file costs its whole image ladder, so aliases are symlinks.
var shapes = new (string File, Shape Kind)[]
{
    ("left_ptr", Shape.Ring),
    ("ew-resize", Shape.Horizontal),
    ("ns-resize", Shape.Vertical),
    ("nwse-resize", Shape.DiagonalDown),
    ("nesw-resize", Shape.DiagonalUp),
    ("fleur", Shape.Move),
    ("xterm", Shape.Text),
    ("vertical-text", Shape.TextSideways),
};

// The names clients ask for, and the art each one gets. X11's legacy names
// (sb_h_double_arrow, top_side) and the CSS names (ew-resize, col-resize)
// are both listed: GTK uses the former, Chromium and Qt the latter, and a
// name missing from the theme falls back to the inherited theme's arrow —
// exactly the arrow-versus-ring mix this theme exists to avoid.
var aliases = new (string Name, string Target)[]
{
    // The pointer itself, and everything whose shape is not a direction.
    ("left_ptr", "left_ptr"),
    ("default", "left_ptr"),
    ("top_left_arrow", "left_ptr"),
    ("arrow", "left_ptr"),
    ("right_ptr", "left_ptr"),
    ("hand1", "left_ptr"),
    ("hand2", "left_ptr"),
    ("pointer", "left_ptr"),
    ("pointing_hand", "left_ptr"),
    ("openhand", "left_ptr"),
    ("closedhand", "left_ptr"),
    ("grab", "left_ptr"),
    ("grabbing", "left_ptr"),
    ("watch", "left_ptr"),
    ("wait", "left_ptr"),
    ("progress", "left_ptr"),
    ("left_ptr_watch", "left_ptr"),
    ("cross", "left_ptr"),
    ("crosshair", "left_ptr"),
    ("cross_reverse", "left_ptr"),
    ("tcross", "left_ptr"),
    ("cell", "left_ptr"),
    ("copy", "left_ptr"),
    ("alias", "left_ptr"),
    ("question_arrow", "left_ptr"),
    ("help", "left_ptr"),
    ("context-menu", "left_ptr"),
    ("no-drop", "left_ptr"),
    ("not-allowed", "left_ptr"),
    ("zoom-in", "left_ptr"),
    ("zoom-out", "left_ptr"),
    ("dnd-move", "left_ptr"),
    ("dnd-copy", "left_ptr"),
    ("dnd-none", "left_ptr"),
    ("dnd-link", "left_ptr"),
    ("dnd-ask", "left_ptr"),

    // Drag an edge or a splitter left/right, or resize a column.
    ("ew-resize", "ew-resize"),
    ("col-resize", "ew-resize"),
    ("sb_h_double_arrow", "ew-resize"),
    ("h_double_arrow", "ew-resize"),
    ("e-resize", "ew-resize"),
    ("w-resize", "ew-resize"),
    ("left_side", "ew-resize"),
    ("right_side", "ew-resize"),

    // Drag up/down, or resize a row.
    ("ns-resize", "ns-resize"),
    ("row-resize", "ns-resize"),
    ("sb_v_double_arrow", "ns-resize"),
    ("v_double_arrow", "ns-resize"),
    ("n-resize", "ns-resize"),
    ("s-resize", "ns-resize"),
    ("top_side", "ns-resize"),
    ("bottom_side", "ns-resize"),

    // Corners: top-left and bottom-right pull along "\", the other pair "/".
    ("nwse-resize", "nwse-resize"),
    ("nw-resize", "nwse-resize"),
    ("se-resize", "nwse-resize"),
    ("top_left_corner", "nwse-resize"),
    ("bottom_right_corner", "nwse-resize"),
    ("nesw-resize", "nesw-resize"),
    ("ne-resize", "nesw-resize"),
    ("sw-resize", "nesw-resize"),
    ("top_right_corner", "nesw-resize"),
    ("bottom_left_corner", "nesw-resize"),

    // Move the whole thing, in any direction.
    ("fleur", "fleur"),
    ("move", "fleur"),
    ("all-scroll", "fleur"),

    // Type here. The sideway beam is for vertical text (Chinese, Japanese).
    ("text", "xterm"),
    ("xterm", "xterm"),
    ("vertical-text", "vertical-text"),
};

// The tool deletes what it does not recognise, so it must only ever run
// over its own theme: a mistyped path must not be able to eat Adwaita.
var marker = Path.Combine(root, "index.theme");
if (Directory.Exists(root)
    && Directory.EnumerateFileSystemEntries(root).Any()
    && (!File.Exists(marker) || !File.ReadAllText(marker).Contains("Name=Rosella Ring", StringComparison.Ordinal)))
{
    Console.Error.WriteLine($"rosella: {root} holds something other than the Rosella Ring theme; pick an empty directory");
    return 1;
}

var cursors = Path.Combine(root, "cursors");
Directory.CreateDirectory(cursors);
var previews = Path.Combine(root, "previews");
Directory.CreateDirectory(previews);

// Previews are regenerated wholesale, and earlier revisions dumped raw RGBA
// at the theme root.
foreach (var path in Directory.EnumerateFiles(previews))
{
    File.Delete(path);
}

foreach (var legacy in new[] { "preview.png", "preview-96.rgba" })
{
    var path = Path.Combine(root, legacy);
    if (File.Exists(path))
    {
        File.Delete(path);
    }
}

foreach (var (file, kind) in shapes)
{
    var images = sizes.Select(size => (Size: size, Pixels: Render(size, kind))).ToArray();

    // A previous run left these names as symlinks to left_ptr; writing
    // through one would overwrite the link's target with this shape's art
    // and leave every resize cursor showing the last shape written.
    var path = Path.Combine(cursors, file);
    if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
    {
        File.Delete(path);
    }

    File.WriteAllBytes(path, Encode(images));

    // 48 px is 2x the usual 24 px pointer, the size a scale-2 output asks
    // for: big enough to inspect, small enough to still judge legibility.
    var preview = images.First(image => image.Size == 48);
    WritePng(Path.Combine(previews, $"{file}.png"), preview.Size, preview.Pixels);

    if (kind is not Shape.Ring)
    {
        PrintAscii(kind, images[0].Size, images[0].Pixels);
    }
}

foreach (var (name, target) in aliases)
{
    // The eight shape files are already written; only the other names are
    // links. Deleting the file that carries its own name would leave a
    // dangling symlink where the art should be.
    if (name == target)
    {
        continue;
    }

    var link = Path.Combine(cursors, name);
    if (File.Exists(link) || new FileInfo(link).LinkTarget is not null)
    {
        File.Delete(link);
    }

    File.CreateSymbolicLink(link, target);
}

// Anything an earlier version of this theme wrote and the current lists no
// longer name would otherwise linger as a stale ring.
var known = shapes.Select(shape => shape.File)
    .Concat(aliases.Select(alias => alias.Name))
    .ToHashSet(StringComparer.Ordinal);
foreach (var path in Directory.EnumerateFiles(cursors))
{
    var name = Path.GetFileName(path);
    if (!known.Contains(name))
    {
        File.Delete(path);
    }
}

File.WriteAllText(Path.Combine(root, "index.theme"),
    $"""
    [Icon Theme]
    Name=Rosella Ring
    Comment=Hollow ring pointer with directional resize and text shapes
    Inherits=Adwaita

    """);

Console.WriteLine($"wrote {root}: {sizes.Length} sizes, {shapes.Length} shapes, {aliases.Length} names");
return 0;

static uint[] Render(int size, Shape shape)
{
    var pixels = shape == Shape.Ring ? RenderRing(size) : RenderGlyph(size, shape);

    // The click point: whole pixels around the hotspot, so the dot is crisp
    // at every nominal size instead of antialiasing away (1 px at 24). A
    // neutral grey reads on both light and dark backgrounds, and on the
    // white fill of a glyph as well as in the ring's hollow centre.
    var side = Math.Max(1, (int)Math.Round(size / 24.0));
    var start = (size / 2) - (side / 2);
    for (var y = start; y < start + side && y < size; y++)
    {
        for (var x = start; x < start + side && x < size; x++)
        {
            pixels[(y * size) + x] = 0xFF808080u;
        }
    }

    return pixels;
}

static uint[] RenderRing(int size)
{
    // An annulus of outer radius 0.40 and width 0.12 of the nominal size,
    // split at its midline into a black outer half and a white inner half
    // so the pointer reads on light and dark backgrounds alike.
    const int Samples = 8;
    var pixels = new uint[size * size];
    var centre = size / 2.0;
    var outer = size * 0.40;
    var thickness = Math.Max(2.6, size * 0.12);
    var inner = outer - thickness;
    var mid = (outer + inner) / 2.0;
    var samples = Samples * Samples;

    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            var white = 0;
            var covered = 0;
            for (var sy = 0; sy < Samples; sy++)
            {
                for (var sx = 0; sx < Samples; sx++)
                {
                    var px = (x + ((sx + 0.5) / Samples)) - centre;
                    var py = (y + ((sy + 0.5) / Samples)) - centre;
                    var distance = Math.Sqrt((px * px) + (py * py));
                    if (distance < inner || distance > outer)
                    {
                        continue;
                    }

                    covered += 255;
                    white += distance > mid ? 0 : 255;
                }
            }

            var alpha = (byte)(covered / samples);
            var value = (byte)(white / samples);
            pixels[(y * size) + x] = (uint)((alpha << 24) | (value << 16) | (value << 8) | value);
        }
    }

    return pixels;
}

static uint[] RenderGlyph(int size, Shape shape)
{
    // The glyph is drawn twice: once grown by the outline width in black,
    // then at its true size in white. That is the black-outline-over-white
    // body of every classic X11 cursor, and it keeps the glyph legible on
    // any window colour without a second theme.
    const int Samples = 8;
    var pixels = new uint[size * size];
    var centre = size / 2.0;
    var outline = Math.Max(1.0, size * 0.050);
    var samples = Samples * Samples;

    for (var y = 0; y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            var black = 0;
            var white = 0;
            for (var sy = 0; sy < Samples; sy++)
            {
                for (var sx = 0; sx < Samples; sx++)
                {
                    var dx = (x + ((sx + 0.5) / Samples)) - centre;
                    var dy = (y + ((sy + 0.5) / Samples)) - centre;
                    if (Inside(shape, dx, dy, size, expand: 0))
                    {
                        white++;
                        black++;
                    }
                    else if (Inside(shape, dx, dy, size, outline))
                    {
                        black++;
                    }
                }
            }

            // Source-over in premultiplied space: white over black. Black
            // contributes no colour, so the premultiplied channels are just
            // the white coverage, and the alpha is the union of both.
            var ab = black / (double)samples;
            var aw = white / (double)samples;
            var alpha = (byte)Math.Round(255 * ((aw + (ab * (1 - aw)))));
            var value = (byte)Math.Round(255 * aw);
            pixels[(y * size) + x] = (uint)((alpha << 24) | (value << 16) | (value << 8) | value);
        }
    }

    return pixels;
}

/// <summary>Whether a point lies in the glyph, grown by <paramref name="expand"/>.</summary>
static bool Inside(Shape shape, double dx, double dy, int size, double expand) => shape switch
{
    Shape.Horizontal => Arrow(dx, dy, size, expand),
    Shape.Vertical => Arrow(dy, dx, size, expand),

    // The diagonals are the same arrow rotated 45°. Rotation keeps its
    // length but its extent along each axis shrinks by √2, so the art is
    // drawn a little longer and keeps the same width: every resize cursor
    // then has the same visual weight.
    Shape.DiagonalDown => Arrow((dx + dy) * 0.7071, (dy - dx) * 0.7071, size, expand, 1.25),
    Shape.DiagonalUp => Arrow((dx - dy) * 0.7071, (dx + dy) * 0.7071, size, expand, 1.25),

    Shape.Move => Arrow(dx, dy, size, expand) || Arrow(dy, dx, size, expand),

    Shape.Text => IBeam(dy, dx, size, expand),
    Shape.TextSideways => IBeam(dx, dy, size, expand),
    _ => false,
};

/// <summary>
/// A horizontal double-headed arrow: shaft between two triangular heads.
/// <paramref name="length"/> stretches only the long axis, which is how the
/// diagonal shapes keep their reach after the 45° rotation.
/// </summary>
static bool Arrow(double u, double v, double size, double expand, double length = 1.0)
{
    var halfLength = (size * 0.37 * length) + expand;
    var shaft = (size * 0.030) + expand;
    var headLength = (size * 0.19 * length) + expand;
    var headHalf = (size * 0.115) + expand;
    var along = Math.Abs(u);
    if (along > halfLength)
    {
        return false;
    }

    // The shaft stops where the heads begin: running it to the tip would
    // leave a blunt end, and the point is what makes the arrow read.
    var body = halfLength - headLength;
    if (along <= body)
    {
        return Math.Abs(v) <= shaft;
    }

    return Math.Abs(v) <= headHalf * (halfLength - along) / headLength;
}

/// <summary>An I-beam: a stem with a serif at each end.</summary>
static bool IBeam(double u, double v, double size, double expand)
{
    var halfLength = (size * 0.36) + expand;
    var stem = (size * 0.045) + expand;
    var serifHalf = (size * 0.19) + expand;
    var serif = (size * 0.08) + expand;
    if (Math.Abs(u) > halfLength)
    {
        return false;
    }

    if (Math.Abs(v) <= stem)
    {
        return true;
    }

    return Math.Abs(u) >= halfLength - serif && Math.Abs(v) <= serifHalf;
}

/// <summary>An ASCII dump of one image, for checking a shape without a viewer.</summary>
static void PrintAscii(Shape shape, int size, uint[] pixels)
{
    Console.WriteLine($"--- {shape} ({size}px) ---");
    for (var y = 0; y < size; y++)
    {
        var line = new char[size];
        for (var x = 0; x < size; x++)
        {
            var pixel = pixels[(y * size) + x];
            var alpha = (pixel >> 24) & 0xFF;
            var value = (pixel >> 16) & 0xFF;
            line[x] = alpha == 0 ? ' '
                : alpha < 128 ? '.'
                : value >= 128 ? '+'
                : '#';
        }

        Console.WriteLine(line);
    }
}

static byte[] Encode((int Size, uint[] Pixels)[] images)
{
    const int FileHeader = 16;
    const int TocEntry = 12;
    const int ImageHeader = 36;
    const uint ImageType = 0xfffd0002;

    var count = images.Length;
    var offsets = new int[count];
    var position = FileHeader + (TocEntry * count);
    for (var i = 0; i < count; i++)
    {
        offsets[i] = position;
        position += ImageHeader + (images[i].Size * images[i].Size * 4);
    }

    var bytes = new byte[position];
    var span = bytes.AsSpan();

    BinaryPrimitives.WriteUInt32LittleEndian(span, 0x72756358); // "Xcur"
    BinaryPrimitives.WriteUInt32LittleEndian(span[4..], FileHeader);
    BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x00010000);
    BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)count);

    for (var i = 0; i < count; i++)
    {
        var toc = span[(FileHeader + (i * TocEntry))..];
        BinaryPrimitives.WriteUInt32LittleEndian(toc, ImageType);
        BinaryPrimitives.WriteUInt32LittleEndian(toc[4..], (uint)images[i].Size);
        BinaryPrimitives.WriteUInt32LittleEndian(toc[8..], (uint)offsets[i]);
    }

    for (var i = 0; i < count; i++)
    {
        var (size, pixels) = images[i];
        var chunk = span[offsets[i]..];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, ImageHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], ImageType);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[8..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[12..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[16..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[20..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[24..], (uint)(size / 2));
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[28..], (uint)(size / 2));
        BinaryPrimitives.WriteUInt32LittleEndian(chunk[32..], 0);

        var data = chunk[ImageHeader..];
        for (var p = 0; p < pixels.Length; p++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data[(p * 4)..], pixels[p]);
        }
    }

    return bytes;
}

/// <summary>A straight-alpha RGBA PNG, so a preview needs no image library.</summary>
static void WritePng(string path, int size, uint[] pixels)
{
    var header = new byte[13];
    BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)size);
    header[8] = 8;  // bit depth
    header[9] = 6;  // truecolour with alpha
    header[10] = 0; // deflate
    header[11] = 0; // adaptive filtering
    header[12] = 0; // no interlace

    // One filter byte (None) per row, then straight-alpha RGBA: Xcursor
    // stores premultiplied ARGB, PNG wants the alpha divided back out.
    var raw = new byte[(size * 4 + 1) * size];
    for (var y = 0; y < size; y++)
    {
        var row = (y * ((size * 4) + 1)) + 1;
        for (var x = 0; x < size; x++)
        {
            var argb = pixels[(y * size) + x];
            var alpha = (byte)(argb >> 24);
            var value = alpha == 0 ? (byte)0 : (byte)Math.Min(255, (((argb >> 16) & 0xFF) * 255) / alpha);
            var offset = row + (x * 4);
            raw[offset] = value;
            raw[offset + 1] = value;
            raw[offset + 2] = value;
            raw[offset + 3] = alpha;
        }
    }

    using var file = File.Create(path);
    file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    WriteChunk(file, "IHDR", header);
    WriteChunk(file, "IDAT", ZlibStored(raw));
    WriteChunk(file, "IEND", []);
}

static void WriteChunk(Stream stream, string type, byte[] data)
{
    var length = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
    stream.Write(length);

    var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
    stream.Write(typeBytes);
    stream.Write(data);

    // PNG's CRC runs over the type first, then the data, with no terminator
    // between: the running register is fed in that order.
    var crc = Crc32(data, Crc32(typeBytes, 0xFFFFFFFF)) ^ 0xFFFFFFFF;
    var crcBytes = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
    stream.Write(crcBytes);
}

static uint Crc32(byte[] data, uint crc)
{
    foreach (var value in data)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
    }

    return crc;
}

/// <summary>zlib with stored blocks: valid, and small enough to write here.</summary>
static byte[] ZlibStored(byte[] data)
{
    var blocks = Math.Max(1, (data.Length + 65534) / 65535);
    var output = new byte[2 + (blocks * 5) + data.Length + 4];
    var position = 0;
    output[position++] = 0x78; // deflate, 32K window
    output[position++] = 0x01; // no preset dictionary, fastest
    for (var block = 0; block < blocks; block++)
    {
        var offset = block * 65535;
        var length = Math.Min(65535, data.Length - offset);
        output[position++] = (byte)(block == blocks - 1 ? 1 : 0); // BFINAL
        output[position++] = (byte)(length & 0xFF);
        output[position++] = (byte)(length >> 8);
        output[position++] = (byte)(~length & 0xFF);
        output[position++] = (byte)((~length >> 8) & 0xFF);
        Array.Copy(data, offset, output, position, length);
        position += length;
    }

    var a = 1u;
    var b = 0u;
    foreach (var value in data)
    {
        a = (a + value) % 65521;
        b = (b + a) % 65521;
    }

    BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(position), (b << 16) | a);
    return output;
}

internal enum Shape
{
    Ring,
    Horizontal,
    Vertical,
    DiagonalDown,
    DiagonalUp,
    Move,
    Text,
    TextSideways,
}