# Social Cleanup

A Dalamud plugin for Final Fantasy XIV that loads your friend list into a sortable, filterable table and removes the
friends you select through a queue, instead of one at a time by hand.

Removing friends in the game is slow: after every manual removal the game reloads the whole friend list, which takes
about 7 seconds. Social Cleanup lets you pick the friends to remove from a table and then works through them for you.

## Warning

Read this before installing.

- **Removals are permanent.** A removed friend is gone; there is no undo. The plugin does not show the game's own
  confirmation dialog for each friend. Its only confirmation is the plugin's own dialog listing everyone who will be
  removed.
- **It automates server-facing actions and hooks game functions.** The plugin calls the game's internal friend-removal
  function directly and hooks several internal game functions. Third-party tools are against the Final Fantasy XIV
  terms of service. Use it at your own risk.
- **Not affiliated with or endorsed by Square Enix.**
- **It disables itself after every game patch.** The plugin only runs on the exact game version it was verified
  against. After a patch it does nothing except show a message, until a new version of the plugin has been released.
  See [Version lock](#version-lock).

## What it does

- Reads the friend list into a table you can sort and filter by name search, home world, data center, online status
  and friend group.
- Multi-select with checkboxes: shift-click to select a range, "Select all filtered", "Deselect filtered",
  "Invert filtered" and "Clear selection".
- Removes the selected friends through a queue, one removal in flight at a time.
- Pending friend requests (sent or received) cannot be selected or removed.
- Every real removal is written to an append-only audit CSV (see [Audit log](#audit-log)).

Command: `/socialcleanup` opens or closes the main window. The Settings button in the main window (or the settings
button in the plugin installer) opens the settings.

### Standard and fast mode

- **Standard mode** (default) sends one removal, then waits for the game's own friend-list reload to confirm it before
  moving on. About 8 seconds per friend.
- **Fast mode** (opt-in checkbox in the confirmation dialog; the choice is remembered) skips the per-removal reload.
  Each result is taken from the server's reply to the removal instead, and the friend list is reloaded and checked once
  at the end. About 0.8 seconds per friend (53 friends in 42 seconds in practice). Fast mode checks itself when it
  starts; if its checks do not pass it is shown as unavailable, with the reason, and removals run in standard mode.

### Safety behaviour

- Only one removal is in flight at a time, and the next one is not sent until the previous one is confirmed.
- Anything unclear (no confirmation within the timeout, an unexpected reply, the friend list changing in a way that
  was not expected) **halts** the queue. Nothing is retried automatically.
- When the batch ends, a final check compares the friend list with the batch: exactly the friends that were removed
  must be gone, and nothing else.
- If the game is closed or the plugin reloaded while a removal was waiting for its result, removals stay blocked on
  the next start until you have checked your in-game friend list and confirmed that in the plugin.

## Installing

### From the custom plugin repository

1. In game, open the Dalamud settings (`/xlsettings`) and go to the **Experimental** tab.
2. Under **Custom Plugin Repositories**, add this URL, tick the checkbox to enable it, and save:

   ```
   https://raw.githubusercontent.com/EtheirysIO/SocialCleanup/master/repo.json
   ```

3. Open the plugin installer (`/xlplugins`), search for **Social Cleanup** and install it.

The release workflow creates `repo.json` when it publishes the first tagged release. Until the first release has been
published the URL above returns "404 Not Found" and Dalamud lists no plugin from it.

### Building from source (dev plugin)

Requirements: Windows, the .NET 10 SDK, and Dalamud installed through XIVLauncher (the build takes Dalamud's
assemblies from `%AppData%\XIVLauncher\addon\Hooks\dev\`, or from the folder in the `DALAMUD_HOME` environment
variable if that is set).

```
dotnet build SocialCleanup.sln -c Debug -p:Platform=x64
```

The plugin is written to `bin\Debug\SocialCleanup.dll` at the repository root. In game, open `/xlsettings`, go to
**Experimental**, add the full path of that DLL under **Dev Plugin Locations**, save, then enable Social Cleanup in
the **Dev Tools** section of the plugin installer.

## Using it

1. Close the in-game Friend List window (see below), then open Social Cleanup with `/socialcleanup` and press
   **Refresh** to load the friend list (a list the game downloads by itself is picked up too).
2. Filter and select the friends to remove.
3. Click **Remove N selected…**. The confirmation dialog lists every friend who will be removed (including selected
   friends hidden by the current filter), shows an estimated time and has the **Fast mode** checkbox. Click
   **Remove N friends** to start.
4. The queue panel shows progress, the friend being removed and a per-friend list with each result.

### Keep the in-game Friend List window closed

The plugin and the game's Friend List window must not work on the friend list at the same time. While the Friend
List window is open, the plugin shows a banner and refuses to start removals or refresh the list. If you open it while
a batch is running, the batch pauses and continues by itself once the window is closed.

### When a batch halts

The queue stops and shows the reason. You then choose:

- **Resume** gets a fresh friend list and checks every friend that may already have been sent against it (gone:
  Removed; still listed: Failed). Those friends are never sent again. The batch continues only if everything could be
  checked; otherwise it stays halted. Friends that were refused before anything was sent are tried again.
- **Cancel batch** stops the batch. Friends not yet started are left alone.

While a batch is running, **Cancel** stops it after the current friend's removal has settled.

Some problems end the batch for good and Resume is not offered: friends who were not part of the batch disappeared
from the list, or the final check did not match. Cancel or clear the batch and check the friend list in game.

### The final check

After the last friend, the plugin compares the friend list with the batch (fast mode reloads the list once for this;
standard mode uses the last full reload). "Final check passed" means exactly the friends marked removed are gone and
everyone else is still there. If it fails, the panel lists every friend involved: friends missing that were not part
of the batch, friends marked removed but still listed, and batch friends missing without a confirmed removal. If the
reload times out, open the in-game Friend List to check it yourself.

## Settings

- Use fast mode by default.
- Delay between removals in standard mode and in fast mode (fast mode at least 200 ms; a small random amount is added).
- Per-friend timeout (10 to 300 seconds) and the fast-mode server reply timeout (2 to 60 seconds).
- Verbose logging: writes per-removal details to `/xllog` at Information level instead of Debug.
- Show developer tools: adds a section with dry runs (check each friend, send nothing), a single-friend removal test,
  raw diagnostics, the Friend List window detection state and the audit file location.

Every value is clamped to a safe range; no setting turns off a safety check.

## Audit log

Every real removal writes an `INTENT` row immediately before the game function is called and an `OUTCOME` row after
it returns, followed by the verdict. Batch start and end, the final check, refusals and dry runs are logged too. The
file is append-only CSV:

```
%AppData%\XIVLauncher\pluginConfigs\SocialCleanup\removal-audit.csv
```

Columns: `utc,local_content_id,target_content_id,name,home_world,dry_run,phase,result,detail`.

The file contains character names and content IDs. Do not post it publicly. The plugin reads it at start-up to detect
a removal that was sent but never confirmed.

## Version lock

The plugin calls and hooks reverse-engineered game functions and reads raw memory offsets that were verified against
exactly one game version, set in [`SocialCleanup/SupportedGameVersion.cs`](SocialCleanup/SupportedGameVersion.cs).

At start-up the plugin reads the running game's version from two independent sources (the version string in game
memory and the `ffxivgame.ver` file next to the running executable). Unless both are readable, agree, and equal the
supported version exactly, the plugin disables itself completely: no signature scans, no hooks, no memory reads, no
configuration load, no audit log. It only registers `/socialcleanup` to show a window with the supported and
detected versions. There is no override. In practice this means the plugin stops working after every game patch until
a new release is published.

### Updating for a new game patch (maintainers)

Do not change the supported version until every item below has been re-verified against the new `ffxiv_dx11.exe`.
The plugin's own run-time checks (unique matches, call-site cross-check, vtable-slot comparison, the fast-mode
self-check) refuse to run when something obviously moved, but they are not a substitute for re-verification.

1. Wait for a Dalamud build for the new patch (the FFXIVClientStructs it ships provides the struct layouts and
   `Framework.GameVersionString` used here). Build and run against it locally.
2. Re-verify against the new executable:
   - `DirectFriendRemover.FunctionSignature`: the start of `RemoveFriend(InfoProxyFriendList* this, uint index)`
     (returns a byte). Must match exactly once in `.text`, with unchanged parameters.
   - `DirectFriendRemover.CallSiteSignature`: a call site of that function (`E8 rel32` at match+6) whose target must be
     the function above.
   - `DirectFriendRemover.GetEntryPrologue`: the prologue bytes of `InfoProxyCommonList.GetEntry`, used as a
     cross-check before each removal.
   - `ReloadSuppressor.ReplyHandlerSignature`: the RemoveFriend reply handler, `void(uint, byte* payload)`, and its
     payload layout as read in `ReloadSuppressor` (ContentId at +0x0, result at +0x8, flags at +0xC, name at +0xD).
   - `ReloadSuppressor.RequestDataSignature`: `InfoProxyFriendList.RequestData`, which must still be vtable slot 3 of
     the friend proxy.
   - `InfoProxyFriendList.EndRequest` at vtable slot 10, hooked by `FriendListReader`.
   - The friend-proxy bytes `ReloadSuppressor.DownloadInFlightOffset` (+0x3ADD: 1 while a download is in flight) and
     `ReloadSuppressor.ListCompleteOffset` (+0xC8: 1 once the list is complete).
   - The request-state bits of `CharacterData.ExtraFlags`, `(ExtraFlags >> 4) & 0xF` in `FriendEntry.RequestState`:
     1 = accepted friend, 2 = request sent, 3 = request received.
3. Set `SupportedGameVersion.Value` to the new `ffxivgame.ver` string and update the pinning test in
   `SocialCleanup.Tests/VersionGateTests.cs` (the `Supported` constant, `SupportedConstant_IsTheVerifiedVersion_AndWellFormed`,
   `ExactMatch_AgainstTheRealConstant_Passes`, and the neighbouring versions in the blocking tests). Update the version
   mentioned in the doc comments of `DirectFriendRemover` and `ReloadSuppressor`.
4. Point CI at the Dalamud build you verified with: update `url` and `sha256` in
   [`.github/actions/setup-dalamud/action.yml`](.github/actions/setup-dalamud/action.yml) (see
   [Releasing](#releasing)).
5. Test in game: a dry run, the single-friend test, a small standard batch and a small fast batch (check that the fast
   mode self-check passes and the final check passes).
6. Bump the plugin version and release.

## Building and running the tests

The tests cover the Dalamud-free parts (queue logic, fast mode decisions, the version gate, the audit latch parser,
the table model). They do not need Dalamud or the game:

```
dotnet test SocialCleanup.Tests/SocialCleanup.Tests.csproj
```

A Release build of the plugin (needs Dalamud, see above):

```
dotnet build SocialCleanup.sln -c Release -p:Platform=x64
```

This writes `bin\Release\SocialCleanup.dll` and the installable package `bin\Release\SocialCleanup\latest.zip`
(containing `SocialCleanup.dll`, `SocialCleanup.json` and `SocialCleanup.deps.json`), produced by DalamudPackager.

NuGet restore uses the committed `SocialCleanup/packages.lock.json`; CI restores with `--locked-mode`. After changing
package references, run `dotnet restore` locally and commit the updated lock file.

## Releasing

Releases are built and published by GitHub Actions.

- `.github/workflows/build.yml` runs on pushes and pull requests to `master`: restore, Release build, tests, and
  uploads the package as a build artifact.
- `.github/workflows/release.yml` runs when a tag starting with `v` is pushed (or manually, for an existing tag). It
  builds with the tag's version, runs the tests, creates the GitHub Release with `latest.zip` (and its SHA-256)
  attached, regenerates `repo.json` from the packaged manifest and commits it to `master`.

CI downloads a pinned Dalamud distribution (`goatcorp/dalamud-distrib`, currently Dalamud 15.0.4.0 for API 15) and
verifies its SHA-256 before building; see `.github/actions/setup-dalamud/action.yml`. Struct layouts from
FFXIVClientStructs are compiled into the plugin, so a release should be built against the same Dalamud build it was
tested with. To move the pin, take the commit of `goatcorp/dalamud-distrib` whose `latest.zip` you want, use
`https://raw.githubusercontent.com/goatcorp/dalamud-distrib/<commit>/latest.zip` as `url` and its SHA-256 as
`sha256`.

To cut a release:

1. Set `<Version>` in `SocialCleanup/SocialCleanup.csproj` to the new four-part version (for example `1.0.0.0`) and
   commit it to `master`.
2. Tag that commit with the same version and push the tag:

   ```
   git tag v1.0.0.0
   git push origin v1.0.0.0
   ```

   `v1.0.0` is also accepted and means `1.0.0.0`. The workflow fails without publishing anything if the tag does not
   match the csproj version or the tests fail.
3. Watch the Release workflow in the Actions tab. When it succeeds, the GitHub Release exists and `repo.json` on
   `master` points to its `latest.zip`; Dalamud picks up the update from the custom repository.

To re-publish an existing tag (for example after a failed run), run the Release workflow manually from the Actions
tab and enter the tag. It replaces the release assets and rewrites `repo.json` to point at that tag.

## License

Licensed under the GNU Affero General Public License v3.0 or later. See [LICENSE](LICENSE).
