# ClassicDesk development conventions

- Prefer Chinese documentation and communication.
- Preserve WPF/.NET 8, existing skins, profile formats, draft state and revision checks; do not introduce unrelated dependencies.
- Inspect the current source and applicable instructions before editing. Preserve user changes and historical releases.
- Build: `dotnet build src/ClassicDesk/ClassicDesk.csproj -c Release`.
- Isolated checks: `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test.ps1 -OutputDirectory <absolute-report-directory>`.
- Do not show windows or popups, send real input, write Windows settings, enable the engine, install services or restart Explorer as part of isolated tests.
- Optional ServiceBundle tests require a reviewed package and separate authorization; absence of that bundle is not real administrator-install acceptance.
- Scheme previews and taskbar/tray designs are illustrative; do not claim they are the current Windows desktop or a complete StartAllBack replacement.
- Distinguish source, packaged files, local installation, public prerelease and actual native acceptance. Do not add overlapping repeat-check totals.
- Keep personal configurations, credentials, generated transaction logs, machine paths and test fixtures out of public packages.
- Frontend update manifests must bind the actual immutable source commit and matching release tag/target_commitish, with hashes for all four independent frontend assets.
- Publish, install, modify system state or clean files only when explicitly authorized for that action. A prerelease must not change the stable channel.
