# Project Globe

Currently a work-in-progress. The mod is meant to simulate globe logic for Vintage Story's 2D world.

Going East or West past a map border will land you at the opposite edge of the map as if you were walking around a real globe.

Pole logic is different, as it behaves in the opposite way of what you think. Walking across a Pole edge lands you at the opposite meridian, so walking across a Northern or Southern corner will land you at about the center of the map, this is how a globe really works when translating it from a map.

**Vintage Story** 1.22.7 is all I have tested, but I know for sure nothing lower than 1.22 will work since the logic this mod deals with was changed since then.

Installs on the server and on every client. The mod works in single player and should work in multiplayer.

## Installation

1. Just download the latest version.
2. Place in Vintage Story data and mods folder.
3. Should work with any other mod that doesn't do the exact same thing. This mod only activates when near a world border.

## Limitations

- Crossing poles becomes a little janky when mounted or in a boat. E-W works fine though. I'm trying to figure out a fix
- Dropped items won't cross map borders, only players and their mounts.
- The default world size is way too big for this mod to even matter. I use a 102k block world. You can use a slightly bigger setting but the journey around the world will be a long one. Use a 1024 block world for testing, don't go smaller as the mod WILL break.
- World generation is not consistent past world borders.

## Plans

- Make dropped items and projectiles capable of crossing to the other side.
- Make it possible to actually see the other side past the world border.
- Redesign the world map to better reflect that it ain't flat no more. *spit*
- Change world gen to make it consistent across a border. No more going from ice caps to grassy tundra.
- Probably more that I'll think of later on.

## Bugs?

Please reach out to me any way you can. I can be found in the VS discord and I'll check this page as much as I can. Logs save headaches.

## Credits

- Author: KumaVio (me)
- Contributors: My wonderful friend Mello for making the mod icon, and the wonderful Mid-West for shaping me into the man I am today.
