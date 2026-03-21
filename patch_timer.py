import sys

content = open("src/Dotty.App/Controls/TerminalGrid.axaml.cs", "r").read()

if "if (e.Property == IsVisibleProperty)" not in content:
    visibility_check = """
            if (e.Property == IsVisibleProperty)
            {
                if (IsVisible)
                {
                    if (_blinkTimer != null) _blinkTimer.Start();
                }
                else
                {
                    if (_blinkTimer != null) _blinkTimer.Stop();
                }
            }
"""
    content = content.replace("if (e.Property == CursorBlinkIntervalProperty)", visibility_check + "\n            else if (e.Property == CursorBlinkIntervalProperty)")
    
    with open("src/Dotty.App/Controls/TerminalGrid.axaml.cs", "w") as f:
        f.write(content)
