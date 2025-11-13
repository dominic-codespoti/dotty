using System;

// This project folder has been superseded by `src/Dotty.Subprocess`.
// The runtime helper previously implemented here has been moved to the
// new `Dotty.Subprocess` project to better reflect its role as the
// GUI-launched subprocess. Keeping a lightweight shim here helps avoid
// breaking any external scripts that reference this path.

namespace Dotty.PtyTests;

class LegacyProgram
{
    static int Main(string[] args)
    {
        Console.WriteLine("This project has moved to src/Dotty.Subprocess/.");
        Console.WriteLine("Please update references to use Dotty.Subprocess.");
        return 0;
    }
}

