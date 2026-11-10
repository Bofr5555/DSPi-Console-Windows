# Vendored dspi-link

Copied from https://github.com/weeblabs/dspi-link: `spec/`, `policy/`, `fixtures/` and `LICENSE` (MIT).

- Commit: 86f10e3 ("Analyser sharing, output latency and lock flows: port from the Console"), spec 1.2 (draft)
- Tag: none yet. Re-vendor from a tag once one is cut, and record it here.

`policy/commands.json` is embedded in DSPiConsole.Core as the hub's command table, and the
fixtures drive DSPiConsole.Core.Tests/Link. Do not edit these files here; change them upstream
and copy them again.

The browser page the hub serves (`DSPiConsole/Web`) is built from `js/` at the same commit by
`Tools/BuildLinkWeb/build.ps1` (Node 20.19 or later). Rebuild it whenever this commit changes.
