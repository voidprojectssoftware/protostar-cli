---
sidebar_position: 4
title: Let your agents search your history
---

# Let your agents search your history

Your coding agent can search everything you have backed up, from every project
and machine, and read how past work was done: "find the session where we added
the EF Core migration and do it the same way", or "what did we learn about DNS
records in the homelab repo". `protostar mcp` serves your sessions and memory to
the agent as MCP tools. They only read: there is no tool that changes or deletes
anything.

## Before you start

- Protostar is set up and signed in on this machine (`protostar setup`, or
  `protostar auth login`). The server uses that sign-in and the same registry the
  other commands use.
- Your sessions and memory are backed up (`protostar sessions push`,
  `protostar memory push`, or the capture hooks do it as you work).
- The registry is recent enough: it lists `search`, `redacted-read`,
  `session-skills` and `memory-redacted-read` in its capabilities, and for your
  custom agents, `agent-search` and `agent-content`. A tool that needs one the registry
  lacks says so instead of answering.

## Add the server to Claude Code

```console
$ claude mcp add --scope user protostar -- protostar mcp
Added stdio MCP server protostar with command: protostar mcp to user config
$ claude mcp list
protostar: protostar mcp - ✔ Connected
```

`--scope user` makes it available in every project. To use a registry other than
your default, pass it to the server:
`claude mcp add --scope user protostar -- protostar mcp --registry https://registry.example`.

Other MCP clients start it the same way: the command is `protostar mcp`, and it
speaks MCP over standard input and output.

## The tools

| Tool | Arguments | What it returns |
|---|---|---|
| `search` | `query`, and optionally `kind` (`session` or `memory`, or `agent` to search your custom agents instead), `project` (an id, text in its name, or `.` for the project of the folder the agent runs in) and `limit` (1 to 100, default 20) | Hits, best first: each with `kind`, a session's `id`, `title` or memory `path`, `project`, `machineName`, `date` and a `snippet` with the matches between « and ». The same as `protostar search --json`. With `kind: agent`, each hit is one of your custom agents (Claude Code subagent definitions) by what its file says, with `name`, `version`, `description` and `project`, for `get_agent`. Agents are only searched when asked for. |
| `list_sessions` | Optionally `project`, `machine` (a machine id), `since` (an ISO 8601 date or time) and `limit` (1 to 100, default 25) | Backed-up sessions, newest activity first, as `protostar sessions list --json` prints them. Sessions not backed up yet are not listed. |
| `get_session` | `id` (a whole id or enough of its start), and optionally `full` and `offset` | The session's record and its messages in order: prompts, replies and tool calls, each with an `index`. With `full`, also tool inputs and results, and each subagent's conversation where it ran. The records are those of `protostar sessions show --json`. |
| `get_session_skills` | `id` (a whole id or enough of its start) | The session's record and the skills it and its subagents used, earliest first: `name`, `uses`, `firstUsedAt`, `lastUsedAt`, and the registry skill it matched (`skillId`) with `version` and `versionInferred: true`. A transcript records only a skill's name, so the registry infers the version as the newest one pushed by the time the session used it; with none pushed by then, `version` is absent. A plugin skill (`plugin:skill`) matches no registry skill. `deleted: true` marks a skill since deleted. |
| `get_memory` | `project` (an id or text in its name), and optionally `path` | The project's memory files as the registry has them now: `path`, `version`, the machine that wrote it and its `content`. |
| `get_agent` | `name`, and optionally `project` (an id or text in its name) and `version` | One of your custom agents as the registry has it: `name`, `projectId`, `project`, `version`, `description`, `path` (in the agents folder), `pushedAt` and `content`, the whole Markdown file. `project` is needed only when the name is both a personal and a project agent, or in several projects; the error `ambiguous_agent` lists them. Installing an agent stays a CLI action. |

A typical exchange: the agent calls `search` with "EF Core migration", takes the
`id` of the session hit, calls `get_session` with it to read the prompts,
replies and tool calls in order, and `get_session_skills` to see which skills it
used, so it can repeat the approach with the same ones. To delegate the way it
did before, it calls `search` with `kind: agent` and what the work needs (for
example "review migrations"), then `get_agent` with the hit's `name` to read the
agent's definition.

## Secrets stay redacted

Everything a tool returns was redacted by the registry first. A token, key or
password the registry recognises reads as `[redacted:<type>]`, for example
`[redacted:github-token]`, in search snippets, in transcripts and in memory. The
server refuses to read sessions or memory from a registry that cannot redact them.

## How much one call returns

An agent's context is finite, so one result is at most 80,000 characters of JSON
(about 20,000 tokens, under Claude Code's default limit for one tool result).

- **A long session comes in pages.** When `get_session` returns `nextOffset`, call
  it again with `offset` set to that value for the next messages. A text longer
  than 4,000 characters (a long reply, tool result or tool input) is cut, and its
  message has `truncated: true`.
- **A large memory folder** returns as many files as fit, in path order, and names
  the rest under `omitted`; read those one at a time with `path`.

## When a call fails

A failed call is a tool error, never a crash: the server keeps running. The error
text is the same object `--json` writes, with the same codes, so an agent can
branch on `code`:

```json
{"error":{"code":"not_signed_in","message":"Not logged in to https://registry.example. Run `protostar auth login` first."}}
```

The codes are listed in [JSON output](../reference/json-output.md#errors). The
tools add `invalid_argument` (an argument out of range, such as `limit` 500) and
`ambiguous_project` (a `get_memory` project name that matches more than one
project; the error lists them under `projects`).
