#version 330 core
in vec2 TexCoord;
out vec4 FragColor;

// Uniforms passed from C# CPU
uniform vec2 u_ScreenSize;      // Pixel size of the viewport
uniform vec2 u_CellSize;        // Pixel size of a single terminal cell
uniform vec2 u_GridSize;        // Columns and Rows mapping (e.g. 80, 24)
uniform sampler2D u_AtlasTex;   // Pre-generated texture of all Harfbuzz glyphs
// uniform samplerBuffer u_CellSSBO; // Packed Cell Data [GlyphID, FG, BG]

void main()
{
    // 1. Determine cell index based on screen pixel
    vec2 pixelPos = TexCoord * u_ScreenSize;
    float col = floor(pixelPos.x / u_CellSize.x);
    float row = floor(pixelPos.y / u_CellSize.y);
    
    // Bounds check
    if (col >= u_GridSize.x || row >= u_GridSize.y) {
        FragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    // [TODO/PLACEHOLDER]: Once C# SSBO data is packed, lookup glyph ID here:
    // int cellIndex = int(row * u_GridSize.x + col);
    // vec4 cellData = texelFetch(u_CellSSBO, cellIndex);
    // float glyphId = cellData.r;

    // Simulated Harfbuzz render: just render a debug color representing the cell grid
    vec2 inCellPos = mod(pixelPos, u_CellSize);
    
    // Make borders
    if (inCellPos.x < 1.0 || inCellPos.y < 1.0) {
        FragColor = vec4(0.2, 0.2, 0.2, 1.0);
    } else {
        // gradient based on column/row to prove shader pipeline works!
        FragColor = vec4(col / u_GridSize.x, row / u_GridSize.y, 0.5, 1.0);
    }
}
