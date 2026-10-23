namespace YuumiX.App.Composition;

/// <summary>
/// 浮动 Toast 消息项（对齐原版 Naive UI n-message-provider 机制）。
/// </summary>
public sealed record ToastItem(long Id, string Message, bool IsError);
