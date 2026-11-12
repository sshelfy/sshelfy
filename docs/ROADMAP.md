# Roadmap

What is planned after the first release. Nothing here is a promise; priorities follow what users
ask for, so comment on or open an issue if something matters to you.

What already shipped is in [CHANGELOG.md](../CHANGELOG.md).

## Distribution

- Code-signed releases (SignPath Foundation for open-source projects), so Windows SmartScreen does
  not warn on first start.
- Installer with Start menu entry and uninstaller, plus a portable ZIP.
- Packages for `winget` and Scoop.
- In-app update: download and install a new release, not only notify about it.

## Platforms

- A signed and notarized macOS build.
- Linux packages beyond the `.tar.gz`: AppImage, `.deb`, Flathub, and an arm64 build.
- The Windows-only features on macOS and Linux. Avalonia UI is cross-platform and the Windows-only
  parts (ConPTY local terminals, Windows Hello, DPAPI) live behind abstractions. Missing pieces:
  a Unix pseudo-terminal for local shells, Keychain / Secret Service for "unlock automatically",
  Touch ID instead of Windows Hello.

## SSH

- Agent and X11 forwarding inside the built-in SSH client (today these sessions run in the
  Windows OpenSSH client, because SSH.NET cannot open the needed channels).
- SFTP with FIDO2 security keys.
- Mosh and serial (COM port) sessions.

## Everyday use

- Sync of hosts and snippets between computers (end-to-end encrypted, with a storage of your choice).
- Interface translations, starting with Polish.
- Accessibility pass (screen readers, keyboard navigation everywhere, high contrast).

## Terminal

- Ligatures and font fallback settings.
- Kitty graphics protocol, in addition to sixel and iTerm2 images.
- Session replay with a timeline.
