using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Serilog;
using SoluPulse.Agent;
using SoluPulse.Agent.Grpc;
using SoluPulse.Agent.Monitoring;
using SoluPulse.Agent.Security;
using SoluPulse.Contracts;
using SoluPulse.Core.Collection;
using SoluPulse.Core.Corrections;
using SoluPulse.Core.Audit;
using SoluPulse.Platform.Windows;
using SoluPulse.Platform.Windows.Collectors;
using SoluPulse.Storage;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var agentOptions = builder.Configuration.GetSection(AgentOptions.Section).Get<AgentOptions>() ?? new AgentOptions();
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.Section));
AccessControl.ProtectDataDirectory(agentOptions.DataDirectory);

// Journaux techniques en rotation : 10 Mo par fichier, 5 fichiers (ENF-MAIN-04).
builder.Services.AddSerilog(logger => logger
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Grpc", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.File(
        Path.Combine(agentOptions.LogDirectory, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 5,
        formatProvider: System.Globalization.CultureInfo.InvariantCulture));

builder.Services.AddWindowsService(options => options.ServiceName = "SoluPulse");

// Canal nommé local protégé par ACL, seule entrée du service (ENF-SEC-02). Aucun port TCP n'est ouvert.
builder.WebHost.UseNamedPipes(options =>
{
    options.CurrentUserOnly = false;
    options.PipeSecurity = AccessControl.PipeSecurity();
});
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenNamedPipe(AgentPipe.Name, listen => listen.Protocols = HttpProtocols.Http2);
    kestrel.AddServerHeader = false;
});
builder.Services.AddGrpc(options =>
{
    options.EnableDetailedErrors = false;
    options.MaxReceiveMessageSize = 1024 * 1024;
    options.Interceptors.Add<ErrorLoggingInterceptor>();
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new NetworkProbeOptions());
builder.Services.AddSingleton(new SqliteDatabase(agentOptions.DatabasePath));
builder.Services.AddSingleton<MeasurementStore>();
builder.Services.AddSingleton<AlertStore>();
builder.Services.AddSingleton<InventoryStore>();
builder.Services.AddSingleton<SensorIssueStore>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<ExecutionJournal>();
builder.Services.AddSingleton<IExecutionJournal>(sp => sp.GetRequiredService<ExecutionJournal>());
builder.Services.AddSingleton<SqliteAuditLog>();
builder.Services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<SqliteAuditLog>());

builder.Services.AddSingleton<IEnumerable<ICollector>>(sp => WindowsPlatform.Collectors(sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<NetworkProbeOptions>()));
builder.Services.AddSingleton(_ => new ActionCatalog(WindowsPlatform.Actions()));
builder.Services.AddSingleton<IRestorePointService, RestorePointService>();
builder.Services.AddSingleton<IAlertVerifier, AlertVerifier>();
builder.Services.AddSingleton<CorrectionExecutor>();

builder.Services.AddSingleton<AgentState>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<AlertMapper>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<DiagnosticRunner>();
builder.Services.AddSingleton<CallerAuthenticator>();
builder.Services.AddHostedService<MonitoringService>();

var app = builder.Build();
app.MapGrpcService<AgentGrpcService>();

var log = app.Services.GetRequiredService<ILogger<Program>>();
var options = app.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
if (!options.RequireTrustedClient)
{
    log.LogWarning("Mode développement : le client du canal nommé n'est pas vérifié.");
}

await app.RunAsync();
