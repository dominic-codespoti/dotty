import sys, re
content = open("src/Dotty.App/Controls/Canvas/TerminalCanvas.cs", "r").read()

content = re.sub(r'public void RequestFrame\(\)\s*\{', 'public void RequestFrame() { if (!IsVisible) return;', content)

with open("src/Dotty.App/Controls/Canvas/TerminalCanvas.cs", "w") as f:
    f.write(content)
