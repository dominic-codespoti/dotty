# Search Test Data

This directory contains search test patterns and content.

## Test Files

### Patterns
- `simple-patterns.txt` - Simple text search patterns
- `regex-patterns.txt` - Regular expression patterns
- `case-test.txt` - Case-sensitive test content
- `unicode-search.txt` - Unicode search test content

### Markers
- `target-markers.txt` - Specific target strings for search tests
- `line-markers.txt` - Line number markers for large buffer tests

## Search Patterns

### Simple Text
- "ERROR" - Find error messages
- "WARNING" - Find warning messages
- "TARGET" - Generic target marker

### Regex Patterns
- `[0-9]+` - Find numbers
- `[A-Z]{3,}` - Find uppercase words
- `\b\w+@\w+\.\w+\b` - Find email patterns
- `test_[a-f0-9]+_data` - Complex pattern

## Usage

Used in SearchE2ETests.cs for comprehensive search testing.
