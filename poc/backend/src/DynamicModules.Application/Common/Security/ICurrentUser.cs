namespace DynamicModules.Application.Common.Security;

public interface ICurrentUser
{
    string UserId { get; }
}

public interface IKnownUsers
{
    bool Exists(string userId);
}
