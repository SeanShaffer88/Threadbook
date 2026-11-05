# Threadbook icon

Original vector artwork reconstructed for Threadbook from the user-selected Threaded Page concept (concept 1). Orange #FF5700 book, three binding tabs and three white thread nodes; transparent exterior. No Snoo or Ferrite artwork is included. Licensed under the repository MIT LICENSE.

From the repository root, run PowerShell 7 in STA:

    pwsh -NoProfile -STA -File tools/GenerateThreadbookIcon.ps1 -SvgPath desktop/Assets/Threadbook.svg -OutputDirectory desktop/Assets

Only local bounded SVG paths/circles with solid supported fills are accepted. This is a build-time asset tool, with no application startup or theme side effects. PNGs include 16/20/24/32/40/48/64/128/256/512; ICO contains the first nine sizes.
