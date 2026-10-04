# Security policy

The DWSIM MCP server can control a running DWSIM window through the **MCP
Bridge** extender. That channel is designed to be reachable only by the
MCP server you installed, on your own Windows account, while you have
switched it on. Its design is described in the README under
*Security model*:

- off by default; enabled per DWSIM session from **Tools > MCP Bridge**
- Windows named pipe (no network port), access restricted to the current
  user, network logons denied, random name per session
- per-session secret key in an owner-only file; HMAC challenge-response on
  every connection (the key never crosses the pipe)
- client allow-list (exact `python.exe` + `server.py`) checked before
  anything else, and a user approval prompt per MCP server process
- the MCP server checks that the pipe is served by the DWSIM process that
  created the session

**Known limitation:** software already running under your own Windows
account can't be fully prevented from interfering (it could inject into
DWSIM or edit its files directly). Keep MCP control switched off when you
aren't using it.

## Supported versions

Only the latest commit on `main` is supported.

## Reporting a vulnerability

Please **don't open a public issue** for security problems. Instead, use
GitHub's private vulnerability reporting: on the repository page, go to
**Security > Report a vulnerability**. Include:

- what an attacker could do and under which conditions (e.g. another
  local user, a web page, a process under the same account)
- steps to reproduce, and the Windows / DWSIM / Python versions

You should get an initial response within two weeks.
