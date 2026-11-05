# Shared chrome fixture

Run `pwsh -NoProfile -File work/Test-PolishChrome.ps1` from the workspace root (script outside this repository). No dependency download, notebook service, personal vault, or product Program startup is used.

This STA executable links WindowFrame, WindowChromeHelper, WindowSizing and ApplicationBrand. Its assembly is deliberately named Threadbook so the unchanged assembly-qualified embedded brand resource URIs resolve. This is an isolated frame fixture, not a packaged-app or full dialog integration test. All test windows close in finally; entry and dispatcher exceptions produce stderr/nonzero status.

Coverage: inert lookup/idempotent wrapping/body identity; managed monitor clamp; synchronous tooltip close; private caption visual/logical/focus/automation detachment and identity restoration; fallback and dynamic theme resources; caption hit sizes, names and focus style; native WindowChrome configuration; real button-triggered minimize/maximize/restore/close with cancellation; native maximized work-area bounds; nonresizable ownerless prompt presentation and later light-theme colors.

Unperformed: physical caption drag/double click, Alt+F4, Alt+Space, Win-key snap, Windows 11 Snap Layout hover, physical multi-monitor/DPI migration and full product integration. Native WindowChrome retains standard nonclient caption processing; tests do not certify those input interactions or accessibility compliance.
