namespace Rosella.Compositor;

/// <summary>
/// A minimal SVG path reader — just the grammar a silhouette needs:
/// moveto, lineto, curveto and closepath, absolute or relative, with the
/// implicit parameter repetition the spec allows. Paths flatten once into
/// polygons and are then hit-tested by even-odd parity, so the shell can
/// ship real vector artwork (the apple, from the canonical silhouette)
/// without a rasteriser dependency or a hand-tuned blob of circles.
/// </summary>
internal sealed class SvgPath
{
    /// <summary>Segments per curve when flattening. 24 is past smooth at icon sizes.</summary>
    private const int CurveSegments = 24;

    private readonly (double X, double Y)[][] _polygons;

    private SvgPath((double X, double Y)[][] polygons)
    {
        _polygons = polygons;
        var (minX, minY, maxX, maxY) = Bounds;
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
    }

    internal double MinX { get; }

    internal double MinY { get; }

    internal double MaxX { get; }

    internal double MaxY { get; }

    /// <summary>The path's extent in path units.</summary>
    internal (double MinX, double MinY, double MaxX, double MaxY) Bounds
    {
        get
        {
            var minX = double.PositiveInfinity;
            var minY = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;
            foreach (var polygon in _polygons)
            {
                foreach (var (x, y) in polygon)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }

            return _polygons.Length == 0 ? (0, 0, 0, 0) : (minX, minY, maxX, maxY);
        }
    }

    /// <summary>Parses path data; any grammar beyond the subset throws.</summary>
    internal static SvgPath Parse(string data)
    {
        var polygons = new List<(double X, double Y)[]>();
        var current = new List<(double X, double Y)>();
        var scanner = new Scanner(data);
        var x = 0.0;
        var y = 0.0;
        var startX = 0.0;
        var startY = 0.0;
        var command = '\0';
        var open = false;

        void Flush()
        {
            if (current.Count > 0)
            {
                polygons.Add([.. current]);
                current.Clear();
            }
        }

        while (true)
        {
            if (scanner.AtEnd)
            {
                break;
            }

            if (scanner.TryCommand(out var next))
            {
                command = next;
            }
            else if (scanner.AtEnd)
            {
                // Only separators remain: an empty (or whitespace-only) path
                // is legal and draws nothing.
                break;
            }
            else if (command == '\0')
            {
                throw new FormatException($"svg path: parameters with no command to consume them at {scanner.Position}");
            }

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    Flush();
                    x = scanner.Number() + (char.IsUpper(command) ? 0 : x);
                    y = scanner.Number() + (char.IsUpper(command) ? 0 : y);
                    startX = x;
                    startY = y;
                    current.Add((x, y));
                    open = true;

                    // Implicit repetition of moveto is a lineto.
                    command = char.IsUpper(command) ? 'L' : 'l';
                    break;

                case 'L':
                    x = scanner.Number() + (char.IsUpper(command) ? 0 : x);
                    y = scanner.Number() + (char.IsUpper(command) ? 0 : y);
                    current.Add((x, y));
                    break;

                case 'H':
                    x = scanner.Number() + (char.IsUpper(command) ? 0 : x);
                    current.Add((x, y));
                    break;

                case 'V':
                    y = scanner.Number() + (char.IsUpper(command) ? 0 : y);
                    current.Add((x, y));
                    break;

                case 'C':
                    {
                        var c1X = scanner.Number() + (char.IsUpper(command) ? 0 : x);
                        var c1Y = scanner.Number() + (char.IsUpper(command) ? 0 : y);
                        var c2X = scanner.Number() + (char.IsUpper(command) ? 0 : x);
                        var c2Y = scanner.Number() + (char.IsUpper(command) ? 0 : y);
                        var endX = scanner.Number() + (char.IsUpper(command) ? 0 : x);
                        var endY = scanner.Number() + (char.IsUpper(command) ? 0 : y);
                        FlattenCubic(current, x, y, c1X, c1Y, c2X, c2Y, endX, endY);
                        x = endX;
                        y = endY;
                        break;
                    }

                case 'Z':
                    if (open)
                    {
                        x = startX;
                        y = startY;
                        open = false;
                    }

                    Flush();

                    // Closepath takes no parameters and has no implicit
                    // repetition: trailing numbers are a grammar error, and
                    // keeping Z as the command would re-run it forever
                    // without consuming them.
                    command = '\0';
                    break;

                default:
                    throw new FormatException($"svg path: unsupported command '{command}' at {scanner.Position}");
            }

            scanner.SkipSeparators();
        }

        Flush();
        return new SvgPath([.. polygons]);
    }

    /// <summary>Whether the point lies inside the filled shape (even-odd).</summary>
    internal bool Inside(double x, double y)
    {
        // Even-odd parity over every flattened edge: subpaths may overlap
        // or nest; parity is exactly what SVG's default fill rule says.
        var inside = false;
        foreach (var polygon in _polygons)
        {
            for (var i = 0; i < polygon.Length; i++)
            {
                var (ax, ay) = polygon[i];
                var (bx, by) = polygon[(i + 1) % polygon.Length];
                if ((ay > y) == (by > y))
                {
                    continue;
                }

                var crossingX = ax + ((bx - ax) * ((y - ay) / (by - ay)));
                if (crossingX > x)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    private static void FlattenCubic(
        List<(double X, double Y)> sink,
        double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        for (var i = 1; i <= CurveSegments; i++)
        {
            var t = i / (double)CurveSegments;
            var u = 1 - t;
            var x = (u * u * u * x0) + (3 * u * u * t * x1) + (3 * u * t * t * x2) + (t * t * t * x3);
            var y = (u * u * u * y0) + (3 * u * u * t * y1) + (3 * u * t * t * y2) + (t * t * t * y3);
            sink.Add((x, y));
        }
    }

    /// <summary>One-pass number and command reader over the path data.</summary>
    private sealed class Scanner(string data)
    {
        private int _index;

        internal int Position => _index;

        internal bool AtEnd => _index >= data.Length;

        internal void SkipSeparators()
        {
            while (_index < data.Length && (data[_index] is ' ' or ',' or '\t' or '\r' or '\n'))
            {
                _index++;
            }
        }

        internal bool TryCommand(out char command)
        {
            SkipSeparators();
            if (_index < data.Length && char.IsAsciiLetter(data[_index]))
            {
                command = data[_index];
                _index++;
                return true;
            }

            command = '\0';
            return false;
        }

        /// <summary>Reads one SVG number: sign, digits, fraction, optional exponent.</summary>
        internal double Number()
        {
            SkipSeparators();
            var start = _index;
            if (_index < data.Length && (data[_index] is '+' or '-'))
            {
                _index++;
            }

            while (_index < data.Length && char.IsAsciiDigit(data[_index]))
            {
                _index++;
            }

            if (_index < data.Length && data[_index] == '.')
            {
                _index++;
                while (_index < data.Length && char.IsAsciiDigit(data[_index]))
                {
                    _index++;
                }
            }

            if (_index < data.Length && (data[_index] is 'e' or 'E'))
            {
                _index++;
                if (_index < data.Length && (data[_index] is '+' or '-'))
                {
                    _index++;
                }

                while (_index < data.Length && char.IsAsciiDigit(data[_index]))
                {
                    _index++;
                }
            }

            if (start == _index)
            {
                throw new FormatException($"svg path: expected a number at {start}");
            }

            return double.Parse(data.AsSpan(start, _index - start), System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
