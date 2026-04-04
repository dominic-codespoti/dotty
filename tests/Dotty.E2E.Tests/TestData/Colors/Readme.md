# ANSI Color Test Sequences

This directory contains ANSI color test sequences for E2E testing.

## Test Files

### Basic 8 Colors
- `basic-colors.txt` - Basic 8 foreground and background colors
- `bright-colors.txt` - Bright color variants (90-97, 100-107)

### 256 Colors
- `256-color-cube.txt` - 6x6x6 color cube (colors 16-231)
- `256-grayscale.txt` - Grayscale ramp (colors 232-255)
- `256-all.txt` - All 256 colors in one file

### TrueColor (24-bit)
- `truecolor-red.txt` - Pure red gradient
- `truecolor-green.txt` - Pure green gradient
- `truecolor-blue.txt` - Pure blue gradient
- `truecolor-rainbow.txt` - Rainbow gradient
- `truecolor-all.txt` - Comprehensive TrueColor test

### Text Attributes
- `attributes.txt` - All text attributes (bold, dim, italic, underline, etc.)
- `combinations.txt` - Combined color + attribute tests

## Usage

Tests in ColorsE2ETests.cs use these sequences to verify color rendering.
