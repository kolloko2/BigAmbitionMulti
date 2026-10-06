# Experimental community fixes for Going Public 0.3.3

This is a **draft patch kit for maintainer review**, based on the published 0.3.3 DLL and Big Ambitions Build 3682. It is not integrated into the production `src/` project. The user reported that the host could not open the rental/business menu and could not open a friend's trunk while holding a hand truck. Additional static review led to the synchronization changes below.

The work was prepared with OpenAI Codex assistance using the published assembly, decompiled mod code and local game metadata. The upstream source repository was found afterward. The maintainer should review and port the changes into the original source before shipping them. No claim is made that every crash or desynchronization is fixed.

## Proposed behavior

- Recover missing host BizMan menu references, tabs and presentation labels without changing balances or fabricating building registrations.
- Allow a loaded hand truck to open a friend's trunk using the existing walk/deposit route; retain the captured destination and reject stale deposit actions.
- Restore missing business-opening references and guard invalid owner/business state.
- Correlate merger answers with their original offer, protect host departure from a company of three or more players, and buffer merger messages/intermediate persistence until commit. Cancel buffered messages and queued work on failure.
- Detect swallowed manifest-write failures, restore the active manifest cache on rollback, and distinguish an actual manifest commit from a later mirror failure.
- Identify economic effects and receipts, reject duplicates/old wallet membership, reserve outgoing funds, prepare both monetary legs before the first effect, and keep reconnect recovery history.
- Use the authoritative company wallet when checking offers. Refresh existing offer rows, persist terminal-offer tombstones, and restore offers deferred for unavailable funds to the Inbox.
- Reject late/invalid loan snapshots. Quote daily loan payments once per loan/day and complete interrupted payments without charging again; guard early repayments and failures after debt mutation.
- Isolate money retries so one malformed journal record or disconnected recipient does not stop unrelated deliveries.
- Track each placement request separately; journal completed storage stages and retry unfinished stages. Restore owner cargo lists, amounts, paid flags, nested cargo and removed sale entries when mutations fail.
- Capture actual rental charges and the original interior; denied rentals restore the baseline and refund once.
- Reject stale interior snapshots/deltas at ingress while preserving cached-scene recovery. Track customer authority generations and synchronize access to the generation table.
- Bound decoding, fragments and queues. Keep queue admission and mutation under one lock. Reject malformed/deep/trailing payload JSON and throttle warnings.
- Use atomic mod-file writes and recover interrupted save/metadata pairs; preserve the game's native serializer.
- Shard the economic journal into 256 buckets and cache parsed records/category indexes while preserving historical deduplication IDs.

`reviewed-method-edits.txt` lists the 61 original method edits. The 22 top-level C# files contain the helper patches; `pack/` contains the Cecil/ILRepack integration and static audits.

## Verification and limits

The local helper build completed with zero errors/warnings. All **119 managed regression checks** passed: 17 host UI, 10 trunk/cart, 92 synchronization/economy/persistence checks. One check uses 2,000 shuffled duplicate monetary deliveries. The queue fixture is copied verbatim from `src/MPTransport.cs` at commit `d1e5d663a47df893541abd9147f6f6b3d5003cbc` and retains the repository's MIT license.

The local merged assembly passed target/argument checks for 57 sync Harmony classes plus 7 host and 3 cart classes. Stack/control-flow checks covered 495 changed/helper method bodies; 12,067 original bodies remained unchanged. These checks are not a full runtime verifier.

**No two-computer Unity session was run.** Managed tests use stubs and cannot prove scene/UI/physical side effects. Full-file automated inventory of 735 decompiled source files was not a manual review of every line.

Remaining limits include hard-crash consistency across game save/manifest/loan ledger, conflicting simultaneous edits from different players (no shared BaseRevision), external Unity effects during business/cargo transfer, partial ConsumeSource/spawn, MergerAbsence, and BookOnce accounting after a crash. Historical operation IDs still grow over a long-lived save.

## Build and test

Requires .NET 9 SDK for the tests/pack tool and .NET Framework 4.8 references for the helper. Run from this directory:

```powershell
dotnet run --project tests/Tests.csproj -c Release
dotnet run --project cart-tests/CartTests.csproj -c Release
dotnet run --project sync-tests/SyncTests.csproj -c Release

# originalDir contains the published 0.3.3 BigAmbitionsMP.dll and Dependencies/0Harmony.dll.
# managedDir is the user's installed game's Big Ambitions_Data/Managed directory.
dotnet build HostFix.csproj -c Release -p:OriginalMod="$originalDir" -p:GameManaged="$managedDir"
dotnet run --project pack/Pack.csproj -c Release -- "$originalDir/BigAmbitionsMP.dll" "bin/Release/net48/BAMP.HostBizManFix.dll" "release/BigAmbitionsMP.dll" "$managedDir"
```

Original DLL SHA256: `964571AFE9F9C120DD651DA3798E2724DD146843EF75D5223D7A2C4851135377`.
Game DLL SHA256: `9427561B81AAAB2ADA09C22A3C98DD9A4017A65225B389513885BF336852E911`.
Local patched DLL SHA256: `3BC35961785A942AFF9A6BB6C6B60481FDDD36F7D137862ADB7171434C191879`.

Use the exact baseline DLLs above; the surgical rewriting is version-specific. Game DLLs, original mod binaries, logs, saves, tokens and machine-specific installers are not included in this contribution.

The experimental assembly changes the wire protocol from **27 to 130**, because payload semantics change. Everyone in a session must use the same patched DLL and compatible game build, then fully restart. It cannot interoperate with stock 0.3.3 or the earlier experimental protocols 128/129. A maintainer integrating these changes should allocate their own protocol version.

Before accepting this draft, port/review the fixes and run two-player tests for host menus, friend's trunk with a loaded cart, reordered placement replies, reconnect mid-transfer, denied rental with a furnished layout, accept/decline from stale merger dialogs, host leaving a three-person company, day rollover/repayment, concurrent editing and save/load.
