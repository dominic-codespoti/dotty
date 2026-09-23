#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <signal.h>
#include <sys/ioctl.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <termios.h>
#include <unistd.h>
#include <pthread.h>

#ifndef TIOCSCTTY
# if defined(__linux__)
#  include <asm/ioctls.h>
#  define TIOCSCTTY 0x540E
# endif
#endif

static int master_fd = -1;
static pid_t child_pid = -1;
static int control_sock_fd = -1;
static char *g_control_path = NULL;

static void *proxy_master_to_stdout(void *arg) {
    (void)arg;
    char buf[8192];
    for (;;) {
        ssize_t r = read(master_fd, buf, sizeof(buf));
        if (r > 0) {
            ssize_t written = 0;
            while (written < r) {
                ssize_t n = write(STDOUT_FILENO, buf + written, (size_t)(r - written));
                if (n > 0) {
                    written += n;
                } else if (n < 0 && errno == EINTR) {
                    continue;
                } else {
                    return NULL;
                }
            }
            continue;
        }
        if (r < 0 && errno == EINTR) continue;
        // Linux reports EIO when the slave side of a PTY closes; other
        // POSIX systems may report EOF instead.
        break;
    }
    return NULL;
}

static void *proxy_stdin_to_master(void *arg) {
    (void)arg;
    char buf[4096];
    for (;;) {
        ssize_t r = read(STDIN_FILENO, buf, sizeof(buf));
        if (r > 0) {
            ssize_t written = 0;
            while (written < r) {
                ssize_t n = write(master_fd, buf + written, (size_t)(r - written));
                if (n > 0) {
                    written += n;
                } else if (n < 0 && errno == EINTR) {
                    continue;
                } else {
                    return NULL;
                }
            }
            continue;
        }
        if (r < 0 && errno == EINTR) continue;
        break;
    }
    return NULL;
}

static void apply_control_line(const char *line) {
    // Look for resize JSON: {"type":"resize","cols":NN,"rows":MM}
    if (strstr(line, "resize") != NULL) {
        int cols = 80, rows = 24;
        const char *c = strstr(line, "\"cols\"");
        if (c) sscanf(c, "\"cols\"%*[^0-9]%d", &cols);
        const char *r = strstr(line, "\"rows\"");
        if (r) sscanf(r, "\"rows\"%*[^0-9]%d", &rows);
        struct winsize ws;
        ws.ws_col = cols;
        ws.ws_row = rows;
        ws.ws_xpixel = 0;
        ws.ws_ypixel = 0;
        ioctl(master_fd, TIOCSWINSZ, &ws);
    }
}

struct control_cleanup_state {
    int *listener_fd;
    int *client_fd;
    const char *path;
};

static void control_cleanup(void *arg) {
    struct control_cleanup_state *state = (struct control_cleanup_state *)arg;
    if (*state->client_fd >= 0) {
        close(*state->client_fd);
        *state->client_fd = -1;
    }
    if (*state->listener_fd >= 0) {
        if (control_sock_fd == *state->listener_fd) control_sock_fd = -1;
        close(*state->listener_fd);
        *state->listener_fd = -1;
    }
    if (state->path) unlink(state->path);
}

