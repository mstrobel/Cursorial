# User Options for Cursorial

## Persistence

Global options and application-specific options, stored separately.

- Stored under ~/.cursorial/
- Applications identified by entry assembly name?
- When loading, application-specific options are overlaid atop (possibly
  overwriting) global options.

## Initial Options

- Terminal capability overrides.
- Nerd font support.
- Toggle emoji support.
- Theme preference (light/dark/auto).
- Always show access key cues.
- Toggle fancy translucent menus and popups.
- Toggle animated transitions.
- Keyboard options
  - Platform-specfic key bindings or PC-standard (e.g., `Ctrl` instead of
    `Super` (⌘, Win)
- Mouse/Pointer options
  - Toggle dead zone for horizontal scrolling (avoids accidental drift during
    horizontal scrolling).
- Disable image support, if terminal is capable.

## User Experience

- User Options Dialog
  - Opened at any time by a key binding (we can have a default, say,
    Ctrl+Shift+O, but make it overridable in the
    `BuildApplication()` chain).
  - Pre-populate based on current saved configuration.
  - UI toggle for all options.
    - For Nerd Font support, we can display a sequence of test glyphs from
      different subsets so a user can easily identify if they are using a
      nerd font.
    - Hide advanced options like terminal capability overrides behind an
      'Advanced' tab with a prominent warning.
- On the first time running a Cursorial application (first Cursorial app ever,
  or once for every app?), if configured:
  - Present a modal welcome "wizard" window with:
    1. Framework-wide key bindings (most critically, the key binding to open the
       user options dialog).
    2. Location of configuration files, so power users can edit them by hand.

## Stage B design (implemented 2026-07-06)

One shared MVVM core drives BOTH surfaces (the code-reuse contract):

- `UserOptionCatalog` — the descriptor table (key, label, description, category, kind,
  choices, default, `RequiresTest`, `ReservedForFuture`) both UIs generate rows from.
- `UserOptionsSession` — the editing lifecycle over the loaded `UserOptionsStore`:
  live preview with explicit absent-means-default semantics (clearing a key live-reverts),
  open-time snapshot for **Reset** (restore without closing) and Cancel, `Save`, and the
  tri-state write surface (`SetValue(scope, key, null)` = inherit). Dangerous keys
  (capability overrides, color tier) never live-apply: they **stage**, are exercised via the
  timed `BeginTest` scope (auto-revert — the "will my screen survive?" probe), and commit at Save.
- `UserOptionsViewModel` / `OptionViewModel`s — categories of tri-state rows
  (`IsSetInCurrentScope`, inheritance badge, `ClearToInherited`), the dialog-level
  `EditScope` switch (Global | This application), and the 5-second test countdown
  (`UITimer` on the frame clock).
- `FirstRunWizardViewModel` — a pager over the SAME options view-model pinned to the
  global layer: Welcome (framework key bindings) → Terminal → Appearance → file locations.

Shells (code-first in Cursorial.UI — XAML would be circular through Cursorial.UI.Xaml):
`UserOptionsDialog` (tabs per category, scope switch, OK/Cancel/**Reset**, Advanced tab
warning + timed test) and `FirstRunWizard` (modal pager; Skip = complete). Both are
`SizeToContentMode.Always` windows (they re-fit as pages/options change) and opt into the
base `Window` chrome via `ControlThemeKey`.

Hooks: `UserConfigurationOptions.OptionsDialogGesture` (default Ctrl+Shift+O, per-root
framework binding beside Ctrl+L), `ShowFirstRunWizard` (**opt-in** per the notes' "if
configured"), `ForceFirstRunWizard`, `UIApplication.ShowUserOptionsDialogAsync()`
(single-instance). First-run marker: `meta.firstRunCompleted` in the GLOBAL store —
once ever per system; skipping counts as completion.
