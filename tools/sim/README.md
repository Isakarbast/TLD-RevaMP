# Multiplayer simulator

The whole mod running as several machines in one process, with no game and no Steam. It's a first version, built to find out whether this works at all.

`python3 tools/sim/run.py [all|refused|lost|late|race|trace <seed>]`

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

`race` has three machines grabbing and dropping one item with late replies. It found that the host can hold a copy: a refusal on the host arrives inside the pickup prefix, before the game sets `pickedUp`, so the lost-grab drop finds nothing to drop. `itemchaos --only aud-host-grab` shows the same in the real game when the host stands beside the player holding the item.

## What it can't check

- **Anything the game decides.** That covers physics, the load path, Steam and visuals. In the real game the host's copy was dropped again when it stood 2.6 m away; the sim has no such rule. A finding here is a question for `itemchaos`, not an answer.
- **False alarms from the hand-written game.** It's only as right as `SimGame.cs`.
- **Most items.** Items have only an id, a position, a body and a pickup, with no cars, parts, tanks or usables yet. `LoadStuff` does nothing, so edits and anything else in a record's other lists aren't tested.
- **Lost or reordered messages.** The fake network can delay and refuse, but it doesn't drop or reorder unreliable ones yet.

## Open

- **Reusing machines isn't clean.** A run on fresh machines costs about 1-2 s, nearly all of it JIT. Reusing them after `Mp.Stop` costs about 60 ms, but 5 of 60 seeds came out wrong against 1 of 60, so something survives the stop. Why isn't known, and it may be the sim. Use fresh machines for results until it's explained.
- **Not in CI.** It would need the stubs committed, and that's a list of the game's API in a public repo.
