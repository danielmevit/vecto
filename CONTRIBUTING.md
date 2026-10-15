# Contributing to Vecto

Thanks for helping. Start at [`AGENTS.md`](AGENTS.md) → [`docs/ai/START_HERE.md`](docs/ai/START_HERE.md).

- The build must stay at 0 warnings / 0 errors (`dotnet build -c Release`), and `dotnet test` must pass.
- Engine changes: check the result with `vecto trace <img> --check` and `vecto bench` before and after.
- Results must stay identical on Windows, Linux and macOS.

## Licence of contributions

Vecto is licensed under the [GNU AGPL-3.0-only](LICENSE) from v0.4.4 and is also offered under
commercial licences (dual licensing, see [NOTICE](NOTICE)). By submitting a contribution, you agree
that it is licensed under the AGPL-3.0-only and that Daniel Mitev (Daniel Mevit, @danielmevit) may
relicense it, including under commercial licences.

The Vecto name and logo are not covered by the code licence: forks must use a different name and logo.
