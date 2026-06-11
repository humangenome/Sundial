# Assets

Avalonia resources for the launcher (app icon, tray icon, brand lockup).

SUNDIAL BRAND (2026-06-10, light theme): a rising golden sun over a floating
green sky-island with a sprout — bright, organic, airy solarpunk.
icon.png/icon.ico = the color sun-island mark (window/taskbar/installer),
tray-icon.ico = the same color mark at tray sizes (16/32/48),
lockup-dark.png = sun-island mark + SUNDIAL wordmark on the Cream100
(#F6EFDD) title-bar background (filename kept for the avares URI; the art is
the light-theme lockup). Palette lives in MainWindow.cs (Cream100/Cream50/
Card warm-cream surfaces, Line #E3D8BD borders, Sun400 #F0B23C golden fills,
Sun600 #A87410 golden text, Sage500 #3F8A4E success, Coral500 #C25E26
warning, Ink900/Ink500 warm charcoal text) and App.cs (Fluent Light Accent
#E8A21B). Keep dist/branding/ in lockstep when art changes.
