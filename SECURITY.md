# Security policy

sshelfy stores SSH passwords and private keys, so security reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Report them privately through
GitHub: open the repository's **Security** tab and choose **Report a vulnerability**
(GitHub private vulnerability reporting).

Include what you can of:

- the affected sshelfy version (shown on the Start tab and in About) and your operating system and version,
- what an attacker needs (a malicious server, a crafted backup or host file, local access…),
- steps to reproduce, and what happens.

You can expect an acknowledgement within a week. Fixes for confirmed issues are released as
soon as practical, and reporters are credited in the release notes unless they prefer not to be.

## Supported versions

Only the latest release receives security fixes.

## Scope and design notes

How secrets are stored and protected is described in the README under **Security**. Things that
are in scope include, for example:

- secrets leaking out of the encrypted vault (into logs, settings, temp files, crash messages),
- a malicious SSH server or file making sshelfy run commands or write files on the user's computer,
- a crafted backup, host file or `~/.ssh/config` doing more than its preview shows,
- weaknesses in the vault, backup or session-recording encryption.

Out of scope: attacks that already require full control of the user's account (malware
running as the same user can read the unlocked app's memory), and issues in third-party
dependencies that are not exploitable through sshelfy (please report those upstream).
