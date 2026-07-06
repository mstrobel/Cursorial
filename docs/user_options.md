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
