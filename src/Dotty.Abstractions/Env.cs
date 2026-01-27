using System;

namespace Dotty
{
    // Simple environment shim that disables all environment-driven
    // behaviors. Returns null for any lookup so code paths guarded by
    // env vars will behave as if the variable is unset.
    public static class Env
    {
        public static string? GetEnvironmentVariable(string name)
        {
            return null;
        }
    }
}
