---
sidebar_position: 3
title: Projects
---

# Projects

Protostar files your sessions and your project skills under a **project**: the
codebase you were working in. The same project has to be recognised on every
machine, even though the folder is `C:\Users\alice\src\demo-app` on one and
`/home/alice/src/demo-app` on another. So a project is never a path. This page
explains what Protostar treats as one project, how to check which project a
folder is in, and how to fix it when Protostar gets it wrong.

## What counts as one project

**A git repository is recognised by its first commit.** That commit is the same
in every clone, on every machine and operating system, whatever the folder is
called and wherever it lives. So these are all the same project:

- the same repo cloned to different paths, or on Windows and Linux;
- every worktree of a repo;
- a subfolder of a repo opened on its own (the subfolder is recorded with each
  session, so you can still tell them apart);
- a fork, which starts from the same first commit as the repo it was forked from.

Remotes play no part in recognising a repo, so renaming or adding a remote, or
moving the repo to another owner on GitHub, keeps it the same project. The
remote is used only for the project's name: `git@github.com:example/demo-app.git`
and `https://github.com/example/demo-app.git` both show as
`github.com/example/demo-app`. A repo with no remote is named after its folder.

**A folder that is not a git repo is recognised by this machine and its path.**
It is a project of its own, named after the folder, and it works like any other.
The same folder on another machine is a different project until you
[link](#link-a-folder-to-a-project) them.

A shallow clone (`git clone --depth 1`) cannot be trusted to know its first
commit, so it is matched by its remote once a full clone of that repo has been
backed up, and by machine and path until then.

## Check which project a folder is in

Run `protostar project` in the folder (or pass `--dir <folder>`):

```console
$ protostar project
Folder         /home/alice/src/demo-app
Project        github.com/example/demo-app
Id             84ae7ab4-363a-4328-b80c-81c5f6495764
Recognised by  the repo's root commit 720d3077c24b
Git            root 720d3077c24b on refs/heads/main, origin
               https://github.com/example/demo-app.git
```

**Recognised by** says why: the repo's first ("root") commit, a link you made,
or this machine and path. In a worktree or a subfolder you also see where the
project is filed on this machine:

```console
$ protostar project --dir ~/src/demo-app/src
Folder         /home/alice/src/demo-app/src
Project        github.com/example/demo-app
Id             84ae7ab4-363a-4328-b80c-81c5f6495764
Recognised by  the repo's root commit 720d3077c24b
Subfolder      src
Filed by       /home/alice/src/demo-app (the repo's main worktree on this machine)
```

A folder nothing has been backed up from yet shows `none yet: the first backup
from here creates it` as its id. `protostar sessions list` shows the same project
names, and `--project` filters by them.

## Fix it

### Link a folder to a project

When a folder should belong to an existing project but does not (typically a
folder that is not a git repo, kept on two machines), link it:

```console
$ cd ~/notes
$ protostar project link github.com/example/demo-app
Linked this folder to github.com/example/demo-app. Its sessions moved with it.
```

Give the project's id or its full name, as `protostar project` shows it. The link
belongs to this folder on this machine, and its sessions move with it. Run it on
each machine whose folder should join.

### Unlink it again

`protostar project unlink` removes the link, and the folder is recognised the
usual way again:

```console
$ protostar project unlink
Unlinked this folder. It now belongs to notes.
```

### Split a fork from its upstream

A fork is the same project as the repo it was forked from, because it starts from
the same commit. When yours has become something else, run
`protostar project unlink` in its folder to give it a project of its own:

```console
$ protostar project unlink
This folder shared its project with other copies of the repo. It now has its
own: github.com/example/demo-app (/home/alice/src/demo-app-fork). Run
`protostar project unlink` again to rejoin.
```

Run `protostar project unlink` again to rejoin.

See the [command reference](../reference/commands.md#protostar-project) for every
option.
