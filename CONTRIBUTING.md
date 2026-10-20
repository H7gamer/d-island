# Contributing to D-Island

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Before you start

- **Bugs:** open an issue with your Windows version (`winver`), what you did, what you expected and
  what happened. Attach `%AppData%\DIsland\error.log` if it has entries. Check it for anything
  personal first.
- **Bigger changes:** open an issue to discuss them first, so no work is wasted.
- **Security problems:** don't open a public issue; see [SECURITY.md](SECURITY.md).

## Building

Requirements: Windows 10 2004 or later, and the .NET 8 SDK.

```powershell
dotnet build              # Debug, with the DISLAND_* test hooks
dotnet run -c Release
.\tools\build-installer.ps1   # installer; needs Inno Setup 6
```

`CLAUDE.md` is the developer handbook: architecture, Windows quirks, design decisions with their
evidence, measurements, debug hooks and test scripts. Read the parts that touch your change.

## Guidelines

- **Match the existing style.** Comments explain *why*, especially Windows quirks.
- **Measure performance changes.** The island runs all day, so idle CPU, animation CPU and memory
  matter. Measure before and after with `tools\bench.ps1`, averaging 3 runs on the same power
  source.
- **Verify visual changes** with a screenshot (or `DISLAND_SNAPSHOT`) in your pull request.
- **Privacy first.** No telemetry, no new network calls without a clear user-facing reason, and
  every data source must be described in [PRIVACY.txt](PRIVACY.txt) in the same pull request.
- **Build cleanly.** Builds must have 0 warnings.

## License of contributions

D-Island is licensed under the [D-Island License](LICENSE.txt). Contributions are accepted only
under the [Contributor License Agreement](CLA.md). You keep the copyright in your work, and the
maintainer may include it in D-Island and license it under the D-Island License or other terms.
Agree by ticking the CLA box in the pull request template; pull requests without it can't be
merged. Only submit work you have the right to submit.

Keep the copyright header at the top of each source file, and add it to new files:

```
// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.
```

Pull requests are reviewed by the maintainer, h7. Merging a change doesn't make a fork or a build
from another source an official release; official releases are described in [NOTICE.txt](NOTICE.txt).
