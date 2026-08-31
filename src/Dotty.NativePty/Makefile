BINDIR ?= bin
TARGET ?= $(BINDIR)/pty-helper
CC ?= cc
CFLAGS ?= -O2 -Wall -Wextra
LDFLAGS ?= -lpthread

all: $(TARGET)

$(BINDIR):
	mkdir -p $(BINDIR)

$(TARGET): $(BINDIR) pty-helper.c
	$(CC) $(CFLAGS) -o $(TARGET) pty-helper.c $(LDFLAGS)

clean:
	rm -rf $(BINDIR)

.PHONY: all clean
