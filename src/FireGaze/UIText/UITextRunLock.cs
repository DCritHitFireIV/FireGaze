namespace FireGaze.UIText;

/// <summary>
///     同一时刻只允许一个「会对插件 DLL / 译文包动手」的任务在跑（一键汉化、写入、还原、编辑器里的批量翻译）。
/// </summary>
/// <remarks>
///     两个窗口（列表页 + 编辑校对）都碰同一份包和同一个 DLL：不互斥就会出现
///     「列表正在翻译、编辑器把旧包存回去」这类互相覆盖。这里用最笨的全局锁——
///     谁拿到谁干活，别人按钮置灰并说明原因。
/// </remarks>
internal sealed class UITextRunLock
{
    private readonly object gate = new();
    private string? holder;
    private string what = string.Empty;

    /// <summary>
    ///     当前有没有任务在跑；<paramref name="what" /> 是给人看的一句话。
    /// </summary>
    public bool IsBusy(out string what)
    {
        lock (this.gate)
        {
            what = this.holder is null ? string.Empty : this.what;
            return this.holder is not null;
        }
    }

    /// <summary>
    ///     这个插件是不是正在被处理（编辑器据此禁用按钮）。
    /// </summary>
    public bool IsBusyWith(string internalName)
    {
        lock (this.gate)
        {
            return this.holder is not null && string.Equals(this.holder, internalName, StringComparison.Ordinal);
        }
    }

    /// <summary>
    ///     抢锁：成功返回 true，失败时 <paramref name="reason" /> 说明是谁在跑。
    /// </summary>
    public bool TryEnter(string internalName, string what, out string reason)
    {
        lock (this.gate)
        {
            if (this.holder is not null)
            {
                reason = this.what;
                return false;
            }

            this.holder = internalName;
            this.what = what;
            reason = string.Empty;
            return true;
        }
    }

    /// <summary>
    ///     放锁（只能由持有者放；不是持有者调用无效）。
    /// </summary>
    public void Exit(string internalName)
    {
        lock (this.gate)
        {
            if (this.holder is not null && string.Equals(this.holder, internalName, StringComparison.Ordinal))
            {
                this.holder = null;
                this.what = string.Empty;
            }
        }
    }
}
