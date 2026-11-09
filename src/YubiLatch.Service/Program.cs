using System.Security.Principal;
using YubiLatch.Service;

// There is deliberately no console/debug mode that could create a vault under a different identity.
if (!WindowsIdentity.GetCurrent().IsSystem || Environment.UserInteractive)
{
    Console.Error.WriteLine("YubiLatch.Service 只能由 Windows 服务管理器以 LocalSystem 身份启动。");
    return 1;
}
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders(); // No request, credential, SDK exception, or crash dump logging.
builder.Services.AddWindowsService(options => options.ServiceName = "YubiLatch");
builder.Services.AddSingleton<Vault>();
builder.Services.AddSingleton<Hardware>();
builder.Services.AddHostedService<Broker>();
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    ServiceDiagnostics.Record(ServiceStage.UnhandledException, e.ExceptionObject as Exception);
try { await builder.Build().RunAsync(); return 0; }
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    ServiceDiagnostics.Record(ServiceStage.HostFailed, ex);
    return 1;
}
