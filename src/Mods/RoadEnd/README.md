# Road End

The long drive's road has an end. The main road (the one with the power poles) stops at a fixed distance from the
start, and where it stops stands the game's own roadside grave, with a note on the ground in front of it:
*"Sorry she died...."* The land, the side roads and everything on them go on, so you can still drive on.

A separate mod, not part of TLD Revamp: it changes the game a lot, so it is a choice for each new game. The game's
new-game screen gets a row **Road end: Off / 5000 km**, Off unless you pick it. Each world keeps its choice: loading
a save of a world started with the end brings it back, one started without it stays endless.

It works on its own (single player) and with TLD Revamp's multiplayer, where it registers itself: the join check
then makes every player have it, and the host's world decides whether the session's road has an end.

## What it does

- **The main road stops** after 5000 km of driving along it by default (where the game's road used to end), and its
  power poles with it.
- **Everything else goes on**: terrain, side roads, trees, rocks, buildings. Only the grave's own spot is kept clear
  (no tree or rock within 15 m, no building within 80 m).
- **The grave** is the game's roadside grave (`itemdatabase.b.kereszt`), added to the game's building generator, so
  the game places, removes and saves it like any other. The note is a sheet of paper lying flat between the end of
  the road and the grave.

## How a world keeps its choice

The game's saves are not changed. A list next to them, `Documents/TheLongDrive/Saves/RoadEnd-worlds.txt`, holds the
seed of each world started with the end, with its distance and direction (`seed km N|S`). A new game started with
the row on Off takes its seed off the list. So two worlds with the same seed share the choice made for the later one.
Deleting a line takes that world's end away.

## Where the end is

The distance is measured along the main road, as you drive it, not in a straight line: the road wanders, and in the
current game versions 5000 km of road end only about 2900 km from the start as the crow flies (seed 20260924).

The game builds its main road in 10 km road chunks along x = 0. Inside a chunk it goes from the southmost of the
chunk's points to the northmost through the others, on a path picked from the chunk's seed; a link road joins each
chunk to the next. When a world starts, the mod works that road out with the game's own code on scratch copies of
the chunks, piece by piece from the start: each piece's length as the game lays it (10 m steps that follow the
game's road noise and the ground), until they add up to the distance. That takes about a second, once per world.
The road stops there, exact to the metre, and the game's log says where (`the main road ends after ... km of road`),
the same every time for a world's seed. Only what comes after the end along the road goes: where the road wanders
further north before it gets there, that part stays.

## Settings

`BepInEx/config/tldrevamp.roadend.cfg` (restart the game after changing). Distance and direction are for new worlds;
each world keeps the ones it was started with:

| Setting | Default | |
| --- | --- | --- |
| `End.Enabled` | true | off: the mod does nothing and the new-game screen has no row |
| `End.DistanceKm` | 5000 | where the main road ends, in km driven along it from the start |
| `End.Direction` | North | which way from the start (North or South) |
| `Grave.NoteText` | Sorry she died.... | what the note says |

## Limits

- Made for the game's own terrain (the normal one and *main road only*). TLD Revamp's *old 2023 terrain* builds its
  road differently and is not handled.
- In *main road only* mode there are no side roads, so past the end there is only open land.
- Multiplayer (the host's world deciding for the joiners) has not been tried in a session yet.

## Building and testing

```sh
dotnet build src/Mods/RoadEnd/RoadEnd.csproj -c Release   # -> bin/Release/netstandard2.1/RoadEnd.dll
TLD_TEST_MODS=RoadEnd tools/roadend.py                     # the new-game row, a world without and one with the end
```

Put the DLL in `BepInEx/plugins/RoadEnd/`. Licence: GPL-3.0, as the rest of the repository.
