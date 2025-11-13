using System;
using System.Text;
using System.Thread ing;
using Dotty.Core;

var pty = UnixPty.Start("/bin/sh", "/tmp", 80, 24, "echo TEST");
Thread.Sleep(500);

var buffer = new byte[256];
int read = pty.Output.Read(buffer, 0, buffer.Length);
Console.WriteLine($"Read: {read} bytes");

if (read > 0)
{
    string output = Encoding.UTF8.GetString(buffer, 0, read);
    Console.WriteLine($"Output: {output}");
}

pty.Dispose();
