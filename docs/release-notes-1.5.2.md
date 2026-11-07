# DejaVu 1.5.2

Fixes Discord leaking into clips with *Captured app audio only* on (the default) on
SteelSeries Sonar and similar setups.

A game that plays on one Sonar device (Gaming) often keeps an idle audio session on
another (Chat) for its own voice chat. DejaVu picked the first device where it found the
game, which could be Chat, and then recorded that whole device, Discord included. It now
records from the device where the game is actually playing: on Sonar that's Gaming, where
it can capture the game by itself. This bug was already in 1.4.
