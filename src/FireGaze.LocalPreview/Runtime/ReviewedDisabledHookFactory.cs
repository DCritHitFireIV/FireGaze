using System.Reflection;

namespace FireGaze.LocalPreview.Runtime;

internal interface IReviewedDisabledHookApi<THook> where THook : class, IDisposable
{
    void Qualify(object owner, Delegate detour);
    void Verify(nint address, Assembly actorAssembly);
    nint FollowJmp(nint address);
    THook Construct(nint address, Delegate detour, Assembly actorAssembly);
    bool IsEnabled(THook hook);
    Delegate Original(THook hook);
    void TrackScoped(THook hook);
}

internal static class ReviewedDisabledHookFactory
{
    internal static THook Create<THook>(IReviewedDisabledHookApi<THook> api, nint address,
        object owner, Delegate detour, List<IDisposable> owned, Action<THook?> bind,
        Func<IDisposable, bool> dispose) where THook : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(detour);
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(dispose);
        if (address <= 0) throw new InvalidOperationException("Invalid reviewed native address.");
        api.Qualify(owner, detour);
        var assembly = owner.GetType().Assembly;
        api.Verify(address, assembly);
        if (api.FollowJmp(address) != address)
            throw new InvalidOperationException("Official FollowJmp changed the reviewed native target.");
        // Called synchronously during single-owner runtime setup. Reserve before the
        // official constructor so immediate ownership transfer cannot allocate.
        owned.EnsureCapacity(checked(owned.Count + 1));
        THook? hook = null;
        try
        {
            hook = api.Construct(address, detour, assembly);
            owned.Add(hook);
            // All fallible getters, holder binding and scoped registration run
            // only after the real runtime cleanup list owns the returned hook.
            bind(hook);
            if (api.IsEnabled(hook)) throw new InvalidOperationException("Reviewed constructor returned an enabled hook.");
            var original = api.Original(hook);
            if (original is null || original.GetType() != detour.GetType())
                throw new InvalidOperationException("Reviewed constructor did not publish a matching Original.");
            api.TrackScoped(hook);
            return hook;
        }
        catch
        {
            if (hook is not null)
            {
                var clean = false;
                try { clean = dispose(hook); } catch { /* Concrete runtime owner remains. */ }
                if (clean)
                {
                    owned.Remove(hook);
                    try { bind(null); } catch { /* Native cleanup already completed. */ }
                }
            }
            throw;
        }
    }
}