static void handle_control_messages(const char *path) {
    if (!path) return;
    if (strlen(path) >= sizeof(((struct sockaddr_un *)0)->sun_path)) {
        fprintf(stderr, "pty-helper: control socket path is too long\n");
        return;
    }

    int lsock = socket(AF_UNIX, SOCK_STREAM, 0);
    if (lsock < 0) return;
    struct sockaddr_un addr;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    memcpy(addr.sun_path, path, strlen(path) + 1);
    unlink(path);
    if (bind(lsock, (struct sockaddr*)&addr, sizeof(addr)) < 0) {
        close(lsock);
        return;
    }
    if (listen(lsock, 1) < 0) {
        close(lsock);
        unlink(path);
        return;
    }
    control_sock_fd = lsock;

    int asock = -1;
    struct control_cleanup_state cleanup = { &lsock, &asock, path };
    pthread_cleanup_push(control_cleanup, &cleanup);
    asock = accept(lsock, NULL, NULL);
    if (asock >= 0) {
        int flags = fcntl(asock, F_GETFD);
        if (flags != -1) fcntl(asock, F_SETFD, flags | FD_CLOEXEC);

        char line[1024];
        size_t line_length = 0;
        for (;;) {
            char input[1024];
            ssize_t n = read(asock, input, sizeof(input));
            if (n > 0) {
                for (ssize_t i = 0; i < n; ++i) {
                    if (input[i] == '\n' || line_length == sizeof(line) - 1) {
                        line[line_length] = '\0';
                        apply_control_line(line);
                        line_length = 0;
                    } else {
                        line[line_length++] = input[i];
                    }
                }
                continue;
            }
            if (n < 0 && errno == EINTR) continue;
            break;
        }
    }
    pthread_cleanup_pop(1);
}

static void *control_thread_entry(void *arg) {
    char *path = (char *)arg;
    pthread_cleanup_push(free, path);
    handle_control_messages(path);
    pthread_cleanup_pop(1);
    return NULL;
}

static void cleanup_and_exit(int signo) {
    (void)signo;
    if (g_control_path) {
        unlink(g_control_path);
    }
    if (child_pid > 0) {
        kill(child_pid, SIGHUP);
    }
    if (master_fd >= 0) close(master_fd);
    _exit(128 + (signo & 0xff));
}

