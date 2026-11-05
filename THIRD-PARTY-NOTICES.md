# Third-party notices

Dotty is licensed under the MIT license (see `License.md`). Dotty release
archives also contain, or are compiled together with, the third-party components
below. Each component remains under its own license; the notices that those
licenses require are reproduced in this file. The GLFW license is additionally
shipped as `licenses/GLFW-LICENSE.md` in every archive.

## Components

| Component | Version | How it ships | License |
|---|---|---|---|
| .NET runtime and libraries | .NET 11 (see `global.json`) | Compiled into the `dotty` executable by NativeAOT | MIT |
| Silk.NET (`Silk.NET.Windowing`, `Silk.NET.Input`, `Silk.NET.OpenGL` and their `Silk.NET.*` dependencies) | 2.23.0 | Compiled into `dotty` | MIT |
| Microsoft.DotNet.PlatformAbstractions, Microsoft.Extensions.DependencyModel (Silk.NET dependencies) | 3.1.6, 9.0.9 | Compiled into `dotty` where referenced | MIT |
| SkiaSharp, SkiaSharp.HarfBuzz | 4.151.1 | Compiled into `dotty`; native `libSkiaSharp` beside it | MIT |
| Skia (inside `libSkiaSharp`) | as bundled by SkiaSharp 4.151.1 | Native library | BSD-3-Clause |
| HarfBuzzSharp | 14.2.1.2 | Compiled into `dotty`; native `libHarfBuzzSharp` beside it | MIT |
| HarfBuzz (inside `libHarfBuzzSharp`) | as bundled by HarfBuzzSharp 14.2.1.2 | Native library | "Old MIT" |
| KeraLua | 1.4.9 | Compiled into `dotty` | MIT |
| Lua 5.4 (`liblua54`/`lua54.dll`, from KeraLua) | as bundled by KeraLua 1.4.9 | Native library | MIT |
| GLFW (Clear Code 3.6 development fork, commit `068858eece20cd6b6128f6f07a1d61f2dc2cbfaf`, **modified**: local patches listed in `vendor/glfw/SOURCE.json`) | 3.6.0 | Native `libglfw.so.3`/`libglfw.3.dylib`/`glfw3.dll`, built from `vendor/glfw/source`; replaces the GLFW 3.4.0 binary of the `Ultz.Native.GLFW` package that `Silk.NET.Windowing.Glfw` restores | zlib/libpng |
| Unicode Character Database 16.0.0 | 16.0.0 | Data used to generate the character-width tables compiled into `dotty` | Unicode License v3 |
| libX11 Xcms color conversion (adapted) | libX11 Xcms | Portable conversion code and fallback profile compiled into `dotty`; no runtime X11 dependency | Tektronix Color Management System terms; The Open Group terms |

