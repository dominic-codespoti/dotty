#version 330 core
layout(location = 0) in vec2 position;
out vec2 TexCoord;

void main()
{
    // Map fullscreen quad [-1,1] to [0,1] texture coords
    TexCoord = position * 0.5 + 0.5;
    TexCoord.y = 1.0 - TexCoord.y; // Flip Y for graphics APIs
    gl_Position = vec4(position, 0.0, 1.0);
}
