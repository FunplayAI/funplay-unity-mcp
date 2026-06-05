# Keepalive Broker (optional, survives domain reloads)

An **opt-in** out-of-process transport that keeps MCP client connections alive across Unity
domain reloads (script recompile / entering Play Mode). It needs **no external runtime** — the
broker runs under the **Unity-bundled Mono**.

## Why

funplay's default `HttpMCPTransport` binds a `TcpListener` **inside Unity's managed AppDomain**.
Every domain reload destroys that AppDomain, so the listener dies and the port is released. From
`beforeAssemblyReload` until the post-reload restart rebinds, any MCP client request to the port
fails with **connection refused** — and the tool call that *triggered* the reload (`enter_play_mode`,
`request_recompile`) loses its response.

A socket living in the reloadable AppDomain fundamentally cannot survive an AppDomain unload. The
only robust fix is to move the client-facing socket **out of process**.

## How it works

```
MCP client (Claude) ──HTTP POST──► broker (separate process, owns the port) ◄──long-poll── Unity plugin
       ▲                                  │ holds request across reload                (BrokerClientTransport)
       └────────── response ◄─────────────┘                                            pulls / executes / pushes
```

- The **broker** (`keepalive-broker.cs`, compiled to an .exe) is a standalone process run under the
  Unity-bundled Mono. It owns the client-facing port and never reloads.
- The Unity plugin runs `BrokerClientTransport` instead of `HttpMCPTransport`: it connects OUT to the
  broker, long-polls `/_b/pull` for work, runs each request through the normal `MCPRequestHandler`,
  and `POST`s the result to `/_b/push`. This realizes the `IsAttachedToExistingServer` seam.
- On a domain reload only the plugin's poll connection drops. The broker **holds** any in-flight
  client request (and re-queues a request that was pulled but not yet answered). funplay's existing
  post-reload restart re-creates `BrokerClientTransport`, which re-attaches and drains the queue. The
  client just sees a slower call — never a refusal.

Back-channel protocol (raw HTTP, **header framing** — the broker is a dumb byte forwarder, no JSON
parsing on its side):

| Direction | Request | Response |
|---|---|---|
| Unity → broker | `GET /_b/pull` | `200` + header `X-Broker-ReqId: N` + body = client json-rpc, or `204` (no work) |
| Unity → broker | `POST /_b/push` + header `X-Broker-ReqId: N` + body = json-rpc response | `200` |
| MCP client → broker | `POST /` + json-rpc | held until Unity answers (or 120s deadline) |

## Usage

In the **Funplay MCP** window, tick **"Broker mode (survive domain reloads)"**. That's it:

- The plugin **auto-launches** the broker under the Unity-bundled Mono (`<editor>/.../MonoBleedingEdge/
  bin/mono keepalive-broker.exe <serverPort>`) and **auto-kills** it when you untick the toggle or quit
  the editor. No Node.js or other install is required.
- If the prebuilt `keepalive-broker.exe` is not shipped, the plugin compiles `keepalive-broker.cs` once
  with the bundled C# compiler (`mcs`) into `Library/funplay-broker/` and caches it.
- The broker binds the **same port** as the MCP server, so **MCP clients need no change** — they keep
  pointing at `http://127.0.0.1:<serverPort>/`. The mode switch is transparent to the client.
- Settings persist (`UserSettings/FunplayMcpSettings.json` → `brokerModeEnabled` / `brokerMonoPath`),
  so broker mode is restored automatically on the next Unity launch.

If Mono cannot be located or the broker fails to start, the plugin logs a warning and **falls back to
the in-process `HttpMCPTransport`**, so funplay keeps working. Headless/CI: set `brokerModeEnabled: true`
in `FunplayMcpSettings.json` (no UI needed).

## Compatibility

The bundled Mono is present in every Unity 2019+ editor (it is the editor scripting runtime), but its
path varies by version — e.g. `Contents/MonoBleedingEdge/` (≤2022) vs `Contents/Resources/Scripting/
MonoBleedingEdge/` (Unity 6). `BrokerProcessManager` resolves it dynamically (known candidates +
bounded recursive search), so a hard-coded path is never assumed. The broker uses only basic BCL
(TcpListener, threads) and is compiled for the Mono 4.5 profile.

Verified: the same broker .exe (built with Unity 6's `mcs`) runs correctly under both Unity 6000's
Mono and Unity 2022.3's Mono (6.13.0); a real domain-reload run kept an MCP client at 100% success
(zero connection-refused), with the request spanning the reload held until the plugin re-attached.

## Notes / future work

- `initialize` / `tools/list` could be cached by the broker to keep the session valid even if a reload
  lands between handshake and first call (currently relayed like any request).
