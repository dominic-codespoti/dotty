using System;
using System.Collections.Concurrent;
class Test {    void M() { ConcurrentDictionary<string, string> d = new(); d.GetAlternateLookup<ReadOnlySpan<char>>(); } }
