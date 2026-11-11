# Changelog

All notable changes to sshelfy are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com), and versions follow [Semantic Versioning](https://semver.org).

## [Unreleased]

### App

- "Support sshelfy on Ko-fi" link in About.

## [1.0.1] - 2026-11-11

First public release.

### Connections

- Hosts in nested groups with search, quick connect (`user@host:port`) and import from `~/.ssh/config` and `known_hosts`.
- Password, private-key (OpenSSH, PEM, PuTTY), SSH agent (Pageant, OpenSSH agent) and certificate authentication; FIDO2 security keys.
- Ed25519/RSA key generation and one-click install into a server's `authorized_keys`.
- Jump hosts, including chains; local, remote and SOCKS port forwarding.
- Agent and X11 forwarding (through the Windows OpenSSH client).
- Per-host startup command, environment variables, pre-connect command (e.g. VPN) and port knocking.
- Automatic reconnect after a dropped connection.

### Terminal

- xterm-256color and truecolor terminal with scrollback, search, reflow, mouse support and clickable links.
- Local terminals (PowerShell, cmd, Git Bash, WSL) through ConPTY.
- Split panes with broadcast input, saved layouts, command palette.
- Color schemes (built-in and imported from Windows Terminal, iTerm2, base16).
- Highlight rules with notifications, long-command notifications.
- Inline images (sixel, iTerm2 protocol).
- Server resource monitor in the status bar.
- Snippets with variables, per-host command history, session recording.

### Files

- SFTP browser with folder tree, two-panel mode, drag and drop, transfer queue with resume and conflict handling.
- Remote text editor, editing in an external editor with automatic upload.
- Folder compare and sync, file preview with live follow.
- Permissions and owner editing.

### Security and data

- Encrypted vault (AES-256-GCM, PBKDF2) with optional Windows Hello or automatic unlock.
- Auto-lock on idle and screen lock; encrypted command history and session recordings.
- Encrypted backup export and import.
- Activity log and diagnostic log.
- Hardening against hostile servers and files: safe local names for downloads, Mark of the Web on downloaded
  files, no option injection into the OpenSSH client, flagged risky hosts in backup imports, warnings for host
  key type changes.

### App

- About dialog, daily check for new releases on GitHub (can be turned off), "Report a problem" with the version
  filled in.

[Unreleased]: https://github.com/sshelfy/sshelfy/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/sshelfy/sshelfy/releases/tag/v1.0.1
