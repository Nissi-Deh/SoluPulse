using SoluPulse.Agent;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "SoluPulse Agent";
});
builder.Services.AddHostedService<MonitoringWorker>();

await builder.Build().RunAsync();
