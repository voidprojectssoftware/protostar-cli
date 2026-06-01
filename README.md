# protostar

Live, continuous refinement of agent skills — the opposite of the "install a versioned
plugin" model. Skills are treated as evolving organisms, not packages: you use them, they sync
to a registry, an engine refines and merges them, and improvements are offered back to you
inside your harness — without the friction of version-control deploys.

The loop: **use → sync → refine → suggest → adopt → use.**

## Status

Built incrementally, one ticket at a time (Jira project `PROT`). The first component is the
**CLI** — see [`src/Protostar.Cli`](src/Protostar.Cli) and the install instructions below as it
lands.

## Repository layout

```text
protostar/
├─ src/                 # source projects (Protostar.*)
└─ protostar.sln
```
