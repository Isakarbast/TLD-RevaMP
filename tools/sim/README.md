# Multiplayer simulator

The whole mod running as several machines in one process, with no game and no Steam.

It's kept as a narrow tool for ownership and message ordering, and the fake game isn't being grown (decided 2026-11-02). It only finds bugs in the mod's own logic, and only as far as `SimGame.cs` is right. Real play and `itemchaos` tell more about what goes wrong for players.

`python3 tools/sim/run.py [all|refused|lost|late|hostgrab|race|trace <seed>|chaos <seeds>|chaosat <seed>|sweep <part> <parts> <seeds>]`

## How it works

- **The mod's code is real.** All of `src/TLDRevamp` is compiled except `SteamCompat.cs`. That means the real `Mp`, `SteamTransport`, `SendOutbox` and `Entities`.
- **Stubs are generated.** `stubgen` reads which parts of the game's API the mod uses, against `lib/*.dll`, and writes them with empty bodies into `GameStubs.g.cs`. The file isn't committed, so this needs `lib/` and CI doesn't run it.
- **Some behaviour is written by hand.** `SimGame.cs` has what has to work: Unity's objects and math, the fake Steam sockets, and the game's pickup, destroy and break methods. Anything declared there is left out of the generated stubs.
- **Each machine has its own statics.** Every machine is the same assembly in its own `AssemblyLoadContext`.
- **Time and network are virtual.** Links are reliable and in order, with random delay, and sends can be refused. A seed is repeatable.
- **Harmony doesn't run.** `Harm` in `SimGame.cs` runs the mod's patches, only for the game methods written by hand there.

## What it has shown

| Case | Switch off | Switch on | In the real game |
|---|---|---|---|
| `refused`: half of all sends refused for 2 s (`QueueRefusedSends`) | 5 of 10 items on the server, 0 on the client | 10 of 10, all agree | Messages lost off, none on |
| `lost`: a copy destroyed twice within 30 s (`RefetchLater`) | Still gone after 45 s | Back after 30 s | Gone after 41 s / back after 30 s |
| `late`: a break before the joiner comes (`BreaksKept`) | Unbroken on the joiner | Broken | The same, on a rabbit |

`race` has three machines grabbing and dropping one item with late replies. It found that the host can hold a copy: a refusal on the host arrives inside the pickup prefix, before the game sets `pickedUp`, so the lost-grab drop finds nothing to drop. `itemchaos --only aud-host-grab` shows the same in the real game when the host stands beside the player holding the item. Fixed (`HostGrabLetGo`), and `hostgrab` checks it off and on.

## What it can't check

- **Anything the game decides.** That covers physics, the load path, Steam and visuals. In the real game the host's copy was dropped again when it stood 2.6 m away; the sim has no such rule. A finding here is a question for `itemchaos`, not an answer.
- **False alarms from the hand-written game.** It's only as right as `SimGame.cs`.
- **Most items.** Items have only an id, a position, a body and a pickup, with no cars, parts, tanks or usables. `LoadStuff` does nothing, so edits and anything else in a record's other lists aren't tested.

## Speed and repeats

- **Fresh machines:** about 1-2 s a run, nearly all of it JIT. A seed repeats exactly.
- **Reused machines:** about 110 ms a `chaos` run. A seed plays out differently from a fresh run, because what the last run left affects timing, so only a whole batch repeats. `chaosat <seed>` replays a batch up to one seed and shows its steps.
- **Reuse and bugs:** the 5 of 60 wrong that reuse once gave against 1 of 60 were the host's copy bug, and both give 0 now. What carries over between runs hasn't been traced.

## `chaos` and `sweep`

- **`chaos`:** several items, with grabs, drops, moves, spawns, removals, destroyed copies, players leaving and rejoining, refused sends and lost unreliable messages. At the end everyone lets go and waits, and every machine must agree. 600 seeds: 0 wrong.
- **`sweep`:** turns off each switch that's on by default and counts what `chaos` notices.

## Not in CI

It would need the stubs committed, and that's a list of the game's API in a public repo.
