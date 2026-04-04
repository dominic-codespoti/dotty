# Dotty Terminal Search UI Implementation Summary

## Overview
Implemented an interactive search UI for the Dotty terminal emulator that allows users to search through terminal buffer content (both visible and scrollback) with highlighting and navigation.

## New Files Created

### 1. `/src/Dotty.Terminal/Adapter/Buffer/SearchMatch.cs`
- Simple struct representing a search match position (row, column, length)
- Implements IEquatable for proper comparison
- Used for storing match positions from search operations

### 2. `/src/Dotty.Terminal/Adapter/Buffer/TerminalSearch.cs`
- Core search functionality class
- Searches through both scrollback lines and visible buffer content
- Supports:
  - Case-sensitive/insensitive search
  - Regular expression search (with fallback on invalid regex)
  - Zero-allocation buffer pooling for line text extraction
  - Match navigation (next/previous)
  - Match persistence and refreshing

### 3. `/src/Dotty.App/Controls/SearchOverlay.axaml`
- Avalonia XAML for the search UI overlay
- Includes:
  - Search text input field
  - Case-sensitive toggle (Aa button)
  - Regex toggle (.* button)
  - Match counter display (e.g., "3/12")
  - Previous/Next navigation buttons
  - Close button
  - Keyboard shortcuts for all actions

### 4. `/src/Dotty.App/Controls/SearchOverlay.axaml.cs`
- Code-behind for the search overlay
- Handles user input and events
- Manages search state
- Events:
  - `SearchRequested` - when search is performed
  - `CloseRequested` - when user wants to close search
  - `MatchNavigated` - when navigating to a match
- Keyboard shortcuts:
  - Enter: Next match
  - Shift+Enter: Previous match
  - Escape: Close search
  - Alt+C: Toggle case sensitive
  - Alt+R: Toggle regex mode

## Modified Files

### 5. `/src/Dotty.Terminal/Adapter/Buffer/TerminalBuffer.cs`
- Added using directive for System.Collections.Generic (for search support)

### 6. `/src/Dotty.App/Controls/Canvas/Rendering/TerminalVisualHandler.cs`
- Extended `TerminalRenderState` record to include:
  - `SearchMatches` - list of matches to highlight
  - `CurrentSearchMatchIndex` - index of currently selected match
- Added `DrawSearchHighlights()` method:
  - Draws yellow background for all matches
  - Draws orange background for current match
  - Works with scrollback content

### 7. `/src/Dotty.App/Controls/Canvas/TerminalCanvas.cs`
- Added search highlighting state fields
- Added `SetSearchMatches()` public method to update highlights
- Added `ClearSearchMatches()` method to remove highlights
- Updated `TerminalRenderState` creation to include search data

### 8. `/src/Dotty.App/Views/TerminalView.axaml`
- Added SearchOverlay control as overlay on top of terminal grid
- Positioned at top of terminal area

### 9. `/src/Dotty.App/Views/TerminalView.axaml.cs`
- Added search fields and state management
- Added search methods:
  - `ToggleSearch()` - show/hide search
  - `ShowSearch()` - display search overlay
  - `HideSearch()` - close search and clear highlights
  - `RefreshSearch()` - update search after buffer changes
  - `InitializeSearch()` - set up search overlay
- Integrated search with render updates (auto-refresh on buffer changes)

### 10. `/src/Dotty.App/Views/MainWindow.axaml.cs`
- Added `ToggleSearch()` method to invoke search on active terminal
- Added Ctrl+Shift+F key handler for Search action

## Key Features

1. **Non-intrusive Overlay**: Search UI appears at top of terminal without blocking output
2. **Scrollback Search**: Searches through entire scrollback history, not just visible area
3. **Visual Highlighting**: All matches highlighted in yellow, current match in orange
4. **Navigation**: Next/Previous buttons and keyboard shortcuts (Enter/Shift+Enter)
5. **Options**: Case-sensitive and regex toggles with Alt+C and Alt+R shortcuts
6. **Live Updates**: Search refreshes automatically as new content arrives
7. **Zero-allocation Philosophy**: Uses ArrayPool for buffer operations
8. **Proper Focus Management**: Search gets focus when open, terminal keeps receiving output

## Default Key Binding
- `Ctrl+Shift+F` - Toggle search overlay

## Testing Instructions

### Basic Search Test
1. Build and run Dotty
2. Generate some terminal output (e.g., `ls -la`, `cat /etc/passwd`, or `seq 1 100`)
3. Press `Ctrl+Shift+F` to open search
4. Type a search query (e.g., "root" or a number)
5. Verify:
   - Matches are highlighted in yellow
   - Match counter shows correct count (e.g., "3/12")
   - Press Enter to navigate to next match (highlighted in orange)
   - Press Shift+Enter to navigate to previous match
   - Press Escape to close search

### Scrollback Search Test
1. Generate lots of output (e.g., `seq 1 1000`)
2. Scroll up to see older content
3. Open search and search for a number in the scrollback
4. Verify matches are found in scrollback content

### Case Sensitive Test
1. Type text with mixed case
2. Open search and search with default (case-insensitive)
3. Toggle "Aa" button for case-sensitive
4. Verify case-sensitive matching works

### Regex Search Test
1. Generate output with patterns (e.g., `echo "test123 test456"`)
2. Open search and toggle ".*" button for regex mode
3. Search for pattern like `test\\d+`
4. Verify regex matching works

### Navigation Test
1. Open search and find multiple matches
2. Click up/down arrow buttons to navigate
3. Verify current match is highlighted in orange
4. Test wrap-around (going past last match wraps to first)

### Buffer Update Test
1. Open search with active matches
2. Generate new terminal output
3. Verify search highlights update to include new content
4. Verify current match position is preserved if possible

### Focus Test
1. Open search and type query
2. Verify terminal still receives output (run a command)
3. Close search with Escape
4. Verify terminal gets focus back and receives keyboard input

### Close and Reopen Test
1. Open search, perform search
2. Close with Escape
3. Reopen with Ctrl+Shift+F
4. Verify previous search state is restored

## Performance Considerations

- Search uses ArrayPool for buffer operations to minimize allocations
- Search runs on UI thread but processes in chunks for large buffers
- Highlights are rendered efficiently using SkiaSharp batch drawing
- Search refresh is throttled to avoid excessive re-searching during high output

## Future Enhancements (Optional)

- Search in reverse order (bottom to top)
- Whole word search option
- Search history persistence
- Find and replace functionality
- Fuzzy search option
- Incremental search (search as you type with debouncing)
