# Native preview actor boundary harness

Run from the Git worktree root:

```powershell
dotnet run --project tools/NativePreviewActorTests/NativePreviewActorTests.csproj -c Release -v:q
```

The project compiles the actual `NativePreviewActor.cs` when present and uses the
installed CN `FFXIVClientStructs.dll`, `InteropGenerator.Runtime.dll`, and Dalamud
service types. The first RED run checked the production type through reflection
and exited 1 with `feature missing`, before the implementation existed.

Each boundary case runs in its own child process. The native interop addresses
are replaced only inside that child with `UnmanagedCallersOnly` fixture methods.
The source, clone, client object manager, reserved child, draw object, native
vtable, object IDs, and generation values live in allocated test memory.

No game process is opened. No game command, plugin load, runtime memory read,
runtime memory write, or UI automation occurs.

The tests cover lifecycle and write boundaries, including original child
visibility. They do not prove native allocator behavior, native clone rendering,
mount seating, animation resource readiness, mod appearance compatibility, or
ordinary gameplay behavior. Those require a parent-controlled real test.

The native simulation treats `Tick` as returning display readiness, so a pending
clone can return false while remaining allocated. Source visibility is expected
to change only after the clone's native readiness and visible draw object are
both available. A slot's pointer and its generation are independently varied.

The manager lookup failure case deliberately keeps the raw manager entry valid
while the native lookup returns null, to verify creation failure does not leak
an actor whose allocation is still demonstrably owned.

The 25 cases additionally require root and all current clone attachments to be
visible before the original hierarchy is hidden, restore original visibility on
draw loss, stop after native reentry changes allocation ownership, clear the
actual `IsTargetable` bit only on the display hierarchy, and enable each pending
attachment draw once instead of repeating the native call every frame.