`libSkiaSharp` and `libHarfBuzzSharp` incorporate further third-party code. The
SkiaSharp project lists ANGLE, HarfBuzz, Skia, etc1, gif, libpng, DNG SDK,
expat, FreeType, ICU, imgui, jsoncpp, libjpeg-turbo, libwebp, piex, SDL, sfntly,
SPIR-V Headers, SPIR-V Tools and zlib, with their license texts, in
[External-Dependency-Info.txt](https://github.com/mono/SkiaSharp/blob/main/External-Dependency-Info.txt).
Which of these are compiled into the desktop native libraries Dotty ships is
determined by SkiaSharp's build, not by Dotty. Likewise the .NET runtime's own
third-party components are listed in
[dotnet/runtime THIRD-PARTY-NOTICES.TXT](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT).

Portions of the native SkiaSharp library may be copyright © The FreeType
Project (www.freetype.org). All rights reserved.

## NuGet package inventory

Every NuGet package restored for `src/Dotty` (`obj/project.assets.json`), except the
NativeAOT/trimming build tools (`Microsoft.DotNet.ILCompiler`,
`runtime.<rid>.Microsoft.DotNet.ILCompiler`, `Microsoft.NET.ILLink.Tasks`), which compile
`dotty` but are not shipped. `scripts/dist/check_notices.py` fails CI when this table and the
restore disagree. "Platforms" lists the host OSes whose restore includes the package.

| Package | Version | Platforms | Component above |
|---|---|---|---|
| HarfBuzzSharp | 14.2.1.2 | all | HarfBuzzSharp |
| HarfBuzzSharp.NativeAssets.Linux | 14.2.1.2 | linux | HarfBuzzSharp |
| HarfBuzzSharp.NativeAssets.macOS | 14.2.1.2 | all | HarfBuzzSharp |
| HarfBuzzSharp.NativeAssets.Win32 | 14.2.1.2 | all | HarfBuzzSharp |
| KeraLua | 1.4.9 | all | KeraLua |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | all | Microsoft.DotNet.PlatformAbstractions |
| Microsoft.Extensions.DependencyModel | 9.0.9 | all | Microsoft.Extensions.DependencyModel |
| Silk.NET.Core | 2.23.0 | all | Silk.NET |
| Silk.NET.GLFW | 2.23.0 | all | Silk.NET |
| Silk.NET.Input | 2.23.0 | all | Silk.NET |
| Silk.NET.Input.Common | 2.23.0 | all | Silk.NET |
| Silk.NET.Input.Glfw | 2.23.0 | all | Silk.NET |
| Silk.NET.Maths | 2.23.0 | all | Silk.NET |
| Silk.NET.OpenGL | 2.23.0 | all | Silk.NET |
| Silk.NET.Windowing | 2.23.0 | all | Silk.NET |
| Silk.NET.Windowing.Common | 2.23.0 | all | Silk.NET |
| Silk.NET.Windowing.Glfw | 2.23.0 | all | Silk.NET |
| SkiaSharp | 4.151.1 | all | SkiaSharp |
| SkiaSharp.HarfBuzz | 4.151.1 | all | SkiaSharp |
| SkiaSharp.NativeAssets.Linux | 4.151.1 | linux | SkiaSharp |
| SkiaSharp.NativeAssets.macOS | 4.151.1 | all | SkiaSharp |
| SkiaSharp.NativeAssets.Win32 | 4.151.1 | all | SkiaSharp |
| Ultz.Native.GLFW | 3.4.0 | all | GLFW |

## License texts

### MIT license: .NET runtime and libraries, Microsoft packages

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### MIT license: Silk.NET

```
MIT License

- Copyright (c) 2019-2020 Ultz Limited
- Copyright (c) 2021- .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### MIT license: SkiaSharp, SkiaSharp.HarfBuzz, HarfBuzzSharp

```
Copyright (c) 2015-2016 Xamarin, Inc.
Copyright (c) 2017-2018 Microsoft Corporation.

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### BSD-3-Clause: Skia

```
Copyright (c) 2011 Google Inc. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of the copyright holder nor the names of its
    contributors may be used to endorse or promote products derived
    from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### "Old MIT": HarfBuzz

```
HarfBuzz is licensed under the so-called "Old MIT" license.

Copyright © 2010-2022  Google, Inc.
Copyright © 2015-2020  Ebrahim Byagowi
Copyright © 2019,2020  Facebook, Inc.
Copyright © 2012,2015  Mozilla Foundation
Copyright © 2011  Codethink Limited
Copyright © 2008,2010  Nokia Corporation and/or its subsidiary(-ies)
Copyright © 2009  Keith Stribley
Copyright © 2011  Martin Hosken and SIL International
Copyright © 2007  Chris Wilson
Copyright © 2005,2006,2020,2021,2022,2023  Behdad Esfahbod
Copyright © 2004,2007,2008,2009,2010,2013,2021,2022,2023  Red Hat, Inc.
Copyright © 1998-2005  David Turner and Werner Lemberg
Copyright © 2016  Igalia S.L.
Copyright © 2022  Matthias Clasen
Copyright © 2018,2021  Khaled Hosny
Copyright © 2018,2019,2020  Adobe, Inc
Copyright © 2013-2015  Alexei Podtelezhnikov

For full copyright notices consult the individual files in the package.

Permission is hereby granted, without written agreement and without
license or royalty fees, to use, copy, modify, and distribute this
software and its documentation for any purpose, provided that the
above copyright notice and the following two paragraphs appear in
all copies of this software.

IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE TO ANY PARTY FOR
DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES
ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION, EVEN
IF THE COPYRIGHT HOLDER HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH
DAMAGE.

THE COPYRIGHT HOLDER SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING,
BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE.  THE SOFTWARE PROVIDED HEREUNDER IS
ON AN "AS IS" BASIS, AND THE COPYRIGHT HOLDER HAS NO OBLIGATION TO
PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.
```

### MIT license: KeraLua

```
Copyright (c) 2019 Vinicius Jarina (viniciusjarina@gmail.com)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### MIT license: Lua

```
Copyright © 1994–2026 Lua.org, PUC-Rio.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

### zlib/libpng license: GLFW

The verbatim text of `vendor/glfw/source/LICENSE.md`, also shipped as
`licenses/GLFW-LICENSE.md`. Dotty's copy is an altered source version: the
local patches are listed in `vendor/glfw/SOURCE.json`.

```
Copyright (c) 2002-2006 Marcus Geelnard

Copyright (c) 2006-2019 Camilla Löwy

This software is provided 'as-is', without any express or implied
warranty. In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
   claim that you wrote the original software. If you use this software
   in a product, an acknowledgment in the product documentation would
   be appreciated but is not required.

2. Altered source versions must be plainly marked as such, and must not
   be misrepresented as being the original software.

3. This notice may not be removed or altered from any source
   distribution.
```

### Tektronix Color Management System and The Open Group notices: adapted libX11 Xcms

The portable OSC color conversion implementation in
`src/Dotty.Terminal/Adapter/OscColorParser.cs` adapts the libX11 Xcms
conversions and fallback profile. It has no runtime X11 dependency.

```
Code and supporting documentation (c) Copyright 1990 1991 Tektronix, Inc.
All Rights Reserved.

This file is based on an X Window System-specific implementation of Xcms
based on the TekColor Color Management System. Permission is hereby granted
to use, copy, modify, sell, and otherwise distribute this software and its
documentation for any purpose and without fee, provided that this copyright,
permission, and disclaimer notice is reproduced in all copies of this
software and in supporting documentation. TekColor is a trademark of
Tektronix, Inc.

Tektronix makes no representation about the suitability of this software
for any purpose. It is provided "as is" and with all faults.

TEKTRONIX DISCLAIMS ALL WARRANTIES APPLICABLE TO THIS SOFTWARE,
INCLUDING THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
PARTICULAR PURPOSE. IN NO EVENT SHALL TEKTRONIX BE LIABLE FOR ANY
SPECIAL, INDIRECT OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER
RESULTING FROM LOSS OF USE, DATA, OR PROFITS, WHETHER IN AN ACTION OF
CONTRACT, NEGLIGENCE, OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN
CONNECTION WITH THE USE OR THE PERFORMANCE OF THIS SOFTWARE.

Tektronix Color Management System permission conditions for TekHVC:
1. This copyright, permission, and disclaimer notice is reproduced in all
   copies of this software and any modification thereof and in supporting
   documentation;
2. Any color-handling application which displays TekHVC color cooordinates
   identifies these as TekHVC color coordinates in any interface that
   displays these coordinates and in any associated documentation;
3. The term "TekHVC" is always used, and is only used, in association with
   the mathematical derivations of the TekHVC Color Space, including those
   provided in this file and any equivalent pathways and mathematical
   derivations, regardless of digital (e.g., floating point or integer)
   representation.

Copyright 1990, 1998 The Open Group

Permission to use, copy, modify, distribute, and sell this software and its
documentation for any purpose is hereby granted without fee, provided that
the above copyright notice appear in all copies and that both that
copyright notice and this permission notice appear in supporting
documentation.

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
THE OPEN GROUP BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF
OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

Except as contained in this notice, the name of The Open Group shall not be
used in advertising or otherwise to promote the sale, use or other dealings
in this Software without prior written authorization from The Open Group.
```


### Unicode License v3: Unicode Character Database

```
UNICODE LICENSE V3

COPYRIGHT AND PERMISSION NOTICE

Copyright © 1991-2026 Unicode, Inc.

NOTICE TO USER: Carefully read the following legal agreement. BY
DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR
SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE
TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

Permission is hereby granted, free of charge, to any person obtaining a
copy of data files and any associated documentation (the "Data Files") or
software and any associated documentation (the "Software") to deal in the
Data Files or Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, and/or sell
copies of the Data Files or Software, and to permit persons to whom the
Data Files or Software are furnished to do so, provided that either (a)
this copyright and permission notice appear with all copies of the Data
Files or Software, or (b) this copyright and permission notice appear in
associated Documentation.

THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
THIRD PARTY RIGHTS.

IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE
BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
FILES OR SOFTWARE.

Except as contained in this notice, the name of a copyright holder shall
not be used in advertising or otherwise to promote the sale, use or other
dealings in these Data Files or Software without prior written
authorization of the copyright holder.
```
