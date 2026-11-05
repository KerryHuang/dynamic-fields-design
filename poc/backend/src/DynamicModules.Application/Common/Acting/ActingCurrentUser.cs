using DynamicModules.Application.Common.Security;

namespace DynamicModules.Application.Common.Acting;

/// <summary>ActingScope 有指定身分時用它，否則交給原實作。</summary>
public sealed class ActingCurrentUser(ICurrentUser inner) : ICurrentUser
{
    public string UserId => ActingScope.UserId ?? inner.UserId;
}
