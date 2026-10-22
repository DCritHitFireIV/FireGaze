using System.Runtime.ExceptionServices;

namespace FireGaze.LocalPreview.Core;

/// <summary>Wraps one synchronous native input without replaying the original call.</summary>
public sealed class NativeInputGate(PreviewSession session)
{
    private int suppressionDepth;
    public bool IsSuppressed => suppressionDepth > 0;

    public IDisposable Suppress()
    {
        suppressionDepth++;
        return new SuppressionScope(this);
    }

    public T Invoke<T>(Func<T> original, Func<T, bool> accepted,
        Action<PreviewSnapshot> suspend, Action<PreviewSnapshot> applyCurrent)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(suspend);
        ArgumentNullException.ThrowIfNull(applyCurrent);
        if (IsSuppressed) return original();

        using var suppression = Suppress();
        if (session.Snapshot.Active.Count == 0) return original();

        var attempt = session.BeginNative();
        var errors = new List<Exception>();
        try { suspend(attempt.Before); }
        catch (Exception error) { errors.Add(error); }

        var value = default(T)!;
        var originalReturned = false;
        try
        {
            value = original();
            originalReturned = true;
        }
        catch (Exception error) { errors.Add(error); }

        var wasAccepted = false;
        if (originalReturned)
        {
            try { wasAccepted = accepted(value); }
            catch (Exception error)
            {
                // Never restore an old visual over an input whose outcome cannot be observed.
                wasAccepted = true;
                errors.Add(error);
            }
        }

        var resolution = session.CompleteNative(attempt, wasAccepted);
        if (resolution.Applied)
        {
            try { applyCurrent(session.Snapshot); }
            catch (Exception error) { errors.Add(error); }
        }

        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException(errors);
        return value;
    }

    private sealed class SuppressionScope(NativeInputGate owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.suppressionDepth--;
        }
    }
}
