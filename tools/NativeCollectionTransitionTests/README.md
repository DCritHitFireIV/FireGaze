# Native collection transition regression

Run with the installed client's `sqpack` directory as the required positional argument:

```powershell
dotnet run --project tools/NativeCollectionTransitionTests --configuration Release -- <SqpackPath>
```

The fixture invokes compiled production `TryApply`, `Clear`, `Update`, and deferred dispatch against private native allocations. It reads the client's sheets but does not connect to or mutate a game process, install hooks, or send actions.

## Failure reproduced

Live evidence showed mount 73 was cleared before accessory 56 could start: `MountId=0`, the old `MountObject` remained, and `DismountTimer` was about 0.5194. The previous implementation immediately called `SetupOrnament`, which the native state rejected; its 60-frame budget did not retry setup.

`red.txt` records the pre-fix failures for mount-to-accessory and mount-to-minion: both issued native setup while the retiring child/timer remained. The new dispatcher tests also failed because the original had no deferred-dispatch method.

## Fix

- Wait for `MountId=0`, `MountObject=null`, and `DismountTimer<=0` before setup.
- Dispatch from `Update` before draw readiness can return early.
- Start the normal 60-frame child-spawn budget only when setup is issued.
- A 180-frame failure deadline releases only the waiting collection.
- Bind pending work to the owned display allocation and the current effect. Clear, cancellation, native acceptance, replacement, map change, and generation reuse cannot revive it.
- Check ownership again after native teardown so cancellation during a callback cannot continue on an obsolete display.
- Keep compatible emote ownership. Real character bytes and genuine minion 95 remain unchanged by the preview switch.

`green.txt` records 15/15 passing cases before final integration. The previous dismount suite passed 79/79 and compatibility suite 151/151. This is offline verification; the live switch must still be checked after the combined build is hot reloaded.
