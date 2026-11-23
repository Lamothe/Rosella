#!/bin/bash
# peek.sh — capture the live Rosella frame and print it as classified
# ASCII so the screen can be inspected from a terminal (or over ssh).
#
#   HOST=silver scripts/peek.sh [cols] [rows] [crop WxH+X+Y]
#
# HOST defaults to the local machine. The frame is the compositor's true
# composited output (written on SIGWINCH by FrameDump). Classification:
#   G = green   b = blue   # = bright   + = grey   : = mid
#   ~ = blue-grey (wallpaper)   . = dark   R = red
set -euo pipefail

COLS=${1:-110}
ROWS=${2:-34}
CROP=${3:-}
TARGET=${HOST:-local}

WORKDIR=$(mktemp -d)
trap 'rm -rf "$WORKDIR"' EXIT

if [ "$TARGET" = "local" ]; then
    # The [R] bracket keeps pgrep from matching this script's own command line.
    PID=$(pgrep -f '[R]osella\.Compositor$' | head -1 || true)
    if [ -z "$PID" ]; then
        echo "peek: no Rosella.Compositor running locally" >&2
        exit 1
    fi
    kill -WINCH "$PID"
    sleep 1
    cp /tmp/rosella-frame.ppm "$WORKDIR/frame.ppm"
else
    PID=$(ssh "$TARGET" 'pgrep -f "[R]osella\\.Compositor$" | head -1' || true)
    if [ -z "$PID" ]; then
        echo "peek: no Rosella.Compositor running on $TARGET" >&2
        exit 1
    fi
    ssh "$TARGET" "kill -WINCH $PID && sleep 1"
    scp -q "$TARGET:/tmp/rosella-frame.ppm" "$WORKDIR/frame.ppm"
fi

GEOMETRY=$(head -c 24 "$WORKDIR/frame.ppm" | tr '\n' ' ')
echo "peek: $GEOMETRY (pid $PID on $TARGET)"

if [ -n "$CROP" ]; then
    magick "$WORKDIR/frame.ppm" -crop "$CROP" +repage -resize "${COLS}x${ROWS}!" -depth 8 rgb:"$WORKDIR/sample.raw"
else
    magick "$WORKDIR/frame.ppm" -resize "${COLS}x${ROWS}!" -depth 8 rgb:"$WORKDIR/sample.raw"
fi

od -An -tu1 -v "$WORKDIR/sample.raw" | awk -v cols="$COLS" '
{ for (i = 1; i <= NF; i++) { buf[n++] = $i } }
END {
    for (p = 0; p < n / 3; p++) {
        r = buf[p * 3]; g = buf[p * 3 + 1]; b = buf[p * 3 + 2];
        mx = r; if (g > mx) mx = g; if (b > mx) mx = b;
        mn = r; if (g < mn) mn = g; if (b < mn) mn = b;
        c = ".";
        if (g > r + 25 && g > b + 25) c = "G";
        else if (b > r + 25 && b > g + 10) c = "b";
        else if (r > 200 && g < 120 && b < 120) c = "R";
        else if (r > 150 && g > 150 && b > 150) c = "#";
        else if (mx - mn < 12 && mx > 70) c = "+";
        else if (mx - mn < 12 && mx > 40) c = ":";
        else if (b > g && g >= r && mx > 45) c = "~";
        printf "%s", c;
        if ((p + 1) % cols == 0) printf "\n";
    }
}'
