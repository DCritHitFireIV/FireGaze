using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.Text;
using FFXIVClientStructs.STD;
using InteropGenerator.Runtime;
using Lumina.Excel.Sheets;

namespace FireGaze.LocalPreview.Interaction;

/// <summary>
/// Wraps only exact, unique native emote templates while the game updates its native log queue.
/// It does not match displayed prose, change queue entries or retain native call pointers.
/// </summary>
internal sealed unsafe class NativeEmoteLogCapture(IDataManager data, IGameInteropProvider interop, IPluginLog log) : IDisposable
{
    private readonly EmoteLogTemplateIndex templates = new();
    private Hook<RaptureLogModule.Delegates.Update>? update;
    private Hook<RaptureTextModule.Delegates.FormatString>? format;
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
            var emoteLogIds = new HashSet<uint>();
            foreach (var emote in data.GetExcelSheet<Emote>())
            {
                if (emote.RowId == 0 || emote.RowId > ushort.MaxValue) continue;
                Add(emote.LogMessageTargeted.RowId, (ushort)emote.RowId);
                Add(emote.LogMessageUntargeted.RowId, (ushort)emote.RowId);
            }
            // If a non-emote native log shares the same template, it is not safe to wrap it.
            foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>())
                if (!emoteLogIds.Contains(row.RowId)) templates.ExcludeNonEmoteTemplate(row.Text.Data.Span);

            var nativeLog = RaptureLogModule.Instance();
            if (nativeLog == null || nativeLog->RaptureTextModule == null || nativeLog->RaptureTextModule->VirtualTable == null
                || nativeLog->RaptureTextModule->VirtualTable->FormatString == null || RaptureLogModule.Addresses.Update.Value == 0)
            {
                Detail = "当前客户端未解析原生日志格式化接口。";
                return false;
            }
            format = interop.HookFromAddress<RaptureTextModule.Delegates.FormatString>(
                (nint)nativeLog->RaptureTextModule->VirtualTable->FormatString, FormatString);
            update = interop.HookFromAddress<RaptureLogModule.Delegates.Update>(RaptureLogModule.Addresses.Update.Value, UpdateLog);
            format.Enable();
            update.Enable();
            Enabled = true;
            Detail = $"原生模板捕获已开启：唯一模板 {UniqueTemplates}，歧义模板 {AmbiguousTemplates}；歧义项不生成试用链接。";
            return true;

            void Add(uint logId, ushort emoteId)
            {
                if (logId == 0 || !data.GetExcelSheet<Lumina.Excel.Sheets.LogMessage>().TryGetRow(logId, out var row)) return;
                emoteLogIds.Add(logId);
                templates.Add(emoteId, row.Text.Data.Span);
            }
        }
        catch (Exception ex)
        {
            update?.Dispose();
            format?.Dispose();
            update = null;
            format = null;
            Detail = "原生情感日志捕获启动失败，基础右键菜单仍可使用。";
            Report(ex, "[LocalPreview] Native emote log capture could not start.");
            return false;
        }
    }

    private void UpdateLog(RaptureLogModule* module)
    {
        logDepth++;
        try { update!.Original(module); }
        catch (Exception ex) { Report(ex, "[LocalPreview] Native log update failed."); }
        finally { logDepth--; }
    }

    private bool FormatString(RaptureTextModule* module, CStringPointer input, StdDeque<TextParameter>* parameters, Utf8String* output)
    {
        var topLevel = formatDepth++ == 0;
        ushort emoteId = 0;
        try
        {
            // Decode before Original, while its input is valid. No pointer escapes this call.
            if (topLevel && logDepth > 0)
            {
                try
                {
                    if (!templates.TryResolve(input.AsSpan(), out emoteId)) FormatsUnmatched++;
                }
                catch (Exception ex) { Report(ex, "[LocalPreview] Native emote template inspection failed."); }
            }

            bool result;
            // Every path invokes the original formatter once, including unknown or malformed input.
            try { result = format!.Original(module, input, parameters, output); }
            catch (Exception ex)
            {
                Report(ex, "[LocalPreview] Native text formatter failed.");
                return false;
            }
            if (!result || emoteId == 0 || output == null) return result;

            byte[]? original = null;
            var writing = false;
            try
            {
                if (NativeEmoteLinkBuilder.TryReadEmoteId(output->AsSpan(), out _)) return result;
                original = output->AsSpan().ToArray();
                var linked = NativeEmoteLinkBuilder.Wrap(original, emoteId);
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

    private void Report(Exception exception, string text)
    {
        try { log.Error(exception, text); } catch { /* never throw a managed logging exception through a native callback */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Enabled = false;
        update?.Dispose();
        format?.Dispose();
        update = null;
        format = null;
        logDepth = 0;
        formatDepth = 0;
    }
}
