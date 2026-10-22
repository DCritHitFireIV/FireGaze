using System.Runtime.CompilerServices;

namespace HMSync.Sync
{
    public sealed class TransformData { public int Value; }
    public sealed class RelaySyncService
    {
        public event Action<string, TransformData, bool>? OnTransformReceived;
        public int Count => OnTransformReceived?.GetInvocationList().Length ?? 0;
        public Type? HandlerType => OnTransformReceived?.GetType();
    }
}
namespace HMSync
{
    public struct LocalActorState { public int Value; }
    public sealed class HMSyncPlugin : IDisposable
    {
        public readonly Sync.RelaySyncService Relay = new();
        public int DisposeCalls;
        public HMSyncPlugin() => Relay.OnTransformReceived += Receive;
        private void Receive(string id, Sync.TransformData data, bool hot) { }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Dispose()
        {
            DisposeCalls++;
            Relay.OnTransformReceived -= Receive;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int GenericMembers()
        {
            Sync.TransformData? item = new() { Value = 7 };
            var lookup = new Dictionary<string, Sync.TransformData> { ["item"] = item };
            IEnumerable<Sync.TransformData> sequence = lookup.Values;
            using var iterator = sequence.GetEnumerator();
            return iterator.MoveNext() ? iterator.Current.Value + lookup.Count : 0;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int NullableMembers()
        {
            LocalActorState? value = new LocalActorState { Value = 7 };
            return value.HasValue ? value.Value.Value : 0;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Touch() => 42;
    }
}
