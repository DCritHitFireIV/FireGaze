using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Text;
using FFXIVClientStructs.STD;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;

namespace FireGaze.LocalPreview.Interaction;

/// <summary>
/// Binds the original emote-log writer's numeric Emote ID to its new queue item.
/// The formatter receives that item's Parameters at offset zero, even after
/// multiple frames. Neither displayed prose nor the latest actor emote is used.
/// </summary>
internal sealed unsafe class NativeEmoteLogCapture(IDataManager data, IGameInteropProvider interop, IPluginLog log,
    Func<byte[], ushort, byte[]> linkMessage) : IDisposable
{
    // CN PlayEmote calls this writer with (controller, emoteId, targetId). It
    // queues the message and owns row overrides such as emote 106 on a minion.
    private const string EmoteLogWriterSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC ?? 48 8B E9 4D 8B F0 8B CA 44 8B FA E8";
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool WriteEmoteLog(EmoteController* controller, uint emoteId, ulong targetId);

    // Native Update compares full EAX against -1/0/1; the FFCS bool delegate
    // loses that distinction. Preserve the observed native ABI here.
    private delegate int FormatNativeString(RaptureTextModule* module, CStringPointer input,
        StdDeque<TextParameter>* parameters, Utf8String* output);

    private readonly EmoteLogTemplateIndex templates = new(); // diagnostics only
    private readonly NativeEmoteQueueBindings bindings = new();
    private Hook<WriteEmoteLog>? writeEmoteLog;
    private Hook<RaptureLogModule.Delegates.Update>? update;
    private Hook<FormatNativeString>? format;
    private int logDepth;
    private int formatDepth;
    private bool disposed;

    public bool Enabled { get; private set; }
    public int UniqueTemplates => templates.UniqueCount;
    public int AmbiguousTemplates => templates.AmbiguousCount;
    public int MessagesLinked { get; private set; }
    public int FormatsUnmatched { get; private set; }
    public string Detail { get; private set; } = "尚未开启原生情感日志捕获。";

    public bool Enable()
    {
        if (disposed) return false;
        if (Enabled) return true;
        try
        {
            foreach (var emote in data.GetExcelSheet<Emote>())
            {
                if (emote.RowId == 0 || emote.RowId > ushort.MaxValue) continue;
                Add(emote.LogMessageTargeted.RowId, (ushort)emote.RowId);
                Add(emote.LogMessageUntargeted.RowId, (ushort)emote.RowId);
            }

            var nativeLog = RaptureLogModule.Instance();
            if (nativeLog == null || nativeLog->RaptureTextModule == null || nativeLog->RaptureTextModule->VirtualTable == null
                || nativeLog->RaptureTextModule->VirtualTable->FormatString == null || RaptureLogModule.Addresses.Update.Value == 0)
            {
                Detail = "当前客户端未解析原生日志格式化接口。";
                return false;
            }
            format = interop.HookFromAddress<FormatNativeString>(
                (nint)nativeLog->RaptureTextModule->VirtualTable->FormatString, FormatString);
            update = interop.HookFromAddress<RaptureLogModule.Delegates.Update>(RaptureLogModule.Addresses.Update.Value, UpdateLog);
            writeEmoteLog = interop.HookFromSignature<WriteEmoteLog>(EmoteLogWriterSignature, EmoteLog);
            format.Enable();
            update.Enable();
            writeEmoteLog.Enable();
            Enabled = true;
            Detail = "原生情感日志捕获已开启：按原始 Emote ID 绑定队列项，共用提示文字的动作同样可生成固定链接。";
            return true;

            void Add(uint logId, ushort emoteId)
            {
                if (logId != 0 && data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>().TryGetRow(logId, out var row))
                    templates.Add(emoteId, row.Text.Data.Span);
            }
        }
        catch (Exception ex)
        {
            ReleaseHooks();
            Detail = "原生情感日志捕获启动失败，基础右键菜单仍可使用。";
            Report(ex, "[LocalPreview] Native emote log capture could not start.");
            return false;
        }
    }

    private bool EmoteLog(EmoteController* controller, uint emoteId, ulong targetId)
    {
        nint[]? before = null;
        try
        {
            if (emoteId is > 0 and <= ushort.MaxValue && data.GetExcelSheet<Emote>().TryGetRow(emoteId, out _))
            {
                before = QueueIdentities(RaptureLogModule.Instance());
                bindings.RetainLive(before);
            }
        }
        catch (Exception ex) { Report(ex, "[LocalPreview] Native emote queue pre-capture failed."); }

        bool result;
        try { result = writeEmoteLog!.Original(controller, emoteId, targetId); }
        catch (Exception ex)
        {
            Report(ex, "[LocalPreview] Native emote log writer failed.");
            return false;
        }

        if (before is not null)
        {
            try { bindings.CaptureEmission((ushort)emoteId, before, QueueIdentities(RaptureLogModule.Instance())); }
            catch (Exception ex) { Report(ex, "[LocalPreview] Native emote queue post-capture failed."); }
        }
        return result;
    }

    private void UpdateLog(RaptureLogModule* module)
    {
        PruneQueue(module);
        logDepth++;
        try { update!.Original(module); }
        catch (Exception ex) { Report(ex, "[LocalPreview] Native log update failed."); }
        finally
        {
            logDepth--;
            PruneQueue(module);
        }
    }

    private int FormatString(RaptureTextModule* module, CStringPointer input, StdDeque<TextParameter>* parameters, Utf8String* output)
    {
        var topLevel = formatDepth++ == 0;
        var key = (nint)parameters;
        ushort emoteId = 0;
        try
        {
            if (topLevel && logDepth > 0 && parameters != null)
            {
                try
                {
                    bindings.RetainLive(QueueIdentities(RaptureLogModule.Instance()));
                    if (!bindings.TryGet(key, out emoteId)) FormatsUnmatched++;
                }
                catch (Exception ex) { Report(ex, "[LocalPreview] Native emote queue identity inspection failed."); }
            }

            int result;
            // Invoke Original once and preserve its full retry/error result.
            try { result = format!.Original(module, input, parameters, output); }
            catch (Exception ex)
            {
                Report(ex, "[LocalPreview] Native text formatter failed.");
                return 0;
            }
            if (emoteId != 0) bindings.CompleteFormat(key, result);
            if (result != 1 || emoteId == 0 || output == null) return result;

            byte[]? original = null;
            var writing = false;
            try
            {
                original = output->AsSpan().ToArray();
                var linked = linkMessage(original, emoteId);
                if (linked.Length == 0) return result;
                writing = true;
                output->SetString(linked);
                MessagesLinked++;
            }
            catch (Exception ex)
            {
                if (writing && original is not null)
                {
                    try { output->SetString(original); }
                    catch (Exception rollback) { Report(rollback, "[LocalPreview] Native emote text rollback failed."); }
                }
                Report(ex, "[LocalPreview] Native emote link rewrite failed; original message preserved where possible.");
            }
            return result;
        }
        finally { formatDepth--; }
    }

    private static nint[] QueueIdentities(RaptureLogModule* module)
    {
        if (module == null) return [];
        ref var queue = ref module->LogMessageQueue;
        if (queue.LongCount is < 0 or > 16384 || (queue.LongCount > 0 && queue.Map == null))
            throw new InvalidOperationException("Native log queue layout is unavailable.");
        var identities = new nint[queue.Count];
        for (var i = 0; i < identities.Length; i++)
            identities[i] = (nint)Unsafe.AsPointer(ref queue[i].Parameters);
        return identities;
    }

    private void PruneQueue(RaptureLogModule* module)
    {
        try { bindings.RetainLive(QueueIdentities(module)); }
        catch (Exception ex)
        {
            bindings.Clear();
            Report(ex, "[LocalPreview] Native queue lifetime validation failed; pending links discarded.");
        }
    }

    private void Report(Exception exception, string text)
    {
        try { log.Error(exception, text); } catch { /* never throw managed logging errors through native callbacks */ }
    }

    private void ReleaseHooks()
    {
        writeEmoteLog?.Dispose();
        update?.Dispose();
        format?.Dispose();
        writeEmoteLog = null;
        update = null;
        format = null;
        bindings.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Enabled = false;
        ReleaseHooks();
        logDepth = 0;
        formatDepth = 0;
    }
}