int main(int argc, char **argv) {
    const char *control_path = getenv("DOTTY_CONTROL_SOCKET");
    const char *shell = NULL;
    int initial_cols = 80;
    int initial_rows = 24;
    const char *initial_cols_env = getenv("DOTTY_INITIAL_COLS");
    const char *initial_rows_env = getenv("DOTTY_INITIAL_ROWS");
    if (initial_cols_env && *initial_cols_env) {
        int parsed = atoi(initial_cols_env);
        if (parsed > 0) initial_cols = parsed;
    }
    if (initial_rows_env && *initial_rows_env) {
        int parsed = atoi(initial_rows_env);
        if (parsed > 0) initial_rows = parsed;
    }
    if (argc > 1) {
        shell = argv[1];
    } else if (getenv("DOTTY_SHELL") && strlen(getenv("DOTTY_SHELL"))>0) {
        shell = getenv("DOTTY_SHELL");
    } else if (getenv("SHELL")) {
        shell = getenv("SHELL");
    } else {
        shell = "/bin/sh";
    }

    // Open PTY master
    master_fd = posix_openpt(O_RDWR | O_NOCTTY);
    if (master_fd < 0) {
        fprintf(stderr, "pty-helper: posix_openpt failed: %s\n", strerror(errno));
        return 1;
    }
    if (grantpt(master_fd) != 0 || unlockpt(master_fd) != 0) {
        fprintf(stderr, "pty-helper: grantpt/unlockpt failed: %s\n", strerror(errno));
        close(master_fd);
        return 1;
    }
    char *slave_name = ptsname(master_fd);
    if (!slave_name) slave_name = "(unknown)";

    // Fork
    pid_t pid = fork();
    if (pid < 0) {
        fprintf(stderr, "pty-helper: fork failed: %s\n", strerror(errno));
        close(master_fd);
        return 1;
    }
    if (pid == 0) {
        // Child: create new session & attach slave as controlling tty
        if (setsid() < 0) {
            // continue anyway
        }
        int slave_fd = open(slave_name, O_RDWR);
        if (slave_fd < 0) {
            fprintf(stderr, "pty-helper (child): open slave %s failed: %s\n", slave_name, strerror(errno));
            _exit(127);
        }
        struct winsize ws;
        ws.ws_col = (unsigned short)initial_cols;
        ws.ws_row = (unsigned short)initial_rows;
        ws.ws_xpixel = 0;
        ws.ws_ypixel = 0;
        ioctl(slave_fd, TIOCSWINSZ, &ws);
#ifdef TIOCSCTTY
        ioctl(slave_fd, TIOCSCTTY, 0);
#endif
        // duplicate slave onto 0,1,2
        if (dup2(slave_fd, STDIN_FILENO) < 0) {
            // ignore
        }
        if (dup2(slave_fd, STDOUT_FILENO) < 0) {
            // ignore
        }
        if (dup2(slave_fd, STDERR_FILENO) < 0) {
            // ignore
        }
        if (slave_fd > STDERR_FILENO) close(slave_fd);
        if (master_fd >= 0) close(master_fd);

        // Exec shell or provided command
        if (argc > 1) {
            // exec argv[1] with remaining args
            // Suppress zsh PROMPT_EOL_MARK by default so shells that set it don't emit an extra '%' on its own line.
            // Users can opt-in to keep the marker by setting DOTTY_KEEP_PROMPT_EOL_MARK=1 in the environment before launching Dotty.
            if (!getenv("DOTTY_KEEP_PROMPT_EOL_MARK")) {
                setenv("PROMPT_EOL_MARK", "", 1);
            }
            execvp(argv[1], &argv[1]);
            fprintf(stderr, "pty-helper: execvp '%s' failed: %s\n", argv[1], strerror(errno));
            _exit(127);
        } else {
            // Exec login shell interactive
            // Suppress zsh PROMPT_EOL_MARK by default (see above)
            if (!getenv("DOTTY_KEEP_PROMPT_EOL_MARK")) {
                setenv("PROMPT_EOL_MARK", "", 1);
            }
            char *sh = (char*)shell;
            char *args[] = {sh, "-i", NULL};
            execvp(sh, args);
            fprintf(stderr, "pty-helper: execvp '%s' failed: %s\n", sh, strerror(errno));
            _exit(127);
        }
    }

    // Parent
    child_pid = pid;
    fprintf(stderr, "pty-helper: started child pid=%d slave=%s\n", (int)child_pid, slave_name);

    // If a control socket is provided, handle it in a joinable thread.
    pthread_t ctrl_thread;
    int control_thread_started = 0;
    if (control_path) {
        g_control_path = strdup(control_path);
        char *path_copy = strdup(control_path);
        if (path_copy && pthread_create(&ctrl_thread, NULL, control_thread_entry, path_copy) == 0) {
            control_thread_started = 1;
        } else {
            free(path_copy);
        }
    }

    pthread_t t1, t2;
    int output_thread_started = pthread_create(&t1, NULL, proxy_master_to_stdout, NULL) == 0;
    int input_thread_started = pthread_create(&t2, NULL, proxy_stdin_to_master, NULL) == 0;

    signal(SIGPIPE, SIG_IGN);
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_handler = cleanup_and_exit;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = 0;
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGTERM, &sa, NULL);
    sigaction(SIGHUP, &sa, NULL);

    int status = 0;
    pid_t waited;
    do {
        waited = waitpid(child_pid, &status, 0);
    } while (waited < 0 && errno == EINTR);

    // Drain all PTY output before stopping the other blocking proxy threads.
    if (output_thread_started) pthread_join(t1, NULL);
    if (input_thread_started) {
        pthread_cancel(t2);
        pthread_join(t2, NULL);
    }
    if (control_thread_started) {
        pthread_cancel(ctrl_thread);
        pthread_join(ctrl_thread, NULL);
    }

    if (master_fd >= 0) {
        close(master_fd);
        master_fd = -1;
    }
    if (control_sock_fd >= 0) {
        close(control_sock_fd);
        control_sock_fd = -1;
    }
    if (g_control_path) {
        unlink(g_control_path);
        free(g_control_path);
        g_control_path = NULL;
    } else if (control_path) {
        unlink(control_path);
    }

    int exit_code = 0;
    if (waited >= 0 && WIFEXITED(status)) exit_code = WEXITSTATUS(status);
    else if (waited >= 0 && WIFSIGNALED(status)) exit_code = 128 + WTERMSIG(status);
    _exit(exit_code);
}
