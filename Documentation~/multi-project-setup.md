# Running Several Unity Projects at Once

*[中文版](multi-project-setup.zh-CN.md)*

Two Unity editors opened on different projects can both serve MCP at the same time. Each project
gets its own port and its own entry in your AI client's config, so a tool call can only ever reach
the editor it was meant for.

This guide covers what happens by default, what the MCP Server window offers, and what to do in the
cases that need a decision.

---

## Quick start

For **new projects** there is nothing to configure:

1. Open each project's Unity, then **Funplay > MCP Server**, and tick **Enable MCP Server**.
2. Click **Configure** in each (pick your client in the dropdown first).
3. Restart your AI client.

Your client config now holds one entry per project:

```json
"funplay-game-alpha": { "url": "http://127.0.0.1:24312/" },
"funplay-game-beta":  { "url": "http://127.0.0.1:21874/" }
```

In clients that namespace tools by server, the tools become `mcp__funplay-game-alpha__execute_code`
and `mcp__funplay-game-beta__execute_code` — mixing the two up is structurally impossible rather
than something you have to be careful about.

> **Upgrading an existing project?** Nothing moves: your project keeps the port it was already
> using, so existing client configs keep working. See [Upgrading](#upgrading-from-an-earlier-version).

---

## How the port is chosen

Resolution order, every time the server starts:

1. **A port you pinned** always wins.
2. Otherwise the port is **derived from the project path**: SHA-256 of the normalized path, mapped
   into **20000–29999**. That range sits above the crowded 8000–9000 developer-tool band and below
   the ephemeral ranges the OS allocates outbound sockets from (macOS 49152+, Linux 32768+).
3. If the resolved port turns out to be **held by another process**, the transport binds a free port
   nearby instead, warns in the console, and reports the port it actually bound.

Derivation is a pure function of the project path, so a project keeps the same port across restarts —
which is what makes a written client config entry stay valid. Nothing about the port is stored on
disk unless you pin it.

### About fallback ports

A fallback port is deliberately **not** persisted: the real port is re-derived on every start, and a
stored fallback would outlive the conflict that caused it. It is remembered for the rest of the
editor session only, which buys two things:

- After a domain reload the transport does not re-wait the full ~10 s retry window on a port it
  already knows is held by someone else.
- It re-binds the same fallback port first, so a client connected to it survives recompiles.

The one-click configuration always writes the project's **stable** port, never a fallback port. A
client pointed at the stable port simply reconnects once that port frees up. If the conflict is
permanent, pin a free port (or click **Pin Current Port** while the fallback is active) and
re-configure.

---

## How the entry name is chosen

The entry name is `funplay-<project folder>` — for example a project in `~/work/game-alpha` becomes
`funplay-game-alpha`. Runs of anything other than ASCII letters and digits collapse to a single `-`,
but camelCase is not split: `~/work/GameAlpha` becomes `funplay-gamealpha`.

- The **project directory name** is used rather than `Application.productName`, because the product
  name is often left at Unity's default or set to non-ASCII text.
- Only ASCII letters and digits survive; everything else collapses to `-`. Client tool names are
  restricted to `[a-zA-Z0-9_-]`, so a name outside that set would produce tools a client rejects.
  A name with nothing usable left (say a fully non-Latin directory name) falls back to a short
  project hash, e.g. `funplay-a3f9c1`.
- Names are truncated so that `mcp__<entry>__<tool>` stays inside the 64-character limit clients
  impose on tool names.

### Two projects with the same folder name

They would resolve to the same entry name. The second project you configure detects that the name
is already taken by a project that is not itself — it has no record of writing it — and appends a
short project hash automatically:

```json
"funplay-client":        { "url": "http://127.0.0.1:24312/" }
"funplay-client-a3f9c1": { "url": "http://127.0.0.1:21874/" }
```

Nothing is overwritten and there is no setting to turn on. The client config panel says when it
added a hash so the suffix is not a mystery.

---

## The MCP Server window

| Control | What it does |
| --- | --- |
| **Server Port** | Shows the port this project is actually on. Typing a port pins it; clearing the field releases the pin. |
| **Pin Current Port** | Pins the port shown. Needed because re-typing the value already displayed commits no change. |
| **Use Per-Project Port** | Releases the pin and goes back to the derived port. Only shown while a port is pinned. |
| The line under the field | Says where the port came from: pinned, derived, or serving on a fallback because the resolved port was in use. |

The client config panel shows the entry it will write (`Entry: funplay-game-alpha ->
http://127.0.0.1:24312/`), notes an auto-added project hash, and notes when a fallback bind is
active.

---

## Both transport modes support this

Per-project ports apply to **Direct HTTP** and **Broker Mode (Experimental)** alike — both resolve
the port the same way, and switching modes does not change the port or the entry name, so no
re-configuration is needed when you toggle it.

| | Direct HTTP | Broker Mode |
| --- | --- | --- |
| Per-project state | the listening port | the port, plus `<project>/Library/FunplayMcp/Broker/` (pid file and the compiled broker) — inside the project, so isolated by construction |
| On a port conflict | remembers the conflict for the session: short retry, and the previous fallback port is tried first | a broker already healthy on a fallback port is kept rather than killed and restarted while the requested port stays occupied |
| Written into the client config | the stable port | the stable port |

The broker is internally single-tenant (one queue, one waiting backend), but that is not a limit
here: each project runs **its own** broker process on its own port.

---

## Upgrading from an earlier version

A project that already has `UserSettings/FunplayMcpSettings.json` was already serving on a port your
clients are configured against, so upgrading **records that port as a pin and changes nothing**. No
re-configuration, no entry rename, no tool rename.

Per-project derived ports are therefore the default for new projects only. To opt an existing
project in:

1. **Funplay > MCP Server** → **Use Per-Project Port**.
2. Click **Configure**.
3. Restart your AI client.

Two consequences worth knowing before you do it:

- The entry is renamed from whatever it was (typically the shared `funplay`) to
  `funplay-<folder>`, so **tool names change prefix**. Update any docs, permission allowlists, or
  hooks that match the old prefix.
- The legacy shared `funplay` entry is **not** deleted automatically — any project on the machine
  could have written it, so removing it is not this plugin's call. The client config panel reports
  that it is still there; delete it by hand once every project has been configured.

Teams should note that `UserSettings/FunplayMcpSettings.json` is per-machine and usually
git-ignored, so each developer goes through this once.

---

## Scenarios

| Situation | What to do |
| --- | --- |
| CI, or a firewall rule that only opens one port | Pin that port in **Server Port**. A pin always wins over derivation and never moves. |
| Two projects with the same folder name | Nothing — the second one configured appends a project hash automatically. |
| The derived port is taken by an unrelated process | Nothing — the server binds a free port nearby and warns. Pin a port if the conflict is permanent. |
| Two upgraded projects still fighting over one port | Click **Use Per-Project Port** in one of them, then re-configure that client. |
| Broker Mode (Experimental) | No extra setup. The broker's pid file is per project, and the broker follows the same resolved port. |
| You moved or renamed the project directory | The derived port and the entry name both change (both come from the path). Re-run **Configure** and delete the stale entry. |

---

## Troubleshooting

**The client cannot connect after an upgrade or a port change.** The client config still points at
the old port. Open the MCP Server window, check the URL on the entry line, click **Configure**, and
restart the client.

**A server in the client shows up as failed.** Most likely the legacy `funplay` entry, which no
project writes any more. Remove it by hand.

**Check what is actually listening:**

```bash
lsof -nP -iTCP -sTCP:LISTEN | grep -E ':(2[0-9]{4}|8765)'
```

**Probe a port directly:**

```bash
curl -s -m 5 -X POST http://127.0.0.1:24312/mcp -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

**Compute a project's derived port yourself** (normalized path = full path, trailing separator
removed, lowercased):

```bash
python3 -c "import hashlib,sys; p=sys.argv[1].rstrip('/').lower(); print(20000+int(hashlib.sha256(p.encode()).hexdigest()[:8],16)%10000)" /path/to/UnityProject
```

**Settings worth reading** in `UserSettings/FunplayMcpSettings.json`:

| Field | Meaning |
| --- | --- |
| `port` / `portConfigured` | The pinned port, and whether a pin is in effect. With `portConfigured: false` the stored `port` is unused and the derived port applies. |
| `mcpLastClientConfigKeys` | Entry name this project last wrote, per client target. This is the ownership evidence that lets a later write retire its own stale entry without touching another project's. |
| `settingsVersion` | Schema version; drives the one-shot upgrade migration above. |

The window is the source of truth for the active port; the file only records what was chosen.

---

## Not supported

- **One shared port in front of several editors.** Each editor owns its own port. A single-endpoint
  hub with a runtime instance selector is a different design, and the current one is deliberately
  free of "which editor is active?" state that a client could get wrong.
- **The same project directory opened twice.** Unity itself does not allow it. Clones made by
  ParrelSync or MPPM live in their own directories and therefore derive their own ports.
- **Reaching an editor across machines.** Everything here is loopback only.
