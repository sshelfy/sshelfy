# Third-party notices

sshelfy is licensed under the MIT License (see `LICENSE`). It includes or depends on the
following third-party components, each under its own license. The full license texts are
available at the linked locations and in the corresponding NuGet packages.

| Component | Used for | License |
|---|---|---|
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent) | User interface | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) (via Avalonia), including [Skia](https://skia.org) | Rendering | MIT; Skia: BSD-3-Clause |
| [HarfBuzzSharp](https://github.com/mono/SkiaSharp) (via Avalonia), including [HarfBuzz](https://harfbuzz.github.io) | Text shaping | MIT; HarfBuzz: Old MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM helpers | MIT |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | SSH and SFTP protocol | MIT |
| [BouncyCastle.Cryptography](https://github.com/bcgit/bc-csharp) (via SSH.NET) | Ed25519 and key handling | MIT |
| [Microsoft.Extensions.DependencyInjection / Logging](https://github.com/dotnet/runtime) | Composition and logging | MIT |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime) | Windows DPAPI | MIT |
| [.NET runtime](https://github.com/dotnet/runtime) (bundled in the self-contained build) | Runtime | MIT |
| [Lucide icons](https://lucide.dev) (converted to geometry in `Themes/Icons.axaml`) | Icons | ISC |

Test-only dependencies (xUnit, Avalonia.Headless.XUnit, Microsoft.NET.Test.Sdk, coverlet) are not
shipped with the application.

## Lucide (ISC License)

```
ISC License

Copyright (c) for portions of Lucide are held by Cole Bemis 2013-2022 as part of Feather (MIT).
All other copyright (c) for Lucide are held by Lucide Contributors 2022.

Permission to use, copy, modify, and/or distribute this software for any purpose with or without
fee is hereby granted, provided that the above copyright notice and this permission notice appear
in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH REGARD TO THIS
SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE
AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT,
NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE
OF THIS SOFTWARE.
```
