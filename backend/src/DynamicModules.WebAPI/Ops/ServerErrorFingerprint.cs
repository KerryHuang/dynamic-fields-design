using System.Diagnostics;
using DynamicModules.Domain.Ops;

namespace DynamicModules.WebAPI.Ops;

public static class ServerErrorFingerprint
{
    /// <summary>未處理例外的指紋：例外型別、正規化訊息、前三個產品框與路由樣板（設計 §10.2.1）。</summary>
    public static string Of(Exception e, string? routeTemplate)
    {
        var frames = new List<string>();
        foreach (var frame in new StackTrace(e, false).GetFrames())
        {
            var method = frame.GetMethod();
            if (method?.DeclaringType?.FullName is { } type)
                frames.Add(ErrorFingerprint.NormalizeFrame(type, method.Name));
        }
        return ErrorFingerprint.ForServer(e.GetType().FullName ?? e.GetType().Name, e.Message, frames, routeTemplate);
    }
}
