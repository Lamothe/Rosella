// Independent check: asks the real libXcursor (the loader GTK and
// libwayland-cursor mirror) to parse the generated theme, so the test does
// not share code with the generator. No headers needed beyond dlfcn.
//
//   cc -O2 -o /tmp/cursor-check tools/Rosella.CursorLab/check.c -ldl
//   /tmp/cursor-check ~/.local/share/icons/Rosella-Ring
//
// It checks that every name resolves to the art its table promises (an
// alias whose checksum differs from its target's, or a resize/text shape
// still identical to the plain pointer, is the aliasing bug that made every
// shape a ring), that hotspots sit on the centre, and that the largest
// nominal size exists as native art.

#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef unsigned int xuint;
typedef unsigned int xdim;
typedef unsigned int xpixel;

typedef struct {
    xuint version;
    xdim size;
    xdim width;
    xdim height;
    xdim xhot;
    xdim yhot;
    xuint delay;
    xpixel *pixels;
} XcursorImage;

typedef struct {
    int nimage;
    XcursorImage **images;
    char *name;
} XcursorImages;

typedef XcursorImages *(*load_file_fn)(const char *, int);

struct expect {
    const char *name;
    const char *target;
    int distinct; // 1: must not be the plain pointer
};

static unsigned int checksum(XcursorImage *image)
{
    unsigned int hash = 2166136261u;
    unsigned int count = image->width * image->height;
    for (unsigned int i = 0; i < count; i++) {
        unsigned int pixel = image->pixels[i];
        hash = (hash ^ (pixel & 0xff)) * 16777619u;
        hash = (hash ^ ((pixel >> 8) & 0xff)) * 16777619u;
        hash = (hash ^ ((pixel >> 16) & 0xff)) * 16777619u;
        hash = (hash ^ ((pixel >> 24) & 0xff)) * 16777619u;
    }
    return hash;
}

int main(int argc, char **argv)
{
    if (argc < 2) {
        fprintf(stderr, "usage: %s <theme-dir>\n", argv[0]);
        return 2;
    }

    void *library = dlopen("libXcursor.so.1", RTLD_NOW);
    if (!library) {
        fprintf(stderr, "libXcursor not available: %s\n", dlerror());
        return 2;
    }

    load_file_fn load = (load_file_fn)dlsym(library, "XcursorFilenameLoadImages");
    if (!load) {
        fprintf(stderr, "XcursorFilenameLoadImages missing\n");
        return 2;
    }

    struct expect expected[] = {
        { "ew-resize", "ew-resize", 1 },
        { "ns-resize", "ns-resize", 1 },
        { "nwse-resize", "nwse-resize", 1 },
        { "nesw-resize", "nesw-resize", 1 },
        { "fleur", "fleur", 1 },
        { "text", "xterm", 1 },
        { "vertical-text", "vertical-text", 1 },
        { "col-resize", "ew-resize", 0 },
        { "row-resize", "ns-resize", 0 },
        { "sb_h_double_arrow", "ew-resize", 0 },
        { "sb_v_double_arrow", "ns-resize", 0 },
        { "n-resize", "ns-resize", 0 },
        { "s-resize", "ns-resize", 0 },
        { "e-resize", "ew-resize", 0 },
        { "w-resize", "ew-resize", 0 },
        { "ne-resize", "nesw-resize", 0 },
        { "sw-resize", "nesw-resize", 0 },
        { "nw-resize", "nwse-resize", 0 },
        { "se-resize", "nwse-resize", 0 },
        { "top_left_corner", "nwse-resize", 0 },
        { "bottom_right_corner", "nwse-resize", 0 },
        { "top_right_corner", "nesw-resize", 0 },
        { "bottom_left_corner", "nesw-resize", 0 },
        { "move", "fleur", 0 },
        { "all-scroll", "fleur", 0 },
        { "left_ptr_watch", "left_ptr", 0 },
        { "hand2", "left_ptr", 0 },
    };

    char path[4096];
    snprintf(path, sizeof path, "%s/cursors/left_ptr", argv[1]);
    XcursorImages *left = load(path, 24);
    if (!left) {
        fprintf(stderr, "left_ptr: NOT FOUND\n");
        return 1;
    }

    unsigned int pointer_sum = checksum(left->images[0]);
    printf("left_ptr        %ux%u hotspot=%u,%u sum=%08x\n", left->images[0]->width,
           left->images[0]->height, left->images[0]->xhot, left->images[0]->yhot, pointer_sum);

    int failures = 0;
    unsigned int text_sum = 0;
    unsigned int sideways_sum = 0;

    for (unsigned int i = 0; i < sizeof expected / sizeof expected[0]; i++) {
        snprintf(path, sizeof path, "%s/cursors/%s", argv[1], expected[i].name);
        XcursorImages *images = load(path, 24);
        if (!images) {
            printf("%-15s NOT FOUND\n", expected[i].name);
            failures++;
            continue;
        }

        snprintf(path, sizeof path, "%s/cursors/%s", argv[1], expected[i].target);
        XcursorImages *target = load(path, 24);
        if (!target) {
            printf("%-15s target %s NOT FOUND\n", expected[i].name, expected[i].target);
            failures++;
            continue;
        }

        XcursorImage *image = images->images[0];
        unsigned int sum = checksum(image);
        unsigned int target_sum = checksum(target->images[0]);
        int centred = image->xhot == image->width / 2 && image->yhot == image->height / 2;
        int wrong = sum != target_sum;
        int ring = expected[i].distinct && sum == pointer_sum;

        printf("%-15s -> %-14s hotspot=%u,%u sum=%08x %s\n", expected[i].name,
               expected[i].target, image->xhot, image->yhot, sum,
               wrong ? "WRONG ART" : ring ? "STILL THE RING" : centred ? "ok" : "HOTSPOT OFF");

        if (wrong || ring || !centred) {
            failures++;
        }

        if (strcmp(expected[i].name, "text") == 0) {
            text_sum = sum;
        } else if (strcmp(expected[i].name, "vertical-text") == 0) {
            sideways_sum = sum;
        }
    }

    if (text_sum != 0 && text_sum == sideways_sum) {
        printf("text and vertical-text share their art\n");
        failures++;
    }

    // The big art must exist natively too: the shake-to-find enlargement
    // asks for 3x the nominal size.
    snprintf(path, sizeof path, "%s/cursors/ew-resize", argv[1]);
    XcursorImages *large = load(path, 144);
    if (!large) {
        printf("ew-resize@144 NOT FOUND\n");
        failures++;
    } else {
        printf("ew-resize@144    %ux%u\n", large->images[0]->width, large->images[0]->height);
        if (large->images[0]->width != 144) {
            printf("ew-resize@144 is not native art\n");
            failures++;
        }
    }

    return failures == 0 ? 0 : 1;
}