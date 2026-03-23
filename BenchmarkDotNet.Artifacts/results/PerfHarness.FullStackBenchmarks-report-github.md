```

BenchmarkDotNet v0.15.8, Linux Arch Linux
AMD Ryzen 7 7735HS with Radeon Graphics 1.11GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.0, 10.0.25.52411), X64 RyuJIT x86-64-v3
  Job-CNUJVU : .NET 10.0.0, X64 NativeAOT x86-64-v3

InvocationCount=1  UnrollFactor=1  

```
| Method                 | Mean      | Error    | StdDev   | Allocated |
|----------------------- |----------:|---------:|---------:|----------:|
| ParseHeavySgr          |  11.99 ms | 0.233 ms | 0.311 ms |         - |
| ParseScrollHeavy       |  70.31 ms | 0.384 ms | 0.300 ms |         - |
| ParseLongLinesAutoWrap |  73.10 ms | 0.526 ms | 0.467 ms |         - |
| ParseMassiveText       | 135.15 ms | 1.081 ms | 0.958 ms |         - |
| ParseComplexUnicode    |  25.74 ms | 0.510 ms | 0.747 ms |         - |
| ParseAltBufferTui      |  23.02 ms | 0.438 ms | 0.450 ms |         - |
