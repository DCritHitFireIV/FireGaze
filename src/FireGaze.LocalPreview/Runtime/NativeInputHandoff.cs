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
