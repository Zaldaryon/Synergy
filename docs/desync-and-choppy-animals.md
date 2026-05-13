Hey! Thanks for reporting this. Let me break it down:


PLAYER INVISIBLE / STUCK ON THE ELK

So this one's actually a vanilla bug that's been around since 2021 — nothing to do with Synergy. The game sometimes just forgets to tell your client that another player moved or dismounted. Sounds still work because those go through a different system.

You can find other people reporting the exact same thing with zero mods installed:
- https://github.com/anegostudios/VintageStory-Issues/issues/3773
- https://github.com/anegostudios/VintageStory-Issues/issues/6687
- https://www.vintagestory.at/forums/topic/5643-players-sometimes-turn-invisible/

Synergy skips players entirely in every single optimization — player position sync uses a completely separate network path that we don't touch at all.

Only workaround: the person who looks "stuck" disconnects and reconnects. That's it. It's been the only fix since this bug first appeared.


CHOPPY ANIMAL MOVEMENT

Yeah, this one's on us. Synergy reduces how often distant animal positions get sent to your client — from 30 updates/sec down to 15 for anything beyond 50 blocks. Fewer updates = choppier movement on your screen.

To fix it, open Synergy.json in your ModConfig folder and set:

    "DistanceBasedSendFrequencyEnabled": false

Still choppy? Also try:

    "RepulseAgentsThrottleEnabled": false

Restart the server after making changes.
