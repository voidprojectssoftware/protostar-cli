# 0001. A local, read-only MCP server for agents

- **Status:** accepted 2026-11-20
- **Date:** 2026-11-20
- **Issue:** voidprojectssoftware/protostar-registry#13 ("My agents can mine my history"), protostar-cli#48 ("My
  agents can search all my memory"), protostar-cli#116 (`get_session_skills`). PR protostar-cli#117, with the
  registry side in protostar-registry#77.

## Context

Coding agents need the developer's backed-up sessions and memory as tools: find a past session by what was done
in it, read how it was done and which skills it used, and read what was learned in another project. The CLI
already has every read this needs (search, `sessions show` with its redacted, streaming reader, `sessions list`,
the memory client) and a JSON contract for agents (`--json`, docs/reference/json-output.md). What was missing is
the access path an agent's harness can call directly.

## Decision

**A local stdio MCP server in the CLI, `protostar mcp`,** registered with `claude mcp add protostar -- protostar
mcp`. It signs in with the CLI's stored session and resolves the registry exactly as every other command does,
afresh on each call. The rejected alternative was a remote MCP endpoint on the registry: it would need its own
OAuth surface for MCP clients, a much larger change, for no gain to a developer who already has the CLI. Built on
the official C# SDK, `ModelContextProtocol.Core` (pinned), without the hosting package, whose generic host logs
to standard output by default.

**Read-only by construction.** The server exposes only reading tools (`search`, `list_sessions`, `get_session`,
`get_session_skills`, `get_memory`), each annotated read-only and not destructive, and none of them calls anything
that writes to the registry or the disk. Read-only is not a permission check that could be bypassed; there is no
write path to reach. A call to any other tool name is refused by the SDK as an unknown tool.

**Standard output is the protocol and nothing else.** The server opens the standard output stream for the
transport first, then points `Console.Out` and Spectre's console at standard error, so a stray write anywhere in
the process cannot corrupt the channel. It configures no logger.

**No content without redaction.** Every text a tool returns was redacted by the registry: search snippets,
transcripts read with `redacted=true`, and memory read with `redacted=true`. Because a registry that does not
support the parameter ignores it and sends the stored bytes, `get_session` requires the `redacted-read`
capability and `get_memory` requires `memory-redacted-read` to be listed in `/v1/meta`; a registry that does not
list them, or is too old to list capabilities at all, is refused with `registry_incompatible` before any content
is requested. `get_session_skills` is likewise gated on `session-skills`.

**Failures are tool errors, never crashes.** A failed call returns `isError` with the same error object and codes
`--json` writes, so agents branch on the codes they already know.

**Bounded results.** One result is at most 80,000 characters of JSON (about 20,000 tokens), under Claude Code's
default 25,000-token limit for a tool result, so the client never cuts a result off. `get_session` pages by
message (`offset` in, `nextOffset` out) and cuts any single text over 4,000 characters, marking the message
`truncated`. It reads the transcript as it downloads and stops when the page is full, so memory stays bounded by
the page; a later page reads again from the start. `get_memory` returns the files that fit and names the rest.

## Consequences

- An MCP face now sits beside the console one. It follows the command, service and presenter split: the tools
  class is the presenter, and it reuses the `--json` record builders, so field names and error codes stay single.
- A new dependency, `ModelContextProtocol.Core`. Removing it means deleting `src/Protostar.Cli/Mcp/` and the
  `mcp` command.
- Paging by re-reading costs one download per page for a very long transcript. A registry range read would make a
  page cheap; correctness does not need it.
- A new tool must keep these rules: read only, registry-redacted text behind its capability, and within the cap.
