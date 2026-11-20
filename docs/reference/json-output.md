---
sidebar_position: 4
title: JSON output for agents and scripts
---

# JSON output for agents and scripts

The `protostar sessions` commands, `protostar search` and `protostar status` take `--json`. With it
they print structured records instead of tables, so a coding agent or a script can
act on the result without parsing text meant for people. `protostar mcp` returns
the same records to agents as [MCP tools](#the-mcp-tools). This page is the
contract: what goes where, the field names and types, the error codes and the exit
codes. The output without `--json` is unchanged.

| Command | Standard output with `--json` |
|---|---|
| `protostar sessions list` | One JSON array of [session records](#sessions-list). |
| `protostar sessions status` | One JSON array of [transcript records](#sessions-status). |
| `protostar sessions push` | One JSON array of [backup records](#sessions-push). |
| `protostar sessions show` | [JSON Lines](#sessions-show): a session record, then one record per message, one per line, as the session downloads. |
| `protostar search` | One JSON array of [search hits](#search). |
| `protostar status` | One JSON object; see [`protostar status`](commands.md#protostar-status). |

## The rules

- **Standard output holds data and nothing else.** There are no colour codes,
  table borders, progress lines or messages on it, so it can be piped straight
  into `jq` or a JSON parser. Each command prints its result on one line, except
  `sessions show`, which prints one record per line.
- **Standard error holds at most one error object**, on one line, when the
  command could not do all it was asked (see [Errors](#errors)). Anything else a
  command has to say is left out under `--json`.
- **Names are camelCase. Absent values are left out**, never written as `null`.
  Enum values are camelCase strings (`neverBackedUp`, `upToDate`). Sizes are
  bytes, as integers. Times are ISO 8601 with an offset
  (`2026-11-19T10:00:00+00:00`).
- **Fields may be added** in later versions. Read the fields you need and ignore
  the rest. A field is not removed or renamed without a major version.

## Ids

Every record names its transcript with `id`, and `harness` beside it.

`id` is the harness's own key for the transcript. For Claude Code that is the
session id (a UUID, the transcript's file name), and for a subagent
`<session id>.agent-<agent id>`. The harness writes it, and the registry stores it
as given, so the same session has the same `id` on every machine: an id taken from
`sessions list` on a laptop names the same session on a build VM.

The registry's full identity for a transcript is `harness` plus `id`. The `id`
alone is used because harness keys are UUIDs, so in practice it never collides
across harnesses, and it is the value people see and type (the table's Id column
shows its first 8 characters). Where a command takes an id, `--harness` narrows it
if two harnesses ever do share a key.

## `sessions list`

One record per session, newest activity first. Each is the registry's record of
the session with `id` added first. The registry's fields are the ones
`sessions list --json` has always printed, so `key` (the same value as `id`) and
`parentKey` stay.

| Field | Type | Meaning |
|---|---|---|
| `id` | string | The session's [id](#ids). |
| `harness` | string | The harness that wrote it, for example `claude-code`. |
| `key` | string | The same as `id`, kept for scripts written before `id`. |
| `kind` | string | `session`. |
| `title` | string | The harness's title, or the first prompt when it has none. |
| `firstPrompt` | string | The first prompt, shortened to 200 characters. |
| `projectId` | string (UUID) | The project, the same on every machine. |
| `project` | string | The project's name, for example `github.com/example/demo-app`. |
| `projectSubpath` | string | The folder within the repo, when the session ran in a subfolder. |
| `workingDirectory` | string | The folder it ran in, on the machine it ran on. |
| `gitBranch` | string | The branch it was on. |
| `machineId` | string (UUID) | The machine it came from. |
| `machineName` | string | That machine's name. |
| `sourcePath` | string | The transcript file on that machine. |
| `startedAt`, `lastActivityAt` | time | Its first and latest timestamps. |
| `userMessages`, `assistantMessages` | integer | Prompts typed and replies. |
| `length` | integer | Bytes stored. |
| `subagentCount` | integer | Subagent transcripts backed up for it. |
| `pending` | object | Only for a session on this machine not fully backed up: `bytes` still to send, `inRegistry` (whether the registry holds part of it), `fromFile` (the registry has none of it, so the row was made from the file and has no title or counts), `path`, `changedAt`. |

When the registry cannot be asked (not signed in, unreachable), the array still
lists this machine's pending sessions, the error object goes to standard error,
and the exit code is `1`.

## `sessions status`

One record per transcript on this machine, sessions and subagents, most recently
changed first. As in the table, only pending transcripts (and ones the registry
holds differently) are listed unless you pass `--all`.

| Field | Type | Meaning |
|---|---|---|
| `id` | string | The transcript's [id](#ids). |
| `harness` | string | The harness. |
| `kind` | string | `session` or `subagent`. |
| `parentId` | string | For a subagent, the `id` of its session. |
| `path` | string | The file. |
| `state` | string | `backedUp`, `neverBackedUp`, `newContent`, `rewritten` (goes again in full), `backupFailed` (a hook's backup failed; a later hook retries), `missing`, `short` or `diverged` (the last three only with `--verify`). |
| `size` | integer | Bytes on disk. |
| `bytesBackedUp` | integer | Bytes this machine has sent. |
| `bytesPending` | integer | Bytes still to send. |
| `lastModifiedAt` | time | When the file last changed. |
| `lastBackedUpAt` | time | When the registry last accepted it. |
| `backupFailedAt` | time | When a hook's backup of its session last failed. |
| `verified` | object | Only with `--verify`: `check` (`confirmed`, `missing`, `short`, `diverged`), and the registry's `revision` and `length`. |

The exit code is `0` when everything is backed up and `1` while anything is
pending, as without `--json`. Pending is not an error, so it writes no error
object.

## `sessions push`

One record per transcript the push looked at. There is no progress display with
`--json`.

| Field | Type | Meaning |
|---|---|---|
| `id`, `harness`, `kind`, `parentId`, `path` | | As in [`sessions status`](#sessions-status). |
| `result` | string | `upToDate`, `uploaded`, `partial` (time ran out), `skipped` or `failed`. |
| `bytesSent` | integer | Bytes sent by this push. |
| `bytesPending` | integer | Bytes the registry still does not have. |
| `rewritten` | boolean | The file was rewritten, so a new revision was started. |
| `message` | string | Why it was skipped or failed. |
| `verified` | object | Only with `--verify`; as in `sessions status`. |

## `sessions show`

[JSON Lines](https://jsonlines.org/): one JSON object per line. The first is the
[session record](#the-session-record); then one per message, in the order they
happened, written as the transcript downloads. A session can be
hundreds of megabytes, so it is never one giant array: read it line by line
(`jq -c 'select(.type == "prompt")'` works on it as it is). The text is redacted
by the registry: secrets appear as `[redacted:<type>]`.

Without `--full` the records are prompts, replies and tool calls (without their
input). `--full` adds each tool call's `input`, every tool result, and each
subagent's messages between a `subagentStart` and a `subagentEnd` record, right
after the result of the call that ran it.

| Field | Type | Meaning |
|---|---|---|
| `type` | string | `prompt`, `reply`, `toolCall`, `toolResult`, `subagentStart` or `subagentEnd` (or `session` for the first record). |
| `transcriptId` | string | The [id](#ids) of the transcript the message is in: the session's, or a subagent's for what ran inside it. |
| `harness` | string | The harness. |
| `at` | time | The message's timestamp. |
| `text` | string | For `prompt`, `reply` and `toolResult`: the text. |
| `tool` | string | For `toolCall`: the tool, for example `Bash`. |
| `toolUseId` | string | For `toolCall` and `toolResult`: the call's id, which links a result to its call. |
| `summary` | string | For `toolCall`: what it acted on, on one line (a command, path, pattern or description). |
| `input` | object | For `toolCall`, only with `--full`: the call's full input. |
| `agentType`, `description` | string | For a `toolCall` that starts a subagent, and for `subagentStart`. |
| `isError` | boolean | For `toolResult`: the tool reported an error. |
| `agentId` | string | For a `toolResult` of a call that ran a subagent: its agent id. |
| `linked` | boolean | For `subagentStart`: matched to the call that ran it. An unmatched subagent comes after the session's own messages. |
| `sidechain` | boolean | A subagent message written into the session's own file (older Claude Code). Only with `--full`. |

Lines of the transcript that are not part of the conversation (system notes,
summaries, titles, the model's thinking, and types protostar does not know) are
skipped.

If the download fails part way, the records already printed stand and a
`registry_unreachable` error object follows on standard error, with exit code `1`.

### The session record

The first line, `type` `session`, describes the session and the skills it used.
It comes before the first message (once the download has started), so read it
with `head -n 1`, or skip it with `select(.type != "session")`.

| Field | Type | Meaning |
|---|---|---|
| `type` | string | `session`. |
| `transcriptId`, `harness` | string | As in the message records. |
| `session` | object | The [`sessions list` record](#sessions-list) of the session. |
| `skills` | array | The skills it used, its subagents' included, earliest first used first. Absent when the registry cannot list them: it does not list `session-skills`, or the request failed. Empty when it used none. |

Each skill has the fields `get_session_skills` gives (see [The MCP tools](#the-mcp-tools)):
`name` (a plugin's skill as `plugin:skill`), `uses`, `firstUsedAt`, `lastUsedAt`,
`skillId` and `version` when it matched one of your registry skills,
`versionInferred` (`true` when `version` is present: it is inferred from when
versions were pushed) and `deleted` (since deleted from the registry). It adds:

| Field | Type | Meaning |
|---|---|---|
| `installedHere` | boolean | A skill of that name is installed on this machine: a personal skill, or a project skill of the folder the command ran in when that folder is the session's project. Absent for a plugin's skill, which is not looked for. |

## `search`

One record per hit, best match first. Every field but the snippet comes from the
registry's own records, and the title and snippet are redacted by the registry.

| Field | Type | Meaning |
|---|---|---|
| `kind` | string | `session` (a session or a subagent) or `memory`, as `--kind` takes them. |
| `id` | string | For a session, its [id](#ids), which `sessions show` takes. Memory has no `id`: `projectId` and `path` name it. |
| `harness` | string | For a session, the harness. |
| `parentId` | string | For a subagent, the `id` of its session. |
| `title` | string | For a session, its title or first prompt. |
| `path` | string | For memory, the file's path in the project's memory folder, for example `dns.md`. |
| `projectId` | string (UUID) | The project. Absent for a session not filed under one yet. |
| `project` | string | The project's name. |
| `machineId` | string (UUID) | For a session, the machine it came from; for memory, the machine that sent the version matched. |
| `machineName` | string | That machine's name. |
| `date` | time | For a session, its latest activity; for memory, when the file last changed. |
| `snippet` | string | The text around the match, with each matched word between `«` and `»`. It can hold line breaks. |

No match is an empty array, `[]`, with exit code `0`. A registry that does not
offer search (it does not list `search` in its `/v1/meta` capabilities, or answers
`404`) is `registry_incompatible`.

## Errors

When a command fails, standard error holds one object:

```json
{"error":{"code":"not_signed_in","message":"Not logged in to https://registry.example. Run `protostar auth login` first."}}
```

`code` is stable: branch on it. `message` is for people and may change. Some
codes add fields of their own, listed below.

| Code | Meaning | Commands |
|---|---|---|
| `invalid_registry` | The registry URL (from `--registry`, the environment or the saved default) is not usable. | all |
| `unknown_harness` | `--harness` names no harness that keeps sessions. | `sessions` |
| `not_signed_in` | No sign-in is stored for the registry. Run `protostar auth login`. | `sessions list`, `show`, `push`, `status --verify`; `search` |
| `session_expired` | The sign-in expired and could not be refreshed. Run `protostar auth login`. | as above |
| `registry_unreachable` | The registry did not answer, or refused or failed the request. | as above |
| `registry_incompatible` | The registry speaks an API version this protostar does not, or is too old to offer the feature: it does not list the capability in `/v1/meta` (the message reads "This registry is too old for …"), or, listing none, answered `404`. The sessions commands need `sessions`, `status --verify` also `transcript-lookup`, `search` needs `search`. The MCP tools refuse a registry that does not list theirs, even one that lists none: `get_session` needs `redacted-read`, `get_session_skills` needs `session-skills`, `get_memory` needs `memory-redacted-read`. See [Troubleshooting](troubleshooting.md#registry-too-old). | as above |
| `not_found` | No transcript has that id, and no session id starts with it. Run `protostar sessions list`. For `get_memory`, no project with memory matches (the error adds `projects`, those that have memory), or it has no file at that path. | `sessions show`; MCP `get_session`, `get_session_skills`, `get_memory` |
| `ambiguous_id` | More than one session id starts with the id given. The error adds `candidates`: up to 10 objects with `id`, `harness`, `title` and `lastActivityAt`. Give more of the id. | `sessions show`; MCP `get_session`, `get_session_skills` |
| `invalid_argument` | A tool argument is out of range or malformed, such as `limit` 500. | MCP tools |
| `ambiguous_project` | The project name matches more than one project with memory. The error adds `projects`: objects with `projectId`, `project` and `files`. Give the id. | MCP `get_memory` |
| `incomplete` | The push ran, but some transcripts failed or were left part way; their records say which. | `sessions push` |
| `interrupted` | Stopped with Ctrl+C. Nothing is lost; push again to carry on. | `sessions push` |

A misplaced or unknown option is refused before the command runs, as text on
standard error with exit code `255`, whether or not `--json` is given.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. For `sessions status`, everything is backed up. |
| `1` | An error (with an error object), or for `sessions status` and `protostar status`, something pending or needing attention (no error object). |
| `130` | `sessions push` was stopped with Ctrl+C (`interrupted`). |
| `255` | The command line was refused (an unknown or misplaced option, or an invalid value such as `search --kind other`). |

Check the error object, not only the exit code, to tell a failure from pending
work.

## The MCP tools

`protostar mcp` serves the same records to agents as MCP tools (see
[Let your agents search your history](../guides/agents-search-history.md)). Each
result is one JSON object in the tool result's text: `search` gives `{"hits":[...]}`
of [search hits](#search), `list_sessions` gives `{"sessions":[...]}` of
[session records](#sessions-list), `get_session` gives `session` (a session
record), `offset`, `messages` ([`sessions show` records](#sessions-show), each
with `index`, and `truncated: true` on one with a field cut at 4,000 characters)
and `nextOffset` when there are more, `get_session_skills` gives `session` (a
session record) and `skills` (each with `name`, `uses`, `firstUsedAt`,
`lastUsedAt`, `skillId`, `version`, `versionInferred` and `deleted`, as the
registry reports them), and `get_memory` gives `projectId`,
`project`, `files` (each with `path`, `version`, `machineId`, `machineName`,
`deletedEverywhere` when set, `content`, and `truncated` when cut) and `omitted`
(paths that did not fit). A failed call is a tool error whose text is the
[error object](#errors).
