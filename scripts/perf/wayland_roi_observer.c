#define _GNU_SOURCE
#include <errno.h>
#include <inttypes.h>
#include <limits.h>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <time.h>
#include <unistd.h>
#include <wayland-client.h>
#include <wayland-client-protocol.h>
#include "wlr-screencopy-unstable-v1-client-protocol.h"
#include "xdg-output-unstable-v1-client-protocol.h"

#define MAX_OUTPUTS 32
#define MAX_FRAME_BYTES ((size_t)1024 * 1024 * 1024)

struct output {
    struct wl_output *wl;
    struct zxdg_output_v1 *xdg;
    char name[256];
    int32_t x, y, w, h;
    bool has_pos, has_size, done;
};

struct app {
    struct wl_display *display;
    struct wl_registry *registry;
    struct wl_shm *shm;
    struct zxdg_output_manager_v1 *xdg_manager;
    struct zwlr_screencopy_manager_v1 *capture_manager;
    uint32_t capture_version;
    struct output outputs[MAX_OUTPUTS];
    size_t output_count;
    struct output *selected;
    struct wl_buffer *buffer;
    void *mapping;
    size_t mapping_size;
    int shm_fd;
    bool buffer_free;
    uint32_t format;
    uint32_t stride;
    uint32_t width, height;
    uint32_t buffer_format, buffer_stride, buffer_width, buffer_height;
    bool have_format;
    bool frame_pending;
    bool ready;
    bool failed;
    bool y_invert;
    struct zwlr_screencopy_frame_v1 *frame;
    uint64_t ready_ns;
    uint64_t interval_ns;
    uint64_t next_sample_ns;
    int32_t rx, ry, rw, rh;
    char *base64;
    size_t base64_capacity;
    char error[512];
};

