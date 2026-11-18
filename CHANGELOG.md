# Changelog

Each release's section is what Dalamud's plugin installer shows. CI publishes the section whose heading matches the tag
(tag `v0.0.1.0` → `## 0.0.1.0`).

## 0.0.1.1

Adds the plugin icon. No functional changes.

## 0.0.1.0

First release of Social Cleanup: review your friend list in a table and remove the friends you pick in bulk, instead of one at a time. Removals are permanent.

- **Friend table:** sort and filter by name, home world, data center, online status and friend group. Select with checkboxes, shift-click for a range, or select, deselect or invert everything the filter shows. Pending friend requests cannot be selected.
- **Removal queue:** a confirmation dialog lists everyone who will be removed, then the queue removes them one at a time and shows the result for each friend. Anything unclear stops the queue; you choose to resume or cancel, and nothing is retried on its own. At the end the friend list is checked to make sure exactly the chosen friends are gone.
- **Standard and fast mode:** standard mode waits for the game to reload the friend list after every removal (about 8 seconds per friend). Fast mode, an opt-in, skips that reload and checks the list once at the end (under a second per friend).
- **Friend List window guard:** while the in-game Friend List window is open, the plugin will not start removals or refresh the list, and a running batch pauses until you close it.
- **Audit log:** every removal is written to a CSV file in the plugin's configuration folder.
- **Game version lock:** the plugin only works on the game version it was tested with (2026.11.03.0000.0000). After a game patch it turns itself off and shows a message until an updated version is released.
- Open it with `/socialcleanup`.
