namespace FireGaze.LocalPreview.Runtime;

/// <summary>A reversible view of preview-owned state during one native input.</summary>
public interface IPreviewSuspension<in TIntent>
{
    void Resume();
    void Accept(TIntent intent);
}

/// <summary>
/// Runs native input once. Nested execution, unlike queue acceptance, can commit the outer input.
/// The native call is never retried, including when restoration or accepted cleanup throws.
/// </summary>
public sealed class NativeInputHandoff<TIntent>
{
    public bool RunGuarded(TIntent intent, System.Func<bool> original, bool startedFromResult,
        System.Func<TIntent, IPreviewSuspension<TIntent>> suspend, System.Action<System.Exception> report)
    {
        bool invoked = false, completed = false, nativeResult = false;
        bool OriginalOnce() { invoked = true; nativeResult = original(); completed = true; return nativeResult; }
        try { return Run(intent, OriginalOnce, startedFromResult, suspend); }
        catch (System.Exception ex)
        {
            try { report(ex); } catch { }
            if (!invoked)
            {
                try { return OriginalOnce(); }
                catch (System.Exception nativeException) { try { report(nativeException); } catch { } }
            }
            return completed && nativeResult;
        }
    }
    public TResult InspectGuarded<TResult>(System.Func<TResult> original,
        System.Func<IPreviewSuspension<TIntent>> suspend, System.Action<System.Exception> report)
    {
        bool invoked = false;
        TResult nativeResult = default!;
        TResult OriginalOnce() { invoked = true; nativeResult = original(); return nativeResult; }
        try { return Inspect(OriginalOnce, suspend); }
        catch (System.Exception ex)
        {
            try { report(ex); } catch { }
            if (!invoked)
            {
                try { return OriginalOnce(); }
                catch (System.Exception nativeException) { try { report(nativeException); } catch { } }
            }
            return nativeResult;
        }
    }
    private int depth;
    private bool started;
    private TIntent acceptedIntent = default!;
    private long nextExternalToken;
    private long externalToken;
    private TIntent externalIntent = default!;
    private IPreviewSuspension<TIntent>? externalLease;

    public bool HasExternal => externalToken != 0;

    /// <summary>
    /// Suspends once before an external plugin reads presentation state. The caller alone invokes
    /// its original operation; native hooks reached inside it share this suppression boundary.
    /// </summary>
    public long BeginExternal(TIntent intent, System.Func<IPreviewSuspension<TIntent>> suspend)
    {
        if (depth != 0 || HasExternal) return 0;
        if (nextExternalToken == long.MaxValue) return 0;
        var lease = suspend();
        externalIntent = intent;
        externalLease = lease;
        externalToken = ++nextExternalToken;
        depth++;
        return externalToken;
    }

    /// <summary>A stale binding releases suppression without replaying either old snapshot.</summary>
    public bool CompleteExternal(long token, bool accepted, bool bindingCurrent)
    {
        if (token == 0 || token != externalToken || externalLease == null) return false;
        var lease = externalLease;
        var intent = externalIntent;
        InvalidateExternal();
        if (!bindingCurrent) return false;
        if (accepted) lease.Accept(intent);
        else lease.Resume();
        return true;
    }

    /// <summary>Actor/map/ownership loss must never resume a previously suspended preview.</summary>
    public void InvalidateExternal()
    {
        if (!HasExternal) return;
        externalToken = 0;
        externalLease = null;
        externalIntent = default!;
        depth--;
    }

    public bool Run(TIntent intent, System.Func<bool> original, bool startedFromResult,
        System.Func<TIntent, IPreviewSuspension<TIntent>> suspend)
    {
        if (depth > 0)
        {
            bool nestedResult = original();
            if (nestedResult && startedFromResult)
            {
                started = true;
                acceptedIntent = intent;
            }
            return nestedResult;
        }

        var lease = suspend(intent);
        started = false;
        acceptedIntent = intent;
        depth++;
        bool result;
        try
        {
            result = original();
            if (result && startedFromResult)
            {
                started = true;
                acceptedIntent = intent;
            }
        }
        catch
        {
            depth--;
            // An inner native execution may have started before an outer caller throws.
            // That accepted input owns the new state and must not be overwritten with a snapshot.
            if (started) lease.Accept(acceptedIntent);
            else lease.Resume();
            throw;
        }
        depth--;
        if (started) lease.Accept(acceptedIntent);
        else lease.Resume();
        return result;
    }

    public TResult Inspect<TResult>(System.Func<TResult> original,
        System.Func<IPreviewSuspension<TIntent>> suspend)
    {
        if (depth > 0) return original();
        var lease = suspend();
        depth++;
        try { return original(); }
        finally
        {
            depth--;
            lease.Resume();
        }
    }
}
