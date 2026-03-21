import sys
content = open("src/Dotty.App/Controls/Canvas/TerminalCanvas.cs", "r").read()

if "if (change.Property == IsVisibleProperty)" not in content:
    prop_method = """protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && IsVisible)
            {
                RequestFrame();
            }
        }
"""
    if "protected override void OnPropertyChanged" in content:
        content = content.replace("base.OnPropertyChanged(change);", "base.OnPropertyChanged(change);\n            if (change.Property == IsVisibleProperty && IsVisible) { var _ = 0; /* hack */ }\n")
    else:
        # just inject it somewhere
        content = content.replace("public TerminalCanvas()", prop_method + "\n        public TerminalCanvas()")

    with open("src/Dotty.App/Controls/Canvas/TerminalCanvas.cs", "w") as f:
        f.write(content)
