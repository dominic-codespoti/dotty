#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
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
    while (1) {
        ssize_t r = read(master_fd, buf, sizeof(buf));
        if (r <= 0) break;
        ssize_t w = 0;
        while (w < r) {
            ssize_t n = write(STDOUT_FILENO, buf + w, r - w);
            if (n <= 0) break;
            w += n;
        }
    }
    return NULL;
}

static void *proxy_stdin_to_master(void *arg) {
    (void)arg;
    char buf[4096];
    while (1) {
        ssize_t r = read(STDIN_FILENO, buf, sizeof(buf));
        if (r <= 0) break;
        ssize_t w = 0;
        while (w < r) {
            ssize_t n = write(master_fd, buf + w, r - w);
            if (n <= 0) break;
            w += n;
        }
    }
    return NULL;
}

static void handle_control_messages(const char *path) {
    if (!path) return;
    int lsock = socket(AF_UNIX, SOCK_STREAM, 0);
    if (lsock < 0) return;
    struct sockaddr_un addr;
    memset(&addr, 0, sizeof(addr));
    addr.sun_family = AF_UNIX;
    strncpy(addr.sun_path, path, sizeof(addr.sun_path) - 1);
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
    // Accept one client and read lines (blocking)
    int asock = accept(lsock, NULL, NULL);
    if (asock < 0) {
        close(lsock);
        unlink(path);
        return;
    }
    // set close-on-exec on accepted socket
    int flags = fcntl(asock, F_GETFD);
    if (flags != -1) fcntl(asock, F_SETFD, flags | FD_CLOEXEC);
    FILE *f = fdopen(asock, "r");
    if (!f) {
        close(asock);
        close(lsock);
        unlink(path);
        return;
    }
    char line[1024];
    while (fgets(line, sizeof(line), f)) {
        // Look for resize JSON: {"type":"resize","cols":NN,"rows":MM}
        if (strstr(line, "resize") != NULL) {
            int cols = 80, rows = 24;
            // crude parse
            char *c = strstr(line, "\"cols\"");
            if (c) sscanf(c, "\"cols\"%*[^0-9]%d", &cols);
            char *r = strstr(line, "\"rows\"");
            if (r) sscanf(r, "\"rows\"%*[^0-9]%d", &rows);
            struct winsize ws;
            ws.ws_col = cols;
            ws.ws_row = rows;
            ws.ws_xpixel = 0;
            ws.ws_ypixel = 0;
            ioctl(master_fd, TIOCSWINSZ, &ws);
        }
    }
    fclose(f);
    close(lsock);
    unlink(path);
}

static void *control_thread_entry(void *arg) {
    char *path = (char *)arg;
    if (path) {
        handle_control_messages(path);
        free(path);
    }
    return NULL;
}

static void cleanup_and_exit(int signo) {
    (void)signo;
    if (g_control_path) {
        unlink(g_control_path);
    }
    // ensure master closed
    if (master_fd >= 0) close(master_fd);
    _exit(128 + (signo & 0xff));
}

int main(int argc, char **argv) {
    const char *control_path = getenv("DOTTY_CONTROL_SOCKET");
    const char *shell = NULL;
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

    // If a control socket is provided, handle it in a background thread
    pthread_t ctrl_thread;
    if (control_path) {
        // store global path for signal cleanup
        g_control_path = strdup(control_path);
        char *path_copy = strdup(control_path);
        if (pthread_create(&ctrl_thread, NULL, control_thread_entry, path_copy) == 0) {
            pthread_detach(ctrl_thread);
        } else {
            free(path_copy);
        }
    }

    // Start proxy threads
    pthread_t t1, t2;
    pthread_create(&t1, NULL, proxy_master_to_stdout, NULL);
    pthread_create(&t2, NULL, proxy_stdin_to_master, NULL);

    // Ignore SIGPIPE so writes to closed pipes don't kill the helper
    signal(SIGPIPE, SIG_IGN);
    // Install simple cleanup handlers to unlink control socket on termination
    struct sigaction sa;
    sa.sa_handler = cleanup_and_exit;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = 0;
    sigaction(SIGINT, &sa, NULL);
    sigaction(SIGTERM, &sa, NULL);
    sigaction(SIGHUP, &sa, NULL);

    // Wait for child to exit
    int status = 0;
    waitpid(child_pid, &status, 0);

    // cleanup
    // Close master; threads will exit when read/write return
    if (master_fd >= 0) close(master_fd);
    if (control_path && control_sock_fd >= 0) close(control_sock_fd);

    // Give threads a moment
    sleep(1);

    if (WIFEXITED(status)) return WEXITSTATUS(status);
    if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
    return 0;
}
