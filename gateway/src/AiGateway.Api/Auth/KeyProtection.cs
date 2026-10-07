using System.Xml.Linq;
using AiGateway.Core.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AiGateway.Api.Auth;

public static class KeyProtection
{
    public static IServiceCollection AddProtectedGatewayKeys(this IServiceCollection services, IConfiguration config)
    {
        var protection = services.AddDataProtection().SetApplicationName("AiGateway").PersistKeysToDbContext<GatewayDbContext>();
        if (config["Security:KeyCertificateThumbprint"] is { Length: > 0 } thumbprint)
            protection.ProtectKeysWithCertificate(thumbprint);
        else if (OperatingSystem.IsWindows())
            protection.ProtectKeysWithDpapi(protectToLocalMachine: false);
        else
            throw new InvalidOperationException("Security:KeyCertificateThumbprint diperlukan di luar Windows.");
        return services;
    }
}
