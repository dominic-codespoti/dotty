# Large File Test Data

This directory contains large test files for 500k line buffer testing.

## Test Files

### Line Files
- `500k-lines.txt` - 500,000 lines for comprehensive buffer testing
- `100k-lines.txt` - 100,000 lines for medium-scale testing
- `50k-lines.txt` - 50,000 lines for smaller tests

### Content Types
- `repetitive.txt` - Repetitive pattern content
- `unique.txt` - Each line has unique content (GUIDs)
- `mixed.txt` - Mix of content types
- `ansi-colored.txt` - Large file with ANSI color sequences

## Generation

Files can be generated programmatically in tests. See LargeBufferE2ETests.cs.

Example generation:
```csharp
for (int i = 0; i < 500000; i++)
{
    Console.WriteLine($"Line {i}: {Guid.NewGuid()}");
}
```

## Memory Requirements

- 500k lines ≈ 50-100 MB of content
- Tests expect max 2GB memory usage
- Processing time target: < 5 minutes
