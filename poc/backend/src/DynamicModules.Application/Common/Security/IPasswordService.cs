namespace DynamicModules.Application.Common.Security;

public interface IPasswordService
{
    string Hash(string password);

    bool Verify(string hash, string password);
}
