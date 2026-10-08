using System.Security.Cryptography;
using System.Text;

namespace Aviary.Core;

// Local control channel between the Aviary app (which owns the VMs) and aviary-mcp. Newline-delimited JSON:
// {"id":1,"method":"list_machines","params":{}} -> {"id":1,"result":...} or {"id":1,"error":"..."}.
// Both ends use PipeOptions.CurrentUserOnly, so only this Windows account can connect or serve.
public static class ControlPipe
{
    public static string Name { get; } = "aviary-control-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16].ToLowerInvariant();
}
