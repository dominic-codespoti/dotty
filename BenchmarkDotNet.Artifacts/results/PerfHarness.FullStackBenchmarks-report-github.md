```

BenchmarkDotNet v0.15.8, Linux Arch Linux
AMD Ryzen 7 7735HS with Radeon Graphics 1.11GHz, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.0, 10.0.25.52411), X64 RyuJIT x86-64-v3
  Job-CNUJVU : .NET 10.0.0, X64 NativeAOT x86-64-v3

InvocationCount=1  UnrollFactor=1  

```
| Method                 | Mean      | Error    | StdDev   | Median    | Allocated |
|----------------------- |----------:|---------:|---------:|----------:|----------:|
| ParseHeavySgr          |  14.55 ms | 0.289 ms | 0.762 ms |  14.63 ms | 4000000 B |
| ParseScrollHeavy       |  70.72 ms | 1.383 ms | 1.226 ms |  70.11 ms |         - |
| ParseLongLinesAutoWrap |  66.90 ms | 0.816 ms | 0.723 ms |  66.67 ms |         - |
| ParseMassiveText       | 136.03 ms | 0.942 ms | 0.835 ms | 136.03 ms |         - |
| ParseComplexUnicode    |  26.14 ms | 0.523 ms | 1.431 ms |  25.85 ms | 3300024 B |
| ParseAltBufferTui      |  23.28 ms | 0.465 ms | 1.060 ms |  22.88 ms |         - |