static volatile sig_atomic_t stop_requested = 0;
static void on_signal(int signum) { (void)signum; stop_requested = 1; }
static void set_error(struct app *a, const char *fmt, ...);
static bool write_all(int fd, const char *p, size_t n);
static void set_error(struct app *a, const char *fmt, ...) {
    if (a->error[0]) return;
    va_list ap; va_start(ap, fmt); vsnprintf(a->error, sizeof(a->error), fmt, ap); va_end(ap);
}
static uint64_t monotonic_ns(void) {
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts) != 0) return 0;
    return (uint64_t)ts.tv_sec * UINT64_C(1000000000) + (uint64_t)ts.tv_nsec;
}
static bool write_all(int fd, const char *p, size_t n) {
    while (n) { ssize_t k = write(fd, p, n); if (k < 0 && errno == EINTR) continue; if (k <= 0) return false; p += k; n -= (size_t)k; }
    return true;
}
static bool emit_frame(struct app *a) {
    static const char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    size_t pixels = (size_t)a->width * a->height;
    if (pixels > SIZE_MAX / 4) { set_error(a, "frame is too large"); return false; }
    size_t raw_len = pixels * 4;
    size_t enc_len = ((raw_len + 2) / 3) * 4;
    if (enc_len > a->base64_capacity) {
        char *p = realloc(a->base64, enc_len);
        if (!p) { set_error(a, "out of memory allocating encoded frame"); return false; }
        a->base64 = p; a->base64_capacity = enc_len;
    }
    size_t out = 0;
    uint32_t acc = 0; unsigned bits = 0;
    for (uint32_t y = 0; y < a->height; ++y) {
        uint32_t source_y = a->y_invert ? a->height - 1 - y : y;
        const uint8_t *row = (const uint8_t *)a->mapping + (size_t)source_y * a->stride;
        for (uint32_t x = 0; x < a->width; ++x) {
            const uint8_t *px = row + (size_t)x * 4;
            uint8_t rgba[4];
#if __BYTE_ORDER__ == __ORDER_LITTLE_ENDIAN__
            rgba[0] = px[2]; rgba[1] = px[1]; rgba[2] = px[0];
            rgba[3] = a->format == WL_SHM_FORMAT_ARGB8888 ? px[3] : 255;
#else
            rgba[0] = px[1]; rgba[1] = px[2]; rgba[2] = px[3];
            rgba[3] = a->format == WL_SHM_FORMAT_ARGB8888 ? px[0] : 255;
#endif
            for (unsigned c = 0; c < 4; ++c) {
                acc = (acc << 8) | rgba[c]; bits += 8;
                while (bits >= 6) { bits -= 6; a->base64[out++] = alphabet[(acc >> bits) & 63]; }
            }
        }
    }
    if (bits) a->base64[out++] = alphabet[(acc << (6 - bits)) & 63];
    while (out < enc_len) a->base64[out++] = '=';
    char prefix[160];
    int n = snprintf(prefix, sizeof(prefix), "{\"monotonic_ns\":%" PRIu64 ",\"width\":%u,\"height\":%u,\"rgba_base64\":\"", a->ready_ns, a->width, a->height);
    if (n < 0 || (size_t)n >= sizeof(prefix) || !write_all(STDOUT_FILENO, prefix, (size_t)n) || !write_all(STDOUT_FILENO, a->base64, enc_len) || !write_all(STDOUT_FILENO, "\"}\n", 3)) {
        set_error(a, "writing frame JSONL to stdout failed: %s", strerror(errno)); return false;
    }
    return true;
}
static void handle_output_geometry(void *data, struct zxdg_output_v1 *xdg, int32_t x, int32_t y) {
    (void)xdg; struct output *o = data; o->x = x; o->y = y; o->has_pos = true;
}
static void handle_output_mode(void *data, struct zxdg_output_v1 *xdg, int32_t w, int32_t h) {
    (void)xdg; struct output *o = data; o->w = w; o->h = h; o->has_size = true;
}
static void handle_output_done(void *data, struct zxdg_output_v1 *xdg) { (void)xdg; ((struct output *)data)->done = true; }
static void handle_output_name(void *data, struct zxdg_output_v1 *xdg, const char *name) {
    (void)xdg; struct output *o = data; snprintf(o->name, sizeof(o->name), "%s", name ? name : "");
}
static void handle_output_description(void *data, struct zxdg_output_v1 *xdg, const char *description) { (void)data; (void)xdg; (void)description; }
static const struct zxdg_output_v1_listener xdg_output_listener = {
    .logical_position = handle_output_geometry, .logical_size = handle_output_mode,
    .done = handle_output_done, .name = handle_output_name, .description = handle_output_description
};
static void output_geometry(void *data, struct wl_output *output, int32_t x, int32_t y, int32_t pw, int32_t ph, int32_t subpixel, const char *make, const char *model, int32_t transform) { (void)data; (void)output; (void)x; (void)y; (void)pw; (void)ph; (void)subpixel; (void)make; (void)model; (void)transform; }
static void output_mode(void *data, struct wl_output *output, uint32_t flags, int32_t width, int32_t height, int32_t refresh) { (void)data; (void)output; (void)flags; (void)width; (void)height; (void)refresh; }
static void output_done(void *data, struct wl_output *output) { (void)data; (void)output; }
static const struct wl_output_listener wl_output_listener = { .geometry = output_geometry, .mode = output_mode, .done = output_done };
static void shm_format(void *data, struct wl_shm *shm, uint32_t format) { (void)data; (void)shm; (void)format; }
static const struct wl_shm_listener shm_listener = { .format = shm_format };
static void registry_global(void *data, struct wl_registry *registry, uint32_t name, const char *interface, uint32_t version) {
    struct app *a = data;
    if (!strcmp(interface, wl_shm_interface.name)) {
        a->shm = wl_registry_bind(registry, name, &wl_shm_interface, 1);
        wl_shm_add_listener(a->shm, &shm_listener, a);
    }
    else if (!strcmp(interface, zxdg_output_manager_v1_interface.name)) a->xdg_manager = wl_registry_bind(registry, name, &zxdg_output_manager_v1_interface, version < 3 ? version : 3);
    else if (!strcmp(interface, zwlr_screencopy_manager_v1_interface.name) && version >= 2) {
        a->capture_version = version < 3 ? version : 3;
        a->capture_manager = wl_registry_bind(registry, name, &zwlr_screencopy_manager_v1_interface, a->capture_version);
    } else if (!strcmp(interface, wl_output_interface.name) && a->output_count < MAX_OUTPUTS) {
        struct output *o = &a->outputs[a->output_count++]; memset(o, 0, sizeof(*o));
        o->wl = wl_registry_bind(registry, name, &wl_output_interface, 1);
        wl_output_add_listener(o->wl, &wl_output_listener, o);
    }
}
static void registry_remove(void *data, struct wl_registry *registry, uint32_t name) { (void)data; (void)registry; (void)name; }
static const struct wl_registry_listener registry_listener = { registry_global, registry_remove };
static int create_memfd(void) {
#ifdef SYS_memfd_create
    return (int)syscall(SYS_memfd_create, "wayland-roi", 0x0001U);
#else
    errno = ENOSYS; return -1;
#endif
}
static void buffer_release(void *data, struct wl_buffer *buffer) { (void)buffer; ((struct app *)data)->buffer_free = true; }
static const struct wl_buffer_listener buffer_listener = { buffer_release };
static bool ensure_buffer(struct app *a) {
    if (a->buffer && a->mapping && a->buffer_format == a->format && a->buffer_stride == a->stride && a->buffer_width == a->width && a->buffer_height == a->height) return true;
    if (a->buffer) { wl_buffer_destroy(a->buffer); a->buffer = NULL; }
    if (a->mapping) { munmap(a->mapping, a->mapping_size); a->mapping = NULL; a->mapping_size = 0; }
    if (a->shm_fd >= 0) { close(a->shm_fd); a->shm_fd = -1; }
    if (!a->width || !a->height || a->width > INT_MAX / 4 || a->stride < a->width * 4 || (size_t)a->stride > SIZE_MAX / a->height) { set_error(a, "invalid compositor buffer geometry"); return false; }
    a->mapping_size = (size_t)a->stride * a->height;
    if (a->mapping_size > MAX_FRAME_BYTES || a->mapping_size > INT_MAX) { set_error(a, "compositor buffer exceeds 1 GiB limit"); return false; }
    a->shm_fd = create_memfd();
    if (a->shm_fd < 0 || ftruncate(a->shm_fd, (off_t)a->mapping_size) < 0) { set_error(a, "creating shared-memory buffer failed: %s", strerror(errno)); return false; }
    a->mapping = mmap(NULL, a->mapping_size, PROT_READ | PROT_WRITE, MAP_SHARED, a->shm_fd, 0);
    if (a->mapping == MAP_FAILED) { a->mapping = NULL; set_error(a, "mapping shared-memory buffer failed: %s", strerror(errno)); return false; }
    struct wl_shm_pool *pool = wl_shm_create_pool(a->shm, a->shm_fd, (int)a->mapping_size);
    if (!pool) { set_error(a, "creating wl_shm pool failed"); return false; }
    a->buffer = wl_shm_pool_create_buffer(pool, 0, (int)a->width, (int)a->height, (int)a->stride, a->format);
    wl_shm_pool_destroy(pool);
    if (!a->buffer) { set_error(a, "creating wl_buffer failed"); return false; }
    wl_buffer_add_listener(a->buffer, &buffer_listener, a);
    a->buffer_format = a->format; a->buffer_stride = a->stride;
    a->buffer_width = a->width; a->buffer_height = a->height;
    a->buffer_free = true;
    return true;
}
static void frame_buffer_done(void *data, struct zwlr_screencopy_frame_v1 *frame);
static void frame_buffer(void *data, struct zwlr_screencopy_frame_v1 *frame, uint32_t format, uint32_t width, uint32_t height, uint32_t stride) {
    struct app *a = data;
    if (format == WL_SHM_FORMAT_ARGB8888 || format == WL_SHM_FORMAT_XRGB8888) {
        if (!a->have_format || format == WL_SHM_FORMAT_ARGB8888) {
            a->format = format; a->width = width; a->height = height; a->stride = stride; a->have_format = true;
        }
        if (a->capture_version < 3) frame_buffer_done(data, frame);
    }
}
static void frame_flags(void *data, struct zwlr_screencopy_frame_v1 *frame, uint32_t flags) { (void)frame; ((struct app *)data)->y_invert = (flags & 1u) != 0; }
static void frame_ready(void *data, struct zwlr_screencopy_frame_v1 *frame, uint32_t sec_hi, uint32_t sec_lo, uint32_t nsec) {
    (void)frame; (void)sec_hi; (void)sec_lo; (void)nsec;
    struct app *a = data; a->ready_ns = monotonic_ns(); a->ready = true;
    if (!emit_frame(a)) a->failed = true;
    a->buffer_free = true;
}
static void frame_failed(void *data, struct zwlr_screencopy_frame_v1 *frame) {
    (void)frame; struct app *a = data; a->failed = true; a->buffer_free = true; set_error(a, "compositor reported screencopy frame failure");
}
static void frame_damage(void *data, struct zwlr_screencopy_frame_v1 *frame, uint32_t x, uint32_t y, uint32_t width, uint32_t height) { (void)data; (void)frame; (void)x; (void)y; (void)width; (void)height; }
static void frame_buffer_done(void *data, struct zwlr_screencopy_frame_v1 *frame) {
    struct app *a = data;
    if (!a->have_format) { a->failed = true; set_error(a, "compositor did not advertise ARGB8888 or XRGB8888 wl_shm format"); return; }
    if (!ensure_buffer(a)) { a->failed = true; return; }
    a->buffer_free = false;
    zwlr_screencopy_frame_v1_copy(frame, a->buffer);
    if (wl_display_flush(a->display) < 0 && errno != EAGAIN) { a->failed = true; set_error(a, "flushing Wayland request failed: %s", strerror(errno)); }
}
static void frame_linux_dmabuf(void *data, struct zwlr_screencopy_frame_v1 *frame, uint32_t format, uint32_t width, uint32_t height) { (void)data; (void)frame; (void)format; (void)width; (void)height; }
static void frame_buffer_done_v3(void *data, struct zwlr_screencopy_frame_v1 *frame) { frame_buffer_done(data, frame); }
static const struct zwlr_screencopy_frame_v1_listener frame_listener = {
    .buffer = frame_buffer, .flags = frame_flags, .ready = frame_ready, .failed = frame_failed,
    .damage = frame_damage, .linux_dmabuf = frame_linux_dmabuf, .buffer_done = frame_buffer_done_v3
};
static bool request_frame(struct app *a) {
    a->have_format = false; a->ready = false; a->failed = false; a->y_invert = false;
    a->frame = zwlr_screencopy_manager_v1_capture_output_region(a->capture_manager, 0, a->selected->wl, a->rx, a->ry, a->rw, a->rh);
    if (!a->frame) { set_error(a, "creating screencopy frame failed"); return false; }
    a->frame_pending = true;
    if (zwlr_screencopy_frame_v1_add_listener(a->frame, &frame_listener, a) < 0) { set_error(a, "registering screencopy listener failed"); return false; }
    if (wl_display_flush(a->display) < 0 && errno != EAGAIN) { set_error(a, "flushing capture request failed: %s", strerror(errno)); return false; }
    return true;
}
static bool parse_positive_hz(const char *s, double *out) {
    char *end = NULL; errno = 0; double value = strtod(s, &end);
    if (errno || !s[0] || !end || *end || !isfinite(value) || value <= 0.0 || value > 1000000000.0) return false;
    *out = value; return true;
}
static bool parse_rect(const char *s, int32_t *x, int32_t *y, int32_t *w, int32_t *h) {
    char tail; long a,b,c,d;
    if (sscanf(s, "%ld,%ld,%ld,%ld%c", &a,&b,&c,&d,&tail) != 4 || a < INT32_MIN || a > INT32_MAX || b < INT32_MIN || b > INT32_MAX || c <= 0 || c > INT32_MAX || d <= 0 || d > INT32_MAX || a + c > INT32_MAX || b + d > INT32_MAX) return false;
    *x=(int32_t)a; *y=(int32_t)b; *w=(int32_t)c; *h=(int32_t)d; return true;
}
static void usage(const char *program) { fprintf(stderr, "usage: %s --rect X,Y,W,H --sample-hz N\n", program); }
static bool finish_frame(struct app *a) {
    if (!a->frame_pending || (!a->ready && !a->failed)) return true;
    zwlr_screencopy_frame_v1_destroy(a->frame);
    a->frame = NULL; a->frame_pending = false;
    if (a->failed) return false;
    if (a->buffer_free && a->next_sample_ns < monotonic_ns()) a->next_sample_ns = monotonic_ns();
    return true;
}
int main(int argc, char **argv) {
    struct app a; memset(&a, 0, sizeof(a)); a.shm_fd = -1;
    const char *rect = NULL; double sample_hz = 0.0;
    for (int i=1; i<argc; ++i) {
        if (!strcmp(argv[i], "--rect") && i+1<argc) rect=argv[++i];
        else if (!strcmp(argv[i], "--sample-hz") && i+1<argc) { if (!parse_positive_hz(argv[++i], &sample_hz)) { usage(argv[0]); return 2; } }
        else { usage(argv[0]); return 2; }
    }
    if (!rect || sample_hz <= 0.0 || !parse_rect(rect,&a.rx,&a.ry,&a.rw,&a.rh)) { usage(argv[0]); return 2; }
    long double interval_ns = 1000000000.0L / (long double)sample_hz;
    if (interval_ns > (long double)UINT64_MAX) { usage(argv[0]); return 2; }
    a.interval_ns = (uint64_t)interval_ns; if (!a.interval_ns) a.interval_ns=1;
    signal(SIGINT, on_signal); signal(SIGTERM, on_signal); signal(SIGPIPE, SIG_IGN);
    a.display = wl_display_connect(NULL);
    if (!a.display) { fprintf(stderr, "wayland ROI observer: cannot connect to Wayland display\n"); return 1; }
    a.registry = wl_display_get_registry(a.display); wl_registry_add_listener(a.registry, &registry_listener, &a);
    if (wl_display_roundtrip(a.display) < 0) { set_error(&a, "initial Wayland registry roundtrip failed"); goto done; }
    if (!a.shm || !a.xdg_manager || !a.capture_manager || !a.output_count) { set_error(&a, "compositor lacks wl_shm, xdg-output, wlr-screencopy, or outputs"); goto done; }
    for (size_t i=0; i<a.output_count; ++i) {
        a.outputs[i].xdg = zxdg_output_manager_v1_get_xdg_output(a.xdg_manager, a.outputs[i].wl);
        zxdg_output_v1_add_listener(a.outputs[i].xdg, &xdg_output_listener, &a.outputs[i]);
    }
    if (wl_display_roundtrip(a.display) < 0) { set_error(&a, "xdg-output metadata roundtrip failed"); goto done; }
    for (size_t i=0; i<a.output_count; ++i) {
        struct output *o = &a.outputs[i];
        int64_t x2=(int64_t)o->x+o->w, y2=(int64_t)o->y+o->h;
        if (o->has_pos && o->has_size && a.rx >= o->x && a.ry >= o->y && (int64_t)a.rx+a.rw <= x2 && (int64_t)a.ry+a.rh <= y2) {
            if (a.selected) { set_error(&a, "ROI is ambiguous across overlapping compositor outputs"); goto done; }
            a.selected=o;
        }
    }
    if (!a.selected) { set_error(&a, "ROI is not fully contained by any compositor output (global logical coordinates)"); goto done; }
    a.rx -= a.selected->x; a.ry -= a.selected->y;
    a.next_sample_ns = monotonic_ns();
    a.buffer_free = true;
    while ((!stop_requested || a.frame_pending || !a.buffer_free) && !a.error[0]) {
        if (!a.frame_pending && a.buffer_free && monotonic_ns() >= a.next_sample_ns) {
            if (!request_frame(&a)) break;
            uint64_t now = monotonic_ns(); a.next_sample_ns += a.interval_ns;
            if (a.next_sample_ns <= now) a.next_sample_ns = now + a.interval_ns;
        }
        if (!finish_frame(&a)) break;
        if (!a.frame_pending && a.buffer_free && monotonic_ns() >= a.next_sample_ns) continue;
        while (wl_display_prepare_read(a.display) != 0) {
            if (wl_display_dispatch_pending(a.display) < 0) { set_error(&a, "dispatching Wayland events failed: %s", strerror(errno)); break; }
            if (!finish_frame(&a)) break;
        }
        if (a.error[0] || a.failed) break;
        int timeout = -1;
        if (!a.frame_pending && a.buffer_free) {
            uint64_t now = monotonic_ns(), delta = a.next_sample_ns > now ? a.next_sample_ns - now : 0;
            timeout = (int)((delta + 999999) / 1000000); if (timeout > 1000) timeout = 1000;
        }
        struct pollfd pfd = { .fd = wl_display_get_fd(a.display), .events = POLLIN };
        int flush_result = wl_display_flush(a.display);
        if (flush_result < 0 && errno != EAGAIN) {
            wl_display_cancel_read(a.display); set_error(&a, "flushing Wayland display failed: %s", strerror(errno)); break;
        }
        if (flush_result < 0) pfd.events |= POLLOUT;
        int pr = poll(&pfd, 1, timeout);
        if (pr < 0) {
            wl_display_cancel_read(a.display);
            if (errno == EINTR) continue;
            set_error(&a, "polling Wayland display failed: %s", strerror(errno)); break;
        }
        if (!pr) { wl_display_cancel_read(a.display); continue; }
        if (pfd.revents & (POLLERR | POLLHUP | POLLNVAL)) {
            wl_display_cancel_read(a.display); set_error(&a, "Wayland display connection closed"); break;
        }
        if (pfd.revents & POLLIN) {
            if (wl_display_read_events(a.display) < 0) { set_error(&a, "reading Wayland events failed: %s", strerror(errno)); break; }
        } else wl_display_cancel_read(a.display);
        if ((pfd.revents & POLLOUT) && wl_display_flush(a.display) < 0 && errno != EAGAIN) {
            set_error(&a, "flushing Wayland display failed: %s", strerror(errno)); break;
        }
        if (wl_display_dispatch_pending(a.display) < 0) { set_error(&a, "dispatching Wayland events failed: %s", strerror(errno)); break; }
        if (!finish_frame(&a)) break;
    }
    if (a.error[0]) goto done;
    if (stop_requested) { /* normal, signal-driven end */ }
done:
    if (a.error[0]) fprintf(stderr,"wayland ROI observer: %s\n",a.error);
    if (a.frame) zwlr_screencopy_frame_v1_destroy(a.frame);
    if (a.buffer) wl_buffer_destroy(a.buffer);
    if (a.mapping) munmap(a.mapping,a.mapping_size);
    if (a.shm_fd>=0) close(a.shm_fd);
    for (size_t i=0;i<a.output_count;++i) { if (a.outputs[i].xdg) zxdg_output_v1_destroy(a.outputs[i].xdg); if (a.outputs[i].wl) wl_output_destroy(a.outputs[i].wl); }
    if (a.capture_manager) zwlr_screencopy_manager_v1_destroy(a.capture_manager);
    if (a.xdg_manager) zxdg_output_manager_v1_destroy(a.xdg_manager);
    if (a.shm) wl_shm_destroy(a.shm);
    if (a.registry) wl_registry_destroy(a.registry);
    if (a.display) wl_display_disconnect(a.display);
    free(a.base64);
    return a.error[0] ? 1 : 0;
}
